using System.Reflection;
using System.Runtime.CompilerServices;
using AgShell.App;
using AgShell.Core;

namespace AgShell.App.Tests
{
    public class LiveClaimsTests
    {
        [Theory]
        [InlineData("p0818bb88f341", false)]
        [InlineData("not-an-id", false)]
        [InlineData("6C55D514-E8EF-4A11-A15E-21B4ECC1E2DF", true)]
        public void IncompatiblePaneKeepsItsClaimAndPreventsPruning(string paneId, bool expectedComplete)
        {
            Guid sessionId = Guid.NewGuid();
            string neighbor = Guid.NewGuid().ToString();
            StateSnapshot snapshot = new StateSnapshot
            {
                Workspaces = new List<WorkspaceSnapshot>
                {
                    new WorkspaceSnapshot
                    {
                        Id = Guid.NewGuid(),
                        Sessions = new List<SessionSnapshot>
                        {
                            new SessionSnapshot { Id = sessionId, HasSplit = true, Cwd = @"C:\saved", PaneIds = new Dictionary<string, string> { ["left"] = paneId, ["right"] = neighbor } },
                        },
                    },
                },
            };
            Assert.False(PaneIdentityInventory.Run(snapshot).Changed);
            AppModel model = AppModel.FromSnapshot(snapshot);
            MainWindow window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            typeof(MainWindow).GetField("model", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, model);
            Type entryType = typeof(MainWindow).Assembly.GetType("AgShell.App.WindowEntry")!;
            object entry = Activator.CreateInstance(entryType)!;
            typeof(MainWindow).GetField("entry", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, entry);

            (List<LiveInventory.Claim> claims, bool complete) = ((List<LiveInventory.Claim>, bool))typeof(MainWindow)
                .GetMethod("LiveClaims", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { "fixture" })!;

            Assert.Equal(expectedComplete, complete);
            Assert.Equal(new[] { paneId, neighbor }, claims.Select(claim => claim.PaneId));
            Assert.All(claims, claim => Assert.Equal(sessionId.ToString().ToUpperInvariant(), claim.SessionId));
            StateSnapshot saved = model.ToSnapshot();
            Assert.Equal(paneId, saved.Workspaces[0].Sessions[0].PaneIds!["left"]);
            Assert.Equal(neighbor, saved.Workspaces[0].Sessions[0].PaneIds!["right"]);
            (List<AgShell.Protocol.ControlLiveEntry> rows, bool inventoryComplete) = LiveInventory.Join(
                new[] { new LiveInventory.Daemon(Guid.NewGuid().ToString(), 0, 123, null) }, claims, complete, _ => false);
            if (!expectedComplete)
            {
                Assert.False(inventoryComplete);
                Assert.Null(LiveInventory.NamesToPrune(rows, inventoryComplete));
            }
        }
    }
}
