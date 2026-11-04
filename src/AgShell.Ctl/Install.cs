using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgShell.Protocol;

namespace AgShell.Ctl
{
    internal static partial class Commands
    {
        private static Command Install()
        {
            Command install = new CtlCommand("install", "Connect agents to agshell: status hooks and the agent skill.");

            CtlCommand hooks = new CtlCommand("hooks", "Add agshell status hooks to Claude Code, Codex, Pi and OpenCode, in Windows and the WSL distributions where they are set up.");
            Option<string?> opencode = new Option<string?>("--opencode")
            {
                Description = "OpenCode version to install for where opencode --version in the login shell tells none: v1, v2, or skip.",
                HelpName = "v1|v2|skip",
            };
            opencode.AcceptOnlyFromAmong("v1", "v2", "skip");
            hooks.Add(opencode);
            hooks.SetAction(result => Installer.RunHooks(result.GetValue(opencode) switch
            {
                "v1" => 1,
                "v2" => 2,
                "skip" => 0,
                _ => null,
            }));
            install.Subcommands.Add(hooks);

            Command skill = new CtlCommand("skill", "Install the agshell skill for Claude Code and Codex, in Windows and every WSL distribution: where ~/.claude or ~/.codex exists, or for Claude Code when neither does.");
            skill.SetAction(result => Installer.RunSkill(result.GetValue(Json)));
            install.Subcommands.Add(skill);

            return install;
        }
    }

    /// <summary>
    /// One place agents' configs live: Windows itself or a WSL distribution. <paramref name="Directory"/> is the home as
    /// Windows reaches it, <paramref name="Distribution"/> null for Windows, and agshellctl is reached from there through
    /// <paramref name="AgShellctlPath"/> (a path the agents' own programs can start) and <paramref name="AgShellctlCommand"/>
    /// (the same, quoted for a hook's command line).
    /// </summary>
    internal sealed record AgentHome(string Label, string Directory, string? Distribution, string UnixHome, string AgShellctlPath, string AgShellctlCommand)
    {
        public string ClaudeDirectory => Path.Combine(Directory, ".claude");

        public string OriginalPath(string relative) => Distribution is null
            ? Path.Combine(Directory, relative)
            : WindowsPath(UnixHome + "/" + relative);

        /// <summary>
        /// A file in the home as Windows writes it. A WSL file linked elsewhere (dotfiles) cannot be followed through
        /// \\wsl.localhost, so the link is resolved inside the distribution.
        /// </summary>
        public string Resolve(string relative)
        {
            string fallback = Path.Combine(Directory, relative.Replace('/', '\\'));
            if (Distribution is null)
            {
                return fallback;
            }

            string? target = Installer.RunWsl("-d " + Distribution + " -e readlink -f \"" + UnixHome + "/" + relative + "\"", Encoding.UTF8)?.Trim();
            return target is { Length: > 0 } && target.StartsWith('/') ? WindowsPath(target) : fallback;
        }

        /// <summary>A path in the home's own terms (POSIX in WSL) as Windows reaches it, a link resolved as in <see cref="Resolve"/>.</summary>
        public string ResolveAbsolute(string path)
        {
            if (Distribution is null)
            {
                return path;
            }

            string? target = Installer.RunWsl("-d " + Distribution + " -e readlink -f \"" + path + "\"", Encoding.UTF8)?.Trim();
            return WindowsPath(target is { Length: > 0 } && target.StartsWith('/') ? target : path);
        }

        /// <summary>A path in the home's own terms as Windows reaches it, links left as they are.</summary>
        public string WindowsPath(string path)
        {
            return Distribution is null ? path : @"\\wsl.localhost\" + Distribution + path.Replace('/', '\\');
        }
    }

    internal static class Installer
    {
        /// <summary>Exit code when OpenCode is set up somewhere but its version needs --opencode.</summary>
        public const int OpenCodeVersionNeeded = 2;

        public static int RunHooks(int? openCodeChoice)
        {
            int failures = 0;
            bool versionNeeded = false;
            foreach (AgentHome home in Homes())
            {
                failures += Step(home.Label + " Claude Code", () => AgentPlugins.InstallClaude(home));
                failures += Step(home.Label + " shell", () => AgentPlugins.InstallShellIntegration(home));
                failures += Step(home.Label + " Codex", () => AgentPlugins.InstallCodex(home));
                failures += Step(home.Label + " Pi", () => AgentPlugins.InstallPi(home));
                failures += Step(home.Label + " OpenCode", () => AgentPlugins.InstallOpenCode(home, openCodeChoice, ref versionNeeded));
            }

            Console.WriteLine("Restart the agents' sessions for the hooks to take effect.");
            return versionNeeded ? OpenCodeVersionNeeded : failures == 0 ? 0 : 1;
        }

