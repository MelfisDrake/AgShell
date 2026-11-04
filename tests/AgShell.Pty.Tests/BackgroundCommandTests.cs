using System.Diagnostics;
using System.Text;

namespace AgShell.Pty.Tests
{
    public class BackgroundCommandTests
    {
        [Fact]
        public async Task InputIsUtf8AndOutputDoesNotNeedAReader()
        {
            string output = Path.Combine(Path.GetTempPath(), "agshell-hook-" + Guid.NewGuid().ToString("N"));
            try
            {
                ProcessStartInfo start = Shell("more > \"" + output + "\" & echo discarded & echo discarded 1>&2");
                start.Environment["AGSHELL_HOOK_TEST"] = "keep";
                using BackgroundCommand command = BackgroundCommand.Start(start, true);
                await command.WriteInputAsync(Encoding.UTF8.GetBytes("{\"name\":\"проверка\"}\n"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Contains("проверка", File.ReadAllText(output, Encoding.UTF8));
            }
            finally
            {
                File.Delete(output);
            }
        }

        [Fact]
        public async Task ExitAndCancellationDoNotWaitForDescendantHoldingInput()
        {
            string pidFile = Path.Combine(Path.GetTempPath(), "agshell-hook-pid-" + Guid.NewGuid().ToString("N"));
            Process? descendant = null;
            try
            {
                using BackgroundCommand command = BackgroundCommand.Start(Shell("start /b \"\" powershell.exe -NoProfile -Command \"$PID | Set-Content '" + pidFile + "'; Start-Sleep -Seconds 30\""), true);
                using CancellationTokenSource cancel = new CancellationTokenSource();
                Task writing = command.WriteInputAsync(new byte[4 * 1024 * 1024], cancel.Token);
                Assert.Equal(0, await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (!File.Exists(pidFile) && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(25);
                }

                descendant = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile).Trim()));
                Assert.False(writing.IsCompleted);
                await cancel.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                if (descendant is not null)
                {
                    if (!descendant.HasExited) descendant.Kill();
                    descendant.Dispose();
                }
                else if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid))
                {
                    using Process child = Process.GetProcessById(pid);
                    if (!child.HasExited) child.Kill();
                }

                File.Delete(pidFile);
            }
        }

        [Fact]
        public async Task StderrWriterSurvivesSnapshotRemovalAndParentExit()
        {
            string root = Path.Combine(Path.GetTempPath(), "agshell-stderr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            AgShell.Protocol.CommandStderrFile capture = AgShell.Protocol.CommandStderrFile.TryCreate()!;
            try
            {
                string done = Path.Combine(root, "done");
                string child = Path.Combine(root, "child.ps1");
                File.WriteAllText(child, "Start-Sleep -Milliseconds 800; [Console]::Error.WriteLine('late'); 'done' | Set-Content '" + done + "'");
                using BackgroundCommand command = BackgroundCommand.Start(Shell("echo early 1>&2 & start /b \"\" powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"" + child + "\""), false, capture.Path);
                Assert.Equal(0, await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                string tail = Encoding.UTF8.GetString(capture.Consume(16384));
                Assert.Contains("early", tail);
                Assert.DoesNotContain("late", tail);
                DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                while (!File.Exists(done) && DateTime.UtcNow < deadline) await Task.Delay(25);
                Assert.True(File.Exists(done));
                Assert.False(File.Exists(capture.Path));
            }
            finally
            {
                capture.Dispose();
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task UnavailableStderrFileFallsBackToNull()
        {
            using BackgroundCommand command = BackgroundCommand.Start(Shell("echo discarded 1>&2"), false,
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.err"));
            Assert.NotNull(command.ErrorCaptureFailure);
            Assert.Equal(0, await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }

        private static ProcessStartInfo Shell(string line)
        {
            return new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "/d /s /c \"" + line + "\"")
            {
                WorkingDirectory = Path.GetTempPath(),
            };
        }
    }
}
