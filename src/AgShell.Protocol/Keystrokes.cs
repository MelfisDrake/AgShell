namespace AgShell.Protocol
{
    /// <summary>Typed text as the terminal receives it: every line ending becomes one Return.</summary>
    public readonly record struct Keystrokes(string Head, bool PacedReturn)
    {
        /// <summary>
        /// The pause before a final Return. Claude Code treats a Return arriving in the same burst as a long text
        /// as part of a paste and does not submit (agterm #679).
        /// </summary>
        public static readonly TimeSpan SubmitGap = TimeSpan.FromMilliseconds(10);

        /// <summary>Normalizes CRLF, CR and LF to Return and holds back a final Return that follows text.</summary>
        public static Keystrokes From(string text)
        {
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\n', '\r');
            bool endsWithReturn = normalized.EndsWith('\r');
            string withoutLast = endsWithReturn ? normalized[..^1] : normalized;
            bool hasText = withoutLast.Any(character => character != '\r');
            return endsWithReturn && hasText ? new Keystrokes(withoutLast, true) : new Keystrokes(normalized, false);
        }
    }
}
