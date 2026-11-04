namespace AgShell.Core
{
    /// <summary>
    /// agterm's TerminalText: every value that reaches a {AGSHELL_X} token (a directory a program reports, a session,
    /// workspace or window name, a remote host) loses the C0 controls and DEL, above all a newline, which the shell that
    /// runs the line reads as the end of a command. A real name or path never has one.
    /// </summary>
    public static class TerminalText
    {
        public static string Sanitized(string value)
        {
            return value.Any(c => c < 0x20 || c == 0x7f) ? new string(value.Where(c => c >= 0x20 && c != 0x7f).ToArray()) : value;
        }

        /// <summary>agterm's sanitized(...).trimmedOrNil: the name as stored, null when nothing is left.</summary>
        public static string? Name(string? value)
        {
            string trimmed = value is null ? string.Empty : Sanitized(value).Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
    }
}
