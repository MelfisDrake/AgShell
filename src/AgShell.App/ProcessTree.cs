using AgShell.Protocol;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AgShell.Core;

namespace AgShell.App
{
    /// <summary>What a shell is running in the foreground, read from the process tree, for re-run restore.</summary>
    internal static class ProcessTree
    {
        /// <summary>One process the snapshot saw: its id, its parent and its executable name.</summary>
        private readonly record struct Entry(int Id, int Parent, string Name);

        /// <summary>agterm's ProcessRecord: a process of a job, by id and start time, so a recycled id is not taken for it.</summary>
        internal readonly record struct JobProcess(int Id, long Started);

        /// <summary>
        /// The program running under a Windows shell: the newest child that is not console plumbing, looking through
        /// nested shells. Returns its executable name and full command line, or null when only shells are there.
        /// </summary>
        public static (string Executable, string CommandLine)? WindowsForeground(int shellId)
        {
            return Foreground(shellId, Snapshot()) is (Entry found, string commandLine) ? (found.Name, commandLine) : null;
        }

        /// <summary>
        /// agterm's replay observation for session.restart: the foreground program as <see cref="WindowsForeground"/> finds
        /// it, with the directory it runs in (null when that cannot be read or is not a directory), or with only shells there
        /// the innermost shell, as the tree reports it.
        /// </summary>
        public static RestartReplay.Observation WindowsReplay(int shellId)
        {
            if (TrySnapshot() is not List<Entry> processes)
            {
                return new RestartReplay.Observation(null, null);
            }

            if (Foreground(shellId, processes) is not (Entry found, string commandLine))
            {
                return new RestartReplay.Observation(null, TreeForeground(shellId, processes.Select(entry => (entry.Id, entry.Parent, entry.Name)).ToList()).Shell);
            }

            string? directory = WorkingDirectoryOf(found.Id);
            return new RestartReplay.Observation((found.Name, commandLine, directory is not null && System.IO.Directory.Exists(directory) ? directory : null), null);
        }

        /// <summary>
        /// agterm's foregroundJob for a restart: what a closed terminal would end, the foreground program and the processes
        /// under it, each with its start time so a recycled id is not taken for it. Empty when only shells run; null when the
        /// process table cannot be read.
        /// </summary>
        public static List<JobProcess>? WindowsForegroundJob(int shellId)
        {
            List<Entry>? processes = TrySnapshot();
            if (processes is null)
            {
                return null;
            }

            List<JobProcess> job = new List<JobProcess>();
            if (Foreground(shellId, processes) is (Entry found, _))
            {
                Queue<int> pending = new Queue<int>(new[] { found.Id });
                HashSet<int> seen = new HashSet<int>();
                while (pending.TryDequeue(out int id))
                {
                    if (seen.Add(id) && StartTicks(id) is long started)
                    {
                        job.Add(new JobProcess(id, started));
                        foreach (Entry child in processes.Where(entry => entry.Parent == id))
                        {
                            pending.Enqueue(child.Id);
                        }
                    }
                }
            }

            return job;
        }

        /// <summary>
        /// agterm's isRunning: whether any process of the job still runs. One that cannot be looked at counts as running, so
        /// a failed read is not taken for its end.
        /// </summary>
        public static bool JobRunning(IReadOnlyList<JobProcess> job)
        {
            foreach (JobProcess member in job)
            {
                IntPtr process = OpenProcess(Synchronize | QueryLimitedInformation, false, member.Id);
                if (process == IntPtr.Zero)
                {
                    if (Marshal.GetLastPInvokeError() != ErrorInvalidParameter)
                    {
                        return true;
                    }

                    continue;
                }

                try
                {
                    if (CreationTicks(process) == member.Started && WaitForSingleObject(process, 0) == WaitTimeout)
                    {
                        return true;
                    }
                }
                finally
                {
                    CloseHandle(process);
                }
            }

            return false;
        }

        /// <summary>agterm's forceEnd: kills what is left of the job, matched on id and start time.</summary>
        public static void EndJob(IReadOnlyList<JobProcess> job)
        {
            foreach (JobProcess member in job)
            {
                IntPtr process = OpenProcess(Terminate | QueryLimitedInformation, false, member.Id);
                if (process == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    if (CreationTicks(process) == member.Started)
                    {
                        TerminateProcess(process, 1);
                    }
                }
                finally
                {
                    CloseHandle(process);
                }
            }
        }

