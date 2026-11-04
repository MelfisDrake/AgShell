namespace AgShell.Core.Tests
{
    public class CommandRestoreTests
    {
        [Theory]
        [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", true)]
        [InlineData("cmd.exe", true)]
        [InlineData("/usr/bin/bash", true)]
        [InlineData("-zsh", true)]
        [InlineData("claude.exe", false)]
        [InlineData("/usr/local/bin/claude", false)]
        public void ShellsAreRecognizedByName(string executable, bool shell)
        {
            Assert.Equal(shell, CommandRestore.IsShell(executable));
        }

        [Fact]
        public void DenyListShellsAndControlCharactersAreNotRestored()
        {
            HashSet<string> denylist = CommandRestore.ParseDenylist("# comment\nssh\n  VIM.exe \n\n");

            Assert.True(CommandRestore.ShouldRestore("claude.exe", "claude --continue", denylist));
            Assert.False(CommandRestore.ShouldRestore("/usr/bin/ssh", "ssh host", denylist));
            Assert.False(CommandRestore.ShouldRestore("vim", "vim notes.md", denylist));
            Assert.False(CommandRestore.ShouldRestore("pwsh.exe", "pwsh -NoLogo", denylist));
            Assert.False(CommandRestore.ShouldRestore("claude", "claude\u001b[2J", denylist));
            Assert.False(CommandRestore.ShouldRestore("claude", "  ", denylist));
        }

        [Fact]
        public void PowerShellCallsAQuotedProgramWithTheCallOperator()
        {
            Assert.Equal("& \"C:\\x y\\claude.exe\" --resume", CommandRestore.ForShell("pwsh", "\"C:\\x y\\claude.exe\" --resume"));
            Assert.Equal("\"C:\\x y\\claude.exe\"", CommandRestore.ForShell("cmd", "\"C:\\x y\\claude.exe\""));
            Assert.Equal("claude", CommandRestore.ForShell("powershell", "claude"));
        }

        [Fact]
        public void PosixJoinQuotesOnlyWhatNeedsIt()
        {
            Assert.Equal("node /usr/lib/claude/cli.js --model=opus 'two words' 'it'\\''s'", CommandRestore.PosixJoin(new[] { "node", "/usr/lib/claude/cli.js", "--model=opus", "two words", "it's" }));
        }

        [Fact]
        public void OnlyTheLaunchArmsTheCaptureAndItIsTakenOnce()
        {
            AppModel model = new AppModel();
            Session session = model.AddSession(model.AddWorkspace("w").Id, "cmd", null);
            model.SetRestoreCommand(session.Id, PaneRole.Main, "claude");
            StateSnapshot saved = model.ToSnapshot();

            AppModel launched = AppModel.FromSnapshot(saved, launchRestore: true);
            Session restored = launched.FindSession(session.Id)!;
            Assert.True(restored.WasRestored);
            Assert.Null(restored.RestoreCommand);
            Assert.Null(launched.ToSnapshot().Workspaces.Single().Sessions.Single().RestoreCommand);
            Assert.Equal("claude", launched.TakePendingCapture(session.Id, PaneRole.Main));
            Assert.Null(launched.TakePendingCapture(session.Id, PaneRole.Main));

            AppModel reopened = AppModel.FromSnapshot(saved);
            Assert.True(reopened.FindSession(session.Id)!.WasRestored);
            Assert.Null(reopened.TakePendingCapture(session.Id, PaneRole.Main));
            Assert.False(session.WasRestored);
        }

        [Fact]
        public void TheLaunchStripsCapturesAndKeepsPins()
        {
            AppModel model = new AppModel();
            Session session = model.AddSession(model.AddWorkspace("w").Id, "cmd", null);
            model.SetRestoreCommand(session.Id, PaneRole.Main, "claude");
            model.SetRestorePin(session.Id, PaneRole.Main, "claude --resume x");
            StateSnapshot saved = model.ToSnapshot();

            Assert.True(AppModel.CarriesCaptures(saved));
            StateSnapshot stripped = AppModel.StripCaptures(saved);
            Assert.False(AppModel.CarriesCaptures(stripped));
            Assert.Equal("claude --resume x", stripped.Workspaces.Single().Sessions.Single().RestorePin);
        }

        [Fact]
        public void ARestoreClearAndASoftCloseDisarmWhatTheLaunchArmed()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("w");
            Session first = model.AddSession(workspace.Id, "cmd", null);
            Session second = model.AddSession(workspace.Id, "cmd", null);
            model.SetRestoreCommand(first.Id, PaneRole.Main, "claude");
            model.SetRestoreCommand(second.Id, PaneRole.Main, "vim");
            model.SetRestorePin(second.Id, PaneRole.Main, "vim x");

            AppModel launched = AppModel.FromSnapshot(model.ToSnapshot(), launchRestore: true);
            launched.ClearRestoreCommands();
            Assert.Null(launched.TakePendingCapture(first.Id, PaneRole.Main));

            launched = AppModel.FromSnapshot(model.ToSnapshot(), launchRestore: true);
            launched.SoftCloseSessions(new[] { second.Id });
            launched.UndoClose();
            Assert.Null(launched.TakePendingCapture(second.Id, PaneRole.Main));
            Assert.Null(launched.TakePendingPin(second.Id, PaneRole.Main));
            Assert.Equal("vim x", launched.FindSession(second.Id)!.RestorePin);
        }

        [Fact]
        public void APinWinsAndIsOnlyTyped()
        {
            Assert.Equal(new RestorePlan(null, "claude --resume x", false), CommandRestore.Plan(new RestoreInputs(true, true, true, "vim", "ssh box", "claude --resume x", true)));
            Assert.Equal(new RestorePlan(null, null, false), CommandRestore.Plan(new RestoreInputs(true, true, true, "vim", "ssh box", "", false)));
            Assert.Equal(new RestorePlan(null, null, false), CommandRestore.Plan(new RestoreInputs(true, false, false, null, "ssh box", "claude", false)));
        }

        [Fact]
        public void ACaptureWinsOverTheCommandEvenWhenRefused()
        {
            Assert.Equal(new RestorePlan(null, "vim", false), CommandRestore.Plan(new RestoreInputs(true, true, true, "vim", "ssh box", null, true)));
            Assert.Equal(new RestorePlan(null, null, false), CommandRestore.Plan(new RestoreInputs(true, true, true, null, "ssh box", null, true)));
        }

        [Fact]
        public void ARestoredSessionRunsItsCommandOnlyInRerunANewOneAlways()
        {
            Assert.Equal(new RestorePlan("ssh box", null, true), CommandRestore.Plan(new RestoreInputs(true, true, false, null, "ssh box", null, true)));
            Assert.Equal(new RestorePlan(null, null, false), CommandRestore.Plan(new RestoreInputs(true, false, false, null, "ssh box", null, true)));
            Assert.Equal(new RestorePlan("ssh box", null, false), CommandRestore.Plan(new RestoreInputs(false, false, false, null, "ssh box", null, false)));
        }

        [Fact]
        public void TheDenylistIsCheckedAgainstTheLinesProgram()
        {
            HashSet<string> denylist = CommandRestore.ParseDenylist("ssh\nvim");
            Assert.False(CommandRestore.ShouldRestoreLine("\"C:\\Program Files\\OpenSSH\\ssh.exe\" box", denylist));
            Assert.False(CommandRestore.ShouldRestoreLine("vim notes.md", denylist));
            Assert.False(CommandRestore.ShouldRestoreLine("'/usr/bin/vim' 'a b'", denylist));
            Assert.True(CommandRestore.ShouldRestoreLine("claude --resume x", denylist));
        }
    }
}
