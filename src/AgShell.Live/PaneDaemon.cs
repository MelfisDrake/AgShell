using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AgShell.Protocol;
using AgShell.Pty;

namespace AgShell.Live
{
    /// <summary>
    /// One live pane's daemon, as agterm's zmx daemon: it owns the pane's ConPTY so the shell outlives the window, and any
    /// number of clients can show it at once: the window, and agshell on another machine through ssh and agshellctl live
    /// proxy. Every client attached gets the output; the size follows the leader, as agterm's lead: the client that
    /// attached last with a claim, or the first to join a pane nobody leads. Output also feeds the pane's terminal state, which a client coming back gets instead of the output
    /// again, as zmx's re-attach; with no client the daemon answers the shell's DA queries itself. It leaves with the shell.
    /// </summary>
    internal sealed class PaneDaemon
    {
        /// <summary>How long a daemon waits for its shell to be opened before it gives up.</summary>
        public static readonly TimeSpan DefaultOpenGrace = TimeSpan.FromSeconds(30);

        /// <summary>zmx's PTY_WRITE_BUF_MAX: typed input larger than this is refused rather than written.</summary>
        internal const int TypeMax = 256 * 1024;

        private readonly TimeSpan openGrace;

        private readonly string pipeName;
        private readonly object sync = new object();
        private readonly List<Client> clients = new List<Client>();
        private readonly HashSet<Client> viewers = new HashSet<Client>();
        private readonly Dictionary<Client, (int Columns, int Rows)> sizes = new Dictionary<Client, (int Columns, int Rows)>();
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly string build = ControlEndpoint.LiveBuildOf(AppContext.BaseDirectory);
        private readonly LiveLog log;
        private int connections;
        private PseudoConsoleSession? pty;
        private VtTerminal? terminal;
        private bool hasPtyOutput;
        private bool hasHadClient;
        private string id = string.Empty;
        private Client? leader;
        private DateTime? created;
        private ulong screenRevision;

        /// <summary>zmx's cwd: the shell's working directory in OSC 7's form, as the terminal last reported it.</summary>
        private string cwd = string.Empty;

        /// <summary>zmx's cwd_path: cwd decoded, empty when it names a directory on another machine.</summary>
        private string cwdPath = string.Empty;

        /// <summary>The terminal's directory as last reported, so an unchanged one is not taken again.</summary>
        private string? reportedPwd;

        public PaneDaemon(string pipeName, TimeSpan? openGrace = null, LiveLog? log = null)
        {
            this.pipeName = pipeName;
            this.log = log ?? LiveLog.ForDaemon(pipeName);
            this.openGrace = openGrace ?? DefaultOpenGrace;
        }

        public async Task RunAsync()
        {
            log.Info("daemon started session=" + pipeName + " pid=" + Environment.ProcessId);
            _ = Task.Run(async () =>
            {
                await Task.Delay(openGrace);
                lock (sync)
                {
                    if (pty is null)
                    {
                        log.Info("no shell opened in " + openGrace.TotalSeconds + "s session=" + pipeName);
                        stop.Cancel();
                    }
                }
            });

            while (!stop.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.WaitForConnectionAsync(stop.Token);
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync();
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(pipe));
            }

            log.Info("shutting down daemon session=" + pipeName);
            lock (sync)
            {
                pty?.Dispose();
                terminal?.Dispose();
                terminal = null;
            }

            // What was queued for the clients, the shell's end among it, gets a moment to reach them.
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            log.Info("daemon loop shutdown");
        }

