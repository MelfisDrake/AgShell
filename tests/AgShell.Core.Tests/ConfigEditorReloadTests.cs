namespace AgShell.Core.Tests
{
    public class ConfigEditorReloadTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void ChangedFileReloadsOnlyWhenItsEditorCloses(bool replace, bool exit)
        {
            string folder = Path.Combine(Path.GetTempPath(), "agshell-editor-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "ghostty.conf");
                File.WriteAllText(path, "title = before\n");
                AppModel model = new AppModel();
                Workspace workspace = model.AddWorkspace();
                Session session = model.AddSession(workspace.Id, "cmd", null);
                model.OpenOverlay(session.Id, new SessionOverlay { Id = "editor", Command = "editor" });
                ConfigEditorReload editor = new ConfigEditorReload(session.Id, "editor", path);
                int reloads = 0;
                model.OverlayClosed += (sessionId, overlayId, slot) =>
                {
                    if (editor.Close(sessionId, overlayId))
                    {
                        reloads++;
                    }
                };

                if (replace)
                {
                    File.WriteAllText(path + ".new", "title = after\n");
                    File.Move(path + ".new", path, true);
                }
                else
                {
                    File.WriteAllText(path, "title = after\n");
                }

                Assert.Equal(0, reloads);
                Assert.False(editor.Close(session.Id, "another-overlay"));
                Assert.False(editor.Close(Guid.NewGuid(), "editor"));
                Assert.NotNull(session.Overlay);
                if (exit)
                {
                    model.OverlayExited(session.Id, "editor", 0);
                }
                else
                {
                    model.CloseOverlay(session.Id, "editor");
                }

                Assert.Null(session.Overlay);
                Assert.Equal(1, reloads);
                Assert.False(editor.Close(session.Id, "editor"));
                Assert.Equal("title = after\n", File.ReadAllText(path));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void UnchangedOrRestoredContentsSkipReload(bool rewrite)
        {
            string folder = Path.Combine(Path.GetTempPath(), "agshell-editor-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "ghostty.conf");
                string original = "title = original\n";
                File.WriteAllText(path, original);
                Guid session = Guid.NewGuid();
                ConfigEditorReload editor = new ConfigEditorReload(session, "editor", path);
                if (rewrite)
                {
                    File.WriteAllText(path, "title = temporary\n");
                    File.WriteAllText(path, original);
                }

                Assert.False(editor.Close(session, "editor"));
                Assert.Equal(original, File.ReadAllText(path));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }
}
