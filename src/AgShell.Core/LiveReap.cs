using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>agterm's ZmxReapPolicy: which daemons the launch ends without the user asking.</summary>
    public static class LiveReap
    {
        /// <summary>A daemon as the launch listing shows it; null clients for one that could not be read.</summary>
        public sealed record Listed(string Name, int? Clients);

        /// <summary>
        /// Null means a requested-live inventory was incomplete and no reap is safe. A deliberate non-live request claims no
        /// daemon and removes every zero-client daemon this app could have started.
        /// </summary>
        public static IReadOnlyList<string>? NamesToKill(IEnumerable<Listed> daemons, bool liveRequested, IReadOnlySet<string>? claimed)
        {
            if (liveRequested && claimed is null)
            {
                return null;
            }

            return daemons
                .Where(daemon => IsDaemonName(daemon.Name) && daemon.Clients == 0)
                .Where(daemon => !liveRequested || !claimed!.Contains(daemon.Name))
                .Select(daemon => daemon.Name)
                .ToList();
        }

        /// <summary>agterm's isDaemonName: only a name the app gives a pane, an upper-case UUID, as this is the one path that ends a shell unasked.</summary>
        public static bool IsDaemonName(string name)
        {
            return Guid.TryParseExact(name, "D", out Guid id) && id.Wire() == name;
        }
    }
}