        private async Task HandleClientAsync(NamedPipeServerStream pipe)
        {
            using (pipe)
            {
                Client client = new Client(new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true }, Interlocked.Increment(ref connections));
                lock (sync)
                {
                    clients.Add(client);
                    log.Info("client connected client=" + client.Number + " total=" + clients.Count);
                }

                Task writing = Task.Run(client.WriteLoopAsync);
                using StreamReader reader = new StreamReader(pipe, new UTF8Encoding(false));
                try
                {
                    while (await reader.ReadLineAsync() is string line)
                    {
                        LiveMessage? message = LiveJson.Parse(line);
                        if (message is not null && Handle(client, message))
                        {
                            client.Close();
                            await writing;
                            End();
                            break;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is JsonException)
                {
                    log.Debug("client read err=" + ex.GetType().Name + " client=" + client.Number);
                }

                client.Close();
                lock (sync)
                {
                    clients.Remove(client);
                    if (viewers.Contains(client))
                    {
                        Leave(client);
                    }

                    log.Info("client disconnected client=" + client.Number + " remaining=" + clients.Count);
                }
            }
        }

        /// <summary>Applies one request; returns true when it ends the shell and the daemon.</summary>
        private bool Handle(Client client, LiveMessage message)
        {
            switch (message.Op)
            {
                case "open":
                    Manage(client, message);
                    Open(client, message);
                    break;
                case "attach":
                    Manage(client, message);
                    if (!Join(client, message.Cols ?? 120, message.Rows ?? 30, takeLead: message.Follow != true))
                    {
                        Send(client, new LiveMessage { Op = "opened", Id = message.Id, Found = false, Pid = 0 });
                        Send(client, new LiveMessage { Op = "exited", Id = message.Id, Code = -1 });
                    }
                    else
                    {
                        Repaint(message.Cols, message.Rows);
                    }

                    break;
                case "detach":
                    lock (sync)
                    {
                        log.Info("client detach session=" + pipeName + " client=" + client.Number);
                        Leave(client);
                    }

                    break;
                case "write":
                    // zmx's handleInput: the leader's input goes to the shell. A managed client leads only by asking: what it
                    // sends while following is its terminal answering queries, or a person typing at a covered pane. Any
                    // other client takes the lead by typing.
                    string input = message.Data ?? string.Empty;
                    PseudoConsoleSession? target;
                    lock (sync)
                    {
                        target = pty;
                        if (leader != client)
                        {
                            if (client.Managed || !VtTerminal.IsUserInput(input))
                            {
                                target = null;
                            }
                            else
                            {
                                SetLeader(client);
                                ResizeToLeader();
                            }
                        }
                    }

                    // Written outside the lock: a shell that does not read its input must not hold up its output.
                    target?.Write(input);
                    break;
                case "inject":
                    // agterm's zmx handleOutput: output from outside the shell, an OSC 52 clipboard set, goes into the pane's
                    // terminal and to every terminal showing it, the shell not; the program then redraws, as after SIGWINCH.
                    bool running;
                    lock (sync)
                    {
                        running = pty is not null;
                        terminal?.Write(message.Data ?? string.Empty);
                        SetPwd();
                        hasPtyOutput = true;
                        screenRevision++;
                        foreach (Client viewer in viewers)
                        {
                            Send(viewer, new LiveMessage { Op = "output", Id = id, Data = message.Data });
                        }

                        if (viewers.Count > 0 && LeaderSize() is { } size)
                        {
                            Repaint(size.Columns, size.Rows);
                        }
                    }

                    // As agterm's zmx print, the sender learns whether the pane was there; the app's own inject ignores it.
                    Send(client, new LiveMessage { Op = "injected", Id = message.Id, Found = running });
                    break;
                case "type":
                    // agterm's zmx handleType: automation typed into the pane without touching the lead, encoded for the
                    // keyboard mode the program asked for; the caller learns whether it was taken.
                    string? keys;
                    PseudoConsoleSession? shell;
                    lock (sync)
                    {
                        shell = pty;
                        keys = shell is null ? null : terminal?.EncodeTyped(message.Data ?? string.Empty);
                        if (shell is not null && keys is null)
                        {
                            log.Warn("failed to encode typed input");
                        }
                    }

                    // Written outside the lock, as a write is: a shell that does not read its input must not hold up its output.
                    bool accepted = shell is not null && keys is not null && keys.Length > 0 && Encoding.UTF8.GetByteCount(keys) <= TypeMax;
                    if (accepted)
                    {
                        shell!.Write(keys!);
                    }

                    Send(client, new LiveMessage { Op = "typed", Request = message.Request, Id = message.Id, Found = accepted });
                    break;
                case "resize":
                    // Only the leader sizes the pane: a second screen on it does not fight the first for rows. As zmx's
                    // handleResize, a client other than a managed one takes a pane nobody leads.
                    lock (sync)
                    {
                        log.Debug("resize rows=" + (message.Rows ?? 24) + " cols=" + (message.Cols ?? 80) + " client=" + client.Number);
                        sizes[client] = (message.Cols ?? 80, message.Rows ?? 24);
                        if (leader is null && !client.Managed)
                        {
                            SetLeader(client);
                        }

                        if (leader == client)
                        {
                            ResizeTerminal(message.Cols ?? 80, message.Rows ?? 24);
                            pty?.Resize(message.Cols ?? 80, message.Rows ?? 24);
                            // The others lay their covered view out at the leader's size, so what they read matches the program.
                            TellLead();
                        }
                    }

                    break;
                case "stop":
                case "shutdown":
                    log.Info("kill received session=" + pipeName);
                    // Another machine showing the pane learns it is gone, which closes its replica.
                    lock (sync)
                    {
                        foreach (Client viewer in viewers.Where(viewer => viewer != client))
                        {
                            Send(viewer, new LiveMessage { Op = "exited", Id = id, Code = -1 });
                        }
                    }

                    // zmx's "killed session NAME": the asking client is told the shell is ending, the one answer a kill
                    // is confirmed by; one sent without a request gets nothing, as before.
                    if (message.Request is int asked)
                    {
                        Send(client, new LiveMessage { Op = "killed", Request = asked, Id = id });
                    }

                    return true;
                case "list":
                    List<LivePaneInfo> list = new List<LivePaneInfo>();
                    lock (sync)
                    {
                        // zmx's handleInfo reads the terminal's directory first.
                        SetPwd();
                        if (pty is not null)
                        {
                            list.Add(new LivePaneInfo(id, pty.ProcessId, viewers.Count > 0, viewers.Count, created, build, cwd.Length > 0 ? cwd : null));
                        }
                    }

                    Send(client, new LiveMessage { Op = "list", Request = message.Request, Panes = list });
                    break;
                case "screen":
                    // agterm's zmx handleScreen: one observation of the daemon's terminal, which has the leader's layout,
                    // whether or not anyone shows it; whatever the program has written so far, not a finished frame.
                    lock (sync)
                    {
                        VtScreen? screen = pty is null ? null : terminal?.Screen(message.All == true);
                        if (pty is not null && screen is null)
                        {
                            log.Warn("failed to read screen");
                        }

                        Send(client, screen is null
                            ? new LiveMessage { Op = "screen", Request = message.Request, Id = message.Id, Found = false }
                            : new LiveMessage { Op = "screen", Request = message.Request, Id = message.Id, Found = true, Data = screen.Text, Revision = screenRevision, Cols = screen.Columns, Rows = screen.Rows, CursorX = screen.CursorX, CursorY = screen.CursorY, Alternate = screen.Alternate });
                    }

                    break;
                default:
                    log.Warn("ignoring unknown op=" + message.Op);
                    break;
            }

            return false;
        }

        /// <summary>
        /// Attaches the client to the shell that kept running, replaying what it printed, or starts it when the daemon has
        /// none yet: the first window to ask for the pane.
        /// </summary>
        private void Open(Client client, LiveMessage message)
        {
            // A window's pane joins without the claim, as agterm's: it leads only when nobody else does.
            if (Join(client, message.Cols ?? 120, message.Rows ?? 30, takeLead: message.Follow != true))
            {
                Repaint(message.Cols, message.Rows);
                return;
            }

            log.Info("creating session=" + pipeName + " pane=" + message.Id);
            lock (sync)
            {
                // zmx's create: the directory the shell starts in, the daemon's own when the window names none.
                SetCwd(string.IsNullOrEmpty(message.Cwd) ? Environment.CurrentDirectory : message.Cwd);
            }

            log.Info("checking pwd=" + cwd + " path=" + cwdPath);
            PseudoConsoleSession started = new PseudoConsoleSession(new PtyStartOptions
            {
                CommandLine = message.CommandLine ?? "cmd.exe",
                WorkingDirectory = message.Cwd,
                Columns = message.Cols ?? 120,
                Rows = message.Rows ?? 30,
                Environment = ShellEnvironment(message.Env),
            });
            started.Output += OnOutput;
            started.Exited += OnExited;
            lock (sync)
            {
                id = message.Id ?? string.Empty;
                pty = started;
                terminal = new VtTerminal(message.Cols ?? 120, message.Rows ?? 30);
                hasHadClient = true;
                created = DateTime.UtcNow;
                viewers.Add(client);
                sizes[client] = (message.Cols ?? 120, message.Rows ?? 30);
                leader = client;
            }

            try
            {
                started.Start();
            }
            catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                log.Error("shell start failed command=" + (message.CommandLine ?? "cmd.exe") + " err=" + ex.Message);
                Send(client, new LiveMessage { Op = "opened", Id = id, Found = false, Pid = 0 });
                Send(client, new LiveMessage { Op = "exited", Id = id, Code = -1 });
                client.Close();
                End();
                return;
            }

            log.Info("shell started session=" + pipeName + " pid=" + started.ProcessId);
            Send(client, new LiveMessage { Op = "opened", Id = id, Found = false, Pid = started.ProcessId });
            if (client.Managed)
            {
                Send(client, new LiveMessage { Op = "leads", Id = id, Lead = "leader" });
            }
        }

