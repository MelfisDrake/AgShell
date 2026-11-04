using AgShell.Protocol;

namespace AgShell.Core.Tests
{
    public class LiveResetTests
    {
        private static readonly DateTime Cutoff = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Before = Cutoff.AddMinutes(-5);
        private static readonly DateTime After = Cutoff.AddMinutes(5);

        private static LiveReset.ClaimWalk Walk(bool complete, params (string Pane, string Session)[] claims)
        {
            return new LiveReset.ClaimWalk(claims.Select(claim => new LiveReset.Claim(claim.Pane, claim.Session)).ToList(), complete);
        }

        [Fact]
        public void SelectTakesClaimedDaemonsStartedBeforeTheCutoff()
        {
            LiveReset.Selection selection = LiveReset.Select(
                Walk(true, ("p1", "s1"), ("p2", "s1"), ("p3", "s2"), ("p4", "s3")),
                new List<LiveReset.Daemon> { new LiveReset.Daemon("p1", 10, Before), new LiveReset.Daemon("p2", 20, After), new LiveReset.Daemon("p3", 30, Before), new LiveReset.Daemon("orphan", 40, Before) },
                Cutoff);
            Assert.True(selection.InventoryComplete);
            Assert.Equal(new[] { "p1", "p3" }, selection.Targets.Select(target => target.PaneIdentity));
            Assert.All(selection.Targets, target => Assert.Equal(LiveReset.Outdated, target.Reason));
            Assert.Equal(10, selection.Targets[0].LeaderPid);
            Assert.Equal(2, selection.SessionCount);
            Assert.Equal(2, selection.OutdatedSessionCount);
        }

        [Fact]
        public void WithoutACutoffNothingIsSelected()
        {
            LiveReset.Selection selection = LiveReset.Select(Walk(true, ("p1", "s1")), new List<LiveReset.Daemon> { new LiveReset.Daemon("p1", 10, Before) }, null);
            Assert.Empty(selection.Targets);
        }

        [Fact]
        public void APaneClaimedTwiceOrAnUnreadWindowMakesTheInventoryIncomplete()
        {
            List<LiveReset.Daemon> daemons = new List<LiveReset.Daemon> { new LiveReset.Daemon("p1", 10, Before) };
            Assert.False(LiveReset.Select(Walk(true, ("p1", "s1"), ("P1", "s2")), daemons, Cutoff).InventoryComplete);
            Assert.False(LiveReset.Select(Walk(false, ("p1", "s1")), daemons, Cutoff).InventoryComplete);
        }

        [Fact]
        public void EquivalentUuidSpellingsCannotHideAConflictingResetClaim()
        {
            string pane = Guid.NewGuid().ToString();
            string alternate = Guid.Parse(pane).ToString("N");
            List<LiveReset.Daemon> daemons = new List<LiveReset.Daemon> { new LiveReset.Daemon(pane, 10, Before) };
            Assert.False(LiveReset.Select(Walk(true, (pane, "s1"), (alternate, "s2")), daemons, Cutoff).InventoryComplete);
            LiveReset.Target target = Assert.Single(LiveReset.Select(Walk(true, (alternate, "s1")), daemons, Cutoff).Targets);
            Assert.Equal(alternate, target.PaneIdentity);
            Assert.Equal(pane, target.Daemon);
            LiveReset.Marker marker = new LiveReset.Marker { Targets = new List<LiveReset.Target> { target } };
            Assert.Single(LiveReset.Narrow(marker, new HashSet<string> { pane }, daemons, Cutoff).Kill);
        }

        private static LiveReset.Target Target(string pane, string session, int leader, string reason = LiveReset.Outdated)
        {
            return new LiveReset.Target { PaneIdentity = pane, SessionId = session, Daemon = pane, LeaderPid = leader, Reason = reason };
        }

