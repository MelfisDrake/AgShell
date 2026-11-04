using System.Net;

namespace AgShell.Core
{
    /// <summary>Where a clicked terminal link came from: only a pane and the scratch have a session a page could open over.</summary>
    public enum LinkOrigin
    {
        Pane,
        Scratch,
        ProgramOverlay,
        Quick,
    }

    public enum LinkAction
    {
        Ignore,
        Browser,
        Overlay,
        Reveal,
    }

    /// <summary>What a link click does: open <see cref="Target"/> in the system, over the session, or show the file in Explorer.</summary>
    public readonly record struct LinkRoute(LinkAction Action, string Target);

    /// <summary>
    /// agterm's LinkPolicy. A terminal shows untrusted output, so a link may carry any scheme: web and mail links open, a
    /// local file:// link is shown in Explorer and never opened (opening a .exe or .cmd would run it), anything else is
    /// ignored. A file:// link of another machine, or one whose path would reach the network, is ignored too, as agterm
    /// ignores remote hosts and automount paths. With links set to open over the session, a web link the page can show,
    /// clicked in a pane or the scratch, goes there.
    /// </summary>
    public static class LinkPolicy
    {
        private static readonly string[] PermittedSchemes = { "http", "https", "mailto", "ftp" };

        /// <param name="localPath">Turns a Linux path a WSL program printed into a Windows one; null when the pane has none.</param>
        public static LinkRoute Route(string raw, bool overlayMode, LinkOrigin origin, Func<string, string?> localPath, IEnumerable<string>? localHosts = null)
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri))
            {
                return new LinkRoute(LinkAction.Ignore, string.Empty);
            }

            string scheme = uri.Scheme.ToLowerInvariant();
            if (PermittedSchemes.Contains(scheme))
            {
                bool over = overlayMode && origin is LinkOrigin.Pane or LinkOrigin.Scratch && OverlayCanShow(uri);
                return new LinkRoute(over ? LinkAction.Overlay : LinkAction.Browser, uri.AbsoluteUri);
            }

            if (scheme != "file")
            {
                return new LinkRoute(LinkAction.Ignore, string.Empty);
            }

            string host = NormalizedHost(uri.Host);
            HashSet<string> local = new HashSet<string>(localHosts ?? LocalHostNames(), StringComparer.OrdinalIgnoreCase);
            if (host.Length > 0 && !local.Contains(host))
            {
                return new LinkRoute(LinkAction.Ignore, string.Empty);
            }

            // An empty or relative path, or a //server one, could only be a guess or a remote target.
            string path = Uri.UnescapeDataString(uri.AbsolutePath);
            // .NET reads file:///C:/x as the path C:/x, without the slash the link wrote.
            if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            {
                path = "/" + path;
            }

            if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal))
            {
                return new LinkRoute(LinkAction.Ignore, string.Empty);
            }

            string normalized = LexicallyNormalized(path);
            string? windows = normalized.Length >= 3 && normalized[0] == '/' && char.IsAsciiLetter(normalized[1]) && normalized[2] == ':'
                ? normalized[1..].Replace('/', '\\') + (normalized.Length == 3 ? "\\" : string.Empty)
                : localPath(normalized);
            // A share other than a WSL distribution's would reach out over the network.
            if (windows is null || (windows.StartsWith(@"\\", StringComparison.Ordinal) && !windows.StartsWith(@"\\wsl.localhost\", StringComparison.OrdinalIgnoreCase) && !windows.StartsWith(@"\\wsl$\", StringComparison.OrdinalIgnoreCase)))
            {
                return new LinkRoute(LinkAction.Ignore, string.Empty);
            }

            return new LinkRoute(LinkAction.Reveal, windows);
        }

        /// <summary>agterm's overlayCanShow: https, or plain http from a host on this network (no dots, .local, an address).</summary>
        public static bool OverlayCanShow(Uri uri)
        {
            return uri.Scheme.ToLowerInvariant() switch
            {
                "https" => true,
                "http" => LoadsPlainHttp(uri.Host),
                _ => false,
            };
        }

        private static bool LoadsPlainHttp(string host)
        {
            string normalized = NormalizedHost(host).Trim('[', ']');
            if (normalized.Length == 0)
            {
                return false;
            }

            return normalized.EndsWith(".local", StringComparison.Ordinal) || !normalized.Contains('.') && !normalized.Contains(':') || IPAddress.TryParse(normalized, out _);
        }

        /// <summary>This machine's names, as GNU ls --hyperlink writes them: localhost and the computer name.</summary>
        private static IEnumerable<string> LocalHostNames()
        {
            yield return "localhost";
            yield return Environment.MachineName;
        }

        private static string NormalizedHost(string host)
        {
            string lower = host.ToLowerInvariant();
            return lower.EndsWith('.') ? lower[..^1] : lower;
        }

        /// <summary>agterm's lexical normalizer: . and .. collapsed without touching the disk, so a path is judged as written.</summary>
        private static string LexicallyNormalized(string path)
        {
            List<string> parts = new List<string>();
            foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".")
                {
                    continue;
                }

                if (part == "..")
                {
                    if (parts.Count > 0)
                    {
                        parts.RemoveAt(parts.Count - 1);
                    }

                    continue;
                }

                parts.Add(part);
            }

            return "/" + string.Join('/', parts);
        }
    }
}
