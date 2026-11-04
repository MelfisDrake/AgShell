using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AgShell.Pty.Tests
{
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);

        public static async Task<int> Main(string[] args)
        {
            if (args.Length != 2 || args[0] != "ctrl-c" || !bool.TryParse(args[1], out bool inheritedIgnore))
            {
                return 2;
            }

            try
            {
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
