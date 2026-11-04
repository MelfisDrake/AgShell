using System.Text;
using AgShell.Core;
using AgShell.Pty;

namespace AgShell.App
{
    /// <summary>One terminal of a session.</summary>
    internal readonly record struct PaneKey(Guid SessionId, PaneRole Pane);

    /// <summary>
    /// Running shells keyed by session and pane. Use from the UI thread; output is buffered per shell and collected by <see cref="Flush"/>.
    /// </summary>
    internal sealed class SessionHost : IDisposable
    {
        private readonly Dictionary<PaneKey, RunningSession> shells = new Dictionary<PaneKey, RunningSession>();

        /// <summary>Raised on a background thread with the shell that exited; <see cref="KeyOf"/> tells where it sits now.</summary>
        public event Action<object, int>? Exited;

        /// <summary>Raised on a reader thread with a live shell whose lead moved to or from this window.</summary>
        public event Action<object, string?>? LeadChanged;

        /// <summary>Raised on a reader thread when a shell prints after all its output was taken.</summary>
        public event Action? OutputPending;

        public bool IsRunning(PaneKey key)
        {
            return shells.ContainsKey(key);
        }

        public void Start(PaneKey key, PtyStartOptions options)
        {
            LocalPane pane = new LocalPane(options);
            Add(key, pane);
            pane.Start();
        }

        /// <summary>Opens a main or split pane on its daemon, which attaches to the shell it kept or starts one.</summary>
        public void StartLive(PaneKey key, string paneId, PtyStartOptions options, LiveDaemons daemons)
        {
            daemons.Open(paneId, options, pane => Add(key, pane, live: true));
        }

        /// <summary>agterm's PaneReattach: the pane joins its daemon again and never creates one.</summary>
        public void RejoinLive(PaneKey key, string paneId, int columns, int rows, LiveDaemons daemons, bool follow)
        {
            daemons.Rejoin(paneId, columns, rows, follow, pane => Add(key, pane, live: true));
        }

        /// <summary>agterm's remote attach: a pane that is another machine's shell, joined through its daemon there.</summary>
        public void StartRemote(PaneKey key, RemotePane pane)
        {
            Add(key, pane);
        }

        private void Add(PaneKey key, IPaneProcess pane, bool live = false)
        {
            if (shells.ContainsKey(key))
            {
                pane.Dispose();
                throw new InvalidOperationException("Pane is already running: " + key);
            }

            RunningSession running = new RunningSession(pane) { Live = live };
            pane.Output += text =>
            {
                if (running.Append(text))
                {
                    OutputPending?.Invoke();
                }
            };
            pane.Exited += code => Exited?.Invoke(running, code);
            if (pane is RemotePane remote)
            {
                remote.LeadChanged += lead => LeadChanged?.Invoke(running, lead);
            }

            running.Key = key;
            shells.Add(key, running);
        }

        /// <summary>The pane a shell sits in now, or null when it was stopped or replaced.</summary>
        public PaneKey? KeyOf(object shell)
        {
            return shell is RunningSession running && shells.TryGetValue(running.Key, out RunningSession? current) && ReferenceEquals(current, running)
                ? running.Key
                : null;
        }

        public bool LostLivePane(object shell)
        {
            return shell is RunningSession { Live: true, Pane: RemotePane { ProcessEnded: false } };
        }

        /// <summary>When the pane last printed anything, or null before its first output.</summary>
        public DateTime? LastOutputAt(PaneKey key)
        {
            return shells.TryGetValue(key, out RunningSession? running) ? running.LastOutputAt : null;
        }

        /// <summary>The last characters the pane printed, escape sequences included.</summary>
        public string OutputTail(PaneKey key)
        {
            return shells.TryGetValue(key, out RunningSession? running) ? running.Tail : string.Empty;
        }

        /// <summary>The shell's process id, or null when the pane runs nothing.</summary>
        public int? ProcessId(PaneKey key)
        {
            return shells.TryGetValue(key, out RunningSession? running) ? running.Pane.ProcessId : null;
        }

        /// <summary>This window's lead of the live pane: leader, follower or unowned; null for a pane with none, such as a local one.</summary>
        public string? Lead(PaneKey key)
        {
            return shells.TryGetValue(key, out RunningSession? running) ? (running.Pane as RemotePane)?.Lead : null;
        }

        /// <summary>agterm's zmx session name: the id of this machine's daemon that keeps the pane's shell; null for any other pane.</summary>
        public string? LivePaneId(PaneKey key)
        {
            return shells.TryGetValue(key, out RunningSession? running) && running.Live && running.Pane is RemotePane remote ? remote.Id : null;
        }

