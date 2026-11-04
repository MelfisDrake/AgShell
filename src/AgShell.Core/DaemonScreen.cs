namespace AgShell.Core
{
    /// <summary>
    /// agterm's ZmxScreen: one read of a live pane daemon's own terminal, which always has the leader's layout. Its plain
    /// text, soft-wrapped rows joined, the grid, the zero-based cursor in the active screen, and whether the alternate
    /// screen is up; the revision advances on every pty read, resize and injected output.
    /// </summary>
    public sealed record DaemonScreen(string Text, int Columns, int Rows, int CursorColumn, int CursorRow, bool Alternate, ulong Revision)
    {
        /// <summary>
        /// The last <paramref name="count"/> content lines, trailing blank rows dropped first, matching what a pane's own
        /// surface returns for the same request.
        /// </summary>
        public string LastLines(int count)
        {
            List<string> rows = Text.Split('\n').ToList();
            while (rows.Count > 0 && rows[^1].All(char.IsWhiteSpace))
            {
                rows.RemoveAt(rows.Count - 1);
            }

            return string.Join("\n", rows.Skip(Math.Max(0, rows.Count - count)));
        }
    }
}
