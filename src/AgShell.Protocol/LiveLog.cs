namespace AgShell.Protocol
{
    /// <summary>
    /// The live daemons' log, as zmx's log.zig: a daemon writes &lt;pipe&gt;.log, the clients that reach daemons (the window,
    /// agshellctl live proxy) write live.log, zmx's zmx.log, all in one directory per state directory, as zmx's
    /// &lt;ZMX_DIR&gt;/logs. Every level from debug is written, as zmx's; past <see cref="MaxBytes"/> the file starts empty again,
    /// as zmx's wipe. A failed write is dropped.
    /// </summary>
    public sealed class LiveLog
    {
        public const long MaxBytes = 2 * 1024 * 1024;

        /// <summary>The file the clients write, zmx's zmx.log.</summary>
        public const string ClientFile = "live.log";

        private readonly object gate = new object();
        private readonly string? path;
        private readonly long limit;

        public LiveLog(string? path, long maxBytes = MaxBytes)
        {
            this.path = path;
            limit = maxBytes;
        }

        /// <summary>
        /// Where a state directory's daemons log: %TEMP%\&lt;their pipe prefix&gt;\logs, as agterm's zmx logs under
        /// /tmp/agterm-zmx-&lt;hash of the state directory&gt;/logs.
        /// </summary>
        public static string Directory(string stateDirectory)
        {
            return DirectoryOf(ControlEndpoint.LivePipePrefix(stateDirectory));
        }

        /// <summary>
        /// The log of the daemon serving a pipe, named as the pipe, as zmx names a session's log by the session; a pipe that
        /// is not a pane's (a test's) logs nowhere.
        /// </summary>
        public static LiveLog ForDaemon(string pipeName)
        {
            int cut = pipeName.LastIndexOf("-live-", StringComparison.Ordinal) + "-live-".Length;
            bool pane = cut >= "-live-".Length && pipeName.Length - cut == 32 && pipeName[cut..].All(char.IsAsciiHexDigitLower);
            return new LiveLog(pane ? Path.Combine(DirectoryOf(pipeName[..cut]), pipeName + ".log") : null);
        }

        /// <summary>The clients' log of a state directory.</summary>
        public static LiveLog ForClients(string stateDirectory)
        {
            return new LiveLog(Path.Combine(Directory(stateDirectory), ClientFile));
        }

        /// <summary>
        /// Removes the logs written before Windows last started: no daemon outlives a restart, and %TEMP% is not emptied at
        /// one as macOS empties the /tmp agterm's zmx logs in.
        /// </summary>
        public static void Prune(string stateDirectory)
        {
            DateTime booted = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
            try
            {
                foreach (string file in System.IO.Directory.GetFiles(Directory(stateDirectory), "*.log"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < booted)
                        {
                            File.Delete(file);
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        public void Debug(string message) => Write("debug", message);

        public void Info(string message) => Write("info", message);

        public void Warn(string message) => Write("warning", message);

        public void Error(string message) => Write("error", message);

        private static string DirectoryOf(string pipePrefix)
        {
            return Path.Combine(Path.GetTempPath(), pipePrefix.TrimEnd('-'), "logs");
        }

        /// <summary>zmx's line: [unix seconds] [level] (scope): message.</summary>
        private void Write(string level, string message)
        {
            if (path is null)
            {
                return;
            }

            string line = "[" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "] [" + level + "] (default): " + message + "\n";
            lock (gate)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    FileInfo file = new FileInfo(path);
                    FileMode mode = file.Exists && file.Length >= limit ? FileMode.Create : FileMode.Append;
                    using FileStream stream = new FileStream(path, mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    stream.Write(System.Text.Encoding.UTF8.GetBytes(line));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
