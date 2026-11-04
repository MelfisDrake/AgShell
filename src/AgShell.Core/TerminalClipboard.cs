using System.Security.Cryptography;
using System.Text;

namespace AgShell.Core
{
    /// <summary>
    /// The direction of a clipboard access a terminal program requests (OSC 52 or the Kitty clipboard protocol): reading the
    /// clipboard into the terminal stream, or writing the terminal's data to it.
    /// </summary>
    public enum ClipboardAccess
    {
        Read,
        Write,
    }

    public enum ClipboardDecision
    {
        Allow,
        Deny,
        Prompt,
    }

    /// <summary>
    /// agterm's ClipboardPromptPolicy: the "don't ask again this session" choice, per direction, for the rest of the app's
    /// run. Read and write are independent, so allowing writes never allows reads.
    /// </summary>
    public sealed class ClipboardPromptPolicy
    {
        private bool? read;
        private bool? write;

        public ClipboardDecision Decision(ClipboardAccess access)
        {
            return (access == ClipboardAccess.Read ? read : write) switch
            {
                true => ClipboardDecision.Allow,
                false => ClipboardDecision.Deny,
                null => ClipboardDecision.Prompt,
            };
        }

        public void Remember(ClipboardAccess access, bool allow)
        {
            if (access == ClipboardAccess.Read)
            {
                read = allow;
            }
            else
            {
                write = allow;
            }
        }
    }

    /// <summary>One representation of clipboard data.</summary>
    public sealed record ClipboardContent(string Mime, byte[] Data);

    /// <summary>Ghostty's clipboard locations: the selection one is the app's own, primary is the system clipboard.</summary>
    public enum ClipboardLocation
    {
        Standard,
        Selection,
        Primary,
    }

    /// <summary>An OSC 52 request: a read when <see cref="Data"/> is null.</summary>
    public sealed record Osc52Request(char Kind, ClipboardLocation Location, byte[]? Data);

    /// <summary>Ghostty's OSC 52: one kind letter or none, then the payload; "?" reads, anything else writes.</summary>
    public static class Osc52
    {
        /// <summary>Null when the sequence is malformed or a write's base64 is not valid: Ghostty drops both.</summary>
        public static Osc52Request? Parse(string data)
        {
            if (data.Length == 0)
            {
                return null;
            }

            char kind;
            string payload;
            if (data[0] == ';')
            {
                kind = 'c';
                payload = data[1..];
            }
            else
            {
                if (data.Length < 2 || data[1] != ';')
                {
                    return null;
                }

                kind = data[0];
                payload = data[2..];
            }

            ClipboardLocation location = kind switch
            {
                's' => ClipboardLocation.Selection,
                'p' => ClipboardLocation.Primary,
                _ => ClipboardLocation.Standard,
            };
            if (payload == "?")
            {
                return new Osc52Request(KindOf(location), location, null);
            }

            // Strict, as the Kitty specification asks of OSC 52 too, but a missing padding tail is tolerated: OSC 52 cannot
            // report an error.
            byte[]? decoded = StrictBase64.Decode(payload, paddingRequired: false);
            return decoded is null ? null : new Osc52Request(KindOf(location), location, decoded);
        }

        /// <summary>Ghostty's answer to a read: the clipboard's text, empty when denied, always ended by ST.</summary>
        public static string Reply(ClipboardLocation location, string text)
        {
            return "\u001b]52;" + KindOf(location) + ";" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "\u001b\\";
        }

        private static char KindOf(ClipboardLocation location)
        {
            return location switch
            {
                ClipboardLocation.Selection => 's',
                ClipboardLocation.Primary => 'p',
                _ => 'c',
            };
        }
    }

    /// <summary>RFC 4648 base64, standard alphabet, nothing outside it and no non-zero padding bits, as Ghostty decodes.</summary>
    public static class StrictBase64
    {
        public static byte[]? Decode(string text, bool paddingRequired)
        {
            List<byte> output = new List<byte>();
            return DecodeInto(text, paddingRequired, output) ? output.ToArray() : null;
        }

