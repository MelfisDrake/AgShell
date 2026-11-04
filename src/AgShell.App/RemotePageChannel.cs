using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using AgShell.Protocol;

namespace AgShell.App
{
    /// <summary>A file request the page channel could not serve: the machine serving refused it, or the channel closed.</summary>
    internal sealed class PageChannelException : Exception
    {
        public PageChannelException(string message)
            : base(message)
        {
        }
    }

    /// <summary>One file a page channel answers: its type, and its body as it arrives.</summary>
    internal sealed record PageResource(string MimeType, Stream Body);

    /// <summary>
    /// agterm's RemotePageChannel: this machine's end of a page's channel to the machine it was opened on, through one ssh
    /// running agshellctl session overlay page-serve there, started when the page opens. Its token claims once, so the channel is not
    /// started again: when it ends the page closes. At most <see cref="PageServe.ResourceLimit"/> file requests are in
    /// flight, the rest wait here; a request's slot is freed only by its terminal frame, which a cancel also produces. At
    /// most <see cref="InboundLimit"/> lines are held unread, so a page that stops reading stops the channel, not memory.
    /// </summary>
    internal sealed class RemotePageChannel : IDisposable
    {
        private const int InboundLimit = 64;

        private const int MaxPendingLines = 32;

        private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

        private readonly Action ended;
        private readonly object gate = new object();
        private readonly Dictionary<int, Transfer> transfers = new Dictionary<int, Transfer>();
        private readonly SemaphoreSlim permits = new SemaphoreSlim(InboundLimit, InboundLimit);
        private readonly SemaphoreSlim resourceSlots = new SemaphoreSlim(PageServe.ResourceLimit, PageServe.ResourceLimit);
        private readonly SemaphoreSlim bridgeSlots = new SemaphoreSlim(PageServe.BridgeLimit, PageServe.BridgeLimit);
        private readonly Dictionary<int, TaskCompletionSource<string>> bridges = new Dictionary<int, TaskCompletionSource<string>>();
        private readonly Channel<string> outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxPendingLines) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        private readonly Process? process;
        private int nextId = 1;
        private bool closed;

        /// <summary>Starts the channel; <paramref name="ended"/> runs on the window's thread when it ends by itself.</summary>
        public RemotePageChannel(string destination, string token, Dispatcher dispatcher, Action ended)
        {
            this.ended = () => dispatcher.BeginInvoke(ended);
            string failed = "the process did not start";
            try
            {
                process = Process.Start(RemoteHosts.SshStart(destination, "agshellctl", "session", "overlay", "page-serve", token));
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                process = null;
                failed = ex.Message;
            }

            if (process is null)
            {
                Close(byItself: true, "helper could not start: " + failed);
                return;
            }

            Process started = process;
            _ = Task.Run(() => DrainAsync(started.StandardError));
            new Thread(() => Read(new BufferedStream(started.StandardOutput.BaseStream, PageServe.MaxFrameBytes))) { IsBackground = true, Name = "page channel" }.Start();
            _ = Task.Run(() => WriteAsync(started.StandardInput.BaseStream));
        }

        /// <summary>
        /// Asks for the file an address path names there; waits for a slot, then for the answer's head. A request the page
        /// stops while it waits for a slot leaves without a trace there; one stopped after it was sent is cancelled there.
        /// </summary>
        public async Task<PageResource> FetchAsync(string urlPath, CancellationToken stopped)
        {
            int id;
            lock (gate)
            {
                if (closed)
                {
                    throw new PageChannelException("page channel closed");
                }

                id = nextId++;
            }

            using (CancellationTokenSource either = CancellationTokenSource.CreateLinkedTokenSource(closing.Token, stopped))
            {
                try
                {
                    await resourceSlots.WaitAsync(either.Token);
                }
                catch (OperationCanceledException) when (!stopped.IsCancellationRequested)
                {
                    throw new PageChannelException("page channel closed");
                }
            }

            Transfer transfer = new Transfer(id, this);
            lock (gate)
            {
                if (closed)
                {
                    throw new PageChannelException("page channel closed");
                }

                transfers[id] = transfer;
            }

            Send(new ControlPageServeFrame { Kind = PageServeKinds.Resource, Id = id, Path = urlPath });
            using (stopped.Register(transfer.Dispose))
            {
                string mimeType = await transfer.Head.Task;
                stopped.ThrowIfCancellationRequested();
                return new PageResource(mimeType, transfer);
            }
        }

        /// <summary>agterm's bridge: relays a page's request, the control request as JSON, and returns the reply's JSON as sent.</summary>
        public async Task<string> BridgeAsync(string request)
        {
            int id;
            lock (gate)
            {
                if (closed)
                {
                    throw new PageChannelException("page channel closed");
                }

                id = nextId++;
            }

            try
            {
                await bridgeSlots.WaitAsync(closing.Token);
            }
            catch (OperationCanceledException)
            {
                throw new PageChannelException("page channel closed");
            }

            TaskCompletionSource<string> answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                if (closed)
                {
                    throw new PageChannelException("page channel closed");
                }

                bridges[id] = answer;
            }

