using System.Text.Json.Nodes;

namespace AgShell.Ctl.Tests
{
    public class HooksInstallerTests
    {
        private const string AgShellctl = "\"C:/agshell/agshellctl.exe\"";

        [Fact]
        public void WslAdapterMigratesOnlyGeneratedCommandsAndIsIdempotent()
        {
            JsonObject root = new JsonObject();
            HooksInstaller.Merge(root, AgShellctl);
            root["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!["command"] = "wrapper agshellctl hook claude blocked";
            const string adapter = "'/home/test space/.config/agshell/agent-status/agshell-claude-status.sh'";
            Assert.True(HooksInstaller.Merge(root, AgShellctl, adapter));
            Assert.Equal(adapter + " completed --auto-reset", root["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
            Assert.Equal("wrapper agshellctl hook claude blocked", root["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
            Assert.False(HooksInstaller.Merge(root, AgShellctl, adapter));
            Assert.True(HooksInstaller.Merge(root, AgShellctl, "'/new/agshell-claude-status.sh'"));
            Assert.Equal("'/new/agshell-claude-status.sh' blocked", root["hooks"]!["Notification"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        }

        [Theory]
        [InlineData("{\"hooks\":null}")]
        [InlineData("{\"hooks\":\"keep\"}")]
        [InlineData("{\"hooks\":{\"Stop\":\"keep\"}}")]
        [InlineData("{\"hooks\":{\"Stop\":[null]}}")]
        public void MalformedHookShapesAreRejectedWithoutMutation(string json)
        {
            JsonObject root = JsonNode.Parse(json)!.AsObject();
            string before = root.ToJsonString();
            Assert.Throws<System.Text.Json.JsonException>(() => HooksInstaller.Merge(root, AgShellctl));
            Assert.Equal(before, root.ToJsonString());
        }

        [Theory]
        [InlineData("agshellctl hook claude completed --auto-reset --color red")]
        [InlineData("wrapper agshellctl hook claude completed --auto-reset")]
        [InlineData("agshellctl hook claude completed --auto-reset && echo done")]
        public void CustomizedCommandsAreKeptWithoutAddingADuplicate(string command)
        {
            JsonObject root = new JsonObject();
            HooksInstaller.Merge(root, AgShellctl);
            root["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"] = command;
            Assert.False(HooksInstaller.Merge(root, AgShellctl));
            Assert.Single(root["hooks"]!["Stop"]!.AsArray());
            Assert.Equal(command, root["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        }

        [Fact]
        public void BackupCanStayBesideTheOriginalLink()
        {
            string directory = Path.Combine(Path.GetTempPath(), "agshell-hooks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string target = Path.Combine(directory, "dotfiles.json");
                string backup = Path.Combine(directory, "settings.json.bak");
                File.WriteAllText(target, "{}");
                HooksInstaller.InstallInto(target, AgShellctl, backup);
                Assert.Equal("{}", File.ReadAllText(backup));
                Assert.False(File.Exists(target + ".bak"));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void AddsAgtermsEventsAndKeepsForeignHooks()
        {
            JsonObject root = JsonNode.Parse("""
                {
                  "model": "opus",
                  "hooks": {
                    "Stop": [ { "hooks": [ { "type": "command", "command": "say done" } ] } ]
                  }
                }
                """)!.AsObject();

            Assert.True(HooksInstaller.Merge(root, AgShellctl));

            Assert.Equal("opus", root["model"]!.GetValue<string>());
            JsonArray stop = root["hooks"]!["Stop"]!.AsArray();
            Assert.Equal(2, stop.Count);
            Assert.Equal("say done", stop[0]!["hooks"]![0]!["command"]!.GetValue<string>());
            Assert.Equal(AgShellctl + " hook claude completed --auto-reset", stop[1]!["hooks"]![0]!["command"]!.GetValue<string>());
            JsonObject notification = root["hooks"]!["Notification"]![0]!.AsObject();
            Assert.Equal("permission_prompt", notification["matcher"]!.GetValue<string>());
            Assert.Equal(AgShellctl + " hook claude blocked", notification["hooks"]![0]!["command"]!.GetValue<string>());
            Assert.Equal(AgShellctl + " hook claude active --blink", root["hooks"]!["UserPromptSubmit"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        }

        [Fact]
        public void SecondRunChangesNothingAndAMovedBinaryIsUpdatedInPlace()
        {
            JsonObject root = new JsonObject();
            HooksInstaller.Merge(root, AgShellctl);
            string first = root.ToJsonString();

            Assert.False(HooksInstaller.Merge(root, AgShellctl));
            Assert.Equal(first, root.ToJsonString());

            Assert.True(HooksInstaller.Merge(root, "\"D:/new/agshellctl.exe\""));
            JsonArray stop = root["hooks"]!["Stop"]!.AsArray();
            Assert.Single(stop);
            Assert.Equal("\"D:/new/agshellctl.exe\" hook claude completed --auto-reset", stop[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        }

        [Fact]
        public void InstallKeepsABackupOfThePreviousFile()
        {
            string directory = Path.Combine(Path.GetTempPath(), "agshell-hooks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string settings = Path.Combine(directory, "settings.json");
                File.WriteAllText(settings, "{ \"theme\": \"тёмная\" }");

                Assert.StartsWith("hooks installed", HooksInstaller.InstallInto(settings, AgShellctl));
                Assert.StartsWith("already up to date", HooksInstaller.InstallInto(settings, AgShellctl));

                Assert.Equal("{ \"theme\": \"тёмная\" }", File.ReadAllText(Path.Combine(directory, "settings.json.bak")));
                Assert.Contains("\"theme\": \"тёмная\"", File.ReadAllText(settings));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void SkillIsEmbedded()
        {
            Assert.StartsWith("---\nname: agshell", Installer.SkillText().Replace("\r\n", "\n"));
        }
    }
}
