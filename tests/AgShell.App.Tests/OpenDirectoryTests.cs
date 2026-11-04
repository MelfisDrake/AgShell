using System.Reflection;
using System.Runtime.CompilerServices;
using AgShell.App;
using AgShell.Core;
using AgShell.Pty;

namespace AgShell.App.Tests
{
    public class OpenDirectoryTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DirectoryWaitsForReadinessEvenWhenWorkspaceExists(bool profilesDetected)
        {
            AppModel model = new AppModel();
            model.AddWorkspace();
            MainWindow window = Window(model, profilesDetected);

            Assert.False(Open(window));
            Assert.Empty(model.AllSessions);
            Assert.Null(model.SelectedSessionId);
        }

        [Fact]
        public void ColdStartQueuesAllDirectoriesUntilWindowSignalsReadiness()
        {
            AppModel model = new AppModel();
            model.AddWorkspace();
            MainWindow window = Window(model, false);
            Type libraryType = typeof(MainWindow).Assembly.GetType("AgShell.App.WindowLibrary")!;
            object library = RuntimeHelpers.GetUninitializedObject(libraryType);
            List<string> pending = new List<string>();
            libraryType.GetField("open", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(library, new List<MainWindow> { window });
            libraryType.GetField("pendingOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(library, pending);
            string[] directories = { @"C:\first folder", @"C:\second folder" };

            libraryType.GetMethod("OpenDirectories")!.Invoke(library, new object[] { directories });

            Assert.Equal(directories, pending);
            Assert.Empty(model.AllSessions);
            Assert.NotNull(typeof(MainWindow).GetField("Ready", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        }

        [Fact]
        public void ReadyWindowWithoutWorkspaceKeepsDirectoryPending()
        {
            MainWindow window = Window(new AppModel(), true);
            Set(window, "<SessionsReady>k__BackingField", true);

            Assert.False(Open(window));
        }

        [Fact]
        public void BypassingReadinessReproducesHistoricalMissingProfileException()
        {
            AppModel model = new AppModel();
            model.AddWorkspace();
            MainWindow window = Window(model, false);
            Set(window, "<SessionsReady>k__BackingField", true);

            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() => Open(window));
            InvalidOperationException cause = Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.Contains("DefaultProfile", cause.StackTrace);
            Assert.Contains("OpenSessionAt", cause.StackTrace);
            Assert.Empty(model.AllSessions);
        }

        private static MainWindow Window(AppModel model, bool profilesDetected)
        {
            // Exercise the real opening guard without a WPF window, WebView2 or shell processes.
            MainWindow window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            Set(window, "model", model);
            Set(window, "profiles", profilesDetected
                ? new ShellProfile[] { new ShellProfile("cmd", "Command Prompt", ShellKind.Cmd, "cmd.exe") }
                : Array.Empty<ShellProfile>());
            return window;
        }

        private static bool Open(MainWindow window)
        {
            return (bool)typeof(MainWindow).GetMethod("OpenSessionAt", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { @"C:\directory-regression" })!;
        }

        private static void Set(MainWindow window, string name, object value)
        {
            typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        }
    }
}
