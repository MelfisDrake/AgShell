using System.Reflection;
using System.Text.Json.Nodes;

namespace AgShell.Ctl
{
    /// <summary>
    /// agterm's SkillInstall and SkillInstaller: the bundled skill folder goes to Claude Code's and Codex's skills
    /// directories in one home (Windows or a WSL distribution), and a same-named skill the user wrote is left alone.
    /// </summary>
    internal static class SkillInstall
    {
        public const string SkillName = "agshell";

        /// <summary>Sits in the bundled SKILL.md (an HTML comment) so a reinstall tells our skill from the user's own.</summary>
        public const string Marker = "<!-- agshell-skill -->";

        public const string ResourcePrefix = "skill/";

        public const string ForeignReason = "a different '" + SkillName + "' skill is already there — left untouched";

        public sealed record Target(string Agent, string SkillDirectory);

        public static string SkillDirectory(string home, string agentBase)
        {
            return Path.Combine(home, agentBase, "skills", SkillName);
        }

        /// <summary>Each agent whose directory exists, and Claude Code when neither does, so the install is never a no-op.</summary>
        public static IReadOnlyList<Target> InstallTargets(string home, bool claudeExists, bool codexExists)
        {
            List<Target> targets = new List<Target>();
            if (claudeExists)
            {
                targets.Add(new Target("Claude Code", SkillDirectory(home, ".claude")));
            }

            if (codexExists)
            {
                targets.Add(new Target("Codex", SkillDirectory(home, ".codex")));
            }

            if (targets.Count == 0)
            {
                targets.Add(new Target("Claude Code", SkillDirectory(home, ".claude")));
            }

            return targets;
        }

        /// <summary>An absent destination is free; a present one only when its SKILL.md carries the marker.</summary>
        public static bool MayOverwrite(bool directoryExists, string? existingSkill)
        {
            if (!directoryExists)
            {
                return true;
            }

            return existingSkill is not null && existingSkill.Contains(Marker, StringComparison.Ordinal);
        }

        /// <summary>The files of plugins/agshell/skills/agshell embedded in agshellctl, by their path inside the folder.</summary>
        public static IReadOnlyList<(string Relative, byte[] Content)> BundledFiles()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            List<(string Relative, byte[] Content)> files = new List<(string Relative, byte[] Content)>();
            foreach (string name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
            {
                using Stream stream = assembly.GetManifestResourceStream(name)!;
                using MemoryStream copy = new MemoryStream();
                stream.CopyTo(copy);
                files.Add((name[ResourcePrefix.Length..].Replace('\\', '/'), copy.ToArray()));
            }

            return files;
        }

        /// <summary>Installs for the agents of one home and adds what happened to <paramref name="report"/>.</summary>
        public static void InstallInto(string home, string place, IReadOnlyList<(string Relative, byte[] Content)> files, SkillReport report)
        {
            IReadOnlyList<Target> targets = InstallTargets(home, Exists(Path.Combine(home, ".claude")), Exists(Path.Combine(home, ".codex")));
            foreach (Target target in targets)
            {
                string destination = target.SkillDirectory;
                FileAttributes? attributes = Attributes(destination);
                if (!MayOverwrite(attributes is not null, ReadSkill(destination)))
                {
                    report.Skipped.Add(new SkillReport.Skip(target.Agent, place, ForeignReason, true));
                    continue;
                }

                try
                {
                    string skills = Path.GetDirectoryName(destination)!;
                    if (!Exists(skills))
                    {
                        report.FreshSkillsDirectory = true;
                    }

                    Directory.CreateDirectory(skills);
                    if (attributes is FileAttributes present)
                    {
                        if (present.HasFlag(FileAttributes.Directory))
                        {
                            Directory.Delete(destination, true);
                        }
                        else
                        {
                            File.Delete(destination);
                        }
                    }

                    foreach ((string relative, byte[] content) in files)
                    {
                        string path = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllBytes(path, content);
                    }

                    report.Installed.Add(new SkillReport.Install(target.Agent, place, destination));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    report.Skipped.Add(new SkillReport.Skip(target.Agent, place, ex.Message, false));
                }
            }
        }

        private static bool Exists(string path)
        {
            return Directory.Exists(path) || File.Exists(path);
        }

        /// <summary>
        /// The destination's own attributes without following a link (agterm's lstat), so a link, dangling or not, counts as
        /// present; one that cannot be read counts as present too and is then left alone.
        /// </summary>
        private static FileAttributes? Attributes(string path)
        {
            try
            {
                return File.GetAttributes(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return FileAttributes.ReparsePoint;
            }
        }

        private static string? ReadSkill(string destination)
        {
            try
            {
                return File.ReadAllText(Path.Combine(destination, "SKILL.md"));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>agterm's install report: where the skill went, what was skipped and why, and whether a skills directory is new.</summary>
    internal sealed class SkillReport
    {
        public sealed record Install(string Agent, string Place, string Path);

        public sealed record Skip(string Agent, string Place, string Reason, bool Foreign);

        public List<Install> Installed { get; } = new List<Install>();

        public List<Skip> Skipped { get; } = new List<Skip>();

        public bool FreshSkillsDirectory { get; set; }

        public string Message()
        {
            List<string> lines = new List<string>();
            if (Installed.Count == 0)
            {
                lines.Add("The skill was not installed.");
            }
            else
            {
                lines.Add("Installed for:");
                lines.AddRange(Installed.Select(entry => "  • " + entry.Agent + " (" + entry.Place + "): " + entry.Path));
                lines.Add(string.Empty);
                lines.Add("The agent uses it automatically when you ask it to control AgShell. It drives the app via the agshellctl CLI, "
                    + "so install that too if you haven't: Command palette ▸ Add agshellctl to PATH.");
            }

            if (Skipped.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Skipped:");
                lines.AddRange(Skipped.Select(entry => "  • " + entry.Agent + " (" + entry.Place + "): " + entry.Reason));
            }

            if (FreshSkillsDirectory)
            {
                lines.Add(string.Empty);
                lines.Add("A skills directory was created for the first time — restart any running agent session so it picks up the new skill.");
            }

            return string.Join(Environment.NewLine, lines);
        }

        public JsonObject ToJson()
        {
            JsonArray installed = new JsonArray();
            foreach (Install entry in Installed)
            {
                installed.Add((JsonNode)new JsonObject { ["agent"] = entry.Agent, ["place"] = entry.Place, ["path"] = entry.Path });
            }

            JsonArray skipped = new JsonArray();
            foreach (Skip entry in Skipped)
            {
                skipped.Add((JsonNode)new JsonObject { ["agent"] = entry.Agent, ["place"] = entry.Place, ["reason"] = entry.Reason, ["foreign"] = entry.Foreign });
            }

            return new JsonObject { ["installed"] = installed, ["skipped"] = skipped, ["freshSkillsDirectory"] = FreshSkillsDirectory };
        }
    }
}
