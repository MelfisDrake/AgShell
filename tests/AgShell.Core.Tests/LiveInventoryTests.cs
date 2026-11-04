using AgShell.Protocol;

namespace AgShell.Core.Tests
{
    public class LiveInventoryTests
    {
        private static readonly string A = "AAAAAAAA-0000-0000-0000-000000000001";
        private static readonly string B = "BBBBBBBB-0000-0000-0000-000000000002";
        private static readonly string C = "CCCCCCCC-0000-0000-0000-000000000003";

        private static LiveInventory.Claim Claim(string id, string pane = "left", bool pending = false, string session = "S1")
        {
            return new LiveInventory.Claim(id, pane, pending, "W1", "window 1", "open", "WS", "work", session, "name");
        }

        private static (List<ControlLiveEntry> Rows, bool Complete) Join(IEnumerable<LiveInventory.Daemon> observed, IEnumerable<LiveInventory.Claim> claims, bool walked = true)
        {
            return LiveInventory.Join(observed, claims, walked, _ => false);
        }

        [Fact]
        public void TheRowsAreTheUnionOfDaemonsAndClaims()
        {
            (List<ControlLiveEntry> rows, bool complete) = Join(
                new[] { new LiveInventory.Daemon(A, 1, 10, null), new LiveInventory.Daemon(B, null, null, null), new LiveInventory.Daemon(C, 0, 12, null) },
                new[] { Claim(A), Claim(B, "right"), Claim("DDDDDDDD-0000-0000-0000-000000000004", pending: true) });

            Assert.True(complete);
            Assert.Equal(new[] { (A, "claimed", "running"), (B, "claimed", "unreadable"), (C, "orphan", "running"), ("DDDDDDDD-0000-0000-0000-000000000004", "pendingClose", "absent") },
                rows.Select(row => (row.Daemon, row.State, row.Observation)));
            Assert.Equal("right", rows[1].Pane);
        }

        [Fact]
        public void AnUnclaimedDaemonIsUnknownWhenTheWalkWasIncomplete()
        {
            (List<ControlLiveEntry> rows, bool complete) = Join(new[] { new LiveInventory.Daemon(C, 0, 12, null) }, Array.Empty<LiveInventory.Claim>(), walked: false);
            Assert.False(complete);
            Assert.Equal("unknown", Assert.Single(rows).State);
            Assert.Null(LiveInventory.NamesToPrune(rows, complete));
        }

        [Fact]
        public void TwoClaimsOnOneDaemonAreConflictedAndNothingIsPruned()
        {
            (List<ControlLiveEntry> rows, bool complete) = Join(new[] { new LiveInventory.Daemon(A, 0, 10, null), new LiveInventory.Daemon(C, 0, 12, null) }, new[] { Claim(A, session: "S1"), Claim(A, session: "S2") });
            Assert.False(complete);
            Assert.Equal("conflicted", rows[0].State);
            Assert.Null(rows[0].SessionId);
            Assert.Equal(LiveInventory.IncompleteInventory, LiveInventory.KillRefusal(rows[0]));
            Assert.Null(LiveInventory.NamesToPrune(rows, complete));
        }

        [Fact]
        public void OnlyADetachedRunningOrphanIsPruned()
        {
            (List<ControlLiveEntry> rows, bool complete) = Join(
                new[] { new LiveInventory.Daemon(A, 1, 10, null), new LiveInventory.Daemon(B, null, null, null), new LiveInventory.Daemon(C, 0, 12, null) },
                Array.Empty<LiveInventory.Claim>());
            Assert.Equal(new[] { C }, LiveInventory.NamesToPrune(rows, complete));
        }

        [Theory]
        [InlineData("N")]
        [InlineData("B")]
        [InlineData("P")]
        [InlineData("X")]
        public void AnotherSpellingOfTheSameUuidCannotMakeAClaimedDaemonAnOrphan(string format)
        {
            string saved = Guid.Parse(A).ToString(format);
            LiveInventory.Claim claim = Claim(saved);
            (List<ControlLiveEntry> rows, bool complete) = Join(new[] { new LiveInventory.Daemon(A, 0, 10, null) }, new[] { claim });

            Assert.True(complete);
            Assert.Equal("claimed", Assert.Single(rows).State);
            Assert.Empty(LiveInventory.NamesToPrune(rows, complete)!);
            Assert.Equal(saved, claim.PaneId);

            (List<ControlLiveEntry> conflicts, bool conflictComplete) = Join(new[] { new LiveInventory.Daemon(A, 0, 10, null) },
                new[] { claim, Claim(A, session: "S2") });
            Assert.False(conflictComplete);
            Assert.Equal("conflicted", Assert.Single(conflicts).State);
            Assert.Null(LiveInventory.NamesToPrune(conflicts, conflictComplete));
        }

        [Fact]
        public void FailedRestartRequiresACompleteInventoryAndItsUniqueOwnerEvenWithoutADaemon()
        {
            (List<ControlLiveEntry> rows, bool complete) = Join(Array.Empty<LiveInventory.Daemon>(), new[] { Claim(A) });
            ControlLiveInventory inventory = new ControlLiveInventory
            {
                Entries = rows,
                InventoryComplete = complete,
                Restore = new ControlRestoreStatus { Configured = "live", RequestedAtLaunch = "live", Active = "live" },
            };
            Assert.Null(LiveInventory.RestartRefusal(inventory, A, "W1", "S1", "left"));
            Assert.NotNull(LiveInventory.RestartRefusal(inventory, A, "W1", "S2", "left"));
            Assert.NotNull(LiveInventory.RestartRefusal(inventory, A, "W1", "S1", "right"));
            Assert.NotNull(LiveInventory.RestartRefusal(inventory with { InventoryComplete = false }, A, "W1", "S1", "left"));

            (rows, complete) = Join(new[] { new LiveInventory.Daemon(A, 0, 10, null) }, new[] { Claim(A), Claim(Guid.Parse(A).ToString("N"), session: "S2") });
            Assert.NotNull(LiveInventory.RestartRefusal(inventory with { Entries = rows, InventoryComplete = complete }, A, "W1", "S1", "left"));
        }

        [Fact]
        public void AKillIsRefusedAsAgtermsKillRefusal()
        {
            (List<ControlLiveEntry> rows, _) = Join(new[] { new LiveInventory.Daemon(B, null, null, null) }, new[] { Claim(A), Claim(B), Claim(C, pending: true) });
            Assert.Equal(A + " is not running", LiveInventory.KillRefusal(rows[0]));
            Assert.Equal(B + " is unreadable; killing it could orphan a live daemon", LiveInventory.KillRefusal(rows[1]));
            Assert.Equal(C + " is not running", LiveInventory.KillRefusal(rows[2]));

            (List<ControlLiveEntry> running, _) = Join(new[] { new LiveInventory.Daemon(A, 0, 1, null), new LiveInventory.Daemon(C, 0, 2, null) }, new[] { Claim(A), Claim(C, pending: true) });
            Assert.Null(LiveInventory.KillRefusal(running[0]));
            Assert.Equal(C + " belongs to a session waiting out its undo window", LiveInventory.KillRefusal(running[1]));
        }
    }
}