        internal static bool DecodeInto(string text, bool paddingRequired, List<byte> output)
        {
            if (paddingRequired && text.Length % 4 != 0)
            {
                return false;
            }

            if (text.Length % 4 == 1)
            {
                return false;
            }

            int padding = text.EndsWith("==", StringComparison.Ordinal) ? 2 : text.EndsWith('=') ? 1 : 0;
            if (padding > 0 && text.Length % 4 != 0)
            {
                return false;
            }

            int body = text.Length - padding;
            int accumulator = 0;
            int bits = 0;
            for (int index = 0; index < body; index++)
            {
                int value = ValueOf(text[index]);
                if (value < 0)
                {
                    return false;
                }

                accumulator = (accumulator << 6) | value;
                bits += 6;
                if (bits >= 8)
                {
                    bits -= 8;
                    output.Add((byte)(accumulator >> bits));
                    accumulator &= (1 << bits) - 1;
                }
            }

            return accumulator == 0;
        }

        private static int ValueOf(char c)
        {
            return c switch
            {
                >= 'A' and <= 'Z' => c - 'A',
                >= 'a' and <= 'z' => c - 'a' + 26,
                >= '0' and <= '9' => c - '0' + 52,
                '+' => 62,
                '/' => 63,
                _ => -1,
            };
        }
    }

    /// <summary>
    /// The payloads of one MIME type in a Kitty write: one base64 stream split at any packet boundary, where only the whole
    /// must be padded correctly. As Ghostty's (and kitty's), a packet ending exactly at padding starts the stream afresh.
    /// </summary>
    internal sealed class Base64Stream
    {
        private string pending = string.Empty;

        public bool Feed(string chunk, List<byte> output)
        {
            string text = pending + chunk;
            int whole = text.Length - text.Length % 4;
            string head = text[..whole];
            string tail = text[whole..];
            // Padding ends a stream, so nothing may follow it; a group cut inside its padding waits for the rest of it.
            int padding = tail.IndexOf('=');
            if ((tail.Length > 0 && head.Contains('=')) || tail.Any(c => c != '=' && !IsAlphabet(c))
                || (padding >= 0 && (padding < 2 || tail[padding..].Any(c => c != '='))))
            {
                return false;
            }

            pending = tail;
            return StrictBase64.DecodeInto(head, paddingRequired: true, output);
        }

        /// <summary>False when the stream stopped inside a group, which is a stream that was not padded correctly.</summary>
        public bool Finish()
        {
            bool complete = pending.Length == 0;
            pending = string.Empty;
            return complete;
        }

        private static bool IsAlphabet(char c)
        {
            return c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/';
        }
    }

    /// <summary>The metadata of one Kitty clipboard packet, decoded.</summary>
    public sealed record KittyClipboardMetadata(string Op, bool Primary, string Id, string Mime, string Pw, string Name);

    /// <summary>A Kitty clipboard read the app answers: it reads the clipboard, maybe asks the user, then replies.</summary>
    public sealed record KittyClipboardRead(bool Primary, IReadOnlyList<string> Mimes, bool List, string Id, bool Granted)
    {
        /// <summary>The request's terminator, which Ghostty's answer ends with.</summary>
        public string Terminator { get; init; } = KittyClipboard.St;
    }

    /// <summary>A committed Kitty clipboard write the app performs, maybe after asking, and then reports.</summary>
    public sealed record KittyClipboardWrite(IReadOnlyList<ClipboardContent> Contents, string Id, bool Granted)
    {
        /// <summary>The terminator of the packet that committed the write, which Ghostty's status ends with.</summary>
        public string Terminator { get; init; } = KittyClipboard.St;
    }

    /// <summary>
    /// Ghostty's Kitty clipboard protocol (OSC 5522) for one terminal: metadata and payload validation, the write
    /// transaction, session grants and the replies. Modeled, as Ghostty's, on kitty's own clipboard.py: a malformed packet
    /// is dropped without an answer, an invalid one inside a write aborts it with EINVAL.
    /// </summary>
    public sealed class KittyClipboard
    {
        public const int MaxIdLength = 512;
        public const int MaxPwLength = 128;
        public const int MaxMimeLength = 256;
        public const int MaxNameLength = 256;
        public const int MaxReadMimes = 4;
        public const int MaxListingMimes = 16;
        public const int ReadChunkSize = 4096;
        public const int MaxWriteSize = 64 * 1024 * 1024;
        public const int MaxWriteMimes = 64;
        public const int MaxWriteAliases = 64;
        public const string TargetsMime = ".";
        public const string St = "\u001b\\";
        public const string Bel = "\u0007";
        private const string OtpAlphabet = "23456789abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ";
        private const int OtpLength = 22;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly string[] TextMimes = { "text/plain", "text/plain;charset=utf-8", "UTF8_STRING", "TEXT", "STRING" };

        private readonly KittyClipboardGrants grants = new KittyClipboardGrants();
        private WriteState? write;
        private string terminator = St;

