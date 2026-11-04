using System.Diagnostics;
using System.IO;
using AgShell.Core;
using AgShell.Protocol;

namespace AgShell.App
{
    /// <summary>
    /// Where live daemons run from: a copy of this build's daemon in %LOCALAPPDATA%\agshell\live\BUILD, so an update replaces
    /// the installed files and closes the window without the daemons and their shells, as agterm's update leaves its zmx
    /// daemons running. Windows locks a running program's files, and the installer closes whatever holds files it replaces.
    /// </summary>
    internal static class LiveDaemonCopy
    {
        public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agshell", "live");

        /// <summary>The build of the daemon installed beside the app.</summary>
        public static string Build { get; } = ControlEndpoint.LiveBuildOf(AppContext.BaseDirectory);

        /// <summary>
        /// agterm's bundled zmx BUILD: a digest of the daemon's sources, set at build time, so it moves only when the daemon
        /// does; null in a build without it, which turns the outdated reset reason off.
        /// </summary>
        public static string? BundledBuildId { get; } = typeof(LiveDaemonCopy).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == "LiveBuild")?.Value;

        /// <summary>Whether the app has a daemon to start.</summary>
        public static bool Installed => File.Exists(Path.Combine(AppContext.BaseDirectory, "agshell-live.exe"));

        /// <summary>
        /// Starts a daemon for the pipe from this build's copy, made first when missing; from the install when it cannot be
        /// made or run (a copy another instance pruned meanwhile, an antivirus). False when neither started.
        /// </summary>
        public static bool Start(string pipe)
        {
            string installed = Path.Combine(AppContext.BaseDirectory, "agshell-live.exe");
            string program = installed;
            try
            {
                program = Ensure();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Warning("live", "daemon copy failed, the daemon runs from the install: " + ex.Message);
            }

            if (Run(program, pipe) is string failure && (program == installed || Run(installed, pipe) is not null))
            {
                Log.Error("live", "could not start the daemon for " + pipe + ": " + failure);
                return false;
            }

            return true;
        }

        /// <summary>Runs the daemon out of the app's job, so it outlives the app; null when it started, else why not.</summary>
        private static string? Run(string program, string pipe)
        {
            return DetachedProcess.Start(program, pipe);
        }

        /// <summary>The copy's agshell-live.exe; a copy is made aside and renamed in, so a half-made one is never run.</summary>
        private static string Ensure()
        {
            string target = Path.Combine(Root, Build);
            string program = Path.Combine(target, "agshell-live.exe");
            if (File.Exists(program))
            {
                return program;
            }

            string staging = Path.Combine(Root, Build + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            try
            {
                Directory.CreateDirectory(staging);
                foreach (string name in ControlEndpoint.LiveDaemonFiles)
                {
                    string source = Path.Combine(AppContext.BaseDirectory, name);
                    if (File.Exists(source))
                    {
                        File.Copy(source, Path.Combine(staging, name));
                    }
                }

                Directory.Move(staging, target);
            }
            catch (IOException) when (File.Exists(program))
            {
                // Another launch made it meanwhile.
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }

            return program;
        }

        /// <summary>Removes the copies of other builds that nothing runs from any more, the daemons and their consoles alike.</summary>
        public static void Prune()
        {
            string[] folders;
            try
            {
                folders = Directory.Exists(Root) ? Directory.GetDirectories(Root) : Array.Empty<string>();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return;
            }

            HashSet<string> running = RunningFrom();
            foreach (string folder in folders)
            {
                string name = Path.GetFileName(folder);
                if (name.Equals(Build, StringComparison.OrdinalIgnoreCase) || running.Any(path => path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Notice("live", "daemon copy " + name + " not removed: " + ex.Message);
                }
            }
        }

        private static HashSet<string> RunningFrom()
        {
            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    if (ProcessTree.ImagePath(process.Id) is string path && path.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
                    {
                        paths.Add(path);
                    }
                }
            }

            return paths;
        }
    }
}
