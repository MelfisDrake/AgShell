namespace AgShell.Protocol
{
    /// <summary>
    /// agterm's uuidString: an id as the control channel, events, hooks and terminals' environment show it, upper case.
    /// Ids are read back in any case.
    /// </summary>
    public static class Ids
    {
        public static string Wire(this Guid id)
        {
            return id.ToString("D").ToUpperInvariant();
        }

        public static string? Wire(this Guid? id)
        {
            return id?.ToString("D").ToUpperInvariant();
        }

        /// <summary>A new id in that form, for overlays, jobs, picks and other things a client is told about.</summary>
        public static string New()
        {
            return Guid.NewGuid().Wire();
        }
    }
}
