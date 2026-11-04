using System.Diagnostics;
using System.IO;
using System.Text;
using AgShell.Core;
using AgShell.Protocol;
using AgShell.Pty;

namespace AgShell.App
{
    /// <summary>
    /// agterm's HookScheduler, one for the app: runs hooks.conf lines against the events of every window, one process per
    /// hook at a time with an ordered queue of up to 256 events behind it, and only the process's exit frees the hook. A
    /// failure is announced once until that hook succeeds again or the file is read again.
    /// </summary>
    internal sealed class HookRunner
    {
        public const int QueueLimit = 256;

        /// <summary>ERROR_BROKEN_PIPE and ERROR_NO_DATA: a script that exits without reading stdin, agterm's EPIPE.</summary>
        private static readonly int[] PipeGone = { unchecked((int)0x8007006D), unchecked((int)0x800700E8) };

        private readonly Dictionary<HookLine, Record> records = new Dictionary<HookLine, Record>();
        private readonly Func<Dictionary<string, string?>> baseEnvironment;
        private List<HookLine> order = new List<HookLine>();
        private long nextRun = 1;

        public HookRunner(Func<Dictionary<string, string?>> baseEnvironment)
        {
            this.baseEnvironment = baseEnvironment;
        }

        /// <summary>agterm's onFailure, the one source of the failure notification: line and reason.</summary>
        public event Action<HookLine, string>? Failed;

        /// <summary>
        /// Replaces the definitions. An unchanged hook keeps its process, queue and counters and takes the new line; a removed
        /// one drops its queue, accepts nothing and stays until its process exits; one added back before that takes it up
        /// again. Every notification is allowed again.
        /// </summary>
        public void Apply(IReadOnlyList<HookLine> lines)
        {
            HashSet<HookLine> live = lines.ToHashSet();
            foreach ((HookLine line, Record record) in records.Where(pair => !live.Contains(pair.Key)).ToList())
            {
                record.Retired = true;
                record.Pending.Clear();
                if (record.Running is null)
                {
                    records.Remove(line);
                }
            }

            foreach (HookLine line in lines)
            {
                if (records.TryGetValue(line, out Record? record))
                {
                    record.Line = line;
                    record.Retired = false;
                }
                else
                {
                    records[line] = new Record(line);
                }
            }

            foreach (Record record in records.Values)
            {
                record.Reported = false;
            }

            order = lines.ToList();
        }

        /// <summary>One event to every live hook of its kind.</summary>
        public void Dispatch(ControlEvent controlEvent)
        {
            foreach (HookLine line in order)
            {
                if (!records.TryGetValue(line, out Record? record) || record.Retired || record.Line.Kind != controlEvent.Kind)
                {
                    continue;
                }

                if (record.Running is null)
                {
                    Start(record, controlEvent);
                    continue;
                }

                record.Pending.Enqueue(controlEvent);
                if (record.Pending.Count > QueueLimit)
                {
                    record.Pending.Dequeue();
                    record.Dropped++;
                }
            }
        }

        /// <summary>agterm's hook entries: the hooks in file order, then those removed from the file that still run.</summary>
        public List<ControlHookEntry> Entries()
        {
            IEnumerable<Record> live = order.Select(line => records.GetValueOrDefault(line)).OfType<Record>();
            IEnumerable<Record> retired = records.Values.Where(record => record.Retired && record.Running is not null)
                .OrderBy(record => record.Line.Kind, StringComparer.Ordinal).ThenBy(record => record.Line.Command, StringComparer.Ordinal);
            return live.Concat(retired).Select(Row).ToList();
        }

        private static ControlHookEntry Row(Record record)
        {
            return new ControlHookEntry
            {
                Kind = record.Line.Kind,
                Command = record.Line.Command,
                Line = record.Line.Number,
                RunningPid = record.Running?.Pid,
                ElapsedSeconds = record.Running is Run running ? (DateTime.UtcNow - running.StartedAt).TotalSeconds : null,
                Pending = record.Pending.Count,
                Dropped = record.Dropped,
                LastFailure = record.LastFailure,
                Retired = record.Retired ? true : null,
                Runs = record.Runs,
                Failures = record.Failures,
            };
        }

