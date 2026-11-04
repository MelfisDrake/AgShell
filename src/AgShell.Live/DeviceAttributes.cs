using System.Text;

namespace AgShell.Live
{
    /// <summary>
    /// zmx's respondToDeviceAttributes (src/util.zig): with no terminal showing the pane, the daemon answers the shell's DA1
    /// and DA2 queries itself, so a shell asking at its start does not wait for an answer nobody gives.
    /// </summary>
    internal static class DeviceAttributes
    {
        public const string PrimaryReply = "\x1b[?62;22c";
        public const string SecondaryReply = "\x1b[>1;10;0c";

        /// <summary>The replies to the queries in one read of the pty's output, in order; empty when it asks none.</summary>
        public static string Respond(string data)
        {
            StringBuilder replies = new StringBuilder();
            int i = 0;
            while (i < data.Length)
            {
                if (data[i] == '\x1b' && i + 1 < data.Length && data[i + 1] == '[')
                {
                    // A reply, with '?' after the CSI, is no query.
                    if (i + 2 < data.Length && data[i + 2] == '?')
                    {
                        i += 3;
                        continue;
                    }

                    if (Matches(data, i, "\x1b[>c") || Matches(data, i, "\x1b[>0c"))
                    {
                        replies.Append(SecondaryReply);
                    }
                    else if (Matches(data, i, "\x1b[c") || Matches(data, i, "\x1b[0c"))
                    {
                        replies.Append(PrimaryReply);
                    }
                }

                i++;
            }

            return replies.ToString();
        }

        private static bool Matches(string data, int at, string sequence)
        {
            return string.CompareOrdinal(data, at, sequence, 0, sequence.Length) == 0 && data.Length - at >= sequence.Length;
        }
    }
}