        /// <summary>zmx's handleManage: the client says it leads only by asking and wants to be told its lead.</summary>
        private void Manage(Client client, LiveMessage message)
        {
            if (message.Managed == true)
            {
                client.Managed = true;
            }
        }

        /// <summary>
        /// The shell's variables with the daemon's pipe, as zmx gives its shell ZMX_SESSION: agshellctl clipboard set prints
        /// through the daemon it names, and a shell no daemon runs has none.
        /// </summary>
        private Dictionary<string, string?> ShellEnvironment(Dictionary<string, string?>? given)
        {
            Dictionary<string, string?> variables = given is null ? new Dictionary<string, string?>() : new Dictionary<string, string?>(given);
            variables[ControlEndpoint.LivePipeVariable] = pipeName;
            return variables;
        }

        /// <summary>
        /// Joins the running shell and replays its terminal state, laid out at this client's size when it leads, as zmx's
        /// handleInit; false when the daemon has no shell yet.
        /// </summary>
        private bool Join(Client client, int columns, int rows, bool takeLead)
        {
            lock (sync)
            {
                // Under the lock new output waits, so the client gets the state first and nothing twice.
                if (pty is null)
                {
                    return false;
                }

                log.Debug("init resize rows=" + rows + " cols=" + columns + " client=" + client.Number);
                viewers.Add(client);
                sizes[client] = (columns, rows);
                Send(client, new LiveMessage { Op = "opened", Id = id, Found = true, Pid = pty.ProcessId });
                bool leads = takeLead || leader is null;
                if (leads)
                {
                    ResizeTerminal(columns, rows);
                }

                // Only a re-attach gets the state, not the first attach, so as not to interfere with the shell's start.
                if (hasPtyOutput && hasHadClient && terminal?.Snapshot() is string state)
                {
                    Send(client, new LiveMessage { Op = "output", Id = id, Data = state });
                }

                hasHadClient = true;

                // A pane coming back without the lead still leads when nobody else does, as agterm's unowned reattach.
                if (leads)
                {
                    SetLeader(client);
                }
                else
                {
                    TellLead(client);
                }

                return true;
            }
        }

