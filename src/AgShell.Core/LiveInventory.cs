using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>
    /// agterm's ZmxInventory: what the daemons report joined against what the panes claim. Rows are the union, sorted by
    /// daemon name, so live list explains both a leaked daemon and a pane whose daemon has gone, and live prune can be
    /// audited against what live list showed.
    /// </summary>
    public static class LiveInventory
    {
        /// <summary>agterm's ZmxPaneClaim: one pane that expects a daemon, with the names and ids that explain and address it.</summary>
        public sealed record Claim(string PaneId, string Pane, bool PendingClose, string WindowId, string? WindowName, string WindowState, string? WorkspaceId, string? WorkspaceName, string SessionId, string? SessionName);

        /// <summary>
        /// A daemon as the listing saw it: Clients null when it did not answer (agterm's unreadable), else its clients, its
        /// shell's pid and when it started.
        /// </summary>
        public sealed record Daemon(string Name, int? Clients, int? LeaderPid, DateTime? Created);

        public const string Claimed = "claimed";
        public const string Orphan = "orphan";
        public const string Unknown = "unknown";
        public const string Conflicted = "conflicted";
        public const string PendingClose = "pendingClose";
        public const string Foreign = "foreign";

        public const string IncompleteInventory = "the pane inventory is incomplete or has conflicting owners, so no daemon can be safely pruned";
        public const string IncompleteRestartInventory = "the pane inventory is incomplete or has conflicting owners, so the pane cannot be safely restarted";

        /// <summary>
        /// agterm's join. A name two panes claim is conflicted and makes the inventory incomplete; an unclaimed one is an
        /// orphan only when the claim walk saw everything, else unknown.
        /// </summary>
        public static (List<ControlLiveEntry> Rows, bool Complete) Join(IEnumerable<Daemon> observed, IEnumerable<Claim> claims, bool walkComplete, Func<DateTime?, bool> outdated)
        {
            Dictionary<string, List<Claim>> byName = claims.GroupBy(claim => Identity(claim.PaneId), StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            HashSet<string> conflicted = byName.Where(pair => pair.Value.Count > 1).Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<ControlLiveEntry> rows = new List<ControlLiveEntry>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Daemon daemon in observed)
            {
                string identity = Identity(daemon.Name);
                seen.Add(identity);
                rows.Add(Row(daemon.Name, daemon.Clients is null ? "unreadable" : "running", daemon, byName.GetValueOrDefault(identity), conflicted.Contains(identity), walkComplete, outdated));
            }

            foreach ((string name, List<Claim> group) in byName.Where(pair => !seen.Contains(pair.Key)))
            {
                rows.Add(Row(name, "absent", null, group, conflicted.Contains(name), walkComplete, outdated));
            }

            return (rows.OrderBy(row => row.Daemon, StringComparer.Ordinal).ToList(), walkComplete && conflicted.Count == 0);
        }

        internal static string Identity(string name)
        {
            return Guid.TryParse(name, out Guid id) ? id.Wire() : name;
        }

        private static ControlLiveEntry Row(string name, string observation, Daemon? daemon, List<Claim>? claims, bool isConflicted, bool walkComplete, Func<DateTime?, bool> outdated)
        {
            Claim? owner = isConflicted ? null : claims?.FirstOrDefault();
            string state = isConflicted ? Conflicted
                : owner is not null ? owner.PendingClose ? PendingClose : Claimed
                : Guid.TryParse(name, out _) ? walkComplete ? Orphan : Unknown
                : Foreign;
            return new ControlLiveEntry
            {
                Daemon = name,
                State = state,
                Observation = observation,
                Clients = daemon?.Clients,
                LeaderPid = daemon?.LeaderPid is > 0 ? daemon.LeaderPid : null,
                Outdated = daemon is { Clients: not null } && outdated(daemon.Created) ? true : null,
                WindowId = owner?.WindowId,
                WindowName = owner?.WindowName,
                WindowState = owner?.WindowState,
                WorkspaceId = owner?.WorkspaceId,
                WorkspaceName = owner?.WorkspaceName,
                SessionId = owner?.SessionId,
                SessionName = owner?.SessionName,
                Pane = owner?.Pane,
            };
        }

        /// <summary>
        /// agterm's ZmxPrunePolicy: null when no prune is safe; otherwise the orphans observed running with no clients. A
        /// closed window's panes are claimed with zero clients, so the count alone never makes an orphan.
        /// </summary>
        public static List<string>? NamesToPrune(IReadOnlyList<ControlLiveEntry> rows, bool complete)
        {
            return complete ? rows.Where(row => row.State == Orphan && row.Observation == "running" && row.Clients == 0).Select(row => row.Daemon).ToList() : null;
        }

        /// <summary>agterm's killRefusal: why a row may not be killed, null when it may.</summary>
        public static string? KillRefusal(ControlLiveEntry row)
        {
            return row.Observation switch
            {
                "absent" => row.Daemon + " is not running",
                "unreadable" => row.Daemon + " is unreadable; killing it could orphan a live daemon",
                _ => row.State switch
                {
                    Claimed => null,
                    PendingClose => row.Daemon + " belongs to a session waiting out its undo window",
                    Unknown or Conflicted => IncompleteInventory,
                    _ => row.Daemon + " is not claimed by that pane",
                },
            };
        }

        public static string? RestartRefusal(ControlLiveInventory inventory, string paneId, string windowId, string sessionId, string pane)
        {
            if (!inventory.InventoryComplete)
            {
                return IncompleteRestartInventory;
            }

            List<ControlLiveEntry> matches = inventory.Entries.Where(row => string.Equals(Identity(row.Daemon), Identity(paneId), StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1 || matches[0] is not { State: Claimed } owner
                || !string.Equals(owner.WindowId, windowId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(owner.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) || owner.Pane != pane)
            {
                return "the pane is not uniquely claimed by that window, session and side";
            }

            return owner.Observation == "absent" ? null : KillRefusal(owner);
        }
    }
}