        /// <summary>agterm's backedByZmx pane token: the daemon pane a live or remote pane attaches to; null for a local shell.</summary>
        public string? DaemonPaneId(PaneKey key)
        {
            return shells.TryGetValue(key, out RunningSession? running) && running.Pane is RemotePane remote ? remote.Id : null;
        }

        /// <summary>The leader's size for a pane this window follows; null otherwise.</summary>
        public (int Columns, int Rows)? LeaderSize(PaneKey key)
        {
            return shells.TryGetValue(key, out RunningSession? running) && running.Pane is RemotePane { Lead: "follower" } remote ? remote.LeaderSize : null;
        }

        /// <summary>Shows a sequence in every terminal of the live pane, this window's included; false for a local pane.</summary>
        public bool Inject(PaneKey key, string sequence)
        {
            if (!shells.TryGetValue(key, out RunningSession? running) || running.Pane is not RemotePane remote)
            {
                return false;
            }

            remote.Inject(sequence);
            return true;
        }

        public void Write(PaneKey key, string data)
        {
            if (shells.TryGetValue(key, out RunningSession? running))
            {
                running.Pane.Write(data);
            }
        }

        public void Resize(PaneKey key, int columns, int rows)
        {
            if (shells.TryGetValue(key, out RunningSession? running))
            {
                running.Pane.Resize(columns, rows);
            }
        }

        public void Stop(PaneKey key)
        {
            if (shells.Remove(key, out RunningSession? running))
            {
                running.Pane.Dispose();
            }
        }

        /// <summary>The pane lets go of its shell, which keeps running: a live one stays with its daemon.</summary>
        public void Detach(PaneKey key)
        {
            if (shells.Remove(key, out RunningSession? running))
            {
                running.Pane.Detach();
            }
        }

        public void StopSession(Guid sessionId)
        {
            foreach (PaneRole pane in Enum.GetValues<PaneRole>())
            {
                Stop(new PaneKey(sessionId, pane));
            }
        }

        /// <summary>Moves a running shell to another pane, for a split promoted to the main pane.</summary>
        public void Move(PaneKey from, PaneKey to)
        {
            Stop(to);
            if (shells.Remove(from, out RunningSession? running))
            {
                running.Key = to;
                shells.Add(to, running);
            }
        }

        /// <summary>Exchanges the shells of two panes.</summary>
        public void Swap(PaneKey first, PaneKey second)
        {
            shells.Remove(first, out RunningSession? a);
            shells.Remove(second, out RunningSession? b);
            // A live pane's daemon is named by the pane's id, which goes with its shell: nothing is renamed.
            if (b is not null)
            {
                b.Key = first;
                shells.Add(first, b);
            }

            if (a is not null)
            {
                a.Key = second;
                shells.Add(second, a);
            }
        }

        public void Flush(Action<PaneKey, string> post)
        {
            foreach (KeyValuePair<PaneKey, RunningSession> pair in shells)
            {
                string? text = pair.Value.Take();
                if (text is not null)
                {
                    post(pair.Key, text);
                }
            }
        }

        public void Dispose()
        {
            // Window-owned shells end with the window; live ones stay with their daemons.
            foreach (RunningSession running in shells.Values)
            {
                running.Pane.Detach();
            }

            shells.Clear();
        }

        private sealed class RunningSession
        {
            private readonly object sync = new object();
            private readonly StringBuilder pending = new StringBuilder();

            public RunningSession(IPaneProcess pane)
            {
                Pane = pane;
            }

            public IPaneProcess Pane { get; }

            /// <summary>The pane's shell is kept by a daemon of this machine, not reached through another's.</summary>
            public bool Live { get; init; }

            /// <summary>The pane the shell currently sits in; it changes when panes are swapped or promoted.</summary>
            public PaneKey Key { get; set; }

            public DateTime? LastOutputAt { get; private set; }

            public string Tail { get; private set; } = string.Empty;

            /// <summary>True when nothing was waiting before this text.</summary>
            public bool Append(string text)
            {
                lock (sync)
                {
                    bool first = pending.Length == 0;
                    pending.Append(text);
                    LastOutputAt = DateTime.UtcNow;
                    string tail = Tail + text;
                    Tail = tail.Length > 256 ? tail[^256..] : tail;
                    return first;
                }
            }

            public string? Take()
            {
                lock (sync)
                {
                    if (pending.Length == 0)
                    {
                        return null;
                    }

                    string text = pending.ToString();
                    pending.Clear();
                    return text;
                }
            }
        }
    }
}
