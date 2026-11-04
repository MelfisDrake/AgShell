using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using AgShell.Protocol;

namespace AgShell.App
{
    /// <summary>
    /// agterm's RemotePresentationClient: one ssh running agshellctl live present for a remote row, as long as the row is
    /// there. A stream that ends, sends something unreadable or stays silent past three missed pings is dropped, what it
    /// put on the row is taken back, and it is started again after the attach's backoff.
    /// </summary>
    internal sealed class RemotePresentationStream : IDisposable
    {
        private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

        private const int MaxFrameBytes = PageServe.MaxFrameBytes;

        /// <summary>A frame, or null. A layout it cannot read decodes as agterm's invalid one, so it never ends a stream.</summary>
        private static ControlPresentationFrame? Decode(string line)
        {
            try
            {
                return ControlJson.DeserializeFrame(line);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private readonly string destination;
        private readonly Guid remoteSessionId;
        private readonly Action<ControlPresentationFrame> received;
        private readonly Action<string> warn;
        private string? warnedReason;
        private readonly Action lost;
        private readonly DispatcherTimer timer;
        private Process? process;
        private int launches;
        private int failures;
        private DateTime lastFrame;
        private DateTime? retryAt;
        private bool running;
        private Channel<string>? outgoing;

        /// <summary>agterm's bridge input: a frame is a few hundred bytes, so this many unwritten means ssh stopped reading.</summary>
        private const int MaxPendingFrames = 256;

        /// <summary>agterm's reader stops past this many undelivered lines, so a busy window backs ssh up into its own pipe.</summary>
        private const int MaxUndeliveredLines = 64;

        /// <summary>agterm's generation and revision: fixed by the answer to this launch's hello; frames of another, or out of order, are dropped.</summary>
        private int? generation;
        private int revision;

        /// <summary>connecting, connected or failed, as agterm's presentation node reads.</summary>
        public string State
        {
            get => state;
            private set
            {
                if (state != value)
                {
                    state = value;
                    StateChanged?.Invoke();
                }
            }
        }

        private string state = "connecting";

        /// <summary>Raised on the window's thread when the stream connects or fails, for the row to show it.</summary>
        public event Action? StateChanged;

        public string? Error { get; private set; }

        /// <summary>The session's machine granted this stream the presenter role.</summary>
        public bool Presenter { get; private set; }

        /// <param name="warn">agterm's warn: a failure's reason, once until the stream connects or fails otherwise.</param>
        public RemotePresentationStream(string destination, Guid remoteSessionId, Dispatcher dispatcher, Action<ControlPresentationFrame> received, Action<string> warn, Action lost)
        {
            this.destination = destination;
            this.remoteSessionId = remoteSessionId;
            this.received = received;
            this.warn = warn;
            this.lost = lost;
            timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (sender, args) => Tick(), dispatcher);
        }

        public void Start()
        {
            running = true;
            timer.Start();
            Launch();
        }

        /// <summary>
        /// Ends the stream for good. Its stdin closes first, so the other side gives the presenter role back at once, as
        /// agterm's origin sees a closed stream; the ssh goes a moment later.
        /// </summary>
        public void Dispose()
        {
            running = false;
            timer.Stop();
            outgoing?.Writer.TryComplete();
            Process? leaving = process;
            process = null;
            if (leaving is not null)
            {
                _ = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ => Kill(leaving), TaskScheduler.Default);
            }
        }

        /// <summary>A pending retry is due now, with the backoff over: the machine woke or the network changed.</summary>
        public void RetryNow()
        {
            failures = 0;
            if (retryAt is not null)
            {
                retryAt = DateTime.UtcNow;
            }
        }

        /// <summary>Tells the session's machine what was done with a question it handed over; dropped with no stream.</summary>
        /// <summary>
        /// Queued and written off the window's thread: an ssh that stops reading must not stall the window. As agterm's send,
        /// a write that cannot go through ends the bridge, and its exit fails the stream.
        /// </summary>
        public void Send(ControlPresentationFrame frame)
        {
            // As agterm's client: the generation its hello was answered under, or 0 before; the revision is the origin's to count.
            if (outgoing is not null && !outgoing.Writer.TryWrite(ControlJson.Serialize(frame with { Gen = generation ?? 0, Rev = 0 }) + "\n"))
            {
                StopBridge(process);
            }
        }

        private static async Task WriteAsync(Process target, ChannelReader<string> lines, Action stalled)
        {
            try
            {
                await foreach (string line in lines.ReadAllAsync())
                {
                    await target.StandardInput.WriteAsync(line);
                    await target.StandardInput.FlushAsync();
                }

                target.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is ObjectDisposedException)
            {
                stalled();
            }
        }

        /// <summary>agterm's stop after a write that did not go through: the bridge ends, and its exit is the stream's failure.</summary>
        private static void StopBridge(Process? bridge)
        {
            if (bridge is null)
            {
                return;
            }

            Log.Notice("RemotePresentation", "presentation bridge stopped taking input; ending it");
            try
            {
                bridge.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
            }
        }

        private void Tick()
        {
            if (process is not null && DateTime.UtcNow - lastFrame > StaleAfter)
            {
                Fail("no frames for 30 seconds");
            }
            else if (process is null && retryAt is DateTime due && DateTime.UtcNow >= due)
            {
                Launch();
            }
        }

