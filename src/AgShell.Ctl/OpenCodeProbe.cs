using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AgShell.Protocol;

namespace AgShell.Ctl
{
    /// <summary>
    /// agterm's OpenCodeEnvironmentProbe: the OpenCode major version and its v2 configuration directory as the user's own shell
    /// sees them. In WSL that is the login shell with its rc files, as in agterm; Windows has no rc files, so its environment
    /// is agshellctl's own and the version comes from cmd.
    /// </summary>
    internal static partial class OpenCodeProbe
    {
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

        /// <summary><paramref name="Version"/> is 1, 2 or null when it cannot be told; the directory is in the home's own terms.</summary>
        public sealed record Result(int? Version, string? V2ConfigurationDirectory);

        public static Result? Detect(AgentHome home)
        {
            return home.Distribution is null ? DetectWindows(home) : DetectWsl(home.Distribution, home.UnixHome);
        }

        private static Result? DetectWindows(AgentHome home)
        {
            ProcessStartInfo start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/d /c opencode --version")
            {
                StandardOutputEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };
            Isolate(start);
            ProcessOutput? output = Installer.Capture(start, Timeout);
            string? overrideDirectory = Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR");
            return new Result(
                output is { Succeeded: true } ? ParseVersion(output.Stdout) : null,
                ConfigurationDirectory(home.Directory, overrideDirectory, Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), Environment.CurrentDirectory, posix: false));
        }

        private static Result? DetectWsl(string distribution, string unixHome)
        {
            string wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            if (!File.Exists(wsl))
            {
                return null;
            }

            string marker = "agshell-opencode-version-" + Guid.NewGuid().ToString("D");
            ProcessStartInfo start = new ProcessStartInfo(wsl)
            {
                StandardOutputEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };
            foreach (string argument in WslArguments(distribution, marker))
            {
                start.ArgumentList.Add(argument);
            }

            Isolate(start);
            ProcessOutput? output = Installer.Capture(start, Timeout);
            return output is null ? null : Parse(output.Stdout, marker, output.Succeeded, unixHome);
        }

        /// <summary>
        /// The login shell, interactive as in agterm so the rc files that put opencode on PATH are read; its startup output
        /// comes before the marker. A POSIX hop prints only exported values, fish's included. wsl -e starts sh without the
        /// user's SHELL in some distributions, so the passwd entry stands in for it.
        /// </summary>
        public static IReadOnlyList<string> WslArguments(string distribution, string marker)
        {
            string command = "/usr/bin/printf '\\n" + marker + "\\n%s\\000%s\\000%s\\000%s\\000%s\\000' "
                + "\"$HOME\" \"$PWD\" \"${OPENCODE_CONFIG_DIR+x}\" \"${OPENCODE_CONFIG_DIR-}\" \"${XDG_CONFIG_HOME-}\"; "
                + "exec /usr/bin/env opencode --version";
            string login = "shell=\"${SHELL:-$(getent passwd \"$(id -un)\" | cut -d: -f7)}\"; exec \"${shell:-/bin/sh}\" -ilc \"$1\"";
            return new[] { "-d", distribution, "-e", "sh", "-c", login, "sh", "exec /bin/sh -c " + ShellQuote(command) };
        }

        /// <summary>The probe's printout: what follows the marker, five NUL-ended fields and the version line.</summary>
        public static Result? Parse(string stdout, string marker, bool succeeded, string fallbackHome)
        {
            string boundary = "\n" + marker + "\n";
            int at = stdout.IndexOf(boundary, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }

            string[] fields = stdout[(at + boundary.Length)..].Split('\0', 6);
            if (fields.Length != 6)
            {
                return null;
            }

            return new Result(
                succeeded ? ParseVersion(fields[5]) : null,
                ConfigurationDirectory(fields[0].Length == 0 ? fallbackHome : fields[0], fields[2].Length == 0 ? null : fields[3], fields[4], fields[1], posix: true));
        }

        /// <summary>agterm's Version(versionOutput:): a semantic version, maybe after "opencode " or "v"; majors 1 and 2 only.</summary>
        public static int? ParseVersion(string output)
        {
            string text = output.Trim();
            if (text.StartsWith("opencode ", StringComparison.Ordinal))
            {
                text = text["opencode ".Length..];
            }

            Match version = VersionPattern().Match(text);
            if (!version.Success)
            {
                return null;
            }

            return version.Groups[1].Value switch
            {
                "1" => 1,
                "2" => 2,
                _ => null,
            };
        }

        /// <summary>
        /// agterm's configurationDirectory: OPENCODE_CONFIG_DIR when set (set but empty tells nothing), else
        /// $XDG_CONFIG_HOME/opencode, else ~/.config/opencode; a relative one is taken from the working directory.
        /// </summary>
        public static string? ConfigurationDirectory(string home, string? opencodeConfigDirectory, string? xdgConfigHome, string workingDirectory, bool posix)
        {
            string separator = posix ? "/" : "\\";
            string path;
            if (opencodeConfigDirectory is not null)
            {
                if (opencodeConfigDirectory.Length == 0)
                {
                    return null;
                }

                path = opencodeConfigDirectory;
            }
            else
            {
                string configBase = string.IsNullOrEmpty(xdgConfigHome) ? home + separator + ".config" : xdgConfigHome;
                path = configBase + separator + "opencode";
            }

            if (!posix)
            {
                return Path.GetFullPath(Path.Combine(workingDirectory, path));
            }

            return Standardized(path.StartsWith('/') ? path : workingDirectory + "/" + path);
        }

        /// <summary>A POSIX path without its empty, "." and ".." parts, as URL.standardizedFileURL leaves it.</summary>
        public static string Standardized(string path)
        {
            List<string> parts = new List<string>();
            foreach (string part in path.Split('/'))
            {
                if (part.Length == 0 || part == ".")
                {
                    continue;
                }

                if (part == "..")
                {
                    if (parts.Count > 0)
                    {
                        parts.RemoveAt(parts.Count - 1);
                    }

                    continue;
                }

                parts.Add(part);
            }

            return "/" + string.Join("/", parts);
        }

        /// <summary>agterm drops its session id from the probe; here every AGSHELL_ variable goes, and WSLENV has nothing of them to carry.</summary>
        public static void Isolate(ProcessStartInfo start)
        {
            foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("AGSHELL_", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                start.Environment.Remove(key);
            }
        }

        private static string ShellQuote(string text)
        {
            return "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        }

        [GeneratedRegex(@"^v?([0-9]+)\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")]
        private static partial Regex VersionPattern();
    }
}
