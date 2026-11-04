using System.Text.Json;

namespace AgShell.Protocol
{
    /// <summary>
    /// Which file a local page's address names: the page's own file, or with the directory it was opened with any file inside
    /// it. Shared by the window that serves a page here and by session overlay page-serve, which serves one shown on another machine.
    /// </summary>
    public static class PageFiles
    {
        /// <summary>
        /// The address path a page's own file is at: under the directory it was opened with, as agterm grants the page that
        /// whole directory (so a link up out of the file's own folder still lands inside it), else its name alone.
        /// </summary>
        public static string AddressOf(string file, string? root)
        {
            string relative = root is null ? Path.GetFileName(file) : Path.GetRelativePath(root, file);
            return "/" + string.Join("/", relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Select(Uri.EscapeDataString));
        }

        /// <summary>
        /// The file an address path names, by its path alone: the path is read from the directory the page was opened with
        /// (or the file's own folder without one), and anything but the file itself needs that directory. Names what a page
        /// shows; reading goes through <see cref="ResolveReal"/>.
        /// </summary>
        public static string? Resolve(string file, string? root, string urlPath)
        {
            string directory = root ?? Path.GetDirectoryName(file)!;
            string relative = urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(directory, relative));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }

            if (string.Equals(full, file, StringComparison.OrdinalIgnoreCase))
            {
                return full;
            }

            string? folder = root?.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return root is not null && full.StartsWith(folder!, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        /// <summary>
        /// agterm's RemotePageResources: the file a page may read for an address path, with both sides' links and junctions
        /// followed before the check, so a link that leads out of the directory is refused. With no directory only the page's
        /// own file; with one, any file inside it.
        /// </summary>
        public static string? ResolveReal(string file, string? root, string urlPath)
        {
            if (Resolve(file, root, urlPath) is not string named || Real(named) is not string real)
            {
                return null;
            }

            if (root is null)
            {
                return Real(file) is string own && string.Equals(real, own, StringComparison.OrdinalIgnoreCase) ? real : null;
            }

            return Real(root) is string folder && real.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? real : null;
        }

        /// <summary>The path with every link or junction on it followed; null when one cannot be.</summary>
        private static string? Real(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string current = Path.GetPathRoot(full)!;
                foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, part);
                    FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                    if (entry.LinkTarget is not null)
                    {
                        current = entry.ResolveLinkTarget(returnFinalTarget: true) is FileSystemInfo target ? Path.GetFullPath(target.FullName) : current;
                    }
                }

                return current;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>
        /// A page file's type by its extension. agterm asks the system's type registry; Windows' registry is wrong for the web
        /// (.mjs is text/plain, .webp, .woff2 and .wasm have no type, .csv is an Excel type), so, as Chromium's own lookup,
        /// a table of the web's types comes first and the registry answers only for an extension the table does not know.
        /// </summary>
        public static string ContentType(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension switch
            {
                ".html" or ".htm" => "text/html; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".js" or ".mjs" => "text/javascript; charset=utf-8",
                ".json" => "application/json; charset=utf-8",
                ".svg" => "image/svg+xml",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".avif" => "image/avif",
                ".bmp" => "image/bmp",
                ".ico" => "image/x-icon",
                ".woff2" => "font/woff2",
                ".woff" => "font/woff",
                ".ttf" => "font/ttf",
                ".otf" => "font/otf",
                ".txt" or ".md" or ".log" => "text/plain; charset=utf-8",
                ".csv" => "text/csv; charset=utf-8",
                ".xml" => "text/xml; charset=utf-8",
                ".vtt" => "text/vtt; charset=utf-8",
                ".pdf" => "application/pdf",
                ".wasm" => "application/wasm",
                ".webm" => "video/webm",
                ".weba" => "audio/webm",
                ".mp4" or ".m4v" => "video/mp4",
                ".mov" => "video/quicktime",
                ".ogv" => "video/ogg",
                ".ogg" or ".oga" or ".opus" => "audio/ogg",
                ".mp3" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                ".aac" => "audio/aac",
                ".wav" => "audio/wav",
                ".flac" => "audio/flac",
                _ => RegistryContentType(extension) ?? "application/octet-stream",
            };
        }

