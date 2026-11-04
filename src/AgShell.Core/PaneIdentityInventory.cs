using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>agterm's PaneIdentityInventory: a saved window whose panes have no ids gets them before anything claims its daemons.</summary>
    public static class PaneIdentityInventory
    {
        /// <summary>The window's pane ids after the upgrade, and whether any had to be made.</summary>
        public sealed record Upgrade(IReadOnlySet<string> Identities, bool Changed);

        public static Upgrade Run(StateSnapshot snapshot)
        {
            HashSet<string> identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            foreach (WorkspaceSnapshot workspace in snapshot.Workspaces)
            {
                for (int index = 0; index < workspace.Sessions.Count; index++)
                {
                    SessionSnapshot session = workspace.Sessions[index];
                    Dictionary<string, string> ids = new Dictionary<string, string>(session.PaneIds ?? new Dictionary<string, string>());
                    bool minted = Ensure(ids, Panes.Name(PaneRole.Main));
                    if (session.HasSplit)
                    {
                        minted |= Ensure(ids, Panes.Name(PaneRole.Split));
                        identities.Add(ids[Panes.Name(PaneRole.Split)]);
                    }

                    identities.Add(ids[Panes.Name(PaneRole.Main)]);
                    if (minted)
                    {
                        workspace.Sessions[index] = session with { PaneIds = ids };
                        changed = true;
                    }
                }
            }

            return new Upgrade(identities, changed);
        }

        private static bool Ensure(Dictionary<string, string> ids, string pane)
        {
            if (ids.TryGetValue(pane, out string? id) && !string.IsNullOrEmpty(id))
            {
                return false;
            }

            ids[pane] = Guid.NewGuid().Wire();
            return true;
        }
    }
}
