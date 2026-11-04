using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Windows.Threading;
using AgShell.Core;
using AgShell.Protocol;

namespace AgShell.App
{
    /// <summary>
    /// agterm's presentation stream on the session's machine: adopted from the control connection agshellctl live present
    /// opened. As agterm's hub: the viewer's hello comes first, and is answered with the kinds both speak and the mode
    /// offered, then the snapshot, then each change as the session changes and notifications from agshellctl notify. A
    /// viewer asks for the presenter role once, and holds it until its connection closes or it stops answering the ping
    /// sent every 10 seconds for 30. As agterm's presentationHelloDeadline, a stream that sends no hello in 10 seconds is closed.
    /// </summary>
    internal sealed class PresentStream
    {
        private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(10);

        /// <summary>agterm's stale timeout: a viewer that has not answered a ping for this long is gone.</summary>
        private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

        /// <summary>agterm's presentationHelloDeadline: how long a viewer has to send its hello before the stream is closed.</summary>
        private static readonly TimeSpan HelloDeadline = TimeSpan.FromSeconds(10);

        private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

        private const int MaxPendingFrames = 256;

        private readonly WindowLibrary library;
        private readonly Stream connection;
        private readonly string session;
        private readonly string presenter = Ids.New();
        private readonly Dispatcher dispatcher;
        private readonly Channel<string> outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxPendingFrames) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        private readonly DispatcherTimer ping;
        private readonly DispatcherTimer helloDeadline;
        private ControlPresentation shown = new ControlPresentation();

        /// <summary>agterm's hub generations: each stream its own, so a viewer tells this connection's frames from an earlier one's.</summary>
        private static int lastGeneration;

        /// <summary>agterm's subscriber generation and revision: every frame carries both, the revision counting up.</summary>
        private readonly int generation = Interlocked.Increment(ref lastGeneration);
        private int revision;

        /// <summary>The viewer's hello came and was taken: agterm's subscriber exists from then on.</summary>
        private bool subscribed;

        /// <summary>agterm's held deltas: what changed while the snapshot was taken, sent after it so nothing falls between.</summary>
        private bool changedMeanwhile;

        /// <summary>
        /// Notifications waiting for the state read before them: agterm publishes in the order things happen, so a status set
        /// before a notify reaches the viewer first.
        /// </summary>
        private readonly List<ControlPresentationFrame> heldNotifications = new List<ControlPresentationFrame>();

        /// <summary>The kinds both sides speak, once the hello was answered; null before.</summary>
        private List<string>? kinds;

        /// <summary>The viewer's hello asked for presenter mode, so its presenter.acquire is honored.</summary>
        private bool wantsPresenter;

        private DateTime lastAck = DateTime.UtcNow;
        private bool queued;
        private bool updating;
        private bool again;
        private bool ended;

        public PresentStream(WindowLibrary library, Stream connection, string session, Dispatcher dispatcher)
        {
            this.library = library;
            this.connection = connection;
            this.session = session;
            this.dispatcher = dispatcher;
            ping = new DispatcherTimer(PingEvery, DispatcherPriority.Background, (sender, args) => Heartbeat(), dispatcher);
            helloDeadline = new DispatcherTimer(HelloDeadline, DispatcherPriority.Background, (sender, args) => NoHello(), dispatcher);
        }

        /// <summary>On the window's thread: a first read checks the session can be presented. The answer to write; on ok the stream runs.</summary>
        public async Task<ControlResponse> OpenAsync()
        {
            ControlResponse first = await library.DispatchAsync(Read(acquire: false));
            if (!first.Ok || first.Result?.Presentation is null)
            {
                return first.Ok ? ControlResponse.Failure("no presentation") : first;
            }

            library.HoldPresentationStream(presenter);
            library.ModelChanged += Changed;
            library.Events.Appended += OnEvent;
            helloDeadline.Start();
            return new ControlResponse { Ok = true };
        }

        /// <summary>agterm's hello deadline: a peer that took the reply and then said nothing would otherwise hold the stream for good.</summary>
        private void NoHello()
        {
            helloDeadline.Stop();
            if (kinds is null)
            {
                Log.Notice("ControlPresentation", "closing a presentation stream that sent no hello");
                End();
            }
        }