        /// <summary>Runs one install and prints what it did; null means the agent is not set up there and prints nothing.</summary>
        private static int Step(string label, Func<string?> install)
        {
            try
            {
                if (install() is string outcome)
                {
                    Console.WriteLine(label + ": " + outcome);
                }

                return 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is InvalidOperationException)
            {
                Console.Error.WriteLine(label + ": error: " + ex.Message);
                return 1;
            }
        }

        public static int RunSkill(bool json)
        {
            SkillReport report = new SkillReport();
            try
            {
                IReadOnlyList<(string Relative, byte[] Content)> files = SkillInstall.BundledFiles();
                if (!files.Any(file => file.Relative == "SKILL.md"))
                {
                    throw new InvalidOperationException("The agent skill is not bundled in this build.");
                }

                foreach (AgentHome home in Homes())
                {
                    SkillInstall.InstallInto(home.Directory, home.Label, files, report);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                if (json)
                {
                    Console.WriteLine(new JsonObject { ["error"] = ex.Message }.ToJsonString());
                }
                else
                {
                    Console.Error.WriteLine("Install Failed: " + ex.Message);
                }

                return 1;
            }

            Console.WriteLine(json ? report.ToJson().ToJsonString() : report.Message());
            return report.Installed.Count > 0 && report.Skipped.Count == 0 ? 0 : 1;
        }

        public static string SkillText()
        {
            return Resource(SkillInstall.ResourcePrefix + "SKILL.md");
        }

        public static string Resource(string name)
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
            using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        /// <summary>Windows, and every WSL distribution that answers; each agent is then installed where it is set up.</summary>
        private static IEnumerable<AgentHome> Homes()
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("cannot locate agshellctl.exe");
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string windowsPath = exe.Replace('\\', '/');
            yield return new AgentHome("Windows", user, null, string.Empty, windowsPath, "\"" + windowsPath + "\"");

            string wslPath = "/mnt/" + char.ToLowerInvariant(exe[0]) + exe[2..].Replace('\\', '/');
            foreach (string distribution in WslDistributions())
            {
                string? home = RunWsl("-d " + distribution + " -e sh -c \"printf %s \\\"$HOME\\\"\"", Encoding.UTF8)?.Trim();
                if (string.IsNullOrEmpty(home) || !home.StartsWith('/'))
                {
                    continue;
                }

                string directory = @"\\wsl.localhost\" + distribution + home.Replace('/', '\\');
                yield return new AgentHome("WSL " + distribution, directory, distribution, home, wslPath, "'" + wslPath + "'");
            }
        }

