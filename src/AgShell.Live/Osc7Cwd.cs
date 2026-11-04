namespace AgShell.Live
{
    /// <summary>
    /// zmx's parseOsc7Cwd, isLocalHost and toOsc7Cwd (src/util.zig): a working directory as OSC 7 carries it,
    /// file://host/path with the path percent-encoded, or a plain path. A Windows path travels as /C:/dir, as a shell here
    /// reports it.
    /// </summary>
    internal static class Osc7Cwd
    {
        /// <summary>The path, decoded, and whether the host is this machine; null when the value is not a usable path.</summary>
        public static (string Path, bool IsLocal)? Parse(string value, string hostname)
        {
            if (value.Length == 0)
            {
                return null;
            }

            if (IsAbsolute(value))
            {
                return (value, true);
            }

            int separator = value.IndexOf("://", StringComparison.Ordinal);
            if (separator <= 0)
            {
                return null;
            }

            // kitty emits kitty-shell-cwd:// from its own shell integration, and accepts it alongside file:// on the way back in.
            string scheme = value[..separator];
            if (!scheme.Equals("file", StringComparison.OrdinalIgnoreCase) && !scheme.Equals("kitty-shell-cwd", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string rest = value[(separator + 3)..];
            int slash = rest.IndexOf('/');
            if (slash < 0)
            {
                return null;
            }

            string host;
            string path;
            try
            {
                host = Uri.UnescapeDataString(rest[..slash]);
                path = Uri.UnescapeDataString(rest[slash..]);
            }
            catch (UriFormatException)
            {
                return null;
            }

            if (path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':')
            {
                path = path[1..].Replace('/', '\\');
            }

            return IsAbsolute(path) ? (path, IsLocalHost(host, hostname)) : null;
        }

        /// <summary>A plain path as OSC 7's form, file://host/path, percent-encoded so it parses back unchanged.</summary>
        public static string ToUri(string path, string hostname)
        {
            string uriPath = path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' ? "/" + path.Replace('\\', '/') : path;
            System.Text.StringBuilder builder = new System.Text.StringBuilder("file://").Append(hostname);
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(uriPath))
            {
                if (IsPathChar(b))
                {
                    builder.Append((char)b);
                }
                else
                {
                    builder.Append('%').Append(b.ToString("X2"));
                }
            }

            return builder.ToString();
        }

        public static bool IsAbsolute(string value)
        {
            return value.StartsWith('/') || Path.IsPathFullyQualified(value);
        }

        private static bool IsLocalHost(string host, string hostname)
        {
            // file:///path omits the host, which conventionally means the local machine.
            if (host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.Equals(hostname, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // The machine's name is often short while OSC 7 carries the FQDN (or the reverse), so the first labels are compared.
            string valueLabel = host.Split('.')[0];
            string hostLabel = hostname.Split('.')[0];
            return hostLabel.Length > 0 && valueLabel.Equals(hostLabel, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>RFC 3986 pchar, minus the sub-delims a shell would find surprising to see left raw.</summary>
        private static bool IsPathChar(byte c)
        {
            return c is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9')
                or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~' or (byte)'/' or (byte)':' or (byte)'@';
        }
    }
}
