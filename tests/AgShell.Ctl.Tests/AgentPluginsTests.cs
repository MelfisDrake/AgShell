namespace AgShell.Ctl.Tests
{
    public class AgentPluginsTests
    {
        private const string AgShellctl = "C:/agshell/agshellctl.exe";

        [Fact]
        public void CodexWslCommandReturnsSessionEnvironmentToWindowsAdapter()
        {
            AgentHome wsl = new AgentHome("WSL Ubuntu", @"\\wsl.localhost\Ubuntu\home\me", "Ubuntu", "/home/me", "/mnt/c/Ag Shell/agshellctl.exe", "'/mnt/c/Ag Shell/agshellctl.exe'");
            string command = AgentPlugins.CodexCommand(wsl);
            Assert.StartsWith("WSLENV=", command);
            foreach (string name in new[] { "AGSHELL_ENABLED", "AGSHELL_SESSION_ID", "AGSHELL_PANE", "AGSHELL_PANE_ID", "AGSHELL_PIPE", "AGSHELL_WINDOW_ID", "AGSHELL_WORKSPACE_ID" })
            {
                Assert.Contains(name, command.Split(' ')[0].Split('=', 2)[1].Split(':'));
            }

            Assert.EndsWith(" '/mnt/c/Ag Shell/agshellctl.exe'", command);
            (string _, string? contents) = AgentPlugins.MergeCodexConfig(string.Empty, command);
            Assert.NotNull(contents);
            foreach ((string eventName, string action) in AgentPlugins.CodexHooks)
            {
                Assert.Contains("[[hooks." + eventName + ".hooks]]\ntype = \"command\"\ncommand = \"" + command + " hook codex " + action + "\"", contents);
            }

            Assert.Equal(("already up to date", (string?)null), AgentPlugins.MergeCodexConfig(contents, command));
            AgentHome windows = new AgentHome("Windows", @"C:\Users\me", null, string.Empty, AgShellctl, "\"" + AgShellctl + "\"");
            Assert.Equal(AgShellctl, AgentPlugins.CodexCommand(windows));
        }

        [Theory]
        [InlineData("x = [")]
        [InlineData("x = 1\nx = 2")]
        [InlineData("\"hooks\" = {}")]
        [InlineData("['hooks']\nStop = []")]
        [InlineData("\"hooks\".Stop = []")]
        public void InvalidTomlAndQuotedForeignHooksAreNeverRewritten(string existing)
        {
            (string outcome, string? contents) = AgentPlugins.MergeCodexConfig(existing, AgShellctl);
            Assert.StartsWith("skipped", outcome);
            Assert.Null(contents);
        }

        [Theory]
        [InlineData("notify = [\"bash\", \"/old/codex-notify.sh\"]\n", false)]
        [InlineData("notify = \"/old/codex-notify.sh\"\n", false)]
        [InlineData("notify = [\"mine\"] # codex-notify.sh\n", true)]
        [InlineData("notify = [\n\"/old/codex-notify.sh\"\n]\n", true)]
        [InlineData("notify = [\"/old/codex-notify.sh\",\n\"arg\"]\n", true)]
        public void LegacyNotifyIsRemovedOnlyWhenTheParsedValueAndWholeLineMatch(string existing, bool kept)
        {
            (string _, string? contents) = AgentPlugins.MergeCodexConfig(existing, AgShellctl);
            Assert.NotNull(contents);
            Assert.Equal(kept, contents.Contains("notify =", StringComparison.Ordinal));
        }

        [Fact]
        public void PiRequiresItsAgentDirectory()
        {
            string directory = Path.Combine(Path.GetTempPath(), "agshell-pi-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(directory, ".pi"));
            try
            {
                AgentHome home = new AgentHome("test", directory, null, string.Empty, AgShellctl, AgShellctl);
                Assert.Null(AgentPlugins.InstallPi(home));
                Assert.False(Directory.Exists(Path.Combine(directory, ".pi", "agent")));
                Directory.CreateDirectory(Path.Combine(directory, ".pi", "agent"));
                Assert.StartsWith("plugin installed", AgentPlugins.InstallPi(home));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void CodexHooksAreAppendedOnceAndRefreshedInPlace()
        {
            (string outcome, string? contents) = AgentPlugins.MergeCodexConfig("model = \"o3\"\n", AgShellctl);
            Assert.Equal("hooks installed", outcome);
            Assert.StartsWith("model = \"o3\"\n\n" + AgentPlugins.MarkerBegin, contents);
            Assert.Contains("[[hooks.Stop.hooks]]\ntype = \"command\"\ncommand = \"C:/agshell/agshellctl.exe hook codex stop\"", contents);
            Assert.Equal(6, contents!.Split("[[hooks.").Length / 2);

            Assert.Equal(("already up to date", (string?)null), AgentPlugins.MergeCodexConfig(contents, AgShellctl));

            string trusted = contents.Replace(AgentPlugins.MarkerEnd, "[hooks.state.abc]\ntrusted = true\n" + AgentPlugins.MarkerEnd);
            (string updated, string? moved) = AgentPlugins.MergeCodexConfig(trusted, "D:/new/agshellctl.exe");
            Assert.Equal("hooks updated", updated);
            Assert.Contains("D:/new/agshellctl.exe hook codex stop", moved);
            Assert.DoesNotContain("C:/agshell", moved);
            Assert.Contains("[hooks.state.abc]\ntrusted = true\n" + AgentPlugins.MarkerEnd, moved);
        }

        [Theory]
        [InlineData("[[hooks.Stop]]\n")]
        [InlineData("[hooks]\nStop = []\n")]
        [InlineData("hooks.Stop = []\n")]
        public void ConfigWithItsOwnHooksIsLeftAlone(string existing)
        {
            (string outcome, string? contents) = AgentPlugins.MergeCodexConfig(existing, AgShellctl);
            Assert.StartsWith("skipped", outcome);
            Assert.Null(contents);
        }

        [Theory]
        [InlineData("Should I push it?", true)]
        [InlineData("Run `make test`?", true)]
        [InlineData("Done. \"Is it?\" was the old title?", true)]
        [InlineData("Done, tests pass.", false)]
        [InlineData("Press ? for shortcuts.", false)]
        [InlineData("Fixed.\n```\nif (a?.b) {}\n```", false)]
        [InlineData("See \"?help\" in the docs.", false)]
        public void StopKnowsAQuestionFromAStatement(string message, bool question)
        {
            Assert.Equal(question, Hook.AsksQuestion(message));
        }

        [Fact]
        public void PluginsCallTheAgShellctlOfTheirHome()
        {
            AgentHome wsl = new AgentHome("WSL Ubuntu", @"\\wsl.localhost\Ubuntu\home\me", "Ubuntu", "/home/me", "/mnt/c/agshell/agshellctl.exe", "'/mnt/c/agshell/agshellctl.exe'");
            foreach (string resource in new[] { "pi-status.ts", "opencode-status.js", "opencode-v2-tui.js" })
            {
                string text = AgentPlugins.Plugin(resource, wsl);
                Assert.Contains("\"/mnt/c/agshell/agshellctl.exe\"", text);
                Assert.Contains("AGSHELL_SESSION_ID", text);
                Assert.DoesNotContain("__AGSHELLCTL__", text);
                Assert.DoesNotContain("AGTERM_", text);
            }
        }

        [Fact]
        public void APluginFileTheUserWroteIsKept()
        {
            string directory = Path.Combine(Path.GetTempPath(), "agshell-plugin-" + Guid.NewGuid());
            string path = Path.Combine(directory, "agshell-status.js");
            try
            {
                Assert.StartsWith("plugin installed", AgentPlugins.WritePlugin(path, "// mark\nv1", "// mark"));
                Assert.StartsWith("already up to date", AgentPlugins.WritePlugin(path, "// mark\nv1", "// mark"));
                Assert.StartsWith("plugin installed", AgentPlugins.WritePlugin(path, "// mark\nv2", "// mark"));
                File.WriteAllText(path, "mine");
                Assert.StartsWith("skipped", AgentPlugins.WritePlugin(path, "// mark\nv3", "// mark"));
                Assert.Equal("mine", File.ReadAllText(path));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void ShellRcGetsTheSourceBlockOnce()
        {
            (string contents, bool changed) = AgentPlugins.AppendShellRc("export FOO=1", "/home/u/.config/agshell/agent-status", "shell/integration.sh");
            Assert.True(changed);
            Assert.Equal("export FOO=1\n\n# >>> agshell agent-status >>>\nsource '/home/u/.config/agshell/agent-status/shell/integration.sh'\n# <<< agshell agent-status <<<\n", contents);
            Assert.Equal((contents, false), AgentPlugins.AppendShellRc(contents, "/other", "shell/integration.sh"));
            Assert.StartsWith("# >>> agshell agent-status >>>\nsource '/it'\\''s/shell/integration.fish'", AgentPlugins.AppendShellRc(string.Empty, "/it's", "shell/integration.fish").Contents);
        }
    }
}