        /// <summary>
        /// A client stops showing the pane. When it led, nobody leads, as zmx's closeClient: the others are told unowned and
        /// join again, and the pane keeps its size until one leads. Called under the lock.
        /// </summary>
        private void Leave(Client client)
        {
            viewers.Remove(client);
            sizes.Remove(client);
            if (leader == client)
            {
                SetLeader(null);
            }
        }

        /// <summary>
        /// A client that took the lead by typing gives the pane its size, as zmx's setLeader asks the client for it. Called
        /// under the lock.
        /// </summary>
        private void ResizeToLeader()
        {
            if (LeaderSize() is { } size)
            {
                ResizeTerminal(size.Columns, size.Rows);
                pty?.Resize(size.Columns, size.Rows);
            }
        }

        /// <summary>Gives the pane its leader and tells every client showing it whether it leads now. Called under the lock.</summary>
        private void SetLeader(Client? next)
        {
            if (next is null)
            {
                if (leader is not null)
                {
                    log.Info("unsetting leader session=" + pipeName + " client=" + leader.Number);
                }
            }
            else if (next != leader)
            {
                log.Info("setting new leader client=" + next.Number);
            }

            leader = next;
            TellLead();
        }

        /// <summary>
        /// Tells the managed clients their lead, with the leader's size for those who follow, as zmx's notifyRoles. Called
        /// under the lock.
        /// </summary>
        private void TellLead(Client? only = null)
        {
            (int Columns, int Rows)? size = LeaderSize();
            foreach (Client viewer in viewers.Where(viewer => viewer.Managed && (only is null || viewer == only)))
            {
                string lead = leader is null ? "unowned" : leader == viewer ? "leader" : "follower";
                Send(viewer, new LiveMessage { Op = "leads", Id = id, Lead = lead, Cols = lead == "follower" ? size?.Columns : null, Rows = lead == "follower" ? size?.Rows : null });
            }
        }

        private (int Columns, int Rows)? LeaderSize()
        {
            return leader is Client current && sizes.TryGetValue(current, out (int Columns, int Rows) found) ? found : null;
        }