        /// <summary>agterm's subscribe: the hello answered with the kinds both speak and the mode offered, then the snapshot.</summary>
        private async Task GreetAsync(ControlPresentationFrame hello)
        {
            helloDeadline.Stop();
            if (ended || kinds is not null)
            {
                return;
            }

            List<string> spoken = ControlPresentationKinds.SupportedKinds.Where(kind => hello.Kinds?.Contains(kind) == true).ToList();
            wantsPresenter = hello.PresentationMode == "presenter";
            ControlResponse response = await library.DispatchAsync(Read(acquire: false, spoken));
            if (ended || response.Result?.Presentation is not ControlPresentation presentation)
            {
                if (!ended)
                {
                    Log.Notice("ControlPresentation", "closing a presentation stream whose session is gone");
                }

                End();
                return;
            }

            // agterm's subscribe: nothing reaches the viewer before the answer to its hello, notifications included.
            kinds = spoken;
            Write(new ControlPresentationFrame { Kind = ControlPresentationKinds.Hello, Version = Math.Min(hello.Version!.Value, ControlPresentationKinds.Version), Kinds = kinds, PresentationMode = wantsPresenter ? "presenter" : "mirror" });

            shown = presentation;
            foreach (ControlPresentationFrame frame in PresentationDiff.Opening(presentation))
            {
                Write(frame);
            }

            lastAck = DateTime.UtcNow;
            ping.Start();
            if (changedMeanwhile)
            {
                changedMeanwhile = false;
                Changed();
            }

            FlushNotifications();
        }

        /// <summary>
        /// agterm's presenter.acquire, answered every time, granted or refused (granted again to the holder), whatever mode the
        /// hello asked for; what is handed to a presenter follows the grant.
        /// </summary>
        private async Task AcquireAsync()
        {
            if (kinds is null)
            {
                return;
            }

            ControlResponse response = await library.DispatchAsync(Read(acquire: true));
            if (ended || response.Result?.Presentation is not ControlPresentation next)
            {
                return;
            }

            Write(new ControlPresentationFrame { Kind = next.Presenter == true ? ControlPresentationKinds.PresenterGranted : ControlPresentationKinds.PresenterRefused });
            foreach (ControlPresentationFrame frame in PresentationDiff.Changes(shown, next))
            {
                Write(frame);
            }

            shown = next;
        }

        /// <summary>agterm's heartbeat: a ping, or the end of a viewer that stopped answering them.</summary>
        private void Heartbeat()
        {
            if (DateTime.UtcNow - lastAck > StaleAfter)
            {
                Log.Notice("ControlPresentation", "closing a presentation stream: stale");
                End();
                return;
            }

            Write(new ControlPresentationFrame { Kind = ControlPresentationKinds.Ping });
        }

