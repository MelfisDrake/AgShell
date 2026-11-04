using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>A bridge request that did not run: the page closed, or the request could not be built.</summary>
    public sealed class PageBridgeException : Exception
    {
        public PageBridgeException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// agterm's PageServeStream: the channel a presenter's helper opened for one page this machine handed over, adopted from
    /// the control connection that claimed it. It serves the page's files from inside its directory and runs the page's
    /// bridge requests here, as a local page's would run: a request past its kind's limit is refused at once, so the reader
    /// keeps reading cancels and the end behind it; files go one at a time in chunks; a cancel stops a transfer between
    /// chunks. The connection closing, or sending what a helper never sends, ends the page; the page ending here shuts the
    /// channel.
    /// </summary>
    public sealed class PageServeStream
    {
        private const int MaxPendingLines = 8;

        private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

        private readonly Stream connection;
        private readonly string file;
        private readonly string? root;
        private readonly Action ended;
        private readonly Func<string, Task<string>> bridge;
        private readonly Channel<string> outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxPendingLines) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly Channel<(int Id, string Path)> files = Channel.CreateUnbounded<(int, string)>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        private readonly object gate = new object();
        private readonly HashSet<int> active = new HashSet<int>();
        private readonly HashSet<int> cancelled = new HashSet<int>();
        private int resourcesTaken;
        private int bridgesTaken;
        private bool ending;

        /// <param name="ended">Runs once when the channel ends by itself, from any thread: the page ends with it.</param>
        /// <param name="bridge">Runs a bridge request, the raw JSON the page sent, where the page sits now; the response's JSON, or a <see cref="PageBridgeException"/>.</param>
        public PageServeStream(Stream connection, string file, string? root, Action ended, Func<string, Task<string>> bridge)
        {
            this.connection = connection;
            this.file = file;
            this.root = root;
            this.ended = ended;
            this.bridge = bridge;
        }

        /// <summary>Serves until the connection ends or is shut; returns once it is done with the connection.</summary>
        public async Task RunAsync()
        {
            Task writer = Task.Run(WriteAsync);
            Task transfers = Task.Run(TransferAsync);
            try
            {
                while (await ReadLineAsync() is string line)
                {
                    if (Take(line) is string reason)
                    {
                        bool shut;
                        lock (gate)
                        {
                            shut = ending;
                        }

                        if (!shut)
                        {
                            Log.Error("PageServe", "ending a page whose channel sent " + reason);
                        }

                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException)
            {
            }

            End(byItself: true);
            await Task.WhenAll(writer, transfers);
        }

        /// <summary>The page ended here: the channel closes without ending it again.</summary>
        public void Shutdown()
        {
            End(byItself: false);
        }

        /// <summary>
        /// One line on the reader: a cancel applies at once, a request is admitted or refused at once. Null when the channel goes
        /// on, else why it ends, in agterm's words.
        /// </summary>
        private string? Take(string line)
        {
            const string Flooded = "more requests than it reads answers to";
            ControlPageServeFrame frame;
            try
            {
                frame = PageServe.Decode(line);
            }
            catch (JsonException)
            {
                return "a malformed frame";
            }

            switch (frame.Kind)
            {
                case PageServeKinds.Cancel:
                    lock (gate)
                    {
                        if (active.Contains(frame.Id))
                        {
                            cancelled.Add(frame.Id);
                        }
                    }

                    return null;
                case PageServeKinds.Resource:
                    bool admitted;
                    lock (gate)
                    {
                        admitted = resourcesTaken < PageServe.ResourceLimit;
                        if (admitted)
                        {
                            resourcesTaken++;
                            active.Add(frame.Id);
                        }
                    }

                    return (admitted ? files.Writer.TryWrite((frame.Id, frame.Path!)) : Refuse(frame.Id, PageServe.Busy)) ? null : Flooded;
                case PageServeKinds.Bridge:
                    lock (gate)
                    {
                        admitted = bridgesTaken < PageServe.BridgeLimit;
                        if (admitted)
                        {
                            bridgesTaken++;
                        }
                    }

                    if (!admitted)
                    {
                        return Refuse(frame.Id, PageServe.Busy) ? null : Flooded;
                    }

                    _ = Task.Run(() => RelayAsync(frame.Id, frame.Request!));
                    return null;
                default:
                    return "a frame only this PC sends";
            }
        }

        /// <summary>agterm: a refusal never waits; a peer that leaves no room for one is not reading what it asked for.</summary>
        private bool Refuse(int id, string error)
        {
            return outgoing.Writer.TryWrite(PageServe.Encode(new ControlPageServeFrame { Kind = PageServeKinds.Failure, Id = id, Error = error }));
        }

        /// <summary>A bridge call has its own slot, so it waits behind at most the chunks already queued, never a whole file.</summary>
        private async Task RelayAsync(int id, string request)
        {
            ControlPageServeFrame answer;
            try
            {
                answer = new ControlPageServeFrame { Kind = PageServeKinds.Reply, Id = id, Response = await bridge(request) };
            }
            catch (PageBridgeException ex)
            {
                answer = new ControlPageServeFrame { Kind = PageServeKinds.Failure, Id = id, Error = ex.Message };
            }

            try
            {
                await SendWaitingAsync(answer);
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is ChannelClosedException)
            {
            }
            finally
            {
                lock (gate)
                {
                    bridgesTaken--;
                }
            }
        }

        private async Task TransferAsync()
        {
            try
            {
                await foreach ((int id, string path) in files.Reader.ReadAllAsync(closing.Token))
                {
                    await ServeAsync(id, path);
                    lock (gate)
                    {
                        active.Remove(id);
                        cancelled.Remove(id);
                        resourcesTaken--;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is ChannelClosedException)
            {
            }
        }

        /// <summary>Every admitted transfer ends in exactly one terminal frame, a cancelled one too.</summary>
        private async Task ServeAsync(int id, string path)
        {
            if (Cancelled(id))
            {
                await SendWaitingAsync(new ControlPageServeFrame { Kind = PageServeKinds.Failure, Id = id, Error = PageServe.Cancelled });
                return;
            }

            if (PageFiles.ResolveReal(file, root, path) is not string resolved)
            {
                await SendWaitingAsync(new ControlPageServeFrame { Kind = PageServeKinds.Failure, Id = id, Error = "file is outside the page's folder" });
                return;
            }

            FileStream handle;
            try
            {
                handle = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, PageServe.MaxChunkBytes, useAsync: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                await SendWaitingAsync(new ControlPageServeFrame { Kind = PageServeKinds.Failure, Id = id, Error = "file cannot be read" });
                return;
            }

            await using (handle)
            {
                await SendWaitingAsync(new ControlPageServeFrame { Kind = PageServeKinds.Head, Id = id, MimeType = PageFiles.ContentType(resolved) });
                byte[] buffer = new byte[PageServe.MaxChunkBytes];
                while (!Cancelled(id))
                {
                    int read;
                    try
                    {
                        read = await handle.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, closing.Token);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        await SendWaitingAsync(new ControlPageServeFrame { Kind = PageServeKinds.Failure, Id = id, Error = "file cannot be read" });
                        return;
                    }

                    if (read == 0)
                    {
                        await SendWaitingAsync(new ControlPageServeFrame { Kind = PageServeKinds.End, Id = id });
                        return;
                    }

                    await SendWaitingAsync(new ControlPageServeFrame { Kind = PageServeKinds.Chunk, Id = id, Data = buffer[..read] });
                }

                await SendWaitingAsync(new ControlPageServeFrame { Kind = PageServeKinds.Failure, Id = id, Error = PageServe.Cancelled });
            }
        }

        private bool Cancelled(int id)
        {
            lock (gate)
            {
                return cancelled.Contains(id);
            }
        }

        /// <summary>Waits for room in the writer's queue; an answer too large for a line goes back as a failure.</summary>
        private async Task SendWaitingAsync(ControlPageServeFrame frame)
        {
            string line;
            try
            {
                line = PageServe.Encode(frame);
            }
            catch (JsonException)
            {
                line = PageServe.Encode(new ControlPageServeFrame { Kind = PageServeKinds.Failure, Id = frame.Id, Error = "answer too large for the channel" });
            }

            await outgoing.Writer.WriteAsync(line, closing.Token);
        }

        /// <summary>agterm's writer: a line the presenter does not take within the timeout ends the channel.</summary>
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

                End(byItself: true);
            }
        }

        private void End(bool byItself)
        {
            lock (gate)
            {
                if (ending)
                {
                    return;
                }

                ending = true;
            }

            closing.Cancel();
            outgoing.Writer.TryComplete();
            files.Writer.TryComplete();
            try
            {
                connection.Dispose();
            }
            catch (IOException)
            {
            }

            if (byItself)
            {
                ended();
            }
        }

        private ControlLineReader? reader;

        /// <summary>A line without its newline; null at the end, and, as agterm's stream owner, for a line past the frame cap.</summary>
        private Task<string?> ReadLineAsync()
        {
            reader ??= new ControlLineReader(connection, PageServe.MaxFrameBytes);
            return reader.ReadAsync(closing.Token);
        }
    }
}
