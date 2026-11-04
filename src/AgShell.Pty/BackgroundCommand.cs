using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgShell.Pty
{
    /// <summary>Detached shell process with explicit file handles and cancellable asynchronous stdin.</summary>
    public sealed partial class BackgroundCommand : IDisposable
    {
        private readonly SafeProcessHandle process;
        private readonly NamedPipeServerStream? input;

        private BackgroundCommand(SafeProcessHandle process, int id, NamedPipeServerStream? input, string? errorCaptureFailure)
        {
            this.process = process;
            this.input = input;
            Id = id;
            ErrorCaptureFailure = errorCaptureFailure;
        }

        public int Id { get; }

        public string? ErrorCaptureFailure { get; }

        public static unsafe BackgroundCommand Start(ProcessStartInfo start, bool pipeInput, string? errorFile = null)
        {
            SecurityAttributes security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
            using SafeFileHandle nil = OpenFile("NUL", ref security, 3);
            SafeFileHandle? errors = null;
            string? errorCaptureFailure = null;
            try
            {
                if (errorFile is not null) errors = OpenFile(errorFile, ref security, 2);
            }
            catch (Win32Exception ex)
            {
                errorCaptureFailure = ex.Message;
            }
            NamedPipeServerStream? input = null;
            SafeFileHandle? reader = null;
            IntPtr attributes = IntPtr.Zero;
            IntPtr inherited = IntPtr.Zero;
            IntPtr environment = IntPtr.Zero;
            bool initialized = false;
            try
            {
                if (pipeInput)
                {
                    string name = "agshell-hook-input-" + Guid.NewGuid().ToString("N");
                    input = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    reader = OpenFile(@"\\.\pipe\" + name, ref security, 3, 0x80000000);
                    input.WaitForConnection();
                }

                IntPtr stdin = reader?.DangerousGetHandle() ?? nil.DangerousGetHandle();
                IntPtr stdout = nil.DangerousGetHandle();
                IntPtr stderr = errors?.DangerousGetHandle() ?? stdout;
                IntPtr[] handles = new[] { stdin, stdout, stderr }.Distinct().ToArray();
                inherited = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
                for (int i = 0; i < handles.Length; i++)
                {
                    Marshal.WriteIntPtr(inherited, i * IntPtr.Size, handles[i]);
                }

                IntPtr size = IntPtr.Zero;
                NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attributes = Marshal.AllocHGlobal(size);
                if (!NativeMethods.InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                initialized = true;
                if (!NativeMethods.UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20002), inherited,
                    handles.Length * IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                NativeMethods.STARTUPINFOEX info = new NativeMethods.STARTUPINFOEX
                {
                    StartupInfo = new NativeMethods.STARTUPINFO
                    {
                        cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>(),
                        dwFlags = NativeMethods.STARTF_USESTDHANDLES,
                        hStdInput = stdin,
                        hStdOutput = stdout,
                        hStdError = stderr,
                    },
                    lpAttributeList = attributes,
                };
                string block = string.Join('\0', start.Environment.Where(pair => pair.Value is not null)
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
                environment = Marshal.StringToHGlobalUni(block);
                string command = "\"" + start.FileName + "\" " + start.Arguments;
                NativeMethods.PROCESS_INFORMATION created;
                fixed (char* line = command)
                {
                    if (!NativeMethods.CreateProcess(start.FileName, line, IntPtr.Zero, IntPtr.Zero, true,
                        NativeMethods.EXTENDED_STARTUPINFO_PRESENT | NativeMethods.CREATE_UNICODE_ENVIRONMENT | 0x08000000,
                        environment, start.WorkingDirectory, ref info, out created))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }
                }

                NativeMethods.CloseHandle(created.hThread);
                return new BackgroundCommand(new SafeProcessHandle(created.hProcess, true), created.dwProcessId, input, errorCaptureFailure);
            }
            catch
            {
                input?.Dispose();
                throw;
            }
            finally
            {
                reader?.Dispose();
                errors?.Dispose();
                if (initialized) NativeMethods.DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
                Marshal.FreeHGlobal(inherited);
                Marshal.FreeHGlobal(environment);
            }
        }

        public async Task WriteInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            if (input is null) throw new InvalidOperationException("This command has no input pipe.");
            try
            {
                await input.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await input.DisposeAsync().ConfigureAwait(false);
            }
        }

        public async Task<int> WaitForExitAsync()
        {
            using WaitHandle exited = new ProcessWaitHandle(process.DangerousGetHandle());
            TaskCompletionSource completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(exited,
                (state, timedOut) => completion.TrySetResult(), null, Timeout.Infinite, true);
            try
            {
                await completion.Task.ConfigureAwait(false);
                if (!NativeMethods.GetExitCodeProcess(process, out int status))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                return status;
            }
            finally
            {
                registration.Unregister(null);
            }
        }

        public void Dispose()
        {
            input?.Dispose();
            process.Dispose();
        }

        private static SafeFileHandle OpenFile(string path, ref SecurityAttributes security, uint disposition, uint access = 0xC0000000)
        {
            SafeFileHandle file = CreateFile(path, access, 7, ref security, disposition, 0x80, IntPtr.Zero);
            if (file.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                file.Dispose();
                throw new Win32Exception(error);
            }

            return file;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Size;
            public IntPtr Descriptor;
            public int Inherit;
        }

        private sealed class ProcessWaitHandle : WaitHandle
        {
            public ProcessWaitHandle(IntPtr process)
            {
                SafeWaitHandle = new SafeWaitHandle(process, false);
            }
        }

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial SafeFileHandle CreateFile(string name, uint access, uint sharing, ref SecurityAttributes security,
            uint disposition, uint flags, IntPtr template);
    }
}
