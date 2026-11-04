using System.Text;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;

namespace AgShell.Ctl
{
    /// <summary>
    /// agterm's status hooks for the agents besides Claude Code: Codex hooks in ~/.codex/config.toml, a Pi extension and
    /// an OpenCode plugin. Each goes in only where the agent is set up, and never over a file the user wrote.
    /// </summary>
    internal static partial class AgentPlugins
    {
        public const string MarkerBegin = "# >>> agshell agent-status >>>";
        public const string MarkerEnd = "# <<< agshell agent-status <<<";

        /// <summary>Codex's lifecycle events and the adapter actions they run, as agterm wires them.</summary>
        public static readonly IReadOnlyList<(string Event, string Action)> CodexHooks = new[]
        {
            ("SessionStart", "session-start"),
            ("UserPromptSubmit", "user-prompt-submit"),
            ("PreToolUse", "pre-tool-use"),
            ("PostToolUse", "post-tool-use"),
            ("PermissionRequest", "permission-request"),
            ("Stop", "stop"),
        };

        private const string PiMarker = "// agshell-pi-status-extension";
        private const string OpenCodeMarker = "// agshell-opencode-status-plugin";
        private const string OpenCodeV2Marker = "// agshell-opencode-v2-status-plugin";

        public static string? InstallCodex(AgentHome home)
        {
            if (!Directory.Exists(Path.Combine(home.Directory, ".codex")))
            {
                return null;
            }

            string path = home.Resolve(".codex/config.toml");
            string existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            (string outcome, string? contents) = MergeCodexConfig(existing, CodexCommand(home));
            if (contents is not null)
            {
                if (File.Exists(path))
                {
                    File.Copy(path, home.OriginalPath(".codex/config.toml") + ".bak", true);
                }

                File.WriteAllText(path, contents, new UTF8Encoding(false));
            }

            return outcome + " (" + path + ")";
        }

        /// <summary>The adapter's command: a Windows path is bare unless it has spaces, so any shell Codex uses can run it.</summary>
        internal static string CodexCommand(AgentHome home)
        {
            if (home.Distribution is not null)
            {
                // ShellProfiles forwards these with /u; the Windows adapter needs them back from WSL too.
                return "WSLENV=AGSHELL_ENABLED:AGSHELL_SESSION_ID:AGSHELL_PANE:AGSHELL_PANE_ID:AGSHELL_PIPE:AGSHELL_WINDOW_ID:AGSHELL_WORKSPACE_ID " + home.AgShellctlCommand;
            }

            return home.AgShellctlPath.Contains(' ') ? "\"" + home.AgShellctlPath + "\"" : home.AgShellctlPath;
        }

        /// <summary>
        /// agterm's config.toml merge: a managed block is refreshed in place (keeping the
        /// [hooks.state…] trust records Codex appends inside it), a file with hooks of its own is left alone, anything else
        /// gets the block appended. Returns what happened and the new text, or null when nothing is to be written.
        /// </summary>
        public static (string Outcome, string? Contents) MergeCodexConfig(string existing, string agshellctl)
        {
            TomlTable parsed;
            try
            {
                SyntaxParser.ParseStrict(existing, validate: true);
                parsed = TomlSerializer.Deserialize(existing, CodexTomlContext.Default.TomlTable) ?? new TomlTable();
            }
            catch (TomlException)
            {
                return ("skipped: config.toml is not valid TOML; add agshell's hooks by hand", null);
            }

            string block = CodexBlock(agshellctl);
            List<string> lines = existing.Replace("\r\n", "\n").Split('\n').ToList();
            int begin = lines.FindIndex(line => line.Trim() == MarkerBegin);
            int end = begin < 0 ? -1 : lines.FindIndex(begin + 1, line => line.Trim() == MarkerEnd);
            if (begin >= 0 && end > begin)
            {
                List<string> body = lines.GetRange(begin + 1, end - begin - 1);
                if (!body.Any(line => line.Contains("agshellctl", StringComparison.Ordinal) && line.Contains(" hook codex ", StringComparison.Ordinal)))
                {
                    return ("skipped: the marker block has hooks of its own", null);
                }

                int state = body.FindIndex(line => line.TrimStart().StartsWith("[hooks.state", StringComparison.Ordinal));
                List<string> replacement = block.Split('\n').ToList();
                if (state >= 0)
                {
                    replacement.Add(string.Empty);
                    replacement.AddRange(body.Skip(state));
                }

                lines.RemoveRange(begin + 1, end - begin - 1);
                lines.InsertRange(begin + 1, replacement);
                string refreshed = string.Join("\n", lines);
                return refreshed == existing.Replace("\r\n", "\n") ? ("already up to date", null) : ("hooks updated", refreshed);
            }

            if (parsed.ContainsKey("hooks"))
            {
                return ("skipped: config.toml has hooks of its own; add agshell's by hand (agshellctl hook codex …)", null);
            }

            string remaining = RemoveLegacyCodexNotify(existing, parsed);
            string prefix = remaining.Length == 0 ? string.Empty : remaining.TrimEnd('\n', '\r') + "\n\n";
            return ("hooks installed", prefix + MarkerBegin + "\n" + block + "\n" + MarkerEnd + "\n");
        }