        /// <summary>Ghostty's isTextMime: the names plain text goes by on the platforms terminals run on.</summary>
        public static bool IsTextMime(string mime)
        {
            return TextMimes.Contains(mime, StringComparer.Ordinal);
        }

        /// <summary>
        /// Handles one packet. Returns what the terminal answers at once, if anything, and a read or a committed write for
        /// the app to carry out. <paramref name="writeDenied"/> is the clipboard-write policy: a denied write fails at once.
        /// As Ghostty's, every answer ends with the packet's own <paramref name="end"/>, BEL or ST.
        /// </summary>
        public (string? Reply, KittyClipboardRead? Read, KittyClipboardWrite? Write) Handle(string data, bool writeDenied, string end = St)
        {
            terminator = end;
            int split = data.IndexOf(';');
            string raw = split < 0 ? data : data[..split];
            string payload = split < 0 ? string.Empty : data[(split + 1)..];
            KittyClipboardMetadata? meta;
            try
            {
                meta = ParseMetadata(raw);
            }
            catch (FormatException)
            {
                // An undecodable value aborts a write in flight when it came on its data or alias packet.
                if (write is WriteState state && OperationOf(raw) is "wdata" or "walias")
                {
                    return (Finish(state, "EINVAL"), null, null);
                }

                return (null, null, null);
            }

            if (meta is null)
            {
                return (null, null, null);
            }

            switch (meta.Op)
            {
                case "read":
                    return (null, ParseRead(meta, payload), null);
                case "write":
                    write = null;
                    if (writeDenied)
                    {
                        return (Status("write", "EPERM", meta.Id, terminator), null, null);
                    }

                    write = new WriteState(meta);
                    return (null, null, null);
                case "wdata":
                    return WriteData(meta, payload);
                default:
                    return (WriteAlias(meta, payload), null, null);
            }
        }

        /// <summary>
        /// Ghostty's paste event in mode 5522: the clipboard's types listed with a fresh one-time password, granted for one
        /// read before the event goes out, so the program's follow-up read needs no prompt.
        /// </summary>
        public string PasteEvent(IReadOnlyList<string> available, bool primary = false)
        {
            string otp = NewOtp();
            grants.Grant(otp, ClipboardAccess.Read, oneTime: true);
            return ReadSuccess(primary, id: string.Empty, pw: otp, list: true, available.Take(MaxListingMimes).ToList(), Array.Empty<ClipboardContent>(), St);
        }

        /// <summary>
        /// Ghostty's completeKittyClipboardRead: the requested types in request order, a text-like name served by the text,
        /// types with nothing to serve left out; with the listing first when it was asked for.
        /// </summary>
        public static string ReadReply(KittyClipboardRead read, string? text, IReadOnlyList<string> available)
        {
            List<ClipboardContent> served = new List<ClipboardContent>();
            bool textRead = read.Mimes.Contains("text/plain");
            foreach (string mime in read.Mimes)
            {
                if (textRead && IsTextMime(mime))
                {
                    served.Add(new ClipboardContent(mime, Encoding.UTF8.GetBytes(text ?? string.Empty)));
                }
            }

            return ReadSuccess(read.Primary, read.Id, null, read.List, available, served, read.Terminator);
        }

        /// <summary>One status packet, the shape of every answer but a successful read.</summary>
        public static string Status(string op, string status, string id, string end = St)
        {
            return Packet(op, status, primary: false, id, mime: null, pw: null, payload: Array.Empty<byte>(), end);
        }

        /// <summary>
        /// agterm's readClipboard: only text is served, and only to a read naming text/plain, so one that wants neither it nor
        /// the listing has nothing to read and is answered at once, with no prompt, as a read that served nothing.
        /// </summary>
        public static bool ReadNeedsClipboard(KittyClipboardRead read)
        {
            return read.List || read.Mimes.Contains("text/plain");
        }

        /// <summary>Whether a read must be confirmed: one asking only for the listing discloses nothing.</summary>
        public static bool ReadPromptExempt(int dataMimes)
        {
            return dataMimes == 0;
        }