        [Fact]
        public void NarrowOnlyKillsWhatStillQualifies()
        {
            LiveReset.Marker marker = new LiveReset.Marker
            {
                Targets = new List<LiveReset.Target> { Target("kill", "s1", 1), Target("gone", "s1", 2), Target("unclaimed", "s2", 3), Target("replaced", "s3", 4), Target("newer", "s4", 5), Target("unsupervised", "s5", 6, "unsupervised") },
            };
            HashSet<string> claimed = new HashSet<string>(new[] { "kill", "gone", "replaced", "newer", "unsupervised" });
            List<LiveReset.Daemon> daemons = new List<LiveReset.Daemon>
            {
                new LiveReset.Daemon("kill", 1, Before), new LiveReset.Daemon("unclaimed", 3, Before), new LiveReset.Daemon("replaced", 44, Before),
                new LiveReset.Daemon("newer", 5, After), new LiveReset.Daemon("unsupervised", 6, Before),
            };
            LiveReset.Narrowed narrowed = LiveReset.Narrow(marker, claimed, daemons, Cutoff);
            Assert.False(narrowed.InventoryFailed);
            Assert.Equal(new[] { "kill" }, narrowed.Kill.Select(target => target.Daemon));
            Dictionary<string, LiveReset.Disposition> byPane = narrowed.Dispositions.ToDictionary(pair => pair.Key.PaneIdentity, pair => pair.Value);
            Assert.Equal(LiveReset.Disposition.Gone, byPane["gone"]);
            Assert.Equal(LiveReset.Disposition.Skipped, byPane["unclaimed"]);
            Assert.Equal(LiveReset.Disposition.Skipped, byPane["replaced"]);
            Assert.Equal(LiveReset.Disposition.Skipped, byPane["newer"]);
            Assert.Equal(LiveReset.Disposition.Skipped, byPane["unsupervised"]);
        }

        [Fact]
        public void AFailedListingOrIncompleteClaimsSkipsEverything()
        {
            LiveReset.Marker marker = new LiveReset.Marker { Targets = new List<LiveReset.Target> { Target("a", "s1", 1) } };
            LiveReset.Narrowed unlisted = LiveReset.Narrow(marker, new HashSet<string> { "a" }, null, Cutoff);
            Assert.True(unlisted.InventoryFailed);
            Assert.Empty(unlisted.Kill);
            LiveReset.Narrowed unclaimed = LiveReset.Narrow(marker, null, new List<LiveReset.Daemon> { new LiveReset.Daemon("a", 1, Before) }, Cutoff);
            Assert.False(unclaimed.InventoryFailed);
            Assert.Equal(LiveReset.Disposition.Skipped, unclaimed.Dispositions.Single().Value);
        }

        [Fact]
        public void OutcomeCountsSessionsResetOnlyWhenEveryPaneWent()
        {
            LiveReset.Narrowed narrowed = new LiveReset.Narrowed(new Dictionary<LiveReset.Target, LiveReset.Disposition>
            {
                [Target("a", "s1", 1)] = LiveReset.Disposition.Kill,
                [Target("b", "s1", 2)] = LiveReset.Disposition.Gone,
                [Target("c", "s2", 3)] = LiveReset.Disposition.Kill,
                [Target("d", "s2", 4)] = LiveReset.Disposition.Skipped,
                [Target("e", "s3", 5)] = LiveReset.Disposition.Kill,
            }, false);
            LiveResetOutcome outcome = LiveReset.Outcome(narrowed, new HashSet<int> { 5 }, false);
            Assert.Equal(5, outcome.Panes.Confirmed);
            Assert.Equal(2, outcome.Panes.Killed);
            Assert.Equal(1, outcome.Panes.Gone);
            Assert.Equal(1, outcome.Panes.Skipped);
            Assert.Equal(new[] { "e" }, outcome.Unconfirmed);
            Assert.Equal(3, outcome.Sessions.Affected);
            Assert.Equal(1, outcome.Sessions.Reset);
            Assert.Equal(2, outcome.Sessions.Partial);
            Assert.Equal(1, outcome.Sessions.Unconfirmed);
            Assert.NotNull(LiveReset.NotificationText(outcome));
        }

        [Fact]
        public void AFullResetIsSilent()
        {
            LiveReset.Narrowed narrowed = new LiveReset.Narrowed(new Dictionary<LiveReset.Target, LiveReset.Disposition> { [Target("a", "s1", 1)] = LiveReset.Disposition.Kill }, false);
            Assert.Null(LiveReset.NotificationText(LiveReset.Outcome(narrowed, new HashSet<int>(), false)));
        }

