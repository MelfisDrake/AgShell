using System.IO.Pipes;
using System.Text;

namespace AgShell.Protocol
{
    /// <summary>
    /// The control pipe, as agterm's socket: each connection carries one JSON request line and gets one response line
    /// back, except a stream request, as agterm's page-serve claim: its handler answers it and, after an ok answer, keeps
    /// the connection. Connections are served one at a time in arrival order, as agterm's serial accept loop does; the
    /// requests that wait on ssh or processes, and the streams, leave the line once read.
    /// </summary>
    public sealed class ControlServer : IAsyncDisposable
    {
        /// <summary>agterm's readTimeoutSeconds and writeTimeoutSeconds: one stalled read or write.</summary>
        private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(5);

        /// <summary>agterm's readDeadlineSeconds and writeDeadlineSeconds: the whole request, the whole response.</summary>
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

        private readonly string pipeName;
        private readonly Func<ControlRequest, Task<ControlResponse>> handler;
        private readonly Func<ControlRequest, Stream, Task>? streams;
        private readonly Func<ControlRequest, ControlResponse?>? fastPath;

        /// <summary>Raised on a background thread once a request's reply was written whole, as agterm quits after live.reset's reply.</summary>
        public event Action<ControlRequest, ControlResponse>? Written;

        /// <summary>Raised after the reply write finishes or fails, so closing the last window cannot cancel its own reply.</summary>
        public event Action<ControlRequest, ControlResponse>? ReplyCompleted;
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private readonly SemaphoreSlim line = new SemaphoreSlim(1, 1);
        private Task? loop;

        /// <param name="streams">Takes a stream request with its connection: it writes the answer itself and owns the connection until it returns.</param>
        /// <param name="fastPath">agterm's fastPathResponse: an answer from what is known without the app, or null.</param>
        public ControlServer(string pipeName, Func<ControlRequest, Task<ControlResponse>> handler, Func<ControlRequest, Stream, Task>? streams = null, Func<ControlRequest, ControlResponse?>? fastPath = null)
        {
            this.pipeName = pipeName;
            this.handler = handler;
            this.streams = streams;
            this.fastPath = fastPath;
        }

        /// <summary>Starts listening. Returns false when another process already serves this pipe.</summary>
        public bool Start()
        {
            NamedPipeServerStream first;
            try
            {
                first = CreatePipe(PipeOptions.FirstPipeInstance);
            }
            catch (UnauthorizedAccessException)
            {
                // A pipe that already has an instance refuses the first one: agterm's flock on the socket held elsewhere.
                Log.Notice("ControlServer", "control pipe " + pipeName + " is already served by another instance — not binding");
                return false;
            }
            catch (IOException ex)
            {
                Log.Notice("ControlServer", "control pipe " + pipeName + " failed: " + ex.Message);
                return false;
            }

            loop = Task.Run(() => AcceptLoopAsync(first));
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            stopping.Cancel();
            if (loop is not null)
            {
                await loop.ConfigureAwait(false);
            }

            stopping.Dispose();
        }

        private NamedPipeServerStream CreatePipe(PipeOptions extra)
        {
            return new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | extra);
        }

