using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Windows.Threading;
using AgShell.Core;
using AgShell.Protocol;

namespace AgShell.App
{
    /// <summary>
    /// agterm's OverlayJobStream: the connection of a helper that claimed a handed-over overlay job. The launch context goes
    /// first, a cancel follows when the job is to stop, and the helper's reports come back: started, then how the program
    /// ended. The connection closing with nothing reported ends the job unknown; a line a helper never sends ends the stream.
    /// </summary>
    internal sealed class OverlayJobStream
    {
        private const int MaxPendingFrames = 16;

        private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

        private readonly AppModel model;
        private readonly OverlayJob job;
        private readonly ControlOverlayJob launch;
        private readonly Stream connection;
        private readonly Dispatcher dispatcher;
        private readonly Channel<string> outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxPendingFrames) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly CancellationTokenSource closing = new CancellationTokenSource();

        /// <summary>The helper reported how the program ended, so the job finishing is no cue to cancel it.</summary>
        private bool reported;

        private bool cancelSent;
        private bool ended;

        public OverlayJobStream(AppModel model, OverlayJob job, ControlOverlayJob launch, Stream connection, Dispatcher dispatcher)
        {
            this.model = model;
            this.job = job;
            this.launch = launch;
            this.connection = connection;
            this.dispatcher = dispatcher;
        }

        /// <summary>On the window's thread, after the ok answer: the launch context, and a cancel already asked for.</summary>
        public void Start()
        {
            // agterm: a job that ended between its claim and now must not launch.
            if (job.State != OverlayJob.Claimed)
            {
                End();
                return;
            }

            Send(new ControlOverlayJobFrame { Kind = OverlayJobKinds.Context, Context = launch });
            model.Changed += Changed;
            Changed();
        }

        /// <summary>Off the window's thread: writes the frames and reads the helper's until the connection ends.</summary>
        public async Task RunAsync()
        {
            Task writer = Task.Run(WriteAsync);
            try
            {
                ControlLineReader input = new ControlLineReader(connection, PageServe.MaxFrameBytes);
                while (await input.ReadAsync(closing.Token) is string line)
                {
                    await dispatcher.InvokeAsync(() => Receive(line));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException)
            {
            }

            await dispatcher.InvokeAsync(Closed);
            await writer;
        }

        private void Receive(string line)
        {
            ControlOverlayJobFrame frame;
            try
            {
                frame = ControlJson.DeserializeJobFrame(line);
            }
            catch (System.Text.Json.JsonException)
            {
                Log.Error("OverlayJobs", "closing a job helper connection on a bad frame");
                End();
                return;
            }

            switch (frame.Kind)
            {
                case OverlayJobKinds.Started:
                    model.OverlayJobStarted(job, DateTimeOffset.UtcNow);
                    break;
                case OverlayJobKinds.Exited when frame.Code is int code:
                    reported = true;
                    model.FinishOverlayJob(job, code, null);
                    break;
                case OverlayJobKinds.Canceled:
                    reported = true;
                    model.FinishOverlayJob(job, null, "canceled");
                    break;
                case OverlayJobKinds.LaunchFailed:
                    Log.Notice("OverlayJobs", "remote overlay job failed to launch: " + frame.Reason);
                    reported = true;
                    model.FinishOverlayJob(job, null, "launch-failed");
                    break;
                case OverlayJobKinds.Exited:
                    Log.Error("OverlayJobs", "closing a job helper connection on a bad frame");
                    End();
                    break;
                default:
                    End();
                    break;
            }
        }

        /// <summary>agterm's cancel hook: once, when the job is to stop and the helper has not said how it ended.</summary>
        private void Changed()
        {
            if (!ended && !cancelSent && !reported && AppModel.OverlayJobStopping(job))
            {
                cancelSent = true;
                Send(new ControlOverlayJobFrame { Kind = OverlayJobKinds.Cancel });
            }
        }

        /// <summary>agterm's helperGone: a job the helper never reported on ends unknown.</summary>
        private void Closed()
        {
            model.OverlayJobHelperGone(job);
            End();
        }

        /// <summary>A helper that leaves no room for a frame is not reading, and the stream ends.</summary>
        private void Send(ControlOverlayJobFrame frame)
        {
            if (!outgoing.Writer.TryWrite(ControlJson.Serialize(frame) + "\n"))
            {
                Log.Error("OverlayJobs", "closing a job helper connection that cannot take a frame");
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

        private void End()
        {
            if (ended)
            {
                return;
            }

            ended = true;
            model.Changed -= Changed;
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