        [Fact]
        public void TheDialogBodyNamesOutdatedSessionsAndReadsInEnglishForAgshellctl()
        {
            Assert.Equal(
                "2 live sessions will be reset. 1 of them predates the last Live sessions update and will be recreated on the current one. AgShell quits and reopens itself right away with your sessions and layout. Commands that were running in those sessions are started again where possible; other work running in them stops, and agent conversations may need to be resumed by hand.",
                Strings.InEnglish(() => LiveReset.DialogBody(2, 1)));
            Assert.StartsWith("1 live session will be reset. It predates ", Strings.InEnglish(() => LiveReset.DialogBody(1, 1)));
            Assert.StartsWith("3 live sessions will be reset. They all predate ", Strings.InEnglish(() => LiveReset.DialogBody(3, 3)));
            Assert.StartsWith("Будет сброшено живых сессий: 3. 2 из них появились", LiveReset.DialogBody(3, 2));
        }

        [Fact]
        public void TheMenuShowsOnlyWhenLiveIsConfiguredAndRunning()
        {
            Assert.True(LiveReset.MenuVisible("live", "live"));
            Assert.False(LiveReset.MenuVisible("live", "none"));
            Assert.False(LiveReset.MenuVisible("rerun", "live"));
        }

        private static string TempDirectory()
        {
            string directory = Path.Combine(Path.GetTempPath(), "agshell-live-reset-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        [Fact]
        public void TheMarkerIsConsumedOnceAndItsFileRenamedFirst()
        {
            string directory = TempDirectory();
            try
            {
                LiveResetMarkerStore store = new LiveResetMarkerStore(directory);
                Assert.Null(store.Consume());
                store.Write(new LiveReset.Marker { CreatedAt = 1, Targets = new List<LiveReset.Target> { Target("p", "s", 7) } });
                string text = File.ReadAllText(Path.Combine(directory, LiveReset.MarkerFilename));
                Assert.Contains("\"sessionID\"", text);
                Assert.Contains("\"leaderPID\": 7", text);
                LiveReset.Marker? consumed = store.Consume();
                Assert.Equal(7, consumed?.Targets.Single().LeaderPid);
                Assert.False(File.Exists(Path.Combine(directory, LiveReset.MarkerFilename)));
                Assert.True(File.Exists(Path.Combine(directory, LiveReset.ConsumedFilename)));
                store.RemoveConsumed();
                Assert.Null(store.Consume());
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void AVersionOneTargetReadsAsUnsupervisedAndAnUnknownVersionIsDiscarded()
        {
            string directory = TempDirectory();
            try
            {
                LiveResetMarkerStore store = new LiveResetMarkerStore(directory);
                string marker = Path.Combine(directory, LiveReset.MarkerFilename);
                File.WriteAllText(marker, "{\"version\":1,\"createdAt\":1,\"targets\":[{\"paneIdentity\":\"p\",\"sessionID\":\"s\",\"daemon\":\"p\",\"leaderPID\":3}]}");
                Assert.Equal("unsupervised", store.Consume()?.Targets.Single().Reason);
                File.WriteAllText(marker, "{\"version\":9,\"createdAt\":1,\"targets\":[]}");
                Assert.Throws<InvalidDataException>(() => store.Consume());
                Assert.False(File.Exists(marker));
                Assert.False(File.Exists(Path.Combine(directory, LiveReset.ConsumedFilename)));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void TheBuildRecordMovesTheCutoffOnlyWhenTheIdChanges()
        {
            string directory = TempDirectory();
            try
            {
                DateTime first = new DateTime(2026, 10, 26, 10, 0, 0, 700, DateTimeKind.Utc);
                Assert.Null(LiveBuildRecord.LaunchCutoff(null, directory, first));
                Assert.Null(LiveBuildRecord.LaunchCutoff("  ", directory, first));
                Assert.Equal(first.AddMilliseconds(-700), LiveBuildRecord.LaunchCutoff("a", directory, first));
                Assert.Equal(first.AddMilliseconds(-700), LiveBuildRecord.LaunchCutoff("a", directory, first.AddDays(1)));
                Assert.Equal(first.AddDays(2).AddMilliseconds(-700), LiveBuildRecord.LaunchCutoff("b", directory, first.AddDays(2)));
                Assert.True(File.Exists(Path.Combine(directory, LiveBuildRecord.Filename)));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
