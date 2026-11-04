using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using AgShell.Core;
using AgShell.Protocol;

namespace AgShell.App
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
#if DEBUG
            // agterm's Debug isolation: in the environment before anything reads it, so shells, the live daemons and agshellctl
            // started from here inherit it too.
            if (DebugStateDirectory.Adopted(Environment.GetEnvironmentVariable(ControlEndpoint.StateDirVariable), ControlEndpoint.StandardStateDirectory()) is string adopted)
            {
                Environment.SetEnvironmentVariable(ControlEndpoint.StateDirVariable, adopted);
            }
#endif
            if (!WaitForRelaunch(args))
            {
                Environment.ExitCode = 1;
                return;
            }

            // agterm's `open -a agterm <path>`: the paths given open as new sessions, each in its folder.
            // agterm's Dock menu item, from the jump list: it reaches the running instance; with none, the app just starts.
            bool dock = args.Contains(WindowLibrary.DockArgument);
            List<string> directories = dock ? new List<string>() : args.Where((arg, index) => !arg.StartsWith("--", StringComparison.Ordinal) && (index == 0 || args[index - 1] != RelaunchAfter))
                .Select(arg => OpenPathResolver.Directory(arg, Environment.CurrentDirectory))
                .OfType<string>()
                .ToList();

            // One process per state directory: a second launch brings the running window forward instead of
            // opening another one that would fight over state.json and the control pipe, and hands it its paths.
            string stateDirectory = ControlEndpoint.StateDirectory();
            string pipeName = ControlEndpoint.PipeName(stateDirectory);
            StateDirectoryClaim claim = StateDirectoryLock.Acquire(stateDirectory, out StateDirectoryLock? owned, out string detail);
            using StateDirectoryLock? instance = owned;
            if (claim == StateDirectoryClaim.Unavailable)
            {
                // agterm's claimStateDirectory: a lock that cannot be taken does not show that nobody else uses the directory.
                Log.Error("agshellApp", "cannot claim the state directory: " + detail);
                Console.Error.WriteLine("agshell: cannot claim the state directory: " + detail);
                Environment.ExitCode = 1;
                return;
            }

            if (claim == StateDirectoryClaim.HeldByAnother)
            {
                if (dock ? !OpenPaths.SendDock(pipeName, args) : (directories.Count == 0 || !OpenPaths.Send(pipeName, directories)) && !ActivateRunningInstance())
                {
                    // agterm's claimStateDirectory: a launch that reached nothing says so and fails.
                    Log.Error("agshellApp", "another instance is already running on " + stateDirectory + "; control it with agshellctl");
                    Console.Error.WriteLine("another instance is already running on " + stateDirectory + "; control it with agshellctl");
                    Environment.ExitCode = 1;
                }

                return;
            }

            // The window library quits the app when its last window closes.
            Application application = new Application();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            WindowLibrary.Start(application);
            OpenPaths.Listen(pipeName, opened => WindowLibrary.Current.OpenDirectories(opened), WindowLibrary.Current.RunDock);
            WindowLibrary.Current.OpenDirectories(directories);
            application.Run();
        }

        /// <summary>agterm's LiveResetRelauncher argument: the process this launch waits out before it starts.</summary>
        public const string RelaunchAfter = "--relaunch-after";

        /// <summary>
        /// agterm's relaunch script: waits for the quitting instance to exit, 300 polls of 0.2 s, and gives up without
        /// launching past that.
        /// </summary>
        private static bool WaitForRelaunch(string[] args)
        {
            int at = Array.IndexOf(args, RelaunchAfter);
            if (at < 0 || at + 1 >= args.Length || !int.TryParse(args[at + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid))
            {
                return true;
            }

            try
            {
                using Process quitting = Process.GetProcessById(pid);
                return quitting.WaitForExit(TimeSpan.FromSeconds(60));
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        /// <summary>Brings the running instance's window forward; false when no such window was found.</summary>
        private static bool ActivateRunningInstance()
        {
            using Process current = Process.GetCurrentProcess();
            foreach (Process process in Process.GetProcessesByName(current.ProcessName))
            {
                using (process)
                {
                    IntPtr window = process.MainWindowHandle;
                    if (process.Id == current.Id || window == IntPtr.Zero)
                    {
                        continue;
                    }

                    if (IsIconic(window))
                    {
                        ShowWindow(window, RestoreCommand);
                    }

                    SetForegroundWindow(window);
                    return true;
                }
            }

            return false;
        }

        private const int RestoreCommand = 9;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);
    }
}