        private static string CodexBlock(string agshellctl)
        {
            return string.Join("\n\n", CodexHooks.Select(hook =>
                "[[hooks." + hook.Event + "]]\n[[hooks." + hook.Event + ".hooks]]\ntype = \"command\"\ncommand = " + TomlString(agshellctl + " hook codex " + hook.Action)));
        }

        private static string TomlString(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string RemoveLegacyCodexNotify(string existing, TomlTable parsed)
        {
            if (!parsed.TryGetValue("notify", out object? notify))
            {
                return existing;
            }

            IEnumerable<string> values = notify is string text ? new[] { text } : notify is TomlArray array ? array.OfType<string>() : Array.Empty<string>();
            if (!values.Any(value => value.Contains("codex-notify.sh", StringComparison.Ordinal)))
            {
                return existing;
            }

            List<string> lines = existing.Split('\n').ToList();
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].TrimStart();
                if (line.StartsWith('['))
                {
                    break;
                }

                int equal = line.IndexOf('=');
                if (equal < 0 || line[..equal].Trim() != "notify")
                {
                    continue;
                }

                try
                {
                    TomlTable single = TomlSerializer.Deserialize(line, CodexTomlContext.Default.TomlTable)!;
                    if (single.ContainsKey("notify") && line.Contains("codex-notify.sh", StringComparison.Ordinal))
                    {
                        lines.RemoveAt(i);
                        return string.Join("\n", lines);
                    }
                }
                catch (TomlException)
                {
                    return existing;
                }
            }

            return existing;
        }

        public static string? InstallPi(AgentHome home)
        {
            if (!Directory.Exists(Path.Combine(home.Directory, ".pi", "agent")))
            {
                return null;
            }

            return WritePlugin(home.Resolve(".pi/agent/extensions/agshell-status.ts"), Plugin("pi-status.ts", home), PiMarker);
        }

        /// <summary>Where the shell integration goes, as agterm's ~/.config/agterm/agent-status.</summary>
        public const string ShellIntegrationDirectory = ".config/agshell/agent-status";

        public const string RcMarkerBegin = "# >>> agshell agent-status >>>";

        public const string RcMarkerEnd = "# <<< agshell agent-status <<<";

        /// <summary>
        /// agterm's shell integration for agents without hooks: its scripts in ~/.config/agshell/agent-status and a marked
        /// <c>source</c> block in ~/.zshrc, ~/.bashrc and, when ~/.config/fish exists, config.fish. Only in WSL: Windows has no
        /// bash, zsh or fish of its own. Null for Windows.
        /// </summary>
        public static string? InstallShellIntegration(AgentHome home)
        {
            if (home.Distribution is null)
            {
                return null;
            }

            string unixDirectory = home.UnixHome + "/" + ShellIntegrationDirectory;
            string wrapper = Installer.Resource("agshell-agent-status.sh").Replace("\r\n", "\n").Replace("__AGSHELLCTL__", ShellQuote(home.AgShellctlPath));
            WriteText(home.ResolveAbsolute(unixDirectory + "/agshell-agent-status.sh"), wrapper);
            WriteText(home.ResolveAbsolute(unixDirectory + "/shell/integration.sh"), Installer.Resource("integration.sh").Replace("\r\n", "\n"));
            WriteText(home.ResolveAbsolute(unixDirectory + "/shell/integration.fish"), Installer.Resource("integration.fish").Replace("\r\n", "\n"));
            Installer.RunWsl("-d " + home.Distribution + " -e chmod +x \"" + unixDirectory + "/agshell-agent-status.sh\"", Encoding.UTF8);

            List<string> added = new List<string>();
            foreach (string name in new[] { ".zshrc", ".bashrc", ".config/fish/config.fish" })
            {
                bool fish = name.EndsWith(".fish", StringComparison.Ordinal);
                // As agterm's: config.fish only when the user already has ~/.config/fish.
                if (fish && !Directory.Exists(Path.Combine(home.Directory, ".config", "fish")))
                {
                    continue;
                }

                string rc = home.Resolve(name);
                string existing = File.Exists(rc) ? File.ReadAllText(rc) : string.Empty;
                (string contents, bool changed) = AppendShellRc(existing, unixDirectory, fish ? "shell/integration.fish" : "shell/integration.sh");
                if (changed)
                {
                    WriteText(rc, contents);
                    added.Add("~/" + name);
                }
            }

            return added.Count == 0 ? "shell integration up to date" : "shell integration installed (" + string.Join(", ", added) + ")";
        }

