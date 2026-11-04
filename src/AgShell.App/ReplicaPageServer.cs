using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AgShell.App
{
    /// <summary>
    /// agterm's scheme handler for a page another machine handed over, as a loopback HTTP server. WebView2 takes an
    /// intercepted response only once its body is whole and never says when the page stops a load (its documentation: the
    /// stream must hold all the content when the deferral completes); a socket gives the page each chunk as it arrives and
    /// shows a stopped load as a closed connection, which cancels the transfer, as WebKit's stop does. Each page has its own
    /// host under localhost, named by a secret, so it is its own origin and nothing that does not know the name is served.
    /// </summary>
    internal sealed class ReplicaPageServer : IDisposable
    {
        private const int MaxHeaderBytes = 64 * 1024;

        /// <summary>What a host serves: the page's channel, its overlay, and whether its own scripts may run.</summary>
        public sealed record Replica(RemotePageChannel Channel, string OverlayId, bool Js)
        {
            /// <summary>Why the page's own document did not come: the reason its channel gave, as agterm shows a refusal as the load's error.</summary>
            public string? DocumentError { get; set; }
        }

        private readonly ConcurrentDictionary<string, Replica> replicas = new ConcurrentDictionary<string, Replica>(StringComparer.OrdinalIgnoreCase);
        private readonly List<TcpListener> listeners = new List<TcpListener>();
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        public ReplicaPageServer()
        {
            // Chromium resolves a name under localhost to both loopback addresses, so both listen on one port.
            TcpListener first = new TcpListener(IPAddress.Loopback, 0);
            first.Start();
            Port = ((IPEndPoint)first.LocalEndpoint).Port;
            listeners.Add(first);
            try
            {
                TcpListener second = new TcpListener(IPAddress.IPv6Loopback, Port);
                second.Start();
                listeners.Add(second);
            }
            catch (SocketException)
            {
            }

            foreach (TcpListener listener in listeners)
            {
                _ = Task.Run(() => AcceptAsync(listener));
            }
        }

        public int Port { get; }

        /// <summary>A new host for a page: its secret name under localhost, served until it is forgotten.</summary>
        /// <summary>Why the page with this overlay id last failed to get its document, or null.</summary>
        public string? DocumentError(string overlayId)
        {
            return replicas.Values.FirstOrDefault(replica => replica.OverlayId == overlayId)?.DocumentError;
        }

        public string Add(Replica replica)
        {
            string host = "r" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() + ".localhost";
            replicas[host] = replica;
            return host;
        }

        public void Remove(string host)
        {
            replicas.TryRemove(host, out _);
        }

        public void Dispose()
        {
            stopping.Cancel();
            foreach (TcpListener listener in listeners)
            {
                listener.Stop();
            }
        }

        private async Task AcceptAsync(TcpListener listener)
        {
            while (!stopping.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stopping.Token);
                }
                catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException || ex is ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        /// <summary>One connection, kept alive across requests; Chromium sends the next only after the answer.</summary>
        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();
                Inbound inbound = new Inbound(stream);
                try
                {
                    while (await inbound.ReadHeadAsync(stopping.Token) is string head)
                    {
                        if (!await AnswerAsync(stream, inbound, head))
                        {
                            return;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is SocketException || ex is OperationCanceledException || ex is ObjectDisposedException)
                {
                }
            }
        }

        /// <summary>Answers one request; false when the connection is done.</summary>
        private async Task<bool> AnswerAsync(NetworkStream stream, Inbound inbound, string head)
        {
            string[] lines = head.Split("\r\n");
            string[] request = lines[0].Split(' ');
            string? host = lines.Skip(1).Select(line => line.Split(':', 2)).FirstOrDefault(pair => pair.Length == 2 && pair[0].Trim().Equals("Host", StringComparison.OrdinalIgnoreCase))?[1].Trim();
            if (request.Length != 3 || host is null || !host.EndsWith(":" + Port, StringComparison.Ordinal)
                || !replicas.TryGetValue(host[..^(Port.ToString().Length + 1)], out Replica? replica))
            {
                await WriteAsync(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no such page"), null, close: true);
                return false;
            }

            if (request[0] is not ("GET" or "HEAD"))
            {
                await WriteAsync(stream, 405, "text/plain; charset=utf-8", Array.Empty<byte>(), null, close: true);
                return false;
            }

            string path = Uri.UnescapeDataString(request[1].Split('?', '#')[0]);
            // The page stopping the load closes the connection: watched while the answer is fetched and written.
            using CancellationTokenSource stopped = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            Task closed = inbound.WatchCloseAsync(stopped);
            // The browser marks a top-level load Sec-Fetch-Dest: document; a missing picture is no failed load.
            bool document = lines.Any(line => line.StartsWith("Sec-Fetch-Dest:", StringComparison.OrdinalIgnoreCase) && line.Contains("document", StringComparison.OrdinalIgnoreCase));
            if (document)
            {
                replica.DocumentError = null;
            }

            PageResource resource;
            try
            {
                resource = await replica.Channel.FetchAsync(path, stopped.Token);
            }
            catch (PageChannelException ex)
            {
                if (document)
                {
                    replica.DocumentError = ex.Message;
                }

                stopped.Cancel();
                await closed;
                await WriteAsync(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(ex.Message), null, close: false);
                return true;
            }

            await using (resource.Body)
            {
                if (request[0] == "HEAD")
                {
                    stopped.Cancel();
                    await closed;
                    await WriteHeadAsync(stream, 200, resource.MimeType, null, length: 0, close: false);
                    return true;
                }

                await WriteHeadAsync(stream, 200, resource.MimeType, PageHost.PolicyFor(resource.MimeType, replica.Js), length: null, close: false);
                byte[] buffer = new byte[Protocol.PageServe.MaxChunkBytes];
                int read;
                while ((read = await resource.Body.ReadAsync(buffer, stopped.Token)) > 0)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(read.ToString("X") + "\r\n"), stopped.Token);
                    await stream.WriteAsync(buffer.AsMemory(0, read), stopped.Token);
                    await stream.WriteAsync("\r\n"u8.ToArray(), stopped.Token);
                }

                await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), stopped.Token);
                stopped.Cancel();
                await closed;
                return true;
            }
        }

        private static async Task WriteAsync(NetworkStream stream, int status, string type, byte[] body, string? policy, bool close)
        {
            await WriteHeadAsync(stream, status, type, policy, body.Length, close);
            await stream.WriteAsync(body);
        }

        /// <summary>No length means a chunked body follows.</summary>
        private static async Task WriteHeadAsync(NetworkStream stream, int status, string type, string? policy, long? length, bool close)
        {
            string reason = status switch { 200 => "OK", 404 => "Not Found", _ => "Method Not Allowed" };
            StringBuilder head = new StringBuilder()
                .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
                .Append("Content-Type: ").Append(type).Append("\r\n")
                .Append("Cache-Control: no-store\r\n")
                .Append(length is long size ? "Content-Length: " + size + "\r\n" : "Transfer-Encoding: chunked\r\n")
                .Append(close ? "Connection: close\r\n" : "Connection: keep-alive\r\n");
            if (policy is not null)
            {
                head.Append("Content-Security-Policy: ").Append(policy).Append("\r\n");
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.Append("\r\n").ToString()));
        }

        /// <summary>A connection's incoming bytes: request heads, and what arrives while an answer is under way.</summary>
        private sealed class Inbound
        {
            private readonly NetworkStream stream;
            private readonly byte[] buffer = new byte[MaxHeaderBytes];
            private int length;

            public Inbound(NetworkStream stream)
            {
                this.stream = stream;
            }

            /// <summary>The next request's head without its blank line; null when the connection ends or sends too much.</summary>
            public async Task<string?> ReadHeadAsync(CancellationToken stop)
            {
                while (true)
                {
                    int end = IndexOfHeadEnd();
                    if (end >= 0)
                    {
                        string head = Encoding.ASCII.GetString(buffer, 0, end);
                        Array.Copy(buffer, end + 4, buffer, 0, length - end - 4);
                        length -= end + 4;
                        return head;
                    }

                    if (length == buffer.Length)
                    {
                        return null;
                    }

                    int read = await stream.ReadAsync(buffer.AsMemory(length), stop);
                    if (read == 0)
                    {
                        return null;
                    }

                    length += read;
                }
            }

            /// <summary>
            /// Until the answer is done: the connection closing stops it. Anything the page sends meanwhile is kept for the next
            /// request; the watch ends when the answer cancels it.
            /// </summary>
            public async Task WatchCloseAsync(CancellationTokenSource stopped)
            {
                try
                {
                    while (length < buffer.Length)
                    {
                        int read = await stream.ReadAsync(buffer.AsMemory(length), stopped.Token);
                        if (read == 0)
                        {
                            stopped.Cancel();
                            return;
                        }

                        length += read;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException)
                {
                    if (ex is IOException)
                    {
                        stopped.Cancel();
                    }
                }
            }

            private int IndexOfHeadEnd()
            {
                for (int index = 0; index + 3 < length; index++)
                {
                    if (buffer[index] == '\r' && buffer[index + 1] == '\n' && buffer[index + 2] == '\r' && buffer[index + 3] == '\n')
                    {
                        return index;
                    }
                }

                return -1;
            }
        }
    }
}
