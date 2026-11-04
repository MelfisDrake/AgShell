using System.Diagnostics;
using System.Text;
using AgShell.Protocol;

namespace AgShell.Ctl
{
    /// <summary>
    /// <c>agshellctl session overlay run-job &lt;job&gt;</c>: agterm's OverlayJobRunner, run through ssh with a terminal by the
    /// machine presenting the session. It claims the job over the control pipe, takes the launch context the app sends on that
    /// connection, runs the program once in this terminal and reports how it ended there; a cancel from the app, or losing
    /// the terminal, stops it. The app going away does not: the program runs on, only unreported.
    /// </summary>
    internal static class OverlayRunJob
    {
        public static int Run(string job, string? pipeOverride)
        {
            string pipe = ControlEndpoint.ResolvePipe(pipeOverride);
            (ControlResponse response, Stream? connection) opened;
            try
            {
                opened = ControlClient.OpenStreamAsync(pipe, new ControlRequest { Cmd = ControlEndpoint.JobRunCommand, Target = job }, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            }
            catch (ControlClientException ex)
            {
                Console.Error.WriteLine((ex.Connect ? "Error: " : "error: ") + ex.Message);
                return 1;
            }

            if (opened.connection is not Stream channel)
            {
                Console.Error.WriteLine("error: " + (opened.response.Error ?? "the app refused the stream"));
                return 1;
            }

            using (channel)
            {
                connection = channel;
                if (ReadFrame() is not { Kind: OverlayJobKinds.Context, Context: ControlOverlayJob launch })
                {
                    Console.Error.WriteLine("error: the app sent no launch context");
                    return 1;
                }

                return RunProgram(launch);
            }
        }

        private static Stream? connection;

        private static readonly object writing = new object();

        /// <summary>Set from the console handler's and the listener's threads as well as the main one.</summary>
        private static volatile bool canceled;

        private static Process? program;

        /// <summary>agterm's defaultGrace: how long a cancelled program gets to exit before it is killed.</summary>
        private static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

        private static readonly object stopping = new object();

        /// <summary>agterm's killAt: set by the first cancel.</summary>
        private static DateTime? killAt;

        /// <summary>agterm's process group: a job this helper joins before the start, so the program and all it starts are in it.</summary>
        private static IntPtr job;

        /// <summary>Set once the end is reported, which the console's close event waits for.</summary>
        private static readonly ManualResetEventSlim finished = new ManualResetEventSlim(false);

        private static int RunProgram(ControlOverlayJob launch)
        {
            // agterm's helper: a program that cannot start, its directory gone included, is launch-failed and exits 127.
            if (launch.Cwd is string cwd && !Directory.Exists(cwd))
            {
                return LaunchFailed("the overlay's directory is gone: " + cwd);
            }

            // The context's command line already holds its shell (cmd.exe, or sh in WSL), as agterm's helper runs /bin/sh: the
            // program is its first word and the rest goes to it untouched.
            string line = launch.Command.TrimStart();
            int split = line.StartsWith('"') ? line.IndexOf('"', 1) + 1 : line.IndexOf(' ');
            ProcessStartInfo start = new ProcessStartInfo(split <= 0 ? line : line[..split].Trim('"'), split <= 0 ? string.Empty : line[split..].TrimStart())
            {
                UseShellExecute = false,
                WorkingDirectory = launch.Cwd ?? Environment.CurrentDirectory,
            };
            foreach (KeyValuePair<string, string?> variable in launch.Environment ?? new Dictionary<string, string?>())
            {
                start.Environment[variable.Key] = variable.Value;
            }

            // As a shell running a foreground command: Ctrl+C and Ctrl+Break are the program's, which gets them from the shared
            // console itself, and only losing the terminal (the ssh that showed it closed) cancels it, as agterm's hangup does.
            SetConsoleCtrlHandler(OnConsoleEvent, true);
            job = CreateJobObject(IntPtr.Zero, null);
            bool joined = job != IntPtr.Zero && AssignProcessToJobObject(job, Process.GetCurrentProcess().Handle);
            try
            {
                program = Process.Start(start);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                return LaunchFailed(ex.Message);
            }

            if (program is null)
            {
                return LaunchFailed("the program did not start");
            }

            if (!joined && job != IntPtr.Zero)
            {
                AssignProcessToJobObject(job, program.Handle);
            }

            using (program)
            {
                if (canceled)
                {
                    Cancel();
                }

                Report(new ControlOverlayJobFrame { Kind = OverlayJobKinds.Started });
                Thread listener = new Thread(Listen) { IsBackground = true, Name = "agshellctl.run-job.listen" };
                listener.Start();
                Supervise(program);
                // Closing the terminal ends the program with the same event the helper's handler gets, on another thread:
                // a program killed by it waits a moment for the handler, so the job reads canceled and not that exit code.
                if (!canceled && program.ExitCode == ConsoleClosedExit)
                {
                    SpinWait.SpinUntil(() => canceled, TimeSpan.FromSeconds(1));
                }

                Report(canceled ? new ControlOverlayJobFrame { Kind = OverlayJobKinds.Canceled } : new ControlOverlayJobFrame { Kind = OverlayJobKinds.Exited, Code = program.ExitCode });
                finished.Set();
                return program.ExitCode;
            }
        }

        /// <summary>agterm's listen: the app's frames until the connection ends; a cancel stops the program.</summary>
        private static void Listen()
        {
            while (ReadFrame() is ControlOverlayJobFrame frame)
            {
                if (frame.Kind == OverlayJobKinds.Cancel)
                {
                    Cancel();
                }
            }
        }

        /// <summary>
        /// agterm's supervise: waits for the program, and after a cancel also for the rest of its job, killing what outlives
        /// the grace, so nothing is left running behind a canceled report.
        /// </summary>
        private static void Supervise(Process started)
        {
            bool killed = false;
            while (true)
            {
                bool exited = started.WaitForExit(20);
                DateTime? deadline;
                lock (stopping)
                {
                    deadline = killAt;
                }

                if (exited && (deadline is null || Others().Count == 0))
                {
                    return;
                }

                if (deadline is DateTime at && !killed && DateTime.UtcNow >= at)
                {
                    Kill(started);
                    killed = true;
                }
            }
        }

        /// <summary>
        /// agterm's cancel: the program is asked first and killed once the grace has passed. Its SIGTERM to the group is the
        /// console's Ctrl+C here, which this helper ignores; safe before the start, when it is sent as soon as the program runs.
        /// </summary>
        private static void Cancel()
        {
            canceled = true;
            lock (stopping)
            {
                killAt ??= DateTime.UtcNow + Grace;
            }

            if (program is not null)
            {
                GenerateConsoleCtrlEvent(CtrlCEvent, 0);
            }
        }

        /// <summary>agterm's SIGKILL to the group: every process of the job but this helper; the program's tree without a job.</summary>
        private static void Kill(Process started)
        {
            if (job == IntPtr.Zero)
            {
                try
                {
                    started.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return;
            }

            foreach (int pid in Others())
            {
                IntPtr process = OpenProcess(ProcessTerminate, false, pid);
                if (process != IntPtr.Zero)
                {
                    TerminateProcess(process, uint.MaxValue);
                    CloseHandle(process);
                }
            }
        }

        /// <summary>The job's processes other than this helper; none without a job.</summary>
        private static List<int> Others()
        {
            List<int> others = new List<int>();
            if (job == IntPtr.Zero)
            {
                return others;
            }

            const int capacity = 4096;
            int size = 8 + IntPtr.Size * capacity;
            IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
            try
            {
                if (QueryInformationJobObject(job, JobObjectBasicProcessIdList, buffer, size, IntPtr.Zero))
                {
                    int count = System.Runtime.InteropServices.Marshal.ReadInt32(buffer, 4);
                    for (int i = 0; i < count; i++)
                    {
                        int pid = (int)System.Runtime.InteropServices.Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size);
                        if (pid != Environment.ProcessId)
                        {
                            others.Add(pid);
                        }
                    }
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            }

            return others;
        }

        private static int LaunchFailed(string why)
        {
            Console.Error.WriteLine("error: " + why);
            Report(new ControlOverlayJobFrame { Kind = OverlayJobKinds.LaunchFailed, Reason = why });
            finished.Set();
            return 127;
        }

        /// <summary>A report the app never receives changes nothing here.</summary>
        private static void Report(ControlOverlayJobFrame frame)
        {
            lock (writing)
            {
                try
                {
                    connection!.Write(Encoding.UTF8.GetBytes(ControlJson.Serialize(frame) + "\n"));
                    connection.Flush();
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                {
                }
            }
        }

        /// <summary>One byte at a time, as agterm's, so nothing behind a line is taken before its reader asks; null at the end.</summary>
        private static ControlOverlayJobFrame? ReadFrame()
        {
            using MemoryStream line = new MemoryStream();
            byte[] one = new byte[1];
            try
            {
                while (true)
                {
                    if (connection!.Read(one) == 0)
                    {
                        return null;
                    }

                    if (one[0] == '\n')
                    {
                        return ControlJson.DeserializeJobFrame(Encoding.UTF8.GetString(line.ToArray()));
                    }

                    line.WriteByte(one[0]);
                    if (line.Length > PageServe.MaxFrameBytes)
                    {
                        return null;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is System.Text.Json.JsonException)
            {
                return null;
            }
        }

        private const int CtrlCEvent = 0;

        private const int CtrlBreakEvent = 1;

        private const int CtrlCloseEvent = 2;

        /// <summary>STATUS_CONTROL_C_EXIT: what a console program ends with when its console closes (or on Ctrl+C).</summary>
        private const int ConsoleClosedExit = unchecked((int)0xC000013A);

        private delegate bool ConsoleEventHandler(int kind);

        private static readonly ConsoleEventHandler OnConsoleEvent = kind =>
        {
            // Windows ends the process once this returns from a close, and gives it 5 seconds: the grace and the report fit.
            if (kind == CtrlCloseEvent)
            {
                Cancel();
                finished.Wait(TimeSpan.FromSeconds(4.5));
            }

            return kind is CtrlCEvent or CtrlBreakEvent or CtrlCloseEvent;
        };

        private const int JobObjectBasicProcessIdList = 3;

        private const int ProcessTerminate = 0x0001;

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool SetConsoleCtrlHandler(ConsoleEventHandler handler, bool add);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GenerateConsoleCtrlEvent(int ctrlEvent, int processGroupId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, int length, IntPtr returnLength);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(int access, bool inherit, int processId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
