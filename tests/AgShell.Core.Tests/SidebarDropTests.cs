using static AgShell.Core.SidebarDrop;

namespace AgShell.Core.Tests
{
    /// <summary>agterm's SidebarDropTests, case for case.</summary>
    public class SidebarDropTests
    {
        private static readonly Guid A = Guid.NewGuid();
        private static readonly Guid B = Guid.NewGuid();
        private const int OnItem = OnItemIndex;

        [Fact]
        public void DirectoryDropTargetsExplicitRowWorkspace()
        {
            Assert.Equal(B, ResolveDirectoryWorkspace(AppModel.TreeMode, B, A, A));
        }

        [Fact]
        public void EmptyDirectoryDropPrefersTheFallbackWorkspace()
        {
            Assert.Equal(B, ResolveDirectoryWorkspace(AppModel.TreeMode, null, B, A));
        }

        [Fact]
        public void EmptyDirectoryDropFallsBackToCurrentWorkspace()
        {
            Assert.Equal(A, ResolveDirectoryWorkspace(AppModel.TreeMode, null, null, A));
        }

        [Fact]
        public void DirectoryDropIsRejectedInFlaggedMode()
        {
            Assert.Null(ResolveDirectoryWorkspace(AppModel.FlaggedMode, B, null, A));
        }

        [Fact]
        public void SessionSameWorkspaceUpDropOnRow()
        {
            Assert.Equal(new SessionResolution(A, 1, 1), ResolveSession(A, 2, new SessionDropTarget.SessionRow(A, 0, 3), OnItem));
        }

        [Fact]
        public void SessionSameWorkspaceDownPastMiddleRowAppliesMinusOne()
        {
            Assert.Equal(new SessionResolution(A, 3, 2), ResolveSession(A, 0, new SessionDropTarget.SessionRow(A, 2, 4), OnItem));
        }

        [Fact]
        public void SessionSameWorkspaceDownBetweenRowsAppliesMinusOne()
        {
            Assert.Equal(new SessionResolution(A, 3, 2), ResolveSession(A, 0, new SessionDropTarget.WorkspaceRow(A, 4), 3));
        }

        [Fact]
        public void SessionSameWorkspaceUpBetweenRowsNoAdjustment()
        {
            Assert.Equal(new SessionResolution(A, 1, 1), ResolveSession(A, 2, new SessionDropTarget.WorkspaceRow(A, 3), 1));
        }

        [Fact]
        public void SessionSameWorkspaceNoOpWhenDroppedIntoOwnSlot()
        {
            Assert.Null(ResolveSession(A, 1, new SessionDropTarget.WorkspaceRow(A, 3), 2));
        }

        [Fact]
        public void SessionSameWorkspaceAppendAlreadyLastIsNoOp()
        {
            Assert.Null(ResolveSession(A, 2, new SessionDropTarget.WorkspaceRow(A, 3), OnItem));
        }

        [Fact]
        public void SessionSameWorkspaceAppendFromMiddleMovesToEnd()
        {
            Assert.Equal(new SessionResolution(A, OnItem, 3), ResolveSession(A, 0, new SessionDropTarget.WorkspaceRow(A, 3), OnItem));
        }

        [Fact]
        public void SessionCrossWorkspaceInsertAtMiddleSlot()
        {
            Assert.Equal(new SessionResolution(B, 2, 2), ResolveSession(A, 0, new SessionDropTarget.WorkspaceRow(B, 3), 2));
        }

        [Fact]
        public void SessionCrossWorkspaceDropOnRowInsertsAfter()
        {
            Assert.Equal(new SessionResolution(B, 2, 2), ResolveSession(A, 0, new SessionDropTarget.SessionRow(B, 1, 3), OnItem));
        }

        [Fact]
        public void SessionCrossWorkspaceAppendOntoHeader()
        {
            Assert.Equal(new SessionResolution(B, OnItem, 2), ResolveSession(A, 0, new SessionDropTarget.WorkspaceRow(B, 2), OnItem));
        }

        [Fact]
        public void BatchSessionCrossWorkspaceInsertAtMiddleSlot()
        {
            SessionResolution? move = ResolveSessions(new[] { new SessionSource(A, 0), new SessionSource(A, 1) }, new SessionDropTarget.WorkspaceRow(B, 3), 2);
            Assert.Equal(new SessionResolution(B, 2, 2), move);
        }

        [Fact]
        public void BatchSessionSameWorkspaceDownAdjustsForEveryRemovedSourceBeforeDrop()
        {
            SessionResolution? move = ResolveSessions(new[] { new SessionSource(A, 0), new SessionSource(A, 1) }, new SessionDropTarget.WorkspaceRow(A, 5), 4);
            Assert.Equal(new SessionResolution(A, 4, 2), move);
        }

        [Fact]
        public void BatchSessionSameWorkspaceContiguousDropInsideSelectionIsNoOp()
        {
            Assert.Null(ResolveSessions(new[] { new SessionSource(A, 1), new SessionSource(A, 2) }, new SessionDropTarget.WorkspaceRow(A, 4), 2));
        }

        [Fact]
        public void BatchSessionSameWorkspaceNonContiguousDropAtFirstSourceCanStillMove()
        {
            SessionResolution? move = ResolveSessions(new[] { new SessionSource(A, 1), new SessionSource(A, 3) }, new SessionDropTarget.WorkspaceRow(A, 5), 1);
            Assert.Equal(new SessionResolution(A, 1, 1), move);
        }

