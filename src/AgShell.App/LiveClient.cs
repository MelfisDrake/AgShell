using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AgShell.Core;
using AgShell.Protocol;
using AgShell.Pty;

namespace AgShell.App
{
    /// <summary>A shell the window drives: one of its own, or one a live daemon keeps.</summary>
    internal interface IPaneProcess : IDisposable
    {
        event Action<string>? Output;

        event Action<int>? Exited;

        int ProcessId { get; }

        void Write(string text);

        void Resize(int columns, int rows);

        /// <summary>Lets go without ending the shell; a window-owned shell has nothing to let go of and ends.</summary>
        void Detach();
    }

    internal sealed class LocalPane : IPaneProcess
    {
        private readonly PseudoConsoleSession pty;

        public LocalPane(PtyStartOptions options)
        {
            pty = new PseudoConsoleSession(options);
            pty.Output += text => Output?.Invoke(text);
            pty.Exited += code => Exited?.Invoke(code);
        }

        public event Action<string>? Output;

        public event Action<int>? Exited;

        public int ProcessId => pty.ProcessId;

        public void Start() => pty.Start();

        public void Write(string text) => pty.Write(text);

        public void Resize(int columns, int rows) => pty.Resize(columns, rows);

        public void Detach() => pty.Dispose();

        public void Dispose() => pty.Dispose();
    }

    /// <summary>
    /// The window's end of a pane daemon's pipe, or of another machine's daemon through ssh. It opens panes on it and
    /// routes their output and exits back. A lost daemon ends every pane it held, as a crashed shell would.
    /// </summary>
    internal sealed class LiveClient : IDisposable
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, RemotePane> panes = new Dictionary<string, RemotePane>();
        private readonly Dictionary<int, TaskCompletionSource<List<LivePaneInfo>>> lists = new Dictionary<int, TaskCompletionSource<List<LivePaneInfo>>>();

        /// <summary>Requests waiting for their answer: screen reads and typed input.</summary>
        private readonly Dictionary<int, TaskCompletionSource<LiveMessage>> screens = new Dictionary<int, TaskCompletionSource<LiveMessage>>();
        /// <summary>zmx's zmx.log: what the clients of the live daemons saw.</summary>
        private static readonly LiveLog Log = LiveLog.ForClients(ControlEndpoint.StateDirectory());

        private Stream? pipe;
        private string target = string.Empty;
        private Process? transport;
        private StreamWriter? writer;
        private readonly System.Threading.Channels.Channel<string> outgoing = System.Threading.Channels.Channel.CreateUnbounded<string>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
        private int nextRequest;
        private Task? writing;

        /// <summary>The connection still stands: the daemon has not gone away and its pipe or ssh is open.</summary>
        public bool Alive => writer is not null;

        /// <summary>A pane's end came from its daemon: the shell ended there, or the daemon had no shell to join.</summary>
        public bool PaneEnded { get; private set; }

        /// <summary>The last line the relay program (ssh) printed on stderr, such as a refused login.</summary>
        public string? LastError { get; private set; }

        /// <summary>The relay program's exit status once it ended: 255 is ssh's own failure.</summary>
        public int? RelayExitCode
        {
            get
            {
                try
                {
                    return transport is Process relay && relay.WaitForExit(500) ? relay.ExitCode : null;
                }
                catch (InvalidOperationException)
                {
                    // The connection was disposed meanwhile, its process with it.
                    return null;
                }
            }
        }

        /// <summary>Raised on a background thread when a pane was opened: whether it kept running, and its shell's id.</summary>
        public event Action<string, bool>? Opened;

        /// <summary>Connects to a running daemon's pipe, trying for up to the given time; false when it is not there.</summary>
        public bool Connect(string pipeName, TimeSpan wait)
        {
            if (pipe is NamedPipeClientStream { IsConnected: true })
            {
                return true;
            }

            DateTime deadline = DateTime.UtcNow + wait;
            do
            {
                NamedPipeClientStream candidate = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    candidate.Connect(100);
                    PipeOwner.Verify(candidate);
                    this.target = pipeName;
                    Log.Info("client loop session=" + pipeName);
                    pipe = candidate;
                    writer = new StreamWriter(candidate, new UTF8Encoding(false)) { AutoFlush = true };
                    _ = Task.Run(ReadLoopAsync);
                    StreamWriter target = writer;
                    writing = Task.Run(() => WriteLoopAsync(target));
                    return true;
                }
                catch (Exception ex) when (ex is TimeoutException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    candidate.Dispose();
                    if (DateTime.UtcNow >= deadline)
                    {
                        Log.Info("connect failed (" + ex.GetType().Name + ") session=" + pipeName);
                    }
                }
            }
            while (DateTime.UtcNow < deadline);