        private KittyClipboardRead? ParseRead(KittyClipboardMetadata meta, string payload)
        {
            // An undecodable list is dropped without an answer: reads have no error status for it.
            byte[]? decoded = StrictBase64.Decode(payload, paddingRequired: true);
            if (decoded is null || !TryUtf8(decoded, out string list))
            {
                return null;
            }

            List<string> mimes = new List<string>();
            bool listing = false;
            foreach (string mime in Tokens(list))
            {
                if (mime == TargetsMime)
                {
                    listing = true;
                }
                else if (mimes.Count < MaxReadMimes)
                {
                    mimes.Add(mime);
                }
            }

            // A password without a name is no password; a prompt-exempt read never spends a one-time grant.
            string pw = meta.Name.Length > 0 ? meta.Pw : string.Empty;
            bool granted = !ReadPromptExempt(mimes.Count) && grants.Use(pw, ClipboardAccess.Read);
            return new KittyClipboardRead(meta.Primary, mimes, listing, meta.Id, granted) { Terminator = terminator };
        }

        private (string? Reply, KittyClipboardRead? Read, KittyClipboardWrite? Write) WriteData(KittyClipboardMetadata meta, string payload)
        {
            if (write is not WriteState state)
            {
                return (null, null, null);
            }

            if (meta.Mime.Length == 0)
            {
                write = null;
                if (state.Commit() is not List<ClipboardContent> contents)
                {
                    return (Status("write", "EINVAL", state.Id, terminator), null, null);
                }

                string pw = state.Name.Length > 0 ? state.Pw : string.Empty;
                return (null, null, new KittyClipboardWrite(contents, state.Id, grants.Use(pw, ClipboardAccess.Write)) { Terminator = terminator });
            }

            string? failure = state.Data(meta.Mime, payload);
            return (failure is null ? null : Finish(state, failure), null, null);
        }

        private string? WriteAlias(KittyClipboardMetadata meta, string payload)
        {
            if (write is not WriteState state)
            {
                return null;
            }

            if (meta.Mime.Length == 0 || !state.Alias(meta.Mime, payload))
            {
                return Finish(state, "EINVAL");
            }

            return null;
        }

        /// <summary>The write's final status, with the id of its opening packet; the transaction is dropped.</summary>
        private string Finish(WriteState state, string status)
        {
            write = null;
            return Status("write", status, state.Id, terminator);
        }

        public static KittyClipboardMetadata? ParseMetadata(string raw)
        {
            string? op = null;
            string loc = string.Empty;
            string id = string.Empty;
            string mime = string.Empty;
            string pw = string.Empty;
            string name = string.Empty;
            foreach (string record in raw.Split(':'))
            {
                int equals = record.IndexOf('=');
                if (equals < 0)
                {
                    return null;
                }

                string value = record[(equals + 1)..];
                switch (record[..equals])
                {
                    case "type":
                        op = value;
                        break;
                    case "loc":
                        loc = value;
                        break;
                    case "id":
                        id = value;
                        break;
                    case "mime":
                        mime = value;
                        break;
                    case "pw":
                        pw = value;
                        break;
                    case "name":
                        name = value;
                        break;
                }
            }

            if (op is not ("read" or "write" or "wdata" or "walias"))
            {
                return null;
            }

            string sanitized = new string(id.Where(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '+' or '.').Take(MaxIdLength).ToArray());
            return new KittyClipboardMetadata(
                op,
                loc == "primary",
                sanitized,
                DecodeValue(mime, MaxMimeLength) ?? throw new FormatException(),
                DecodeValue(pw, MaxPwLength) ?? string.Empty,
                DecodeValue(name, MaxNameLength) ?? throw new FormatException());
        }

        /// <summary>A base64 UTF-8 value; null when it is too long, FormatException when it does not decode.</summary>
        private static string? DecodeValue(string value, int maxLength)
        {
            if (value.Length > (maxLength + 2) / 3 * 4)
            {
                return null;
            }

            byte[] decoded = StrictBase64.Decode(value, paddingRequired: true) ?? throw new FormatException();
            if (!TryUtf8(decoded, out string text))
            {
                throw new FormatException();
            }

            return decoded.Length > maxLength ? null : text;
        }

        private static string? OperationOf(string raw)
        {
            string? op = null;
            foreach (string record in raw.Split(':'))
            {
                int equals = record.IndexOf('=');
                if (equals < 0)
                {
                    return null;
                }

                if (record[..equals] == "type")
                {
                    op = record[(equals + 1)..];
                }
            }

            return op;
        }

        private static bool TryUtf8(byte[] bytes, out string text)
        {
            try
            {
                text = StrictUtf8.GetString(bytes);
                return true;
            }
            catch (DecoderFallbackException)
            {
                text = string.Empty;
                return false;
            }
        }