        [Fact]
        public void BatchSessionMixedTargetAdjustsForMovedRowsAlreadyInTarget()
        {
            SessionResolution? move = ResolveSessions(new[] { new SessionSource(A, 0), new SessionSource(B, 0) }, new SessionDropTarget.WorkspaceRow(B, 3), 2);
            Assert.Equal(new SessionResolution(B, 2, 1), move);
        }

        [Fact]
        public void RelativeSameWorkspaceAfterAnchor()
        {
            Assert.Equal(new SessionResolution(A, 1, 1), ResolveRelative(new SessionSource(A, 2), A, 0, 3, placeAfter: true));
        }

        [Fact]
        public void RelativeSameWorkspaceAfterAnchorDownwardAppliesMinusOne()
        {
            Assert.Equal(new SessionResolution(A, 3, 2), ResolveRelative(new SessionSource(A, 0), A, 2, 4, placeAfter: true));
        }

        [Fact]
        public void RelativeSameWorkspaceBeforeAnchor()
        {
            Assert.Equal(new SessionResolution(A, 1, 1), ResolveRelative(new SessionSource(A, 2), A, 1, 3, placeAfter: false));
        }

        [Fact]
        public void RelativeCrossWorkspaceAfterAnchorNoAdjustment()
        {
            Assert.Equal(new SessionResolution(B, 2, 2), ResolveRelative(new SessionSource(A, 0), B, 1, 3, placeAfter: true));
        }

        [Fact]
        public void RelativeCrossWorkspaceBeforeAnchorNoAdjustment()
        {
            Assert.Equal(new SessionResolution(B, 1, 1), ResolveRelative(new SessionSource(A, 0), B, 1, 3, placeAfter: false));
        }

        [Fact]
        public void RelativeAnchorIsSourceBeforeIsNoOp()
        {
            Assert.Null(ResolveRelative(new SessionSource(A, 1), A, 1, 3, placeAfter: false));
        }

        [Fact]
        public void RelativeAnchorIsSourceAfterIsNoOp()
        {
            Assert.Null(ResolveRelative(new SessionSource(A, 1), A, 1, 3, placeAfter: true));
        }

        [Fact]
        public void WorkspaceMoveUp()
        {
            Assert.Equal(new WorkspaceResolution(1, 1), ResolveWorkspace(2, 3, 1));
        }

        [Fact]
        public void WorkspaceMoveDownAppliesMinusOne()
        {
            Assert.Equal(new WorkspaceResolution(2, 1), ResolveWorkspace(0, 3, 2));
        }

        [Fact]
        public void WorkspaceMoveToBottomAppend()
        {
            Assert.Equal(new WorkspaceResolution(3, 2), ResolveWorkspace(0, 3, OnItem));
        }

        [Fact]
        public void WorkspaceMoveNoOpIntoOwnSlot()
        {
            Assert.Null(ResolveWorkspace(1, 3, 2));
        }

        [Fact]
        public void WorkspaceMoveNoOpAppendAlreadyLast()
        {
            Assert.Null(ResolveWorkspace(2, 3, OnItem));
        }

        [Fact]
        public void WorkspaceInsertIndexPassesTheSlotThroughOnAnUnfilteredTree()
        {
            int[] all = { 0, 1, 2, 3 };
            Assert.All(Enumerable.Range(0, 5), slot => Assert.Equal(slot, WorkspaceInsertIndex(all, slot)));
        }

        [Fact]
        public void WorkspaceInsertIndexAboveTheFirstVisibleRowTakesThatRowsIndex()
        {
            Assert.Equal(1, WorkspaceInsertIndex(new[] { 1, 3 }, 0));
        }

        [Fact]
        public void WorkspaceInsertIndexLandsJustAfterTheLastRowAboveTheCursor()
        {
            Assert.Equal(2, WorkspaceInsertIndex(new[] { 1, 3 }, 1));
            Assert.Equal(4, WorkspaceInsertIndex(new[] { 1, 3 }, 2));
        }

        [Fact]
        public void WorkspaceInsertIndexClampsASlotPastTheLastVisibleRow()
        {
            Assert.Equal(3, WorkspaceInsertIndex(new[] { 0, 2 }, 9));
        }

        [Fact]
        public void WorkspaceInsertIndexWithNoVisibleRows()
        {
            Assert.Equal(0, WorkspaceInsertIndex(Array.Empty<int>(), 0));
            Assert.Equal(0, WorkspaceInsertIndex(Array.Empty<int>(), 3));
        }

        [Fact]
        public void FilteredWorkspaceDropBeforeTheFirstVisibleRowKeepsTheHiddenWorkspaceAhead()
        {
            int insert = WorkspaceInsertIndex(new[] { 1, 3 }, 0);
            Assert.Equal(new WorkspaceResolution(1, 1), ResolveWorkspace(3, 4, insert));
        }

        [Fact]
        public void FilteredWorkspaceDropOntoItsOwnSlotIsStillANoOp()
        {
            int insert = WorkspaceInsertIndex(new[] { 1, 3 }, 1);
            Assert.Null(ResolveWorkspace(1, 4, insert));
        }
    }
}
