using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgShell.Pty.Tests
{
    internal static class Program
    {
        private const int StandardInputHandle = -10;
        private const uint PipeFileType = 3;
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int handle);

        [DllImport("kernel32.dll")]
        private static extern uint GetFileType(IntPtr handle);

        public static async Task<int> Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "hold-input")
                {
                    if (GetFileType(GetStdHandle(StandardInputHandle)) != PipeFileType)
                    {
                        throw new InvalidOperationException("The input fixture must inherit a pipe.");
                    }

                    Console.WriteLine(Environment.ProcessId);
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    return 0;
                }

                if (args.Length == 2 && args[0] == "input-parent")
                {
                    ProcessStartInfo start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "AgShell.Pty.Tests.exe"))
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                    };
                    start.ArgumentList.Add("hold-input");
                    using Process child = Process.Start(start)!;
                    try
                    {
                        string? ready = await child.StandardOutput.ReadLineAsync();
                        if (!int.TryParse(ready, out int id) || id != child.Id)
                        {
                            throw new InvalidOperationException("The input descendant did not report its identity.");
                        }

                        await File.WriteAllTextAsync(args[1], ready);
                        return 0;
                    }
                    catch
                    {
                        if (!child.HasExited) child.Kill();
                        throw;
                    }
                }

                if (args.Length != 2 || args[0] != "ctrl-c" || !bool.TryParse(args[1], out bool inheritedIgnore))
                {
                    return 2;
                }

                if (!SetConsoleCtrlHandler(IntPtr.Zero, inheritedIgnore))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                // WSL/console launchers can pass an inheritable ignore flag. Only this isolated fixture resets it.
                if (!SetConsoleCtrlHandler(IntPtr.Zero, false))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                Console.WriteLine($"inheritedIgnore={inheritedIgnore}; reset before ConPTY; runtime={Environment.Version}");
                await PseudoConsoleSessionTests.CheckCtrlCAsync();
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }
    }
}