        /// <summary>Off the window's thread: writes the frames and reads answers until the connection or the stream ends.</summary>
        public async Task RunAsync()
        {
            Task writer = Task.Run(WriteAsync);
            try
            {
                ControlLineReader input = new ControlLineReader(connection, PageServe.MaxFrameBytes);
                while (await input.ReadAsync(closing.Token) is string line)
                {
                    ControlPresentationFrame answer;
                    try
                    {
                        answer = ControlJson.DeserializeFrame(line);
                    }
                    catch (System.Text.Json.JsonException ex)
                    {
                        // agterm: a frame it cannot read ends the stream, the viewer reconnects.
                        Log.Error("ControlPresentation", "closing a presentation stream on a bad frame: " + ex.Message);
                        break;
                    }

                    await dispatcher.InvokeAsync(() => Answer(answer));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException)
            {
            }

            await dispatcher.InvokeAsync(End);
            await writer;
        }

        private void Answer(ControlPresentationFrame answer)
        {
            if (ended)
            {
                return;
            }

            // agterm's receive: the first frame must be the hello, in a version both speak; after it, only frames of this
            // stream's generation count, the rest are left over from an earlier connection.
            if (!subscribed)
            {
                if (answer.Kind != ControlPresentationKinds.Hello)
                {
                    Log.Error("ControlPresentation", "closing a presentation stream that did not open with hello");
                    End();
                    return;
                }

                if (answer.Version is not int theirs || theirs < 1)
                {
                    Log.Error("ControlPresentation", "refusing a presentation stream: unsupportedVersion(" + answer.Version + ")");
                    End();
                    return;
                }

                subscribed = true;
                _ = GreetAsync(answer);
                return;
            }

            if (answer.Gen != generation)
            {
                return;
            }

            switch (answer.Kind)
            {
                case ControlPresentationKinds.Hello:
                    return;
                case ControlPresentationKinds.PresenterAcquire:
                    _ = AcquireAsync();
                    return;
                case ControlPresentationKinds.Ack:
                    lastAck = DateTime.UtcNow;
                    return;
                case ControlPresentationKinds.Ping:
                    // agterm's hub answers a viewer's ping as the viewer answers its own.
                    Write(new ControlPresentationFrame { Kind = ControlPresentationKinds.Ack });
                    return;
            }

            // A late answer, after the question ended or went back, is refused there; nothing to do here.
            _ = library.DispatchAsync(new ControlRequest { Cmd = "live.answer", Target = session, Args = new ControlArgs { Presenter = presenter, Answer = answer } });
        }

        /// <summary>Something changed: one read after the window's current work, however many changes came.</summary>
        private void Changed()
        {
            if (ended || queued || !subscribed)
            {
                return;
            }

            if (kinds is null)
            {
                changedMeanwhile = true;
                return;
            }

            queued = true;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = UpdateAsync()));
        }

        private async Task UpdateAsync()
        {
            queued = false;
            if (ended)
            {
                return;
            }

            if (updating)
            {
                again = true;
                return;
            }

            updating = true;
            try
            {
                do
                {
                    again = false;
                    ControlResponse response = await library.DispatchAsync(Read(acquire: false));
                    if (ended)
                    {
                        return;
                    }

                    if (!response.Ok || response.Result?.Presentation is not ControlPresentation next)
                    {
                        End();
                        return;
                    }

                    foreach (ControlPresentationFrame frame in PresentationDiff.Changes(shown, next))
                    {
                        Write(frame);
                    }

                    shown = next;
                }
                while (again && !ended);
            }
            finally
            {
                updating = false;
                if (ended)
                {
                    Release();
                }
                else
                {
                    FlushNotifications();
                }
            }
        }

        /// <summary>After any read under way, or the read would count the stream as reading again.</summary>
        private void Release()
        {
            _ = library.DispatchAsync(new ControlRequest { Cmd = "live.presentation", Target = session, Args = new ControlArgs { Presenter = presenter, Mode = "release" } });
        }

        private ControlRequest Read(bool acquire, List<string>? spoken = null)
        {
            return new ControlRequest
            {
                Cmd = "live.presentation",
                Target = session,
                Args = new ControlArgs { Presenter = presenter, Mode = acquire ? "acquire" : null, Kinds = spoken ?? kinds },
            };
        }

        /// <summary>agterm's notify frame for agshellctl notify on the session; the session closing ends the stream.</summary>
        private void OnEvent(ControlEvent item)
        {
            if (item.Session != session)
            {
                return;
            }

            if (item.Kind == ControlEventKinds.SessionClosed)
            {
                Log.Notice("ControlPresentation", "closing a presentation stream whose session is gone");
                End();
            }
            else if (item.Kind == ControlEventKinds.Notify && item.Payload.Source == "control" && subscribed)
            {
                heldNotifications.Add(new ControlPresentationFrame { Kind = ControlPresentationKinds.Notify, Title = item.Payload.Title, Body = item.Payload.Body });
                FlushNotifications();
            }
        }

        /// <summary>Held notifications go once no state read is due before them.</summary>
        private void FlushNotifications()
        {
            if (kinds is null || queued || updating)
            {
                return;
            }

            foreach (ControlPresentationFrame held in heldNotifications)
            {
                Write(held);
            }

            heldNotifications.Clear();
        }

        /// <summary>
        /// One frame per line. agterm's codec refuses one over 256 KB and ends the stream; dropping it would leave the viewer
        /// showing a stale state as if current, so the stream ends and the viewer reconnects. A viewer that leaves no room
        /// is not reading, and the stream ends too.
        /// </summary>
        private void Write(ControlPresentationFrame frame)
        {
            if (ended)
            {
                return;
            }

            string line = ControlJson.Serialize(frame with { Gen = generation, Rev = revision++ });
            int bytes = Encoding.UTF8.GetByteCount(line);
            if (bytes > PageServe.MaxFrameBytes)
            {
                Log.Error("ControlPresentation", "closing a presentation stream on an unsendable frame: oversize(" + bytes + ")");
            }

            if (bytes > PageServe.MaxFrameBytes || !outgoing.Writer.TryWrite(line + "\n"))
            {
                Log.Notice("ControlPresentation", "closing a presentation stream: stalled");
                End();
            }
        }

        private async Task WriteAsync()
        {
            try
            {
                await foreach (string line in outgoing.Reader.ReadAllAsync(closing.Token))
                {
                    using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
                    timeout.CancelAfter(WriteTimeout);
                    await connection.WriteAsync(Encoding.UTF8.GetBytes(line), timeout.Token);
                    await connection.FlushAsync(timeout.Token);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                if (!closing.IsCancellationRequested)
                {
                    Log.Notice("ControlStream", "control stream write failed or timed out; closing");
                }

                _ = dispatcher.BeginInvoke(End);
            }
        }

        /// <summary>Once, on the window's thread: the role goes back at once, as agterm's origin drops a closed stream.</summary>
        private void End()
        {
            if (ended)
            {
                return;
            }

            ended = true;
            ping.Stop();
            helloDeadline.Stop();
            library.ModelChanged -= Changed;
            library.Events.Appended -= OnEvent;
            if (!updating)
            {
                Release();
            }

            outgoing.Writer.TryComplete();
            closing.Cancel();
            try
            {
                connection.Dispose();
            }
            catch (IOException)
            {
            }
        }
    }
}