        private static string? RegistryContentType(string extension)
        {
            if (!OperatingSystem.IsWindows() || extension.Length < 2)
            {
                return null;
            }

            try
            {
                using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(extension);
                return key?.GetValue("Content Type") is string type && type.Contains('/') ? type : null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
                return null;
            }
        }
    }

    /// <summary>The kinds of a page channel's lines, agterm's PageServeFrame.</summary>
    public static class PageServeKinds
    {
        /// <summary>Asks for one of the page's files by its address path.</summary>
        public const string Resource = "resource";

        /// <summary>Starts a file's answer with its type; chunks follow, then end.</summary>
        public const string Head = "head";

        public const string Chunk = "chunk";

        public const string End = "end";

        /// <summary>Ends a file or bridge request the other side could not serve.</summary>
        public const string Failure = "failure";

        /// <summary>Withdraws a file request whose answer is no longer wanted.</summary>
        public const string Cancel = "cancel";

        /// <summary>A page's control request, as the raw JSON the page sent.</summary>
        public const string Bridge = "bridge";

        /// <summary>The answer to a bridge request, as the raw JSON response.</summary>
        public const string Reply = "reply";
    }

    /// <summary>
    /// One line of a page channel: the ssh a presenter opens back to the machine a page was opened on, carrying that page's
    /// files and bridge requests, numbered so they may interleave.
    /// </summary>
    public sealed record ControlPageServeFrame
    {
        public required string Kind { get; init; }

        public int Id { get; init; }

        public string? Path { get; init; }

        public string? MimeType { get; init; }

        /// <summary>A chunk's bytes, base64 in JSON.</summary>
        public byte[]? Data { get; init; }

        public string? Error { get; init; }

        public string? Request { get; init; }

        public string? Response { get; init; }
    }

    /// <summary>agterm's PageServeCodec and its channel limits.</summary>
    public static class PageServe
    {
        public const int MaxChunkBytes = 64 * 1024;

        /// <summary>The presentation stream's line cap, as agterm's page channel shares its codec's.</summary>
        public const int MaxFrameBytes = 256 * 1024;

        /// <summary>File requests a channel takes in at once; past it the machine serving refuses and the presenter queues.</summary>
        public const int ResourceLimit = 8;

        public const int BridgeLimit = 8;

        /// <summary>The failure of a request past its kind's limit.</summary>
        public const string Busy = "too many requests in flight";

        /// <summary>The failure that ends a transfer the presenter cancelled.</summary>
        public const string Cancelled = "cancelled";

        /// <summary>One line, newline included; a line over the cap or a chunk over its size is refused.</summary>
        public static string Encode(ControlPageServeFrame frame)
        {
            CheckChunk(frame);
            string line = ControlJson.Serialize(frame);
            if (System.Text.Encoding.UTF8.GetByteCount(line) >= MaxFrameBytes)
            {
                throw new JsonException("frame too large");
            }

            return line + "\n";
        }

        /// <summary>One line without its newline; what is not a frame of a known kind, with what that kind needs, is refused.</summary>
        public static ControlPageServeFrame Decode(string line)
        {
            if (System.Text.Encoding.UTF8.GetByteCount(line) > MaxFrameBytes)
            {
                throw new JsonException("frame too large");
            }

            ControlPageServeFrame frame = ControlJson.DeserializePageServeFrame(line);
            bool complete = frame.Kind switch
            {
                PageServeKinds.Resource => frame.Path is not null,
                PageServeKinds.Head => frame.MimeType is not null,
                PageServeKinds.Chunk => frame.Data is not null,
                PageServeKinds.Failure => frame.Error is not null,
                PageServeKinds.Bridge => frame.Request is not null,
                PageServeKinds.Reply => frame.Response is not null,
                PageServeKinds.End or PageServeKinds.Cancel => true,
                _ => false,
            };
            if (!complete)
            {
                throw new JsonException("malformed " + frame.Kind + " frame");
            }

            CheckChunk(frame);
            return frame;
        }

        private static void CheckChunk(ControlPageServeFrame frame)
        {
            if (frame.Kind == PageServeKinds.Chunk && frame.Data?.Length > MaxChunkBytes)
            {
                throw new JsonException("chunk too large");
            }
        }
    }
}