        /// <summary>
        /// zmx's setPwd: the terminal's directory becomes the daemon's when it changed. Besides OSC 7 the terminal takes
        /// OSC 9;9, which the shells here report a plain path with, PowerShell's in quotes; the quotes go, as the window's.
        /// Called under the lock.
        /// </summary>
        private void SetPwd()
        {
            if (terminal?.Pwd() is string pwd && pwd != reportedPwd)
            {
                reportedPwd = pwd;
                SetCwd(pwd.Length >= 2 && pwd[0] == '"' && pwd[^1] == '"' ? pwd[1..^1] : pwd);
            }
        }

        /// <summary>
        /// zmx's setCwd: kept in OSC 7's form, a plain path made into one, with the decoded path when it is on this machine.
        /// Called under the lock.
        /// </summary>
        private void SetCwd(string value)
        {
            string hostname = Environment.MachineName;
            if (Osc7Cwd.Parse(value, hostname) is not var (path, isLocal))
            {
                log.Warn("ignoring unusable cwd=" + value);
                return;
            }

            cwd = Osc7Cwd.IsAbsolute(value) ? Osc7Cwd.ToUri(value, hostname) : value;
            cwdPath = isLocal ? path : string.Empty;
            log.Info("set cwd=" + cwd + " path=" + cwdPath);
        }

        /// <summary>The terminal takes a new size, which agterm's zmx counts as a change of the screen. Called under the lock.</summary>
        private void ResizeTerminal(int columns, int rows)
        {
            terminal?.Resize(columns, rows);
            screenRevision++;
        }

        /// <summary>
        /// A size change is ConPTY's only way to have the program redraw, as zmx's SIGWINCH on re-attach. The step is up and
        /// back: a step down with the cursor on the bottom row scrolls ConPTY's screen a row up and prints nothing, and the
        /// pane's terminal would no longer match it.
        /// </summary>
        private void Repaint(int? columns, int? rows)
        {
            int width = columns ?? 120;
            int height = rows ?? 30;
            pty?.Resize(width, height + 1);
            pty?.Resize(width, height);
        }

        private void OnOutput(string text)
        {
            string replies = string.Empty;
            lock (sync)
            {
                terminal?.Write(text);
                SetPwd();
                hasPtyOutput = true;
                if (viewers.Count == 0)
                {
                    replies = DeviceAttributes.Respond(text);
                }

                screenRevision++;
                foreach (Client viewer in viewers)
                {
                    Send(viewer, new LiveMessage { Op = "output", Id = id, Data = text });
                }
            }

            if (replies.Length > 0)
            {
                try
                {
                    pty?.Write(replies);
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
                {
                    log.Warn("device attributes reply failed err=" + ex.Message);
                }
            }
        }

        private void OnExited(int code)
        {
            log.Info("shell exited session=" + pipeName + " exit_code=" + code);
            List<Client> told;
            lock (sync)
            {
                told = viewers.ToList();
            }

            foreach (Client viewer in told)
            {
                Send(viewer, new LiveMessage { Op = "exited", Id = id, Code = code });
                viewer.Close();
            }

            End();
        }

        /// <summary>The daemon takes no one new from now on, and goes once what it queued has reached its clients.</summary>
        private void End()
        {
            stop.Cancel();
        }

        /// <summary>Queues a line for one client; its own writer sends it, so nothing here waits on a pipe under the lock.</summary>
        private static void Send(Client client, LiveMessage message)
        {
            client.Outgoing.Writer.TryWrite(LiveJson.Serialize(message));
        }

        private sealed class Client
        {
            private readonly StreamWriter writer;

            public Client(StreamWriter writer, int number)
            {
                this.writer = writer;
                Number = number;
            }

            /// <summary>Which connection this is, in the log, where zmx names a client by its socket.</summary>
            public int Number { get; }

            /// <summary>zmx's managed: the client leads only by asking and is told its lead.</summary>
            public bool Managed { get; set; }

            /// <summary>What goes to the client, in order: a slow client only grows its own queue and never stalls the daemon.</summary>
            public System.Threading.Channels.Channel<string> Outgoing { get; } = System.Threading.Channels.Channel.CreateUnbounded<string>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

            public async Task WriteLoopAsync()
            {
                try
                {
                    await foreach (string line in Outgoing.Reader.ReadAllAsync())
                    {
                        await writer.WriteLineAsync(line);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                {
                }
            }

            public void Close()
            {
                Outgoing.Writer.TryComplete();
            }
        }
    }
}