        private void Launch()
        {
            retryAt = null;
            lastFrame = DateTime.UtcNow;
            generation = null;
            revision = 0;
            State = "connecting";
            int launch = ++launches;
            ProcessStartInfo start = RemoteHosts.SshStart(destination, "agshellctl", "live", "present", remoteSessionId.Wire());
            try
            {
                process = Process.Start(start);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                process = null;
                Fail("could not run ssh: " + ex.Message);
                return;
            }

            if (process is null)
            {
                Fail("could not run ssh");
                return;
            }

            Process reading = process;
            Dispatcher dispatcher = timer.Dispatcher;
            outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxPendingFrames) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
            // agterm's hello: the version, every kind this machine speaks, and presenter mode asked for.
            Send(new ControlPresentationFrame { Kind = ControlPresentationKinds.Hello, Version = ControlPresentationKinds.Version, Kinds = ControlPresentationKinds.SupportedKinds.ToList(), PresentationMode = "presenter" });
            _ = Task.Run(() => WriteAsync(reading, outgoing.Reader, () => StopBridge(reading)));
            // As agterm's bridge: what ssh says on stderr goes to the log only; the stream's failure is how it exited.
            _ = Task.Run(async () =>
            {
                string? line;
                while ((line = await reading.StandardError.ReadLineAsync()) is not null)
                {
                    if (line.Trim().Length > 0)
                    {
                        Log.Notice("RemotePresentation", "presentation bridge: " + line);
                    }
                }
            });
            _ = Task.Run(async () =>
            {
                // A line past agterm's 256 KB frame ends the read undelivered, which ssh sees as a closed pipe.
                ControlLineReader input = new ControlLineReader(reading.StandardOutput.BaseStream, MaxFrameBytes);
                using SemaphoreSlim undelivered = new SemaphoreSlim(MaxUndeliveredLines);
                try
                {
                    while (await input.ReadAsync(CancellationToken.None) is string line)
                    {
                        await undelivered.WaitAsync();
                        _ = dispatcher.BeginInvoke(() =>
                        {
                            Receive(line, launch);
                            undelivered.Release();
                        });
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
                {
                }

                string reason = await Task.Run(() => ExitOf(reading));
                _ = dispatcher.BeginInvoke(() => Closed(launch, reason));
            });
        }

        private void Receive(string line, int launch)
        {
            if (!running || launch != launches || process is null)
            {
                return;
            }

            if (Decode(line) is not ControlPresentationFrame frame)
            {
                Fail("bad frame");
                return;
            }

            lastFrame = DateTime.UtcNow;
            if (!Accept(frame))
            {
                return;
            }

            if (frame.Kind == ControlPresentationKinds.Snapshot)
            {
                failures = 0;
                warnedReason = null;
                State = "connected";
                Error = null;
            }
            else if (frame.Kind == ControlPresentationKinds.Hello && frame.PresentationMode == "presenter")
            {
                // agterm's client asks for the role once, when the answer offers it.
                Send(new ControlPresentationFrame { Kind = ControlPresentationKinds.PresenterAcquire });
            }
            else if (frame.Kind is ControlPresentationKinds.PresenterGranted or ControlPresentationKinds.PresenterRefused)
            {
                Presenter = frame.Kind == ControlPresentationKinds.PresenterGranted;
            }
            else if (frame.Kind == ControlPresentationKinds.Ping)
            {
                Send(new ControlPresentationFrame { Kind = ControlPresentationKinds.Ack });
            }

            received(frame);
        }

        /// <summary>
        /// agterm's accept: the first hello fixes the generation; everything else has to match it and advance the revision,
        /// which drops what an earlier connection left in flight. Nothing before the hello counts.
        /// </summary>
        private bool Accept(ControlPresentationFrame frame)
        {
            if (generation is not int expected)
            {
                if (frame.Kind != ControlPresentationKinds.Hello)
                {
                    return false;
                }

                generation = frame.Gen;
                revision = frame.Rev;
                return true;
            }

            if (frame.Gen != expected || frame.Rev <= revision)
            {
                return false;
            }

            revision = frame.Rev;
            return true;
        }

        /// <summary>
        /// agterm's close: after the output's end, the bridge's exit, waited for 2 seconds (a descendant can hold the pipe)
        /// and then ended, as nothing more can be read from it.
        /// </summary>
        private static string ExitOf(Process bridge)
        {
            try
            {
                if (!bridge.WaitForExit(2000))
                {
                    bridge.Kill(entireProcessTree: true);
                    bridge.WaitForExit(2000);
                }

                return bridge.HasExited ? "exit " + bridge.ExitCode : "the bridge did not exit";
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                return "the bridge did not exit";
            }
        }

        private void Closed(int launch, string reason)
        {
            if (running && launch == launches && process is not null)
            {
                Fail(reason);
            }
        }

        private void Fail(string reason)
        {
            // As agterm's, a failure is logged and shown on the row's cloud, never raised as a notification.
            if (warnedReason != reason)
            {
                warnedReason = reason;
                warn(reason);
            }

            State = "failed";
            Error = reason;
            Presenter = false;
            Drop();
            lost();
            retryAt = DateTime.UtcNow + RemoteHosts.RetryDelay(++failures);
        }

        private void Drop()
        {
            outgoing?.Writer.TryComplete();
            outgoing = null;
            if (process is not null)
            {
                Kill(process);
            }

            process = null;
        }

        private static void Kill(Process target)
        {
            try
            {
                target.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            target.Dispose();
        }
    }
}
