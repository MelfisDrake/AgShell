using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AgShell.Protocol;

namespace AgShell.Ctl
{
    /// <summary>
    /// agterm's clipboard set: the text goes out through the pane the command runs in, as OSC 52 to every terminal showing
    /// it, this machine's and another's alike. Like agterm's, it talks to the pane's daemon, named in the shell's
    /// AGSHELL_LIVE_PIPE as zmx names its own in ZMX_SESSION, not to the app, so it works while no window is open; a pane no
    /// daemon keeps (scratch, quick, overlay, another restore mode) has none and is refused.
    /// </summary>
    internal static class LiveClipboard
    {
        /// <summary>agterm's cap on the OSC 52 payload, base64 as it goes out.</summary>
        private const int MaxEncodedBytes = 8 * 1024 * 1024 - 64;

        public static int Run(byte[] data)
        {
            if (data.Length == 0)
            {
                return Fail("nothing to copy");
            }

            long encoded = (data.Length + 2L) / 3 * 4;
            if (encoded > MaxEncodedBytes)
            {
                return Fail("text is too large to copy through the terminal: " + encoded + " bytes encoded, the limit is " + MaxEncodedBytes);
            }

            if (DaemonPipe(Environment.GetEnvironmentVariable(ControlEndpoint.LivePipeVariable), ControlEndpoint.StateDirectory()) is not string daemon)
            {
                return Fail("this pane has no live shell: run it in a main or split pane under restore mode live");
            }

            using NamedPipeClientStream pipe = new NamedPipeClientStream(".", daemon, PipeDirection.InOut, PipeOptions.None);
            try
            {
                pipe.Connect(2000);
            }
            catch (Exception ex) when (ex is TimeoutException || ex is IOException)
            {
                return Fail("this pane has no live shell: run it in a main or split pane under restore mode live");
            }

            try
            {
                PipeOwner.Verify(pipe);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Fail(ex.Message);
            }

            string sequence = "\u001b]52;c;" + Convert.ToBase64String(data) + "\u0007";
            using (Utf8JsonWriter writer = new Utf8JsonWriter(pipe))
            {
                writer.WriteStartObject();
                writer.WriteString("op", "inject");
                writer.WriteString("data", sequence);
                writer.WriteEndObject();
            }

            pipe.WriteByte((byte)'\n');
            pipe.Flush();
            // The daemon answers whether its shell was there, as agterm's zmx print exits.
            using StreamReader reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            Task<bool?> answer = Task.Run(() =>
            {
                while (reader.ReadLine() is string line)
                {
                    using JsonDocument reply = JsonDocument.Parse(line);
                    if (reply.RootElement.TryGetProperty("op", out JsonElement op) && op.GetString() == "injected")
                    {
                        return reply.RootElement.TryGetProperty("found", out JsonElement found) ? found.GetBoolean() : (bool?)null;
                    }
                }

                return null;
            });
            if (answer.Wait(TimeSpan.FromSeconds(2)) && answer.IsCompletedSuccessfully && answer.Result == false)
            {
                return Fail("this pane has no live shell: run it in a main or split pane under restore mode live");
            }

            return 0;
        }

        /// <summary>
        /// The daemon pipe a shell's variable names, when it is one of this state directory's, as agterm takes ZMX_SESSION only
        /// when it is a name agterm would make: one inherited from an unrelated shell is never printed into.
        /// </summary>
        internal static string? DaemonPipe(string? variable, string stateDirectory)
        {
            string prefix = ControlEndpoint.LivePipePrefix(stateDirectory);
            if (variable is null || !variable.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string rest = variable[prefix.Length..];
            return rest.Length == 32 && rest.All(char.IsAsciiHexDigitLower) ? variable : null;
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine("error: " + message);
            return 1;
        }
    }
}
