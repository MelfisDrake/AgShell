namespace AgShell.Core
{
    /// <summary>
    /// agterm's HtmlPageOutcomes: how each page ended, keyed by page id rather than by slot and kept for the whole app, so
    /// a caller blocked on a page reads the answer after the page, its session or its window are gone. Open pages are kept
    /// apart and never pushed out; finished outcomes keep the most recent 32.
    /// </summary>
    public static class PageOutcomes
    {
        private const int RetainedLimit = 32;

        private static readonly object Sync = new object();
        private static readonly HashSet<string> Pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<(string Id, string Outcome, string? Value)> Finished = new List<(string, string, string?)>();

        public static void Register(string id)
        {
            lock (Sync)
            {
                Pending.Add(id);
            }
        }

        /// <summary>Records how a pending page ended; the first answer stands.</summary>
        public static void Finish(string id, string outcome, string? value)
        {
            lock (Sync)
            {
                if (!Pending.Remove(id))
                {
                    return;
                }

                Finished.Add((id, outcome, value));
                if (Finished.Count > RetainedLimit)
                {
                    Finished.RemoveRange(0, Finished.Count - RetainedLimit);
                }
            }
        }

        /// <summary>pending, submitted (with its value) or dismissed; null for a page never opened or long forgotten.</summary>
        public static (string Outcome, string? Value)? Find(string id)
        {
            lock (Sync)
            {
                if (Pending.Contains(id))
                {
                    return ("pending", null);
                }

                for (int index = Finished.Count - 1; index >= 0; index--)
                {
                    if (string.Equals(Finished[index].Id, id, StringComparison.OrdinalIgnoreCase))
                    {
                        return (Finished[index].Outcome, Finished[index].Value);
                    }
                }

                return null;
            }
        }
    }
}
