using System.Collections;
using System.Reflection;
using AgShell.App;
using AgShell.Core;
using AgShell.Pty;

namespace AgShell.App.Tests
{
    public class LiveExitTests
    {
        [Theory]
        [InlineData(false, -1, true)]
        [InlineData(true, -1, false)]
        [InlineData(true, 0, false)]
        public void TransportLossAndAnExplicitShellExitHaveDifferentOutcomes(bool processEnded, int code, bool expectedLoss)
        {
            Assembly assembly = typeof(MainWindow).Assembly;
            Type clientType = assembly.GetType("AgShell.App.LiveClient")!;
            object client = Activator.CreateInstance(clientType)!;
            object pane = clientType.GetMethod("Open")!.Invoke(client, new object[] { Guid.NewGuid().ToString(), new PtyStartOptions { CommandLine = "cmd.exe" } })!;
            Type hostType = assembly.GetType("AgShell.App.SessionHost")!;
            object host = Activator.CreateInstance(hostType)!;
            Type keyType = assembly.GetType("AgShell.App.PaneKey")!;
            object key = Activator.CreateInstance(keyType, Guid.NewGuid(), PaneRole.Main)!;
            hostType.GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, new[] { key, pane, true });
            IDictionary shells = (IDictionary)hostType.GetField("shells", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            object running = shells.Values.Cast<object>().Single();

            if (processEnded)
            {
                pane.GetType().GetMethod("RaiseExited", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pane, new object[] { code, true });
            }
            else
            {
                clientType.GetMethod("Abandon")!.Invoke(client, new object[] { code });
            }

            Assert.Equal(expectedLoss, hostType.GetMethod("LostLivePane")!.Invoke(host, new[] { running }));
            Assert.NotNull(hostType.GetMethod("KeyOf")!.Invoke(host, new[] { running }));
            ((IDisposable)host).Dispose();
            ((IDisposable)client).Dispose();
        }
    }
}
