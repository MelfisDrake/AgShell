using AgShell.Protocol;

namespace AgShell.Core.Tests
{
    public class LiveReapTests
    {
        [Fact]
        public void ReapUsesCompleteLiveInventoryAndZeroClientAppNamesOnly()
        {
            string known = Guid.NewGuid().Wire();
            string orphan = Guid.NewGuid().Wire();
            List<LiveReap.Listed> daemons = new List<LiveReap.Listed>
            {
                new LiveReap.Listed(known, 0),
                new LiveReap.Listed(orphan, 0),
                new LiveReap.Listed(Guid.NewGuid().Wire(), 1),
                new LiveReap.Listed("other", 0),
                new LiveReap.Listed(Guid.NewGuid().Wire(), null),
            };

            Assert.Equal(new[] { orphan }, LiveReap.NamesToKill(daemons, true, new HashSet<string> { known }));
            Assert.Null(LiveReap.NamesToKill(daemons, true, null));
            Assert.Equal(new[] { known, orphan }, LiveReap.NamesToKill(daemons, false, null));
        }

        [Fact]
        public void ReapRefusesEveryNameOutsideTheGeneratedDaemonShape()
        {
            string ours = Guid.NewGuid().Wire();
            List<LiveReap.Listed> daemons = new List<LiveReap.Listed>
            {
                new LiveReap.Listed(ours, 0),
                new LiveReap.Listed("agshell-notes", 0),
                new LiveReap.Listed(ours.ToLowerInvariant(), 0),
                new LiveReap.Listed(ours.Replace("-", string.Empty), 0),
                new LiveReap.Listed(ours + "-2", 0),
            };

            Assert.Equal(new[] { ours }, LiveReap.NamesToKill(daemons, true, new HashSet<string>()));
        }
    }
}