        /// <summary>The foreground process under a shell, with its command line when it can be read; null with only shells.</summary>
        private static (Entry Found, string? CommandLine)? Foreground(int shellId, List<Entry> processes)
        {
            int current = shellId;
            for (int depth = 0; depth < 6; depth++)
            {
                Entry? child = processes
                    .Where(entry => entry.Parent == current && !IsConsoleHost(entry.Name))
                    .OrderByDescending(entry => StartTime(entry.Id))
                    .Select(entry => (Entry?)entry)
                    .FirstOrDefault();
                if (child is not Entry found)
                {
                    return null;
                }

                // agterm's isIdleShell: a shell running a script or a -c line is the program; one with only flags is looked through.
                string? commandLine = CommandLineOf(found.Id);
                if (!CommandRestore.IsShell(found.Name) || (commandLine is not null && !IdleShell(found.Name, commandLine)))
                {
                    return (found, commandLine);
                }

                current = found.Id;
            }

            return null;
        }

        /// <summary>
        /// agterm's foreground read for the tree: under a Windows shell, the program running now as its argv, or with only
        /// shells there the innermost shell's name. One snapshot serves every pane of a tree.
        /// </summary>
        public static (List<string>? Argv, string? Shell) TreeForeground(int shellId, IReadOnlyList<(int Id, int Parent, string Name)> processes)
        {
            int current = shellId;
            string? shell = processes.Where(entry => entry.Id == shellId).Select(entry => CommandRestore.ProgramName(entry.Name)).FirstOrDefault();
            for (int depth = 0; depth < 6; depth++)
            {
                (int Id, int Parent, string Name)? child = processes
                    .Where(entry => entry.Parent == current && !IsConsoleHost(entry.Name))
                    .OrderByDescending(entry => StartTime(entry.Id))
                    .Select(entry => ((int, int, string)?)entry)
                    .FirstOrDefault();
                if (child is not (int id, int _, string name))
                {
                    return (null, shell);
                }

                string? line = CommandLineOf(id);
                if (!CommandRestore.IsShell(name) || (line is not null && !IdleShell(name, line)))
                {
                    // A process that cannot be read gives nothing, as agterm's: neither a program nor a shell is known.
                    return line is not null ? (Argv(line), null) : (null, null);
                }

                shell = CommandRestore.ProgramName(name);
                current = id;
            }

            return (null, shell);
        }

        /// <summary>The processes now, for <see cref="TreeForeground"/>.</summary>
        public static IReadOnlyList<(int Id, int Parent, string Name)> Processes()
        {
            return Snapshot().Select(entry => (entry.Id, entry.Parent, entry.Name)).ToList();
        }

        /// <summary>A command line split as the program itself sees it, by Windows' own rules.</summary>
        /// <summary>A Windows shell process with only flags after it, by its command line.</summary>
        private static bool IdleShell(string name, string commandLine)
        {
            List<string> argv = Argv(commandLine);
            if (argv.Count > 0)
            {
                argv[0] = name;
            }

            return CommandRestore.IsIdleShell(argv);
        }

        private static List<string> Argv(string commandLine)
        {
            IntPtr list = CommandLineToArgvW(commandLine, out int count);
            if (list == IntPtr.Zero)
            {
                return new List<string> { commandLine };
            }

            try
            {
                List<string> argv = new List<string>(count);
                for (int index = 0; index < count; index++)
                {
                    argv.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(list, index * IntPtr.Size)) ?? string.Empty);
                }