        private static IEnumerable<string> Tokens(string text)
        {
            return text.Split(new[] { ' ', '\t', '\n', '\r', '\v', '\f' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string ReadSuccess(bool primary, string id, string? pw, bool list, IReadOnlyList<string> available, IReadOnlyList<ClipboardContent> contents, string end)
        {
            StringBuilder reply = new StringBuilder();
            reply.Append(Packet("read", "OK", primary, id, null, pw, Array.Empty<byte>(), end));
            if (list)
            {
                // The listing joins the types with spaces and ends with a newline, in one packet that always goes out.
                StringBuilder listing = new StringBuilder();
                foreach (string mime in available)
                {
                    int separator = listing.Length > 0 ? 1 : 0;
                    if (Encoding.UTF8.GetByteCount(listing.ToString()) + separator + Encoding.UTF8.GetByteCount(mime) + 1 > ReadChunkSize)
                    {
                        break;
                    }

                    listing.Append(separator > 0 ? " " : string.Empty).Append(mime);
                }

                string head = Head("read", "DATA", false, id, TargetsMime, pw);
                reply.Append(head);
                if (available.Count > 0)
                {
                    reply.Append(';').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(listing + "\n")));
                }

                reply.Append(end);
            }

            foreach (ClipboardContent content in contents)
            {
                for (int offset = 0; offset < content.Data.Length; offset += ReadChunkSize)
                {
                    int length = Math.Min(ReadChunkSize, content.Data.Length - offset);
                    reply.Append(Packet("read", "DATA", false, id, content.Mime, pw, content.Data.AsSpan(offset, length).ToArray(), end));
                }
            }

            reply.Append(Packet("read", "DONE", false, id, null, pw, Array.Empty<byte>(), end));
            return reply.ToString();
        }

        /// <summary>
        /// One packet in kitty's field order; an empty payload has no payload section. It ends as Ghostty's does, with the
        /// request's own terminator; xterm.js does not tell which one came, so the page reads it from the output itself.
        /// </summary>
        private static string Packet(string op, string status, bool primary, string id, string? mime, string? pw, byte[] payload, string end)
        {
            string head = Head(op, status, primary, id, mime, pw);
            return head + (payload.Length > 0 ? ";" + Convert.ToBase64String(payload) : string.Empty) + end;
        }

        private static string Head(string op, string status, bool primary, string id, string? mime, string? pw)
        {
            StringBuilder head = new StringBuilder("\u001b]5522;type=").Append(op).Append(":status=").Append(status);
            if (primary)
            {
                head.Append(":loc=primary");
            }

            if (id.Length > 0)
            {
                head.Append(":id=").Append(id);
            }

            if (mime is not null)
            {
                head.Append(":mime=").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(mime)));
            }