        private static IEnumerable<string> WslDistributions()
        {
            string? output = RunWsl("-l -q", Encoding.Unicode);
            if (output is null)
            {
                return Array.Empty<string>();
            }

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => name.Trim('\0'))
                .Where(name => name.Length > 0 && !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase));
        }

        public static string? RunWsl(string arguments, Encoding encoding)
        {
            string wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            return File.Exists(wsl) ? Run(wsl, arguments, encoding) : null;
        }

        /// <summary>A program's output, or null when it cannot start, fails or takes over 15 seconds.</summary>
        public static string? Run(string program, string arguments, Encoding encoding)
        {
            ProcessStartInfo start = new ProcessStartInfo(program, arguments)
            {
                StandardOutputEncoding = encoding,
                CreateNoWindow = true,
            };
            return Capture(start, TimeSpan.FromSeconds(15)) is { Succeeded: true } output ? output.Stdout : null;
        }

        /// <summary>The program's output through agterm's capture, or null when it cannot start or its streams stay open.</summary>
        public static ProcessOutput? Capture(ProcessStartInfo start, TimeSpan timeout)
        {
            try
            {
                return ProcessOutputCapture.Run(start, timeout);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>Merges agshell's status hooks into a Claude Code settings.json, keeping everything else as it was.</summary>
    internal static class HooksInstaller
    {
        /// <summary>The same events and states agterm installs.</summary>
        public static readonly IReadOnlyList<(string Event, string? Matcher, string State)> ClaudeHooks = new[]
        {
            ("UserPromptSubmit", (string?)null, "active --blink"),
            ("PostToolUse", null, "active --blink"),
            ("Stop", null, "completed --auto-reset"),
            ("PermissionRequest", null, "blocked"),
            ("Notification", "permission_prompt", "blocked"),
        };

        /// <summary>Installs into the file and says what happened. The previous file is kept as settings.json.bak.</summary>
        public static string InstallInto(string path, string agshellctl, string? backupPath = null, string? claudeAdapter = null)
        {
            JsonObject root;
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path);
                root = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) as JsonObject ?? throw new JsonException(path + " is not a JSON object");
            }
            else
            {
                root = new JsonObject();
            }

            if (!Merge(root, agshellctl, claudeAdapter))
            {
                return "already up to date (" + path + ")";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
            {
                File.Copy(path, backupPath ?? path + ".bak", true);
            }

            File.WriteAllText(path, Serialize(root), new UTF8Encoding(false));
            return "hooks installed (" + path + ")";
        }

        /// <summary>Adds or updates agshell's entries; returns false when nothing changed.</summary>
        public static bool Merge(JsonObject root, string agshellctl, string? claudeAdapter = null)
        {
            if (root.ContainsKey("hooks") && root["hooks"] is not JsonObject)
            {
                throw new JsonException("hooks is not a JSON object");
            }

            if (root["hooks"] is JsonObject original)
            {
                foreach ((string name, string? _, string _) in ClaudeHooks)
                {
                    if (original.ContainsKey(name) && (original[name] is not JsonArray entries || entries.Any(entry => entry is not JsonObject)))
                    {
                        throw new JsonException("hooks." + name + " is not an array of objects");
                    }
                }
            }

            bool changed = false;
            if (root["hooks"] is not JsonObject hooks)
            {
                hooks = new JsonObject();
                root["hooks"] = hooks;
                changed = true;
            }

            foreach ((string name, string? matcher, string state) in ClaudeHooks)
            {
                // agterm's Claude Code adapter: the wrapper behind the worker guard.
                string command = (claudeAdapter ?? agshellctl + " hook claude") + " " + state;
                if (hooks[name] is not JsonArray entries)
                {
                    entries = new JsonArray();
                    hooks[name] = entries;
                }

                JsonObject? existing = entries.OfType<JsonObject>().FirstOrDefault(entry =>
                    entry["matcher"]?.GetValue<string>() == matcher && HookCommands(entry).Any(IsOurs));
                if (existing is null)
                {
                    JsonObject entry = new JsonObject();
                    if (matcher is not null)
                    {
                        entry["matcher"] = matcher;
                    }

                    entry["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command });
                    entries.Add((JsonNode)entry);
                    changed = true;
                    continue;
                }

                foreach (JsonObject hook in HookCommands(existing).Where(IsOurs))
                {
                    string? previous = hook["command"]?.GetValue<string>();
                    if (previous != command && IsGeneratedCommand(previous, name, state))
                    {
                        hook["command"] = command;
                        changed = true;
                    }
                }
            }

            return changed;
        }

        private static bool IsGeneratedCommand(string? command, string name, string state)
        {
            if (command is null)
            {
                return false;
            }

            foreach (string suffix in name == "UserPromptSubmit" ? new[] { state, "active" } : new[] { state })
            {
                foreach (string adapter in new[] { " hook claude ", " hook ", " " })
                {
                    string tail = adapter + suffix;
                    if (!command.EndsWith(tail, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string executable = command[..^tail.Length];
                    if (executable.Length >= 2 && executable[0] is '\'' or '"' && executable[^1] == executable[0])
                    {
                        executable = executable[1..^1];
                    }
                    else if (executable.Any(char.IsWhiteSpace))
                    {
                        continue;
                    }

                    if (adapter == " ")
                    {
                        if (executable.EndsWith("/agshell-claude-status.sh", StringComparison.Ordinal)) return true;
                        continue;
                    }

                    if (executable.EndsWith("/agshellctl.exe", StringComparison.OrdinalIgnoreCase) ||
                        executable.EndsWith("\\agshellctl.exe", StringComparison.OrdinalIgnoreCase) ||
                        executable is "agshellctl.exe" or "agshellctl")
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static IEnumerable<JsonObject> HookCommands(JsonObject entry)
        {
            return entry["hooks"] is JsonArray hooks ? hooks.OfType<JsonObject>() : Enumerable.Empty<JsonObject>();
        }

        private static bool IsOurs(JsonObject hook)
        {
            string? command = hook["command"] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
            return command is not null && ((command.Contains("agshellctl", StringComparison.OrdinalIgnoreCase) && command.Contains(" hook ", StringComparison.Ordinal))
                || command.Contains("/agshell-claude-status.sh", StringComparison.Ordinal));
        }

        private static string Serialize(JsonObject root)
        {
            using MemoryStream stream = new MemoryStream();
            using (Utf8JsonWriter writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                root.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
        }
    }
}
