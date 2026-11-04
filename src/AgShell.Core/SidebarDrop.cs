namespace AgShell.Core
{
    /// <summary>
    /// agterm's SidebarDrop: the index arithmetic of sidebar drag and drop (the drop-on-row redirect, the post-removal off-by-one,
    /// no-op detection), kept apart from the page so it is tested on its own. The page names the row and the side; the
    /// resolvers below turn that into the move the model makes.
    /// </summary>
    public static class SidebarDrop
    {
        /// <summary>A drop on a row rather than between rows, as AppKit's NSOutlineViewDropOnItemIndex.</summary>
        public const int OnItemIndex = -1;

        /// <summary>Keeps an accidental Explorer multi-selection from starting an unbounded number of shells.</summary>
        public const int MaximumDirectoryImportCount = 20;

        /// <summary>
        /// The workspace a folder drop opens in: only in the tree, the row's workspace, else the one the focus filter narrows
        /// the tree to, else the current one.
        /// </summary>
        public static Guid? ResolveDirectoryWorkspace(string sidebarMode, Guid? rowWorkspaceId, Guid? fallbackWorkspaceId, Guid? currentWorkspaceId)
        {
            if (sidebarMode != AppModel.TreeMode)
            {
                return null;
            }

            return rowWorkspaceId ?? fallbackWorkspaceId ?? currentWorkspaceId;
        }

        /// <summary>A session drop: the target workspace, the slot before removal to highlight, and the index after removal.</summary>
        public sealed record SessionResolution(Guid Workspace, int DropChildIndex, int Destination);

        /// <summary>Where a dragged session sits before anything is removed.</summary>
        public sealed record SessionSource(Guid Workspace, int Index);

        /// <summary>The row a session is dropped on: a workspace row takes the child index, a session row goes to its workspace.</summary>
        public abstract record SessionDropTarget
        {
            public sealed record WorkspaceRow(Guid Id, int SessionCount) : SessionDropTarget;

            public sealed record SessionRow(Guid Workspace, int SessionIndex, int SessionCount) : SessionDropTarget;
        }

        /// <summary>
        /// The move a dropped session makes, null when the order would not change. A move down within one workspace takes one
        /// off, as the slot shifts up once the session is out; one landing back in its own slot (an append of the last one
        /// included) is a no-op.
        /// </summary>
        public static SessionResolution? ResolveSession(Guid sourceWorkspace, int sourceIndex, SessionDropTarget target, int childIndex)
        {
            (Guid workspace, int targetCount, int dropChildIndex) = Place(target, childIndex);
            bool sameWorkspace = sourceWorkspace == workspace;
            int destination = dropChildIndex;
            if (dropChildIndex == OnItemIndex)
            {
                destination = targetCount;
            }
            else if (sameWorkspace && sourceIndex < dropChildIndex)
            {
                destination = dropChildIndex - 1;
            }

            if (sameWorkspace)
            {
                // The model takes the session out first and then clamps, so the slot it lands in is the clamped destination.
                int landed = Math.Max(0, Math.Min(destination, targetCount - 1));
                if (landed == sourceIndex)
                {
                    return null;
                }
            }

            return new SessionResolution(workspace, dropChildIndex, destination);
        }

        /// <summary>
        /// A drop of several sessions as one block: all are taken out, then the block goes in at the returned index. The
        /// sources are in the block's order; a block already standing at that place is a no-op.
        /// </summary>
        public static SessionResolution? ResolveSessions(IReadOnlyList<SessionSource> sources, SessionDropTarget target, int childIndex)
        {
            if (sources.Count == 0)
            {
                return null;
            }

            (Guid workspace, int targetCount, int dropChildIndex) = Place(target, childIndex);
            int rawDestination = dropChildIndex == OnItemIndex ? targetCount : dropChildIndex;
            List<int> inTarget = sources.Where(source => source.Workspace == workspace).Select(source => source.Index).Order().ToList();
            int destination = rawDestination - inTarget.Count(index => index < rawDestination);
            if (inTarget.Count == sources.Count
                && inTarget.Select((index, n) => index - n).Distinct().Count() == 1
                && destination == inTarget[0])
            {
                return null;
            }

            return new SessionResolution(workspace, dropChildIndex, destination);
        }

        /// <summary>A placement after or before an anchor session (session.move --after/--before), by the same drop math.</summary>
        public static SessionResolution? ResolveRelative(SessionSource source, Guid anchorWorkspace, int anchorIndex, int anchorCount, bool placeAfter)
        {
            SessionDropTarget target = new SessionDropTarget.SessionRow(anchorWorkspace, anchorIndex, anchorCount);
            return ResolveSession(source.Workspace, source.Index, target, placeAfter ? OnItemIndex : anchorIndex);
        }

        /// <summary>A workspace drop: the slot before removal to highlight and the index after removal.</summary>
        public sealed record WorkspaceResolution(int DropChildIndex, int Destination);

        /// <summary>
        /// Maps a slot among the shown workspaces onto the full list: above the first shown one it takes that one's index,
        /// otherwise the index right after the last shown one above the cursor, so a dragged workspace never jumps across those
        /// the focus filter hides. Unfiltered it is the slot itself; with nothing shown, 0.
        /// </summary>
        public static int WorkspaceInsertIndex(IReadOnlyList<int> visibleIndices, int slot)
        {
            if (visibleIndices.Count == 0)
            {
                return 0;
            }

            return slot > 0 ? visibleIndices[Math.Min(slot, visibleIndices.Count) - 1] + 1 : visibleIndices[0];
        }

        /// <summary>A workspace reorder, null when it would not change the order; <paramref name="count"/> is before removal.</summary>
        public static WorkspaceResolution? ResolveWorkspace(int sourceIndex, int count, int childIndex)
        {
            int dropChildIndex = childIndex == OnItemIndex ? count : childIndex;
            int destination = sourceIndex < dropChildIndex ? dropChildIndex - 1 : dropChildIndex;
            return sourceIndex == destination ? null : new WorkspaceResolution(dropChildIndex, destination);
        }

        private static (Guid Workspace, int Count, int DropChildIndex) Place(SessionDropTarget target, int childIndex)
        {
            return target switch
            {
                SessionDropTarget.WorkspaceRow row => (row.Id, row.SessionCount, childIndex),
                SessionDropTarget.SessionRow row => (row.Workspace, row.SessionCount, childIndex == OnItemIndex ? row.SessionIndex + 1 : childIndex),
                _ => throw new ArgumentOutOfRangeException(nameof(target)),
            };
        }
    }
}