            if (pw is not null)
            {
                head.Append(":pw=").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(pw)));
            }

            return head.ToString();
        }

        private static string NewOtp()
        {
            StringBuilder otp = new StringBuilder();
            int limit = 256 / OtpAlphabet.Length * OtpAlphabet.Length;
            while (otp.Length < OtpLength)
            {
                foreach (byte value in RandomNumberGenerator.GetBytes(2 * OtpLength))
                {
                    if (value >= limit)
                    {
                        continue;
                    }

                    otp.Append(OtpAlphabet[value % OtpAlphabet.Length]);
                    if (otp.Length == OtpLength)
                    {
                        break;
                    }
                }
            }

            return otp.ToString();
        }

        /// <summary>Ghostty's WriteState: wdata chunks and walias aliases gathered until the commit packet.</summary>
        private sealed class WriteState
        {
            private readonly List<byte> spool = new List<byte>();
            private readonly List<(string Mime, int Start, int Length)> entries = new List<(string, int, int)>();
            private readonly List<(string Alias, string Target)> aliases = new List<(string, string)>();
            private readonly Base64Stream decoder = new Base64Stream();
            private int? current;

            public WriteState(KittyClipboardMetadata meta)
            {
                Id = meta.Id;
                Pw = meta.Pw;
                Name = meta.Name;
            }

            public string Id { get; }

            public string Pw { get; }

            public string Name { get; }

            /// <summary>Null when taken; else the status that fails the whole write.</summary>
            public string? Data(string mime, string payload)
            {
                if (current is not int index || entries[index].Mime != mime)
                {
                    if (current is int previous)
                    {
                        // The previous type's stream must have ended on a whole group.
                        if (!decoder.Finish())
                        {
                            return "EINVAL";
                        }

                        entries[previous] = entries[previous] with { Length = spool.Count - entries[previous].Start };
                    }

                    // A type sent again starts a fresh region in place of its earlier one.
                    int again = entries.FindIndex(entry => entry.Mime == mime);
                    if (again >= 0)
                    {
                        entries[again] = (mime, spool.Count, 0);
                        current = again;
                    }
                    else if (entries.Count >= MaxWriteMimes)
                    {
                        current = null;
                        return null;
                    }
                    else
                    {
                        entries.Add((mime, spool.Count, 0));
                        current = entries.Count - 1;
                    }
                }

                List<byte> decoded = new List<byte>();
                if (!decoder.Feed(payload, decoded))
                {
                    return "EINVAL";
                }

                // The limit covers the whole transaction; going over it never leaves partial contents behind.
                if (decoded.Count > MaxWriteSize - spool.Count)
                {
                    return "EFBIG";
                }

                spool.AddRange(decoded);
                return null;
            }

            public bool Alias(string target, string payload)
            {
                byte[]? decoded = StrictBase64.Decode(payload, paddingRequired: true);
                if (decoded is null || !TryUtf8(decoded, out string list))
                {
                    return false;
                }

                List<string> names = Tokens(list).Where(name => name.Length <= MaxMimeLength).ToList();
                foreach (string name in names)
                {
                    int existing = aliases.FindIndex(alias => alias.Alias == name);
                    if (existing >= 0)
                    {
                        aliases[existing] = (name, target);
                    }
                    else if (aliases.Count >= MaxWriteAliases)
                    {
                        return true;
                    }
                    else
                    {
                        aliases.Add((name, target));
                    }
                }

                return true;
            }

            /// <summary>The final types in arrival order, then the aliases applied in turn; null when the last stream was cut.</summary>
            public List<ClipboardContent>? Commit()
            {
                if (current is int index)
                {
                    if (!decoder.Finish())
                    {
                        return null;
                    }

                    entries[index] = entries[index] with { Length = spool.Count - entries[index].Start };
                    current = null;
                }

                List<ClipboardContent> contents = entries.Select(entry => new ClipboardContent(entry.Mime, spool.GetRange(entry.Start, entry.Length).ToArray())).ToList();
                foreach ((string alias, string target) in aliases)
                {
                    if (contents.FirstOrDefault(content => content.Mime == target) is not ClipboardContent source)
                    {
                        continue;
                    }

                    int existing = contents.FindIndex(content => content.Mime == alias);
                    if (existing >= 0)
                    {
                        contents[existing] = contents[existing] with { Data = source.Data };
                    }
                    else
                    {
                        contents.Add(new ClipboardContent(alias, source.Data));
                    }
                }

                return contents;
            }
        }
    }

    /// <summary>
    /// Ghostty's session grants: a request carrying a granted password skips the prompt. A one-time grant (a paste event's)
    /// is spent by the check itself, whichever direction it asked.
    /// </summary>
    internal sealed class KittyClipboardGrants
    {
        private const int MaxEntries = 32;
        private readonly List<Entry> entries = new List<Entry>();

        public void Grant(string pw, ClipboardAccess access, bool oneTime)
        {
            if (pw.Length == 0 || pw.Length > KittyClipboard.MaxPwLength)
            {
                return;
            }

            Entry? entry = entries.FirstOrDefault(candidate => candidate.Pw == pw);
            if (entry is not null)
            {
                entry.OneTime = entry.OneTime && oneTime;
            }
            else
            {
                if (entries.Count >= MaxEntries)
                {
                    entries.RemoveAt(0);
                }

                entry = new Entry(pw) { OneTime = oneTime };
                entries.Add(entry);
            }

            if (access == ClipboardAccess.Read)
            {
                entry.Read = true;
            }
            else
            {
                entry.Write = true;
            }
        }

        public bool Use(string pw, ClipboardAccess access)
        {
            if (pw.Length == 0)
            {
                return false;
            }

            int index = entries.FindIndex(candidate => candidate.Pw == pw);
            if (index < 0)
            {
                return false;
            }

            Entry entry = entries[index];
            bool allowed = access == ClipboardAccess.Read ? entry.Read : entry.Write;
            if (entry.OneTime)
            {
                // Ghostty's swapRemove: the last entry takes the spent one's place.
                entries[index] = entries[^1];
                entries.RemoveAt(entries.Count - 1);
            }

            return allowed;
        }

        private sealed class Entry
        {
            public Entry(string pw)
            {
                Pw = pw;
            }

            public string Pw { get; }

            public bool Read { get; set; }

            public bool Write { get; set; }

            public bool OneTime { get; set; }
        }
    }
}