            Send(new ControlPageServeFrame { Kind = PageServeKinds.Bridge, Id = id, Request = request });
            return await answer.Task;
        }

        /// <summary>A bridge call's answer: its slot is free, and its caller gets the reply or the failure.</summary>
        private bool Answer(int id, string? reply, string? error)
        {
            TaskCompletionSource<string>? answer;
            lock (gate)
            {
                if (!bridges.Remove(id, out answer))
                {
                    return false;
                }
            }

            bridgeSlots.Release();
            if (reply is not null)
            {
                answer.TrySetResult(reply);
            }
            else
            {
                answer.TrySetException(new PageChannelException(error!));
            }

            return true;
        }

        /// <summary>Stops the channel without closing the page: it is closing already.</summary>
        public void Dispose()
        {
            Close(byItself: false, "page closed");
        }

        /// <summary>Queued for the writer; a channel that stopped taking requests closes.</summary>
        private void Send(ControlPageServeFrame frame)
        {
            string line;
            try
            {
                line = PageServe.Encode(frame);
            }
            catch (JsonException)
            {
                // agterm: a frame over the cap fails that one call; the channel stays up.
                if (!Answer(frame.Id, null, "request too large for the page channel"))
                {
                    Finish(frame.Id, "request too large for the page channel");
                }

                return;
            }

            if (!outgoing.Writer.TryWrite(line))
            {
                Close(byItself: true, "the channel stopped taking requests");
            }
        }

        private async Task WriteAsync(Stream input)
        {
            try
            {
                await foreach (string line in outgoing.Reader.ReadAllAsync(closing.Token))
                {
                    using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
                    timeout.CancelAfter(WriteTimeout);
                    await input.WriteAsync(Encoding.UTF8.GetBytes(line), timeout.Token);
                    await input.FlushAsync(timeout.Token);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                if (!closing.IsCancellationRequested)
                {
                    Log.Notice("ControlStream", "control stream write failed or timed out; closing");
                }

                Close(byItself: true, "channel ended");
            }
        }

        private void Read(Stream source)
        {
            string reason = "channel ended";
            try
            {
                while (ReadLine(source) is string line)
                {
                    permits.Wait(closing.Token);
                    ControlPageServeFrame frame;
                    try
                    {
                        frame = PageServe.Decode(line);
                    }
                    catch (JsonException)
                    {
                        permits.Release();
                        reason = "a malformed frame";
                        break;
                    }

                    if (!Receive(frame))
                    {
                        reason = "a frame only this PC sends";
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException)
            {
            }

            Close(byItself: true, reason);
        }

        /// <summary>One frame from the machine serving; false for one only this machine sends, which ends the channel.</summary>
        private bool Receive(ControlPageServeFrame frame)
        {
            Transfer? transfer;
            lock (gate)
            {
                transfer = transfers.GetValueOrDefault(frame.Id);
            }

            switch (frame.Kind)
            {
                case PageServeKinds.Head:
                    permits.Release();
                    transfer?.Head.TrySetResult(frame.MimeType!);
                    return true;
                case PageServeKinds.Chunk:
                    if (transfer is null || !transfer.Take(frame.Data!))
                    {
                        permits.Release();
                    }

                    return true;
                case PageServeKinds.End:
                    permits.Release();
                    Finish(frame.Id, null);
                    return true;
                case PageServeKinds.Failure:
                    permits.Release();
                    if (!Answer(frame.Id, null, frame.Error))
                    {
                        Finish(frame.Id, frame.Error);
                    }

                    return true;
                case PageServeKinds.Reply:
                    permits.Release();
                    Answer(frame.Id, frame.Response, null);
                    return true;
                default:
                    permits.Release();
                    return false;
            }
        }

        /// <summary>A transfer's terminal frame: its slot is free, and its body ends there, cleanly or with the error.</summary>
        private void Finish(int id, string? error)
        {
            Transfer? transfer;
            lock (gate)
            {
                if (!transfers.Remove(id, out transfer))
                {
                    return;
                }
            }

            resourceSlots.Release();
            transfer.End(error);
        }

        /// <summary>A body dropped before its end, by a page that stopped the load, withdraws its transfer there.</summary>
        private void Cancel(Transfer transfer)
        {
            lock (gate)
            {
                if (closed || !transfers.ContainsKey(transfer.Id))
                {
                    return;
                }
            }

            Send(new ControlPageServeFrame { Kind = PageServeKinds.Cancel, Id = transfer.Id });
        }

        /// <summary>Ends the channel once: the ssh is stopped and every waiter fails; the page closes unless it is closing already.</summary>
        private void Close(bool byItself, string reason)
        {
            List<Transfer> owed;
            List<TaskCompletionSource<string>> calls;
            lock (gate)
            {
                if (closed)
                {
                    return;
                }

                closed = true;
                Log.Notice("RemotePage", "page channel closed: " + reason);
                owed = transfers.Values.ToList();
                transfers.Clear();
                calls = bridges.Values.ToList();
                bridges.Clear();
            }

            foreach (TaskCompletionSource<string> call in calls)
            {
                call.TrySetException(new PageChannelException("page channel closed"));
            }

            closing.Cancel();
            outgoing.Writer.TryComplete();
            foreach (Transfer transfer in owed)
            {
                transfer.End("page channel closed");
            }

            if (process is not null)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                process.Dispose();
            }

            if (byItself)
            {
                ended();
            }
        }

        private static async Task DrainAsync(StreamReader errors)
        {
            try
            {
                while (await errors.ReadLineAsync() is string line)
                {
                    Log.Notice("RemotePage", "page helper: " + line);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is ObjectDisposedException)
            {
            }
        }

        /// <summary>A line without its newline, null at the end; one past the frame cap is cut, so it fails to decode.</summary>
        private static string? ReadLine(Stream source)
        {
            using MemoryStream line = new MemoryStream();
            byte[] one = new byte[1];
            while (true)
            {
                if (source.Read(one, 0, 1) == 0)
                {
                    return null;
                }

                if (one[0] == '\n')
                {
                    return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
                }

                if (line.Length <= PageServe.MaxFrameBytes)
                {
                    line.WriteByte(one[0]);
                }
            }
        }

        /// <summary>
        /// One file's answer: the head it waits for, then a body read as chunks arrive. A chunk's inbound permit is held until
        /// the body is read past it; a body disposed before its end cancels the transfer and drops what it held.
        /// </summary>
        private sealed class Transfer : Stream
        {
            private readonly RemotePageChannel channel;
            private readonly Queue<byte[]> chunks = new Queue<byte[]>();
            private TaskCompletionSource arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            private int offset;
            private bool done;
            private string? error;
            private bool dropped;

            public Transfer(int id, RemotePageChannel channel)
            {
                Id = id;
                this.channel = channel;
            }

            public int Id { get; }

            public TaskCompletionSource<string> Head { get; } = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            /// <summary>A chunk for the reader; false when nobody will read it, so its permit goes back at once.</summary>
            public bool Take(byte[] data)
            {
                lock (chunks)
                {
                    if (dropped || done)
                    {
                        return false;
                    }

                    chunks.Enqueue(data);
                    Wake();
                    return true;
                }
            }

            public void End(string? failure)
            {
                lock (chunks)
                {
                    done = true;
                    error = failure;
                    Wake();
                }

                Head.TrySetException(new PageChannelException(failure ?? "empty answer"));
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                while (true)
                {
                    Task wait;
                    lock (chunks)
                    {
                        if (chunks.Count > 0)
                        {
                            byte[] chunk = chunks.Peek();
                            int taken = Math.Min(buffer.Length, chunk.Length - offset);
                            chunk.AsMemory(offset, taken).CopyTo(buffer);
                            offset += taken;
                            if (offset == chunk.Length)
                            {
                                chunks.Dequeue();
                                offset = 0;
                                channel.permits.Release();
                            }

                            return taken;
                        }

                        if (dropped)
                        {
                            return 0;
                        }

                        if (done)
                        {
                            return error is null ? 0 : throw new IOException(error);
                        }

                        wait = arrived.Task;
                    }

                    await wait.WaitAsync(cancellationToken);
                }
            }

            public override Task<int> ReadAsync(byte[] buffer, int start, int count, CancellationToken cancellationToken)
            {
                return ReadAsync(buffer.AsMemory(start, count), cancellationToken).AsTask();
            }

            public override int Read(byte[] buffer, int start, int count)
            {
                return ReadAsync(buffer.AsMemory(start, count)).AsTask().GetAwaiter().GetResult();
            }

            /// <summary>Under the lock: whoever waits looks again.</summary>
            private void Wake()
            {
                TaskCompletionSource woken = arrived;
                arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                woken.TrySetResult();
            }

            protected override void Dispose(bool disposing)
            {
                int held;
                bool open;
                lock (chunks)
                {
                    if (dropped)
                    {
                        return;
                    }

                    dropped = true;
                    open = !done;
                    held = chunks.Count;
                    chunks.Clear();
                    Wake();
                }

                if (held > 0)
                {
                    channel.permits.Release(held);
                }

                if (open)
                {
                    channel.Cancel(this);
                }

                base.Dispose(disposing);
            }

            public override void Flush()
            {
            }

            public override long Seek(long position, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int start, int count) => throw new NotSupportedException();
        }
    }
}
