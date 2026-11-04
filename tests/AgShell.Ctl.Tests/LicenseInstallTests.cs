using System.Text;

namespace AgShell.Ctl.Tests
{
    public class LicenseInstallTests
    {
        [Fact]
        public void StandaloneSkillAndInstalledPluginsRetainTheWholeUpstreamLicense()
        {
            string home = Path.Combine(Path.GetTempPath(), "agshell-license-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(home);
            try
            {
                foreach (string folder in new[] { ".codex", ".claude", ".pi/agent", ".config/opencode" })
                {
                    Directory.CreateDirectory(Path.Combine(home, folder));
                }

                SkillReport report = new SkillReport();
                SkillInstall.InstallInto(home, "test", SkillInstall.BundledFiles(), report);
                Assert.Equal(2, report.Installed.Count);
                foreach (SkillReport.Install target in report.Installed)
                {
                    AssertLicense(File.ReadAllText(Path.Combine(target.Path, "LICENSE-agterm")));
                }

                AgentHome agent = new AgentHome("test", home, null, string.Empty, "agshellctl", "agshellctl");
                Assert.NotNull(AgentPlugins.InstallClaude(agent));
                Assert.NotNull(AgentPlugins.InstallCodex(agent));
                Assert.NotNull(AgentPlugins.InstallPi(agent));
                bool versionNeeded = false;
                Assert.NotNull(AgentPlugins.InstallOpenCode(agent, new OpenCodeProbe.Result(1, null), null, ref versionNeeded));
                AssertLicense(File.ReadAllText(Path.Combine(home, ".pi/agent/extensions/agshell-status.ts")));
                AssertLicense(File.ReadAllText(Path.Combine(home, ".config/opencode/plugins/agshell-status.js")));
                Assert.NotNull(AgentPlugins.InstallOpenCode(agent, new OpenCodeProbe.Result(2, Path.Combine(home, ".config/opencode")), null, ref versionNeeded));
                AssertLicense(File.ReadAllText(Path.Combine(home, ".config/opencode/plugins/agshell-v2/tui.js")));
                foreach (string resource in new[] { "agshell-agent-status.sh", "agshell-claude-status.sh", "integration.sh", "integration.fish" })
                {
                    string installed = Path.Combine(home, "shell", resource);
                    Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
                    File.WriteAllText(installed, Installer.Resource(resource).Replace("__AGSHELLCTL__", "agshellctl"), new UTF8Encoding(false));
                    AssertLicense(File.ReadAllText(installed));
                }
            }
            finally
            {
                Directory.Delete(home, true);
            }
        }

        private static void AssertLicense(string text)
        {
            string normalized = string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimStart(' ', '#', '/')));
            string upstream = Encoding.UTF8.GetString(Assert.Single(SkillInstall.BundledFiles(), file => file.Relative == "LICENSE-agterm").Content).Replace("\r\n", "\n").Trim();
            Assert.Contains("Copyright (c) 2026 Umputun", normalized);
            Assert.Contains(upstream, normalized);
        }
    }
}