        /// <summary>A process that does not start is a failure, and the next event in the queue is tried at once.</summary>
        private void Start(Record record, ControlEvent controlEvent)
        {
            ControlEvent? next = controlEvent;
            while (next is ControlEvent current)
            {
                next = null;
                long run = nextRun++;
                try
                {
                    BackgroundCommand process = Launch(record.Line, current);
                    record.Running = new Run(process.Id, DateTime.UtcNow, run);
                    _ = SuperviseAsync(record, process, current, run);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is IOException || ex is UnauthorizedAccessException)
                {
                    record.Runs++;
                    Fail(record, "spawn failed: " + ex.Message);
                    if (record.Pending.Count > 0)
                    {
                        next = record.Pending.Dequeue();
                    }
                }
            }
        }

        /// <summary>
        /// agterm's launch: through the shell, cmd.exe here, with no window and its output discarded; the event goes in as
        /// one line of JSON on stdin, and its fields as variables.
        /// </summary>
        private BackgroundCommand Launch(HookLine line, ControlEvent controlEvent)
        {
            ProcessStartInfo start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "/d /s /c \"" + line.Command + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            foreach ((string name, string? value) in baseEnvironment())
            {
                start.Environment[name] = value;
            }

            start.Environment["AGSHELL_EVENT_KIND"] = controlEvent.Kind;
            start.Environment["AGSHELL_EVENT_STATUS"] = controlEvent.Payload.Status ?? string.Empty;
            start.Environment["AGSHELL_EVENT_HOST"] = controlEvent.Payload.Host ?? string.Empty;
            start.Environment["AGSHELL_SESSION_ID"] = controlEvent.Session ?? string.Empty;
            start.Environment["AGSHELL_WORKSPACE_ID"] = controlEvent.Workspace ?? string.Empty;
            start.Environment["AGSHELL_WINDOW_ID"] = controlEvent.Window ?? string.Empty;
            return BackgroundCommand.Start(start, pipeInput: true);
        }

        private async Task SuperviseAsync(Record record, BackgroundCommand process, ControlEvent controlEvent, long run)
        {
            using (process)
            {
                using CancellationTokenSource delivery = new CancellationTokenSource();
                Task writing = DeliverAsync(record, process, controlEvent, run, delivery.Token);
                int status = await process.WaitForExitAsync();
                await delivery.CancelAsync();
                await writing;
                Exited(record, status, run);
            }
        }

        private async Task DeliverAsync(Record record, BackgroundCommand process, ControlEvent controlEvent, long run, CancellationToken cancellationToken)
        {
            try
            {
                await process.WriteInputAsync(Encoding.UTF8.GetBytes(ControlJson.Serialize(controlEvent) + "\n"), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException ex)
            {
                if (!PipeGone.Contains(ex.HResult) && record.Running?.Id == run)
                {
                    record.Running = record.Running with { DeliveryFailed = true };
                    Fail(record, "delivery failed: " + ex.Message);
                }
            }
        }

        private void Exited(Record record, int status, long run)
        {
            if (record.Running is not Run running || running.Id != run)
            {
                return;
            }

            record.Running = null;
            record.Runs++;
            if (status != 0)
            {
                Fail(record, "exit " + status);
            }
            else if (!running.DeliveryFailed)
            {
                record.LastFailure = null;
                record.Reported = false;
            }

            if (record.Retired)
            {
                if (records.TryGetValue(record.Line, out Record? held) && held == record)
                {
                    records.Remove(record.Line);
                }

                return;
            }

            if (record.Pending.Count > 0)
            {
                Start(record, record.Pending.Dequeue());
            }
        }

        private void Fail(Record record, string message)
        {
            record.LastFailure = message;
            record.Failures++;
            if (record.Reported)
            {
                return;
            }

            record.Reported = true;
            Failed?.Invoke(record.Line, message);
        }

        private sealed record Run(int Pid, DateTime StartedAt, long Id)
        {
            public bool DeliveryFailed { get; init; }
        }

        private sealed class Record
        {
            public Record(HookLine line)
            {
                Line = line;
            }

            public HookLine Line { get; set; }

            public Run? Running { get; set; }

            public Queue<ControlEvent> Pending { get; } = new Queue<ControlEvent>();

            public int Dropped { get; set; }

            public string? LastFailure { get; set; }

            /// <summary>agterm's bannerShown.</summary>
            public bool Reported { get; set; }

            public bool Retired { get; set; }

            public int Runs { get; set; }

            public int Failures { get; set; }
        }
    }
}