                return argv;
            }
            finally
            {
                LocalFree(list);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        /// <summary>
        /// The program running in a WSL pane. Linux processes are invisible from Windows, so the distribution lists the
        /// processes that carry this pane's AGSHELL_* variables, and the newest one under its shell that is not a shell wins.
        /// </summary>
        public static async Task<(string Executable, string CommandLine)?> WslForeground(string distribution, Guid sessionId, string pane)
        {
            return await WslPane(distribution, sessionId, pane).ConfigureAwait(false) is { } rows && WslProgram(rows) is { } program
                ? (program.Argv[0], CommandRestore.PosixJoin(program.Argv))
                : null;
        }

        /// <summary>
        /// agterm's replay observation and foreground job for session.restart in a WSL pane, from one listing: the program
        /// with its directory (null when unreadable or not a directory), or with no program the shell leading the terminal's
        /// foreground group, as agterm reads the foreground by tcgetpgrp; and the members of that group when the pane's shell
        /// is not in it. Null when the processes cannot be listed.
        /// </summary>
        public static async Task<(RestartReplay.Observation Replay, List<JobProcess> Job)?> WslRestart(string distribution, Guid sessionId, string pane)
        {
            if (await WslPane(distribution, sessionId, pane).ConfigureAwait(false) is not { } rows)
            {
                return null;
            }

            WslRow? program = WslProgram(rows);
            // agterm's ProcessSweep.foregroundJob: the pane's shell is the process whose parent is not of the pane.
            HashSet<int> ids = rows.Select(row => row.Id).ToHashSet();
            List<JobProcess> job = new List<JobProcess>();
            if (rows.Where(row => !ids.Contains(row.Parent)).OrderBy(row => row.Id).Select(row => (WslRow?)row).FirstOrDefault() is WslRow leader
                && leader.Foreground > 1 && leader.Foreground != leader.Group)
            {
                job = rows.Where(row => row.Group == leader.Foreground && row.Id != leader.Id).Select(row => new JobProcess(row.Id, row.Started)).ToList();
            }

            if (program is WslRow found)
            {
                return (new RestartReplay.Observation((found.Argv[0], CommandRestore.PosixJoin(found.Argv), found.Directory), null), job);
            }

            string? shell = rows.Where(row => row.Id == row.Foreground && CommandRestore.IsIdleShell(row.Argv)).Select(row => row.Argv[0]).FirstOrDefault()
                ?? rows.Where(row => IsWslLeader(row, ids)).Select(row => row.Argv[0]).FirstOrDefault();
            return (new RestartReplay.Observation(null, shell), job);
        }

        /// <summary>agterm's isDaemonLeader with isKnownShell: the pane's root process (its parent is not of the pane) is a shell.</summary>
        private static bool IsWslLeader(WslRow row, HashSet<int> ids)
        {
            return !ids.Contains(row.Parent) && row.Argv.Length > 0 && CommandRestore.IsShell(row.Argv[0]);
        }

        /// <summary>
        /// agterm's programEnded for a WSL pane, run inside the distribution: a second for the job to act on the hangup, then
        /// SIGKILL to what is left and half a second more. False when a member still runs or the distribution did not answer.
        /// </summary>
        public static async Task<bool> WslJobEnded(string distribution, IReadOnlyList<JobProcess> job)
        {
            if (job.Count == 0)
            {
                return true;
            }

            const string script = "alive() { left=''; for m in \"$@\"; do p=${m%%:*}; [ -r /proc/$p/stat ] || continue; "
                + "set -- $(sed 's/^.*) //' /proc/$p/stat 2>/dev/null); [ \"${20}\" = \"${m#*:}\" ] && left=\"$left $m\"; done; }; "
                + "wait_for() { i=0; while [ $i -lt $1 ]; do alive $job; [ -z \"$left\" ] && return 0; sleep 0.1; i=$((i+1)); done; alive $job; [ -z \"$left\" ]; }; "
                + "job=\"$*\"; if wait_for 10; then echo ended; exit; fi; "
                + "for m in $left; do kill -KILL ${m%%:*} 2>/dev/null; done; "
                + "if wait_for 5; then echo ended; else echo running; fi";
            ProcessStartInfo start = new ProcessStartInfo("wsl.exe") { CreateNoWindow = true };
            foreach (string argument in new[] { "-d", distribution, "-e", "sh", "-c", script, "agshell" }.Concat(job.Select(member => member.Id + ":" + member.Started)))
            {
                start.ArgumentList.Add(argument);
            }

            try
            {
                return await ProcessOutputCapture.RunAsync(start, TimeSpan.FromSeconds(6)).ConfigureAwait(false) is { TimedOut: false } captured
                    && captured.Stdout.Trim() == "ended";
            }
            catch (Exception ex) when (ex is Win32Exception || ex is OperationCanceledException || ex is InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>One process of a WSL pane: its group, its terminal's foreground group, start time (clock ticks) and directory.</summary>
        private readonly record struct WslRow(int Id, int Parent, int Group, int Foreground, long Started, string? Directory, string[] Argv);

        private static WslRow? WslProgram(List<WslRow> rows)
        {
            HashSet<int> ids = rows.Select(row => row.Id).ToHashSet();
            // Top-level programs: their parent is a shell of this pane, and they are no shell themselves. agterm's
            // isDaemonLeader: the pane's root, a known shell with whatever arguments (a -c line or the replay that starts the
            // login shell), is the shell holding the pane, never its program.
            HashSet<int> shells = rows.Where(row => CommandRestore.IsIdleShell(row.Argv) || IsWslLeader(row, ids)).Select(row => row.Id).ToHashSet();
            return rows
                .Where(row => !shells.Contains(row.Id) && (shells.Contains(row.Parent) || !ids.Contains(row.Parent)))
                .OrderByDescending(row => row.Id)
                .Select(row => (WslRow?)row)
                .FirstOrDefault();
        }

        private static async Task<List<WslRow>?> WslPane(string distribution, Guid sessionId, string pane)
        {
            const string script = "for d in /proc/[0-9]*; do "
                + "tr '\\0' '\\n' 2>/dev/null < \"$d/environ\" | grep -qix \"AGSHELL_SESSION_ID=$1\" || continue; "
                + "tr '\\0' '\\n' 2>/dev/null < \"$d/environ\" | grep -qx \"AGSHELL_PANE=$2\" || continue; "
                + "set -- \"$1\" \"$2\" $(sed 's/^.*) //' \"$d/stat\" 2>/dev/null); "
                + "c=$(readlink \"$d/cwd\" 2>/dev/null); [ -d \"$c\" ] || c=''; "
                + "printf '%s\\t%s\\t%s\\t%s\\t%s\\t%s\\t' \"${d#/proc/}\" \"$4\" \"$5\" \"$8\" \"${22}\" \"$c\"; "
                + "tr '\\0' '\\001' 2>/dev/null < \"$d/cmdline\"; echo; set -- \"$1\" \"$2\"; done";
            ProcessStartInfo start = new ProcessStartInfo("wsl.exe") { CreateNoWindow = true };
            foreach (string argument in new[] { "-d", distribution, "-e", "sh", "-c", script, "agshell", sessionId.Wire(), pane })
            {
                start.ArgumentList.Add(argument);
            }

            try
            {
                if (await ProcessOutputCapture.RunAsync(start, TimeSpan.FromSeconds(4)).ConfigureAwait(false) is not { TimedOut: false } captured)
                {
                    return null;
                }

                List<WslRow> rows = new List<WslRow>();
                foreach (string line in captured.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = line.Split('\t', 7);
                    if (parts.Length == 7 && int.TryParse(parts[0], out int id) && int.TryParse(parts[1], out int parent)
                        && int.TryParse(parts[2], out int group) && int.TryParse(parts[3], out int foreground) && long.TryParse(parts[4], out long started))
                    {
                        string[] argv = parts[6].TrimEnd('\u0001').Split('\u0001');
                        if (argv.Length > 0 && argv[0].Length > 0)
                        {
                            rows.Add(new WslRow(id, parent, group, foreground, started, parts[5].Length > 0 ? parts[5] : null, argv));
                        }
                    }
                }

                return rows;
            }
            catch (Exception ex) when (ex is Win32Exception || ex is OperationCanceledException || ex is InvalidOperationException)
            {
                return null;
            }
        }

        private static bool IsConsoleHost(string name)
        {
            return CommandRestore.ProgramName(name) is "conhost" or "OpenConsole" or "openconsole";
        }

        private static DateTime StartTime(int id)
        {
            try
            {
                using Process process = Process.GetProcessById(id);
                return process.StartTime;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is Win32Exception)
            {
                return DateTime.MinValue;
            }
        }

        private static List<Entry> Snapshot()
        {
            return TrySnapshot() ?? new List<Entry>();
        }

        private static List<Entry>? TrySnapshot()
        {
            List<Entry> entries = new List<Entry>();
            IntPtr snapshot = CreateToolhelp32Snapshot(0x2, 0);
            if (snapshot == new IntPtr(-1))
            {
                return null;
            }

            try
            {
                ProcessEntry entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
                for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                {
                    entries.Add(new Entry((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return entries;
        }

        /// <summary>The program a process of this user runs, or null when it cannot be read.</summary>
        public static string? ImagePath(int id)
        {
            IntPtr process = OpenProcess(QueryLimitedInformation, false, id);
            if (process == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                StringBuilder path = new StringBuilder(1024);
                int length = path.Capacity;
                return QueryFullProcessImageNameW(process, 0, path, ref length) ? path.ToString(0, length) : null;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        private static long? StartTicks(int id)
        {
            IntPtr process = OpenProcess(QueryLimitedInformation, false, id);
            if (process == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return CreationTicks(process);
            }
            finally
            {
                CloseHandle(process);
            }
        }

        private static long? CreationTicks(IntPtr process)
        {
            return GetProcessTimes(process, out long created, out _, out _, out _) ? created : null;
        }

        /// <summary>
        /// The current directory of a process of the same user, from its process parameters in the PEB, as Process Explorer
        /// reads it; the 32-bit PEB for a WOW64 process, whose own copy is the one it changes.
        /// </summary>
        internal static string? WorkingDirectoryOf(int id)
        {
            IntPtr process = OpenProcess(QueryLimitedInformation | VmRead, false, id);
            if (process == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                string? path;
                if (Environment.Is64BitProcess && IsWow64Process(process, out bool wow64) && wow64)
                {
                    path = NtQueryInformationProcess(process, 26, out IntPtr peb32, IntPtr.Size, out _) == 0
                        && ReadUInt32(process, peb32 + 0x10) is uint parameters
                        && ReadUInt16(process, (IntPtr)(parameters + 0x24)) is ushort length
                        && ReadUInt32(process, (IntPtr)(parameters + 0x28)) is uint buffer
                        ? ReadString(process, (IntPtr)buffer, length)
                        : null;
                }
                else
                {
                    ProcessBasicInformation basic = default;
                    path = NtQueryInformationProcess(process, 0, ref basic, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
                        && ReadPointer(process, basic.PebBaseAddress + 4 * IntPtr.Size) is IntPtr parameters
                        && ReadUInt16(process, parameters + 0x38) is ushort length
                        && ReadPointer(process, parameters + 0x40) is IntPtr buffer
                        ? ReadString(process, buffer, length)
                        : null;
                }

                return path is { Length: > 3 } && path.EndsWith('\\') ? path[..^1] : path;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        private static IntPtr? ReadPointer(IntPtr process, IntPtr address)
        {
            byte[]? bytes = ReadBytes(process, address, IntPtr.Size);
            return bytes is null ? null : IntPtr.Size == 8 ? (IntPtr)BitConverter.ToInt64(bytes) : (IntPtr)BitConverter.ToInt32(bytes);
        }

        private static uint? ReadUInt32(IntPtr process, IntPtr address)
        {
            byte[]? bytes = ReadBytes(process, address, 4);
            return bytes is null ? null : BitConverter.ToUInt32(bytes);
        }

        private static ushort? ReadUInt16(IntPtr process, IntPtr address)
        {
            byte[]? bytes = ReadBytes(process, address, 2);
            return bytes is null ? null : BitConverter.ToUInt16(bytes);
        }

        private static string? ReadString(IntPtr process, IntPtr address, ushort length)
        {
            byte[]? bytes = length == 0 ? null : ReadBytes(process, address, length);
            return bytes is null ? null : System.Text.Encoding.Unicode.GetString(bytes);
        }

        private static byte[]? ReadBytes(IntPtr process, IntPtr address, int count)
        {
            byte[] bytes = new byte[count];
            return ReadProcessMemory(process, address, bytes, count, out IntPtr read) && read == count ? bytes : null;
        }

        /// <summary>The command line of a process of the same user, read with NtQueryInformationProcess as Task Manager does.</summary>
        private static string? CommandLineOf(int id)
        {
            IntPtr process = OpenProcess(0x1000, false, id);
            if (process == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                int size = 4096;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    IntPtr buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        int status = NtQueryInformationProcess(process, 60, buffer, size, out int needed);
                        if (status == 0)
                        {
                            ushort length = (ushort)Marshal.ReadInt16(buffer);
                            IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                            return Marshal.PtrToStringUni(text, length / 2);
                        }

                        if (needed <= size)
                        {
                            return null;
                        }

                        size = needed;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }

                return null;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

        private const uint Terminate = 0x0001;
        private const uint VmRead = 0x0010;
        private const uint QueryLimitedInformation = 0x1000;
        private const uint Synchronize = 0x00100000;
        private const int ErrorInvalidParameter = 87;
        private const uint WaitTimeout = 0x102;

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public IntPtr ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public IntPtr BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll")]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll")]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll")]
        private static extern bool IsWow64Process(IntPtr process, out bool wow64);

        [DllImport("kernel32.dll")]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, ref ProcessBasicInformation buffer, int length, out int returnLength);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, out IntPtr buffer, int length, out int returnLength);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr buffer, int length, out int returnLength);
    }
}
