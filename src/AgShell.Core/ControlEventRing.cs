using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>
    /// Bounded event history for one app run. A reader keeps a cursor (run id and sequence) and asks for what came after it;
    /// nothing is pushed, so a slow or vanished reader costs nothing.
    /// </summary>
    public sealed class ControlEventRing
    {
        public const int Capacity = 4096;

        private readonly List<ControlEvent> entries = new List<ControlEvent>();
        private long sequence;

        public string Run { get; } = Ids.New();

        /// <summary>Raised for every event as it is recorded, for hooks.</summary>
        public event Action<ControlEvent>? Appended;

        public void Append(string kind, Guid? workspace, Guid? session, ControlEventPayload payload)
        {
            sequence++;
            ControlEvent entry = new ControlEvent
            {
                Seq = sequence,
                // agterm's timeIntervalSince1970, to the tick rather than the millisecond.
                Ts = (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalSeconds,
                Kind = kind,
                Workspace = workspace.Wire(),
                Session = session.Wire(),
                Payload = payload,
            };
            entries.Add(entry);
            if (entries.Count > Capacity)
            {
                entries.RemoveRange(0, entries.Count - Capacity);
            }

            Appended?.Invoke(entry);
        }

        /// <summary>Records an event another ring had, in this ring's sequence and marked with its window: the app's one stream.</summary>
        public void Append(ControlEvent source, string? window)
        {
            sequence++;
            ControlEvent entry = source with { Seq = sequence, Window = window };
            entries.Add(entry);
            if (entries.Count > Capacity)
            {
                entries.RemoveRange(0, entries.Count - Capacity);
            }

            Appended?.Invoke(entry);
        }

        /// <summary>
        /// Events after the cursor. Without a cursor it only anchors at the current end, returning no history.
        /// Returns an error text when the cursor belongs to another run, has expired, or points past the end.
        /// </summary>
        public (ControlEventBatch Batch, string? Error) Read(string? run, long? after, IReadOnlySet<string>? kinds, int limit)
        {
            ControlEventBatch anchor = new ControlEventBatch { Run = Run, Next = sequence, Items = new List<ControlEvent>() };
            if (run is null || after is not long cursor)
            {
                return (anchor, null);
            }

            // agterm parses the run as an id, so its case does not matter.
            if (!string.Equals(run, Run, StringComparison.OrdinalIgnoreCase))
            {
                return (anchor, "event run changed");
            }

            if (cursor > sequence)
            {
                return (anchor, "event cursor is ahead of the current sequence");
            }

            if (entries.Count > 0 && cursor < entries[0].Seq - 1)
            {
                return (anchor, "event cursor expired");
            }

            List<ControlEvent> items = new List<ControlEvent>();
            foreach (ControlEvent entry in entries)
            {
                if (entry.Seq <= cursor || (kinds is not null && !kinds.Contains(entry.Kind)))
                {
                    continue;
                }

                items.Add(entry);
                if (items.Count == limit)
                {
                    return (new ControlEventBatch { Run = Run, Next = entry.Seq, Items = items }, null);
                }
            }

            return (new ControlEventBatch { Run = Run, Next = sequence, Items = items }, null);
        }
    }
}
