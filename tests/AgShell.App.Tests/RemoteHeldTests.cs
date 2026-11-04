using System.Reflection;
using System.Runtime.CompilerServices;
using AgShell.App;
using AgShell.Core;

namespace AgShell.App.Tests
{
    public class RemoteHeldTests
    {
        [Theory]
        [InlineData("held", true)]
        [InlineData("disconnected", false)]
        public void HeldClassificationFollowsTheRemoteDaemonThroughSwap(string state, bool expected)
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("fixture");
            string daemon = Guid.NewGuid().ToString();
            Session session = model.AddRemoteSession(workspace.Id, "fixture", Guid.NewGuid(), "remote", true,
                mainDaemon: daemon, splitDaemon: Guid.NewGuid().ToString());
            MainWindow window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            Set(window, "model", model);
            Set(window, "remoteStates", new Dictionary<(Guid, string), string> { [(session.Id, daemon)] = state });
            Type keyType = typeof(MainWindow).Assembly.GetType("AgShell.App.PaneKey")!;
            Set(window, "commandHeld", Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(keyType))!);

            Assert.Equal(expected, window.IsHeld(session.Id, PaneRole.Main));
            Assert.False(window.IsHeld(session.Id, PaneRole.Split));
            model.SwapPanes(session.Id);
            Assert.False(window.IsHeld(session.Id, PaneRole.Main));
            Assert.Equal(expected, window.IsHeld(session.Id, PaneRole.Split));
        }

        private static void Set(MainWindow window, string field, object value)
        {
            typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        }
    }
}
