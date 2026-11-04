using System.Text;

namespace AgShell.Ctl.Tests
{
    public class SkillInstallTests
    {
        private static readonly IReadOnlyList<(string Relative, byte[] Content)> Files = new[]
        {
            ("SKILL.md", Encoding.UTF8.GetBytes("---\nname: agshell\n---\n" + SkillInstall.Marker + "\nnew")),
            ("reference/commands.md", Encoding.UTF8.GetBytes("commands")),
        };

        [Fact]
        public void TargetsFollowAgtermsRules()
        {
            Assert.Equal(new[] { "Claude Code" }, SkillInstall.InstallTargets("h", false, false).Select(target => target.Agent));
            Assert.Equal(new[] { "Codex" }, SkillInstall.InstallTargets("h", false, true).Select(target => target.Agent));
            IReadOnlyList<SkillInstall.Target> both = SkillInstall.InstallTargets("h", true, true);
            Assert.Equal(new[] { "Claude Code", "Codex" }, both.Select(target => target.Agent));
            Assert.Equal(Path.Combine("h", ".codex", "skills", "agshell"), both[1].SkillDirectory);
        }

        [Fact]
        public void OnlyAMarkedSkillMayBeOverwritten()
        {
            Assert.True(SkillInstall.MayOverwrite(false, null));
            Assert.False(SkillInstall.MayOverwrite(true, null));
            Assert.False(SkillInstall.MayOverwrite(true, "# my own agshell skill"));
            Assert.True(SkillInstall.MayOverwrite(true, "x\n" + SkillInstall.Marker + "\n"));
        }

        [Fact]
        public void BundlesTheWholeFolderWithTheMarker()
        {
            IReadOnlyList<(string Relative, byte[] Content)> files = SkillInstall.BundledFiles();
            (string Relative, byte[] Content) skill = Assert.Single(files, file => file.Relative == "SKILL.md");
            Assert.Contains(SkillInstall.Marker, Encoding.UTF8.GetString(skill.Content));
            string folder = Path.Combine(Root(), "plugins", "agshell", "skills", "agshell");
            int expected = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count();
            Assert.Equal(expected, files.Count);
        }

        [Fact]
        public void InstallsForThePresentAgentsAndReplacesThePreviousInstall()
        {
            string home = TempHome();
            try
            {
                Directory.CreateDirectory(Path.Combine(home, ".claude", "skills", "agshell", "old"));
                File.WriteAllText(Path.Combine(home, ".claude", "skills", "agshell", "SKILL.md"), SkillInstall.Marker);
                File.WriteAllText(Path.Combine(home, ".claude", "skills", "agshell", "old", "stale.md"), "stale");
                Directory.CreateDirectory(Path.Combine(home, ".codex"));
                SkillReport report = new SkillReport();

                SkillInstall.InstallInto(home, "Windows", Files, report);

                Assert.Equal(new[] { "Claude Code", "Codex" }, report.Installed.Select(entry => entry.Agent));
                Assert.Empty(report.Skipped);
                Assert.True(report.FreshSkillsDirectory);
                Assert.False(Directory.Exists(Path.Combine(home, ".claude", "skills", "agshell", "old")));
                Assert.Equal("commands", File.ReadAllText(Path.Combine(home, ".codex", "skills", "agshell", "reference", "commands.md")));
                Assert.EndsWith("new", File.ReadAllText(Path.Combine(home, ".claude", "skills", "agshell", "SKILL.md")));
                Assert.Empty(report.ToJson()["skipped"]!.AsArray());
            }
            finally
            {
                Directory.Delete(home, true);
            }
        }

        [Fact]
        public void LeavesAForeignSkillAlone()
        {
            string home = TempHome();
            try
            {
                string mine = Path.Combine(home, ".claude", "skills", "agshell");
                Directory.CreateDirectory(mine);
                File.WriteAllText(Path.Combine(mine, "SKILL.md"), "my own");
                Directory.CreateDirectory(Path.Combine(home, ".claude", "skills", "other"));
                SkillReport report = new SkillReport();

                SkillInstall.InstallInto(home, "WSL Ubuntu", Files, report);

                Assert.Empty(report.Installed);
                SkillReport.Skip skip = Assert.Single(report.Skipped);
                Assert.True(skip.Foreign);
                Assert.Equal("WSL Ubuntu", skip.Place);
                Assert.False(report.FreshSkillsDirectory);
                Assert.Equal("my own", File.ReadAllText(Path.Combine(mine, "SKILL.md")));
                string message = report.Message();
                Assert.Contains("The skill was not installed.", message);
                Assert.Contains("Claude Code (WSL Ubuntu): " + SkillInstall.ForeignReason, message);
            }
            finally
            {
                Directory.Delete(home, true);
            }
        }

        [Fact]
        public void ADirectoryWithoutSkillMdCountsAsForeign()
        {
            string home = TempHome();
            try
            {
                Directory.CreateDirectory(Path.Combine(home, ".claude", "skills", "agshell"));
                SkillReport report = new SkillReport();

                SkillInstall.InstallInto(home, "Windows", Files, report);

                Assert.Empty(report.Installed);
                Assert.True(Assert.Single(report.Skipped).Foreign);
            }
            finally
            {
                Directory.Delete(home, true);
            }
        }

        private static string TempHome()
        {
            string home = Path.Combine(Path.GetTempPath(), "agshell-skill-" + Guid.NewGuid());
            Directory.CreateDirectory(home);
            return home;
        }

        private static string Root()
        {
            string root = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(root, "agshell.slnx")))
            {
                root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("agshell.slnx not found above the tests");
            }

            return root;
        }
    }
}
