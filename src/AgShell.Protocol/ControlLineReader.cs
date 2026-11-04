using System.Text;

namespace AgShell.Protocol
{
    /// <summary>
    /// agterm's ControlStreamOwner read loop for a connection that stays open: lines split on '\n' only, and a line past
    /// the cap ends the stream instead of being read whole into memory.
    /// </summary>
    public sealed class ControlLineReader
    {
        private readonly Stream stream;
        private readonly int maxLineBytes;
        private readonly byte[] chunk = new byte[16 * 1024];
        private MemoryStream buffer = new MemoryStream();

        public ControlLineReader(Stream stream, int maxLineBytes)
        {
            this.stream = stream;
            this.maxLineBytes = maxLineBytes;
        }

        /// <summary>The next line without its newline; null at the end of the stream or past the cap, either way the end.</summary>
        public async Task<string?> ReadAsync(CancellationToken token)
        {
            while (true)
            {
                byte[] data = buffer.GetBuffer();
                int length = (int)buffer.Length;
                int newline = Array.IndexOf(data, (byte)'\n', 0, length);
                if (newline >= 0)
                {
                    if (newline > maxLineBytes)
                    {
                        Log.Error("ControlStream", "control stream line exceeds " + maxLineBytes + " bytes; closing");
                        return null;
                    }

                    string line = Encoding.UTF8.GetString(data, 0, newline);
                    MemoryStream rest = new MemoryStream();
                    rest.Write(data, newline + 1, length - newline - 1);
                    buffer = rest;
                    return line;
                }

                if (length > maxLineBytes)
                {
                    Log.Error("ControlStream", "control stream line exceeds " + maxLineBytes + " bytes; closing");
                    return null;
                }

                int read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
    }
}