        private async Task AcceptLoopAsync(NamedPipeServerStream pipe)
        {
            while (true)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                NamedPipeServerStream connected = pipe;
                _ = Task.Run(() => ServeAsync(connected));
                pipe = CreatePipe(PipeOptions.None);
            }
        }

        /// <summary>
        /// One connection: its turn on the line, the request read within the deadlines, the answer written within them. A
        /// stream or a request that waits on the network gives the line up once it is read.
        /// </summary>
        private async Task ServeAsync(NamedPipeServerStream pipe)
        {
            bool held = false;
            bool resetConfirmed = false;
            await using (pipe.ConfigureAwait(false))
            {
                try
                {
                    await line.WaitAsync(stopping.Token).ConfigureAwait(false);
                    held = true;
                    string? text = await ReadLineAsync(pipe).ConfigureAwait(false);
                    if (text is null)
                    {
                        await WriteAsync(pipe, ControlResponse.Failure("request too large or read failed")).ConfigureAwait(false);
                        return;
                    }

                    ControlRequest request;
                    try
                    {
                        request = ControlJson.DeserializeRequest(text);
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // agterm's invalidRequestMessage for data that does not decode.
                        await WriteAsync(pipe, ControlResponse.Failure("invalid request: The given data was not valid JSON.")).ConfigureAwait(false);
                        return;
                    }

                    if (fastPath?.Invoke(request) is ControlResponse cached)
                    {
                        await WriteAsync(pipe, cached).ConfigureAwait(false);
                        return;
                    }

                    if (streams is not null && ControlEndpoint.StreamCommands.Contains(request.Cmd))
                    {
                        line.Release();
                        held = false;
                        await streams(request, pipe).ConfigureAwait(false);
                        return;
                    }

                    if (ControlEndpoint.ParallelCommands.Contains(request.Cmd))
                    {
                        line.Release();
                        held = false;
                    }

                    ControlResponse response = await HandleAsync(request).ConfigureAwait(false);
                    resetConfirmed = request.Cmd == "live.reset" && response.Ok;
                    try
                    {
                        await WriteAsync(pipe, response).ConfigureAwait(false);
                        Written?.Invoke(request, response);
                    }
                    finally
                    {
                        ReplyCompleted?.Invoke(request, response);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException)
                {
                    // The client went away, or stalled past the deadlines; there is nobody to answer.
                    if (resetConfirmed)
                    {
                        Log.Error("ControlServer", "live.reset reply was not written; the reset stays pending for a later quit");
                    }
                }
                finally
                {
                    if (held)
                    {
                        line.Release();
                    }
                }
            }
        }

        private async Task<ControlResponse> HandleAsync(ControlRequest request)
        {
            try
            {
                return await handler(request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ControlResponse.Failure("internal error: " + ex.Message);
            }
        }

        /// <summary>
        /// The bytes up to the first newline. As agterm's readLine: null at the size cap, on a read error or a stall, and
        /// at the end of the stream with nothing read; a last line without a newline counts.
        /// </summary>
        private async Task<string?> ReadLineAsync(Stream stream)
        {
            using CancellationTokenSource whole = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            whole.CancelAfter(Deadline);
            using MemoryStream buffer = new MemoryStream();
            byte[] chunk = new byte[16 * 1024];
            try
            {
                while (true)
                {
                    using CancellationTokenSource step = CancellationTokenSource.CreateLinkedTokenSource(whole.Token);
                    step.CancelAfter(StepTimeout);
                    int read = await stream.ReadAsync(chunk, step.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                    }

                    int newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
                    buffer.Write(chunk, 0, newline < 0 ? read : newline);
                    if (buffer.Length > ControlEndpoint.MaxRequestBytes)
                    {
                        return null;
                    }

                    if (newline >= 0)
                    {
                        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                    }
                }
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
            {
                return null;
            }
        }

        /// <summary>
        /// The answer as one line, written in pieces within agterm's deadlines, then left for the client to read: a client
        /// that stops reading costs the line ten seconds at most.
        /// </summary>
        private async Task WriteAsync(NamedPipeServerStream pipe, ControlResponse response)
        {
            byte[] reply = Encoding.UTF8.GetBytes(ControlJson.Serialize(response) + "\n");
            using CancellationTokenSource whole = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            whole.CancelAfter(Deadline);
            for (int offset = 0; offset < reply.Length; offset += 64 * 1024)
            {
                using CancellationTokenSource step = CancellationTokenSource.CreateLinkedTokenSource(whole.Token);
                step.CancelAfter(StepTimeout);
                await pipe.WriteAsync(reply.AsMemory(offset, Math.Min(64 * 1024, reply.Length - offset)), step.Token).ConfigureAwait(false);
            }

            await pipe.FlushAsync(whole.Token).ConfigureAwait(false);
            if (OperatingSystem.IsWindows())
            {
                // The drain blocks with no timeout of its own; past the deadline the pipe is closed under it.
                Task drain = Task.Run(pipe.WaitForPipeDrain);
                _ = drain.ContinueWith(task => task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                await Task.WhenAny(drain, Task.Delay(Timeout.Infinite, whole.Token)).ConfigureAwait(false);
                if (!drain.IsCompleted)
                {
                    pipe.Dispose();
                    throw new OperationCanceledException();
                }

                await drain.ConfigureAwait(false);
            }
        }
    }
}
