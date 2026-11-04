using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgShell.Pty
{
    /// <summary>
    /// One process running inside a ConPTY pseudo console.
    /// Output and Exited are raised on background threads; handlers must not block on the thread that calls Dispose.
    /// </summary>
    public sealed class PseudoConsoleSession : IDisposable
    {
        private static readonly TimeSpan TerminateTimeout = TimeSpan.FromSeconds(3);

        private readonly PtyStartOptions options;
        private readonly object sync = new object();
        private readonly TaskCompletionSource<int> exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource outputClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private IntPtr pseudoConsole;
        private FileStream? input;
        private FileStream? output;
        private SafeProcessHandle? process;
        private bool started;
        private bool disposed;

        public PseudoConsoleSession(PtyStartOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            ValidateSize(options.Columns, options.Rows);
            this.options = options;
        }

        public event Action<string>? Output;

        public event Action<int>? Exited;

        public int ProcessId { get; private set; }

        public Task<int> Exit => exit.Task;

        public Task OutputClosed => outputClosed.Task;

        public void Start()
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (started)
                {
                    throw new InvalidOperationException("Session is already started.");
                }

                started = true;
                StartCore();
            }

            Thread reader = new Thread(ReadOutput) { IsBackground = true, Name = "agshell-pty-output" };
            reader.Start();
            Thread waiter = new Thread(WaitForExit) { IsBackground = true, Name = "agshell-pty-exit" };
            waiter.Start();
        }

        public void Write(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (input is null)
                {
                    throw new InvalidOperationException("Session is not started.");
                }

                input.Write(bytes, 0, bytes.Length);
                input.Flush();
            }
        }

        public void Resize(int columns, int rows)
        {
            ValidateSize(columns, rows);
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (pseudoConsole == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Session is not started.");
                }

                int hr = NativeMethods.ResizePseudoConsole(pseudoConsole, ToCoord(columns, rows));
                Marshal.ThrowExceptionForHR(hr);
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                if (pseudoConsole != IntPtr.Zero)
                {
                    // Closing the pseudo console ends the attached processes and closes the output pipe, which stops the reader.
                    NativeMethods.ClosePseudoConsole(pseudoConsole);
                    pseudoConsole = IntPtr.Zero;
                }

                input?.Dispose();
                input = null;
            }

            if (!started)
            {
                outputClosed.TrySetResult();
                return;
            }

            _ = TerminateIfStillRunningAsync();
        }

        // A process that had not attached to the pseudo console yet when it was closed keeps running on its own.
        private async Task TerminateIfStillRunningAsync()
        {
            Task finished = await Task.WhenAny(exit.Task, Task.Delay(TerminateTimeout));
            if (finished == exit.Task)
            {
                return;
            }

            try
            {
                NativeMethods.TerminateProcess(process!, 1);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void StartCore()
        {
            SafeFileHandle? inputRead = null;
            SafeFileHandle? inputWrite = null;
            SafeFileHandle? outputRead = null;
            SafeFileHandle? outputWrite = null;
            IntPtr attributeList = IntPtr.Zero;
            bool attributeListInitialized = false;
            bool succeeded = false;

            try
            {
                if (!NativeMethods.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe failed for input.");
                }

                if (!NativeMethods.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe failed for output.");
                }

                int hr = NativeMethods.CreatePseudoConsole(ToCoord(options.Columns, options.Rows), inputRead, outputWrite, 0, out pseudoConsole);
                Marshal.ThrowExceptionForHR(hr);

                IntPtr attributeListSize = IntPtr.Zero;
                NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
                attributeList = Marshal.AllocHGlobal(attributeListSize);
                if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList failed.");
                }

                attributeListInitialized = true;
                if (!NativeMethods.UpdateProcThreadAttribute(attributeList, 0, NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, pseudoConsole, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute failed.");
                }

                NativeMethods.STARTUPINFOEX startupInfo = new NativeMethods.STARTUPINFOEX();
                startupInfo.StartupInfo.cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>();
                // Without empty std handles the child inherits the parent's redirected stdio instead of the pseudo console.
                startupInfo.StartupInfo.dwFlags = NativeMethods.STARTF_USESTDHANDLES;
                startupInfo.lpAttributeList = attributeList;

                char[] commandLine = (options.CommandLine + '\0').ToCharArray();
                char[]? environmentBlock = EnvironmentBlock.Build(options.Environment);
                NativeMethods.PROCESS_INFORMATION processInfo;
                bool created;
                // Child processes inherit the error mode. Without this, a shell whose pseudo console is closed while it
                // is still starting shows a system "unable to start correctly" dialog and hangs until someone clicks it.
                uint previousErrorMode = NativeMethods.SetErrorMode(NativeMethods.SEM_FAILCRITICALERRORS | NativeMethods.SEM_NOGPFAULTERRORBOX);
                unsafe
                {
                    fixed (char* commandLinePointer = commandLine)
                    fixed (char* environmentPointer = environmentBlock)
                    {
                        created = NativeMethods.CreateProcess(
                            null,
                            commandLinePointer,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            false,
                            NativeMethods.EXTENDED_STARTUPINFO_PRESENT | NativeMethods.CREATE_UNICODE_ENVIRONMENT,
                            (IntPtr)environmentPointer,
                            options.WorkingDirectory,
                            ref startupInfo,
                            out processInfo);
                    }
                }

                NativeMethods.SetErrorMode(previousErrorMode);

                if (!created)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateProcess failed for: " + options.CommandLine);
                }

                NativeMethods.CloseHandle(processInfo.hThread);
                process = new SafeProcessHandle(processInfo.hProcess, true);
                ProcessId = processInfo.dwProcessId;

                input = new FileStream(inputWrite, FileAccess.Write, 0, false);
                output = new FileStream(outputRead, FileAccess.Read, 0, false);
                succeeded = true;
            }
            finally
            {
                if (attributeListInitialized)
                {
                    NativeMethods.DeleteProcThreadAttributeList(attributeList);
                }

                if (attributeList != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(attributeList);
                }

                // The pseudo console holds its own copies of these ends.
                inputRead?.Dispose();
                outputWrite?.Dispose();

                if (!succeeded)
                {
                    if (pseudoConsole != IntPtr.Zero)
                    {
                        NativeMethods.ClosePseudoConsole(pseudoConsole);
                        pseudoConsole = IntPtr.Zero;
                    }

                    inputWrite?.Dispose();
                    outputRead?.Dispose();
                }
            }
        }

        private void ReadOutput()
        {
            FileStream stream = output!;
            byte[] buffer = new byte[16 * 1024];
            char[] chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
            Decoder decoder = Encoding.UTF8.GetDecoder();

            try
            {
                while (true)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }

                    int charCount = decoder.GetChars(buffer, 0, read, chars, 0, false);
                    if (charCount > 0)
                    {
                        Output?.Invoke(new string(chars, 0, charCount));
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                stream.Dispose();
                outputClosed.TrySetResult();
            }
        }

        private void WaitForExit()
        {
            SafeProcessHandle handle = process!;
            int exitCode = -1;
            try
            {
                NativeMethods.WaitForSingleObject(handle, NativeMethods.INFINITE);
                if (!NativeMethods.GetExitCodeProcess(handle, out exitCode))
                {
                    exitCode = -1;
                }
            }
            finally
            {
                handle.Dispose();
            }

            exit.TrySetResult(exitCode);
            Exited?.Invoke(exitCode);
        }

        private static void ValidateSize(int columns, int rows)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, short.MaxValue);
            ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, short.MaxValue);
        }

        private static NativeMethods.COORD ToCoord(int columns, int rows)
        {
            return new NativeMethods.COORD { X = (short)columns, Y = (short)rows };
        }
    }
}
