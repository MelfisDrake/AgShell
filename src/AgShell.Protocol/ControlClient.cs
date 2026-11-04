using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace AgShell.Protocol
{
    public sealed class ControlClientException : Exception
    {
        public ControlClientException(string message, bool connect = false)
            : base(message)
        {
            Connect = connect;
        }

        /// <summary>The pipe could not be connected to at all, as agterm's failed connect().</summary>
        public bool Connect { get; }
    }

    public readonly record struct ControlReply(ControlResponse Response, string Line);

    public static partial class ControlClient
    {
        /// <summary>agterm's cap on a reply line: a session.text --all reply carries the whole scrollback.</summary>
        public const int MaxResponseBytes = 64 << 20;

        /// <summary>
        /// A stream request: the answer, and on ok the connection, which then carries what the request opened. The answer is
        /// read a byte at a time, so nothing behind it is taken from the stream.
        /// </summary>
        /// <exception cref="ControlClientException">agshell is not running, or the answer cannot be read.</exception>
        public static async Task<(ControlResponse Response, Stream? Connection)> OpenStreamAsync(string pipeName, ControlRequest request, TimeSpan connectTimeout)
        {
            NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await ConnectAsync(pipe, pipeName, connectTimeout, CancellationToken.None);
                PipeOwner.Verify(pipe);
                try
                {
                    await pipe.WriteAsync(Encoding.UTF8.GetBytes(ControlJson.Serialize(request) + "\n"));
                    await pipe.FlushAsync();
                }
                catch (IOException)
                {
                    throw new ControlClientException("could not send the request");
                }

                using MemoryStream line = new MemoryStream();
                byte[] one = new byte[1];
                while (true)
                {
                    if (await pipe.ReadAsync(one) == 0 || line.Length > ControlEndpoint.MaxRequestBytes)
                    {
                        throw new ControlClientException("the app closed the connection without answering");
                    }

                    if (one[0] == '\n')
                    {
                        break;
                    }

                    line.WriteByte(one[0]);
                }

                ControlResponse response;
                try
                {
                    response = ControlJson.DeserializeResponse(Encoding.UTF8.GetString(line.ToArray()));
                }
                catch (System.Text.Json.JsonException ex)
                {
                    throw new ControlClientException("could not decode the reply: " + ex.Message);
                }

                if (!response.Ok)
                {
                    await pipe.DisposeAsync();
                    return (response, null);
                }

                return (response, pipe);
            }
            catch (UnauthorizedAccessException ex)
            {
                await pipe.DisposeAsync();
                throw new ControlClientException(ex.Message + " (" + pipeName + ")");
            }
            catch (IOException)
            {
                await pipe.DisposeAsync();
                throw new ControlClientException("the app closed the connection without answering");
            }
            catch (ControlClientException)
            {
                await pipe.DisposeAsync();
                throw;
            }
        }

        /// <exception cref="ControlClientException">agshell is not running, or the reply cannot be read.</exception>
        public static async Task<ControlReply> SendAsync(string pipeName, ControlRequest request, TimeSpan connectTimeout, CancellationToken cancellationToken = default)
        {
            string line = ControlJson.Serialize(request);
            int size = Encoding.UTF8.GetByteCount(line);
            if (size > ControlEndpoint.MaxRequestBytes)
            {
                throw new ControlClientException("request too large (" + size + " bytes, cap " + ControlEndpoint.MaxRequestBytes + ") — split the input into smaller requests");
            }

            using NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await ConnectAsync(pipe, pipeName, connectTimeout, cancellationToken);

            // The callers know one failure type: a pipe of someone else and a link that broke mid-reply are both refusals.
            byte[]? reply;
            try
            {
                PipeOwner.Verify(pipe);
                await pipe.WriteAsync(new UTF8Encoding(false).GetBytes(line + "\n"), cancellationToken);
                await pipe.FlushAsync(cancellationToken);
                reply = await ReadLineAsync(pipe, cancellationToken);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ControlClientException(ex.Message + " (" + pipeName + ")");
            }
            catch (IOException ex)
            {
                throw new ControlClientException("the connection to AgShell broke: " + ex.Message);
            }

            if (reply is null)
            {
                throw new ControlClientException("no response from " + pipeName);
            }

            string text = Encoding.UTF8.GetString(reply);
            try
            {
                return new ControlReply(ControlJson.DeserializeResponse(text), text);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new ControlClientException("could not decode response: " + ex.Message);
            }
        }

        /// <summary>
        /// A connect that fails names the pipe and why, as agterm's connect(PATH) failed. A missing pipe fails at once, as agterm's
        /// ENOENT; only a busy one is waited for, the pipe's stand-in for a listen backlog.
        /// </summary>
        private static async Task ConnectAsync(NamedPipeClientStream pipe, string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
        {
            string path = @"\\.\pipe\" + pipeName;
            if (Missing(path))
            {
                throw ConnectFailed(path, pipeName, MissingPipe);
            }

            try
            {
                await pipe.ConnectAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw ConnectFailed(path, pipeName, Missing(path) ? MissingPipe : "All pipe instances are busy");
            }
        }

        /// <summary>Whether no instance of the pipe exists. A file check would open one and hand its server a broken connection.</summary>
        private static bool Missing(string path)
        {
            return !WaitNamedPipe(path, 1) && Marshal.GetLastPInvokeError() == ErrorFileNotFound;
        }

        private const int ErrorFileNotFound = 2;

        [LibraryImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool WaitNamedPipe(string name, uint timeout);

        private const string MissingPipe = "The system cannot find the file specified";

        /// <summary>
        /// agterm's hint: the ownership lock held says the owner is present; otherwise, or when that cannot be answered, AgShell
        /// may be stopped. The lock is the state directory's: a pipe has no file beside it, so it is found only for the pipe of
        /// this state directory, and another pipe gets no answer.
        /// </summary>
        private static ControlClientException ConnectFailed(string path, string pipeName, string reason)
        {
            string stateDirectory = ControlEndpoint.StateDirectory();
            return Hinted(path, reason, ControlEndpoint.PipeName(stateDirectory) == pipeName ? stateDirectory : null);
        }

        internal static ControlClientException Hinted(string path, string reason, string? ownerDirectory)
        {
            bool owner = ownerDirectory is not null && StateDirectoryLock.Held(ownerDirectory) == true;
            string hint = owner ? "the pipe owner is present but not accepting connections" : "AgShell may be stopped or unable to accept connections";
            return new ControlClientException("connect(" + path + ") failed: " + reason + " — " + hint, connect: true);
        }

        /// <summary>agterm's readLine: up to the first newline, the bytes so far at end of stream, nothing past the cap.</summary>
        private static async Task<byte[]?> ReadLineAsync(Stream pipe, CancellationToken cancellationToken)
        {
            using MemoryStream buffer = new MemoryStream();
            byte[] chunk = new byte[64 * 1024];
            while (true)
            {
                int read = await pipe.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                {
                    return buffer.Length == 0 ? null : buffer.ToArray();
                }

                int newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
                if (newline >= 0)
                {
                    buffer.Write(chunk, 0, newline);
                    return buffer.ToArray();
                }

                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxResponseBytes)
                {
                    return null;
                }
            }
        }
    }
}