            return false;
        }

        /// <summary>
        /// agterm's remote attach: reaches a live pane's daemon on another machine through a program that relays its pipe on
        /// stdin and stdout, ssh HOST agshellctl live proxy PANE. False when the program does not start.
        /// </summary>
        public bool ConnectThrough(string program, IEnumerable<string> arguments)
        {
            ProcessStartInfo start = new ProcessStartInfo(program)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false),
            };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            try
            {
                transport = Process.Start(start);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log.Warn("relay failed to start program=" + program + " err=" + ex.Message);
                return false;
            }

            if (transport is null)
            {
                return false;
            }

            this.target = program + " " + string.Join(' ', start.ArgumentList);
            Log.Info("client loop relay=" + target);

            pipe = transport.StandardOutput.BaseStream;
            writer = new StreamWriter(transport.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
            Process relay = transport;
            _ = Task.Run(async () =>
            {
                string? line;
                while ((line = await relay.StandardError.ReadLineAsync()) is not null)
                {
                    if (line.Trim().Length > 0)
                    {
                        LastError = RemoteHosts.Reason(line);
                    }
                }
            });
            _ = Task.Run(ReadLoopAsync);
            StreamWriter input = writer;
            writing = Task.Run(() => WriteLoopAsync(input));
            return true;
        }

        /// <summary>
        /// Joins a pane's shell and never starts one, taking its lead unless it follows. Another machine's pane is borrowed:
        /// closing it here leaves the shell running there.
        /// </summary>
        public RemotePane Attach(string id, int columns, int rows, bool follow = false, bool borrowed = true)
        {
            RemotePane pane = new RemotePane(this, id) { Borrowed = borrowed, Columns = columns, Rows = rows };
            lock (sync)
            {
                panes[id] = pane;
            }

            Send(new LiveMessage { Op = "attach", Id = id, Cols = columns, Rows = rows, Follow = follow ? true : null, Managed = true });
            return pane;
        }

        /// <summary>
        /// Opens a pane: attaches to the shell the daemon kept under this id, or has the daemon start it. It joins without the
        /// claim, as agterm's ZmxLeadAttachment(claim: false): a pane another machine leads comes back covered.
        /// </summary>
        public RemotePane Open(string id, PtyStartOptions options)
        {
            RemotePane pane = new RemotePane(this, id) { Columns = options.Columns, Rows = options.Rows };
            lock (sync)
            {
                panes[id] = pane;
            }

            Send(new LiveMessage
            {
                Op = "open",
                Id = id,
                CommandLine = options.CommandLine,
                Cwd = options.WorkingDirectory,
                Env = options.Environment?.ToDictionary(pair => pair.Key, pair => pair.Value),
                Cols = options.Columns,
                Rows = options.Rows,
                Follow = true,
                Managed = true,
            });
            return pane;
        }

        /// <summary>The daemon's shells; null when it did not answer in time, agterm's unreadable row.</summary>
        public async Task<List<LivePaneInfo>?> ListAsync()
        {
            TaskCompletionSource<List<LivePaneInfo>> reply = new TaskCompletionSource<List<LivePaneInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            int request;
            lock (sync)
            {
                request = ++nextRequest;
                lists[request] = reply;
            }

            Send(new LiveMessage { Op = "list", Request = request });
            Task finished = await Task.WhenAny(reply.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            return finished == reply.Task ? reply.Task.Result : null;
        }

        /// <summary>
        /// agterm's zmx screen: the daemon's own terminal, the visible screen or with <paramref name="all"/> the history too,
        /// with its grid and cursor; null when the daemon has no such shell, cannot read it or does not answer.
        /// </summary>
        public async Task<DaemonScreen?> ScreenAsync(string id, bool all)
        {
            TaskCompletionSource<LiveMessage> reply = new TaskCompletionSource<LiveMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            int request;
            lock (sync)
            {
                request = ++nextRequest;
                screens[request] = reply;
            }

            Send(new LiveMessage { Op = "screen", Request = request, Id = id, All = all });
            // A daemon from before the terminal read answers with its raw output and no revision: as agterm's zmx without the
            // query, that is a failed read, not a screen.
            if (await Task.WhenAny(reply.Task, Task.Delay(TimeSpan.FromSeconds(3))) != reply.Task || reply.Task.Result is not { Found: true, Data: string text, Revision: ulong revision } answer)
            {
                return null;
            }

            return new DaemonScreen(text, answer.Cols ?? 0, answer.Rows ?? 0, answer.CursorX ?? 0, answer.CursorY ?? 0, answer.Alternate == true, revision);
        }

        /// <summary>
        /// agterm's zmx type: <paramref name="text"/> typed into the pane without taking the lead, each CR a Return key for
        /// the program's keyboard mode; true when the daemon took it, not that the program read it. Never retried: a second
        /// attempt after an unclear failure types twice.
        /// </summary>
        public async Task<bool> TypeAsync(string id, string text)
        {
            TaskCompletionSource<LiveMessage> reply = new TaskCompletionSource<LiveMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            int request;
            lock (sync)
            {
                request = ++nextRequest;
                screens[request] = reply;
            }

            Send(new LiveMessage { Op = "type", Request = request, Id = id, Data = text });
            // A daemon from before the operation does not answer, as agterm's zmx without it fails the call.
            return await Task.WhenAny(reply.Task, Task.Delay(TimeSpan.FromSeconds(3))) == reply.Task && reply.Task.Result.Found == true;
        }

        /// <summary>
        /// agterm's killConfirmed: the daemon is told to end its shell, and only its exact answer that it did counts; null then,
        /// else why not, as agterm reports zmx's first line or "no output".
        /// </summary>
        public async Task<string?> StopConfirmedAsync(string id)
        {
            TaskCompletionSource<LiveMessage> reply = new TaskCompletionSource<LiveMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            int request;
            lock (sync)
            {
                request = ++nextRequest;
                screens[request] = reply;
            }

            Send(new LiveMessage { Op = "stop", Request = request, Id = id });
            Stopping?.Invoke(id);
            Flush();
            if (await Task.WhenAny(reply.Task, Task.Delay(TimeSpan.FromSeconds(3))) != reply.Task)
            {
                return "no output";
            }

            return reply.Task.Result is { Op: "killed" } killed && string.Equals(killed.Id, id, StringComparison.OrdinalIgnoreCase) ? null : "no output";
        }

        public void Stop(string id)
        {
            Send(new LiveMessage { Op = "stop", Id = id });
            Stopping?.Invoke(id);
        }

        /// <summary>Raised when this side asks a shell to end, before its daemon has gone.</summary>
        public event Action<string>? Stopping;

        /// <summary>Ends the daemon's shell and the daemon, for a launch that no longer wants live sessions.</summary>
        public void Shutdown()
        {
            Send(new LiveMessage { Op = "shutdown" });
        }

        /// <summary>Waits a moment for the queued lines to reach the daemon, for a client about to go.</summary>
        public void Flush()
        {
            outgoing.Writer.TryComplete();
            writing?.Wait(TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// Queues a line for the daemon. A writer of its own drains the queue, so no caller ever waits on the pipe while
        /// holding anything the reader needs: the daemon and this side writing to each other at once must not deadlock.
        /// </summary>
        internal void Send(LiveMessage message)
        {
            outgoing.Writer.TryWrite(LiveJson.Serialize(message));
        }

        private async Task WriteLoopAsync(StreamWriter target)
        {
            try
            {
                await foreach (string line in outgoing.Reader.ReadAllAsync())
                {
                    await target.WriteLineAsync(line);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// A connection to one pane's daemon closes once its pane is let go: what was queued for the daemon, a stop or a
        /// detach, goes out first.
        /// </summary>
        public bool ClosesWhenEmpty { get; init; }

        internal void Forget(RemotePane pane)
        {
            bool empty;
            lock (sync)
            {
                if (panes.TryGetValue(pane.Id, out RemotePane? current) && ReferenceEquals(current, pane))
                {
                    panes.Remove(pane.Id);
                }

                empty = panes.Count == 0;
            }

            if (empty && ClosesWhenEmpty)
            {
                outgoing.Writer.TryComplete();
                // ssh's stdin closes too, so live proxy on the far side sees the end and ssh exits.
                _ = (writing ?? Task.CompletedTask).ContinueWith(_ =>
                {
                    pipe?.Dispose();
                    try
                    {
                        transport?.StandardInput.Close();
                    }
                    catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is ObjectDisposedException)
                    {
                    }
                });
            }
        }

        /// <summary>The daemon could not be reached: report a failed attachment without claiming that its shell exited.</summary>
        public void Abandon(int code = -1)
        {
            Log.Warn("daemon not reached, giving up session=" + target);
            List<RemotePane> lost;
            lock (sync)
            {
                lost = panes.Values.ToList();
                panes.Clear();
            }

            outgoing.Writer.TryComplete();
            foreach (RemotePane pane in lost)
            {
                pane.RaiseExited(code);
            }
        }

        private async Task ReadLoopAsync()
        {
            using StreamReader reader = new StreamReader(pipe!, new UTF8Encoding(false));
            try
            {
                while (await reader.ReadLineAsync() is string line)
                {
                    // A line that is not the protocol (a login banner or a profile's output on the far side) is skipped.
                    LiveMessage? message;
                    try
                    {
                        message = LiveJson.Parse(line);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (message is not null)
                    {
                        Dispatch(message);
                    }
                }

                Log.Info("server closed connection session=" + target);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                Log.Info("connection reset or broken pipe (" + ex.GetType().Name + ") session=" + target);
            }

            List<RemotePane> lost;
            lock (sync)
            {
                lost = panes.Values.ToList();
                panes.Clear();
                writer = null;
            }

            foreach (RemotePane pane in lost)
            {
                pane.RaiseExited(-1);
            }
        }

        private void Dispatch(LiveMessage message)
        {
            RemotePane? pane;
            lock (sync)
            {
                pane = message.Id is string id && panes.TryGetValue(id, out RemotePane? found) ? found : null;
            }

            switch (message.Op)
            {
                case "opened" when pane is not null:
                    pane.ProcessId = message.Pid ?? 0;
                    Opened?.Invoke(pane.Id, message.Found == true);
                    break;
                case "output":
                    pane?.RaiseOutput(message.Data ?? string.Empty);
                    break;
                case "leads" when pane is not null:
                    pane.Lead = message.Lead;
                    pane.LeaderSize = message.Cols is int columns && message.Rows is int rows ? (columns, rows) : null;
                    pane.RaiseLeadChanged(message.Lead);
                    break;
                case "exited" when pane is not null:
                    PaneEnded = true;
                    Forget(pane);
                    pane.RaiseExited(message.Code ?? -1, processEnded: true);
                    break;
                case "list":
                    TaskCompletionSource<List<LivePaneInfo>>? reply = null;
                    lock (sync)
                    {
                        if (message.Request is int request && lists.Remove(request, out TaskCompletionSource<List<LivePaneInfo>>? waiting))
                        {
                            reply = waiting;
                        }
                    }

                    reply?.TrySetResult(message.Panes ?? new List<LivePaneInfo>());
                    break;
                case "screen":
                case "typed":
                case "killed":
                    TaskCompletionSource<LiveMessage>? screen = null;
                    lock (sync)
                    {
                        if (message.Request is int asked && screens.Remove(asked, out TaskCompletionSource<LiveMessage>? waiting))
                        {
                            screen = waiting;
                        }
                    }

                    screen?.TrySetResult(message);
                    break;
            }
        }

        public void Dispose()
        {
            pipe?.Dispose();
            if (transport is { HasExited: false })
            {
                transport.Kill();
            }

            transport?.Dispose();
        }
    }

    internal sealed class RemotePane : IPaneProcess
    {
        private readonly LiveClient client;

        public RemotePane(LiveClient client, string id)
        {
            this.client = client;
            Id = id;
        }

        public event Action<string>? Output;

        public event Action<int>? Exited;

        /// <summary>Raised on the reader thread when this side's lead of the pane changes.</summary>
        public event Action<string?>? LeadChanged;

        public string Id { get; }

        /// <summary>agterm's lead: leader (this side's size is the program's), follower or unowned; null until the daemon says.</summary>
        public string? Lead { get; set; }

        /// <summary>The leader's size while this side follows, which the covered view takes so it reads as the program draws.</summary>
        public (int Columns, int Rows)? LeaderSize { get; set; }

        /// <summary>The last size this side gave the pane, which a lead taken back applies.</summary>
        public int Columns { get; set; }

        public int Rows { get; set; }

        public int ProcessId { get; set; }

        public bool ProcessEnded { get; private set; }

        /// <summary>Attached from another machine's host: this side never ends its shell.</summary>
        public bool Borrowed { get; init; }

        public void Write(string text) => client.Send(new LiveMessage { Op = "write", Id = Id, Data = text });

        public void Resize(int columns, int rows)
        {
            Columns = columns;
            Rows = rows;
            client.Send(new LiveMessage { Op = "resize", Id = Id, Cols = columns, Rows = rows });
        }

        /// <summary>Puts a sequence in front of every terminal showing the pane, as if the program printed it.</summary>
        public void Inject(string sequence) => client.Send(new LiveMessage { Op = "inject", Id = Id, Data = sequence });

        /// <summary>The window goes away; the daemon keeps the shell running for the next one.</summary>
        public void Detach()
        {
            client.Send(new LiveMessage { Op = "detach", Id = Id });
            client.Forget(this);
        }

        /// <summary>The session is closed: the daemon ends the shell, unless it is another machine's, which keeps it.</summary>
        public void Dispose()
        {
            if (Borrowed)
            {
                Detach();
                return;
            }

            client.Stop(Id);
            client.Forget(this);
        }

        internal void RaiseOutput(string text) => Output?.Invoke(text);

        internal void RaiseExited(int code, bool processEnded = false)
        {
            ProcessEnded = processEnded;
            Exited?.Invoke(code);
        }

        internal void RaiseLeadChanged(string? lead) => LeadChanged?.Invoke(lead);
    }
}
