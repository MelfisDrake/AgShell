using System.Text;

namespace AgShell.Pty.Tests
{
    public sealed class ShellProfilesTests : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("agshell cwd ");

        public void Dispose()
        {
            directory.Delete(true);
        }

        [Fact]
        public void DetectFindsBuiltInShells()
        {
            IReadOnlyList<ShellProfile> profiles = ShellProfiles.Detect();

            Assert.Contains(profiles, profile => profile.Id == ShellProfiles.CmdId);
            Assert.Contains(profiles, profile => profile.Id == ShellProfiles.WindowsPowerShellId);
        }

        [Fact]
        public async Task EnvironmentOverrideReachesProcess()
        {
            string output = await RunUntilAsync(
                new PtyStartOptions
                {
                    CommandLine = "cmd.exe /c echo value=%AGSHELL_TEST_VAR%",
                    Environment = new Dictionary<string, string?> { ["AGSHELL_TEST_VAR"] = "из-agshell" },
                },
                "value=из-agshell");

            Assert.Contains("value=из-agshell", output);
        }

        [Fact]
        public async Task ACommandRunsThroughTheShellAndEndsWithItsStatus()
        {
            // agterm runs a command through /bin/sh -c; here cmd.exe, for a PowerShell profile too.
            ShellProfile powerShell = ShellProfiles.Detect().Single(profile => profile.Id == ShellProfiles.WindowsPowerShellId);
            using PseudoConsoleSession session = new PseudoConsoleSession(ShellProfiles.CreateCommandOptions(powerShell, "echo one&& set /a 2+3 >nul&& exit 5", directory.FullName, 120, 30));
            session.Start();

            Assert.Equal(5, await session.Exit.WaitAsync(Timeout));
        }

        [Fact]
        public async Task CmdReportsWorkingDirectory()
        {
            ShellProfile cmd = ShellProfiles.Detect().Single(profile => profile.Id == ShellProfiles.CmdId);

            await RunUntilAsync(ShellProfiles.CreateStartOptions(cmd, directory.FullName, 120, 30), "\u001b]9;9;" + directory.FullName);
        }

        [Fact]
        public async Task PowerShellReportsWorkingDirectory()
        {
            ShellProfile powerShell = ShellProfiles.Detect().Single(profile => profile.Id == ShellProfiles.WindowsPowerShellId);

            // Ghostty's shell integration form: OSC 7 kitty-shell-cwd://host/path, the path as it is.
            await RunUntilAsync(ShellProfiles.CreateStartOptions(powerShell, directory.FullName, 120, 30), "\u001b]7;kitty-shell-cwd://" + Environment.MachineName + "/" + directory.FullName.Replace('\\', '/') + "\u0007");
        }

        [Fact]
        public async Task MissingWorkingDirectoryFallsBackToHome()
        {
            ShellProfile cmd = ShellProfiles.Detect().Single(profile => profile.Id == ShellProfiles.CmdId);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            await RunUntilAsync(ShellProfiles.CreateStartOptions(cmd, @"C:\agshell-no-such-directory", 120, 30), "\u001b]9;9;" + home);
        }

        [Fact]
        public async Task WslReportsWorkingDirectory()
        {
            ShellProfile? wsl = ShellProfiles.Detect().FirstOrDefault(profile => profile.Kind == ShellKind.Wsl);
            if (wsl is null)
            {
                return;
            }

            string output = await RunUntilAsync(ShellProfiles.CreateStartOptions(wsl, "/tmp", 120, 30), "/tmp\u0007");
            Assert.Matches("\u001b\\]7;kitty-shell-cwd://[^/]*/tmp\u0007", output);
        }

        [Fact]
        public async Task CmdRunsTheReplayFirstAndStaysInteractive()
        {
            ShellProfile cmd = ShellProfiles.Detect().Single(profile => profile.Id == ShellProfiles.CmdId);

            string output = await RunUntilAsync(ShellProfiles.CreateStartOptions(cmd, directory.FullName, 120, 30, replay: "echo replayed-%OS%"), "\u001b]9;9;" + directory.FullName);
            Assert.InRange(output.IndexOf("replayed-Windows_NT", StringComparison.Ordinal), 0, output.IndexOf("\u001b]9;9;", StringComparison.Ordinal));
        }

        [Fact]
        public async Task PowerShellRunsTheReplayFirstAndStaysInteractive()
        {
            ShellProfile powerShell = ShellProfiles.Detect().Single(profile => profile.Id == ShellProfiles.WindowsPowerShellId);

            string output = await RunUntilAsync(ShellProfiles.CreateStartOptions(powerShell, directory.FullName, 120, 30, replay: "Write-Output ('replayed-' + (1 + 2))"), "\u001b]7;kitty-shell-cwd://");
            Assert.InRange(output.IndexOf("replayed-3", StringComparison.Ordinal), 0, output.IndexOf("\u001b]7;", StringComparison.Ordinal));
        }

        [Fact]
        public async Task WslRunsTheReplayInTheLoginShellAndBecomesIt()
        {
            ShellProfile? wsl = ShellProfiles.Detect().FirstOrDefault(profile => profile.Kind == ShellKind.Wsl);
            if (wsl is null)
            {
                return;
            }

            // The login shell runs the line, then is exec'd interactive, whose prompt reports the directory.
            string output = await RunUntilAsync(ShellProfiles.CreateStartOptions(wsl, "/tmp", 120, 30, replay: "echo replayed-$((1+2)) \"$0\""), "/tmp\u0007");
            Assert.InRange(output.IndexOf("replayed-3", StringComparison.Ordinal), 0, output.IndexOf("\u001b]7;", StringComparison.Ordinal));
        }

        private static async Task<string> RunUntilAsync(PtyStartOptions options, string expected)
        {
            object sync = new object();
            StringBuilder output = new StringBuilder();
            using PseudoConsoleSession session = new PseudoConsoleSession(options);
            session.Output += chunk =>
            {
                lock (sync)
                {
                    output.Append(chunk);
                }
            };
            session.Start();

            DateTime deadline = DateTime.UtcNow + Timeout;
            while (true)
            {
                string text;
                lock (sync)
                {
                    text = output.ToString();
                }

                if (text.Contains(expected, StringComparison.OrdinalIgnoreCase))
                {
                    session.Dispose();
                    await session.Exit.WaitAsync(Timeout);
                    return text;
                }

                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("Output did not contain '" + expected + "'. Output was: " + text);
                }

                await Task.Delay(50);
            }
        }
    }
}
