using AgShell.Protocol;

namespace AgShell.Core.Tests
{
    public class TitlebarCompositionTests
    {
        private static AppSettings Visible(string mode) => new AppSettings
        {
            ToolbarMode = mode,
            ShownInterfaceElements = new List<string> { "workspaceName", "windowName", "sessionContext", "remoteHost" },
        };

        [Fact]
        public void IdentityUsesFocusedMainOrSplitButNeverScratch()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace(" home ");
            Session session = model.AddSession(workspace.Id, "cmd", "/main", name: "task");
            model.UpdateTitle(session.Id, "main program");
            model.SetSplit(session.Id, true);
            model.UpdateTitle(session.Id, "split program", PaneRole.Split);
            model.UpdateCwd(session.Id, "/split", PaneRole.Split);
            model.SetScratch(session.Id, true);
            TitlebarComposition result = TitlebarComposition.Compose(session, workspace, "window", Visible("normal"));
            Assert.Equal("home — task — window", result.Title);
            Assert.Equal("split program", result.Subtitle);
            model.FocusPane(session.Id, PaneRole.Main);
            Assert.Equal("main program", TitlebarComposition.Compose(session, workspace, null, Visible("normal")).Subtitle);
        }

        [Fact]
        public void ContextReplacesDetailAndFollowsHostInCompact()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("home");
            Session session = model.AddRemoteSession(workspace.Id, "host", Guid.NewGuid(), "task", split: false);
            model.ApplyRemoteContext(session.Id, "remote");
            model.SetContext(session.Id, "local");
            Assert.Equal("local", TitlebarComposition.Compose(session, null, null, Visible("normal")).Subtitle);
            TitlebarComposition compact = TitlebarComposition.Compose(session, null, null, Visible("compact"));
            Assert.Equal("host", compact.Host);
            Assert.Equal(" · local", compact.Tail);
            Assert.Equal("", compact.Subtitle);
            model.SetContext(session.Id, null);
            Assert.Equal(" · remote", TitlebarComposition.Compose(session, null, null, Visible("compact")).Tail);
        }

        [Fact]
        public void HiddenAndVisibilityGatesDoNotChangeDefaults()
        {
            AppModel model = new AppModel();
            Session session = model.AddSession(model.AddWorkspace("home").Id, "cmd", "/main", name: "task");
            model.SetContext(session.Id, "context");
            Assert.Equal(new TitlebarComposition("", null, "", ""), TitlebarComposition.Compose(session, null, "window", Visible("hidden")));
            AppSettings settings = Visible("normal");
            settings.HiddenInterfaceElements = new List<string> { "sessionName", "sessionContext", "windowName" };
            settings.ShownInterfaceElements = new List<string>();
            TitlebarComposition result = TitlebarComposition.Compose(session, new Workspace(Guid.NewGuid(), "workspace"), "window", settings);
            Assert.Equal("", result.Title);
            Assert.Equal("/main", result.Subtitle);
            Assert.Equal("compact", new AppSettings().EffectiveToolbarMode);
            Assert.Equal("AgShell — window", TitlebarComposition.Compose(null, new Workspace(Guid.NewGuid(), "empty"), "window", Visible("normal")).Title);
        }

        [Fact]
        public void WorkspaceCapKeepsGraphemesAndDropsBlankNames()
        {
            Session session = new Session(Guid.NewGuid(), "cmd", null, "task");
            string longName = string.Concat(Enumerable.Repeat("e\u0301", 25));
            string expected = string.Concat(Enumerable.Repeat("e\u0301", 24)) + "… — task";
            Assert.Equal(expected, TitlebarComposition.Compose(session, new Workspace(Guid.NewGuid(), longName), null, Visible("compact")).Title);
            Assert.Equal("task", TitlebarComposition.Compose(session, new Workspace(Guid.NewGuid(), "  "), null, Visible("compact")).Title);
        }
    }
}
