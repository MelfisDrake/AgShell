namespace AgShell.Pty
{
    public sealed class PtyStartOptions
    {
        public required string CommandLine { get; init; }

        public string? WorkingDirectory { get; init; }

        public int Columns { get; init; } = 120;

        public int Rows { get; init; } = 30;

        /// <summary>Variables added to the inherited environment; a null value removes the variable.</summary>
        public IReadOnlyDictionary<string, string?>? Environment { get; init; }
    }
}