        /// <summary>agterm's appendShellRC: the marked source line once, after one blank line; a begin marker already there is left.</summary>
        public static (string Contents, bool Changed) AppendShellRc(string existing, string scriptDirectory, string scriptName)
        {
            if (existing.Contains(RcMarkerBegin, StringComparison.Ordinal))
            {
                return (existing, false);
            }

            string block = RcMarkerBegin + "\n" + "source " + ShellQuote(scriptDirectory + "/" + scriptName) + "\n" + RcMarkerEnd + "\n";
            if (existing.Length == 0)
            {
                return (block, true);
            }

            return ((existing.EndsWith('\n') ? existing : existing + "\n") + "\n" + block, true);
        }

        private static string ShellQuote(string value)
        {
            return "'" + value.Replace("'", "'\\''") + "'";
        }

        private static void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        public const string OpenCodeVersionUnknown = "could not determine the OpenCode version in the login shell (opencode --version); run again with --opencode v1, --opencode v2 or --opencode skip";

        /// <summary>
        /// agterm's installOpenCodePlugin: the version and v2 directory from the probe; v1 always in ~/.config/opencode. A version
        /// the probe cannot tell is <paramref name="choice"/> (1, 2, or 0 to skip), agterm's chooser; without one the step says
        /// so and <paramref name="versionNeeded"/> is set. Null when OpenCode is not set up there.
        /// </summary>
        public static string? InstallOpenCode(AgentHome home, int? choice, ref bool versionNeeded)
        {
            return InstallOpenCode(home, OpenCodeProbe.Detect(home), choice, ref versionNeeded);
        }

        public static string? InstallOpenCode(AgentHome home, OpenCodeProbe.Result? probe, int? choice, ref bool versionNeeded)
        {
            string legacyDirectory = Path.Combine(home.Directory, ".config", "opencode");
            int? version = probe?.Version;
            // No probe result and no default config: neither answer of the chooser could install anything.
            if (probe is null && !Directory.Exists(legacyDirectory))
            {
                return null;
            }

            if (version is null && !Directory.Exists(legacyDirectory) && probe?.V2ConfigurationDirectory is string probed && !Directory.Exists(home.WindowsPath(probed)))
            {
                return null;
            }

            if (version is null)
            {
                if (choice is null)
                {
                    versionNeeded = true;
                    return "skipped: " + OpenCodeVersionUnknown;
                }

                if (choice == 0)
                {
                    return "status plugin installation was skipped";
                }

                version = choice;
            }

            if (version == 1)
            {
                return Directory.Exists(legacyDirectory)
                    ? WritePlugin(home.Resolve(".config/opencode/plugins/agshell-status.js"), Plugin("opencode-status.js", home), OpenCodeMarker)
                    : null;
            }

            if (probe?.V2ConfigurationDirectory is not string directory)
            {
                throw new InvalidOperationException("OpenCode v2 installation was skipped because its configuration directory could not be determined from your login shell. Check shell startup and OPENCODE_CONFIG_DIR, then run this again.");
            }

            if (!Directory.Exists(home.WindowsPath(directory)))
            {
                return null;
            }

            string separator = home.Distribution is null ? "\\" : "/";
            string plugin = string.Join(separator, directory.TrimEnd('/', '\\'), "plugins", "agshell-v2", "tui.js");
            return WritePlugin(home.ResolveAbsolute(plugin), Plugin("opencode-v2-tui.js", home), OpenCodeV2Marker) + ", OpenCode 2";
        }

        /// <summary>The bundled plugin with the agshellctl this home reaches written in.</summary>
        public static string Plugin(string resource, AgentHome home)
        {
            return Installer.Resource(resource).Replace("__AGSHELLCTL__", home.AgShellctlPath);
        }

        /// <summary>Writes a plugin unless a file there is not agshell's, which is left for the user.</summary>
        public static string WritePlugin(string path, string text, string marker)
        {
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path);
                if (!existing.Contains(marker, StringComparison.Ordinal))
                {
                    return "skipped: " + path + " is not agshell's";
                }

                if (existing == text)
                {
                    return "already up to date (" + path + ")";
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return "plugin installed (" + path + ")";
        }
    }
}
