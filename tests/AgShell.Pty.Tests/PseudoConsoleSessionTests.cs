using System.Diagnostics;
using System.Text;
using Xunit.Abstractions;

namespace AgShell.Pty.Tests
{
    public class PseudoConsoleSessionTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
        private readonly ITestOutputHelper testOutput;

        public PseudoConsoleSessionTests(ITestOutputHelper testOutput)
        {
            this.testOutput = testOutput;
        }

        [Fact]
        public async Task CommandOutputReachesSubscriber()
        {
            OutputCollector collector = new OutputCollector();
            using PseudoConsoleSession session = new PseudoConsoleSession(new PtyStartOptions { CommandLine = "cmd.exe /c echo agshell-test-marker" });
            session.Output += collector.Append;
            session.Start();

            int exitCode = await session.Exit.WaitAsync(Timeout);
            session.Dispose();
            await session.OutputClosed.WaitAsync(Timeout);

            Assert.Equal(0, exitCode);
            Assert.Contains("agshell-test-marker", collector.Text);
        }

        [Fact]
        public async Task ExitCodeIsReported()
        {
            using PseudoConsoleSession session = new PseudoConsoleSession(new PtyStartOptions { CommandLine = "cmd.exe /c exit 7" });
            int reported = -100;
            session.Exited += code => reported = code;
            session.Start();

            int exitCode = await session.Exit.WaitAsync(Timeout);

            Assert.Equal(7, exitCode);
            Assert.Equal(7, reported);
        }

        [Fact]
        public async Task InputIsExecutedByShell()
        {
            OutputCollector collector = new OutputCollector();
            using PseudoConsoleSession session = new PseudoConsoleSession(new PtyStartOptions { CommandLine = "cmd.exe" });
            session.Output += collector.Append;
            session.Start();

            session.Write("echo agshell-%OS%\r");

            await collector.WaitForAsync("agshell-Windows_NT", Timeout);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CtrlCInterruptsRunningCommand(bool inheritedIgnore)
        {
            string directory = Path.Combine(Path.GetTempPath(), "agshell-pty-ctrlc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            ProcessStartInfo start = new ProcessStartInfo(Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory
            };
            start.ArgumentList.Add("ctrl-c");
            start.ArgumentList.Add(inheritedIgnore.ToString());
            start.Environment["AGSHELL_STATE_DIR"] = directory;
            start.Environment["AGSHELL_PIPE"] = "agshell-pty-test-" + Guid.NewGuid().ToString("N");
            using Process host = new Process { StartInfo = start };
            bool started = false;
            try
            {
                started = host.Start();
                Task<string> stdout = host.StandardOutput.ReadToEndAsync();
                Task<string> stderr = host.StandardError.ReadToEndAsync();
                // The scenario has 15 s; the second budget permits its finally to drain its own ConPTY.
                await host.WaitForExitAsync().WaitAsync(Timeout + Timeout);
                string diagnostics = await stdout + await stderr;
                testOutput.WriteLine(diagnostics);
                Assert.True(host.ExitCode == 0, diagnostics);
            }
            finally
            {
                if (started && !host.HasExited)
                {
                    host.Kill(entireProcessTree: true);
                    await host.WaitForExitAsync().WaitAsync(Timeout);
                }

                Directory.Delete(directory, true);
            }
        }

        internal static async Task CheckCtrlCAsync()
        {
            OutputCollector collector = new OutputCollector();
            using PseudoConsoleSession session = new PseudoConsoleSession(new PtyStartOptions { CommandLine = "cmd.exe" });
            session.Output += collector.Append;
            session.Start();
            using Process shell = Process.GetProcessById(session.ProcessId);
            _ = shell.Handle;
            using CancellationTokenSource deadline = new CancellationTokenSource(Timeout);
            Process? ping = null;
            try
            {
                session.Write("echo agshell-ready-%OS%\r");
                await collector.WaitForAsync("agshell-ready-Windows_NT", Timeout).WaitAsync(deadline.Token);
                session.Write("ping -t 127.0.0.1\r");
                await collector.WaitForAsync("Reply from", Timeout).WaitAsync(deadline.Token);
                ping = ChildProcess.FindPing(session.ProcessId);
                _ = ping.Handle;
                Assert.True(ping.StartTime >= shell.StartTime);
                Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE"), ping.MainModule!.FileName, ignoreCase: true);
                Console.WriteLine($"shell={session.ProcessId} ping={ping.Id} created={ping.StartTime.ToUniversalTime():O} path={ping.MainModule.FileName}");
                Assert.False(ping.HasExited);
                Assert.False(session.Exit.IsCompleted);
                session.Write("\x03");
                await ping.WaitForExitAsync(deadline.Token);
                Console.WriteLine($"ping exited={ping.ExitCode} before Dispose");
                Assert.Equal(unchecked((int)0xC000013A), ping.ExitCode);
                await collector.WaitForAsync("Control-C", Timeout).WaitAsync(deadline.Token);
                Assert.False(session.Exit.IsCompleted);
                session.Write("echo agshell-after-%OS%\r");
                await collector.WaitForAsync("agshell-after-Windows_NT", Timeout).WaitAsync(deadline.Token);
                session.Write("exit 0\r");
                Assert.Equal(0, await session.Exit.WaitAsync(deadline.Token));
            }
            finally
            {
                session.Dispose();
                await session.Exit.WaitAsync(Timeout);
                await session.OutputClosed.WaitAsync(Timeout);
                if (ping is not null)
                {
                    await ping.WaitForExitAsync().WaitAsync(Timeout);
                    ping.Dispose();
                }

                Console.WriteLine(collector.Text);
            }
        }

        [Fact]
        public async Task NonAsciiOutputIsDecoded()
        {
            OutputCollector collector = new OutputCollector();
            using PseudoConsoleSession session = new PseudoConsoleSession(new PtyStartOptions { CommandLine = "cmd.exe /c echo привет-мир" });
            session.Output += collector.Append;
            session.Start();

            await session.Exit.WaitAsync(Timeout);
            session.Dispose();
            await session.OutputClosed.WaitAsync(Timeout);

            Assert.Contains("привет-мир", collector.Text);
        }

        [Fact]
        public async Task ResizeWorksAndDisposeEndsShell()
        {
            PseudoConsoleSession session = new PseudoConsoleSession(new PtyStartOptions { CommandLine = "cmd.exe" });
            session.Start();

            session.Resize(80, 20);
            session.Resize(200, 50);
            session.Dispose();

            await session.Exit.WaitAsync(Timeout);
            await session.OutputClosed.WaitAsync(Timeout);
        }

        [Fact]
        public void InvalidSizeIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PseudoConsoleSession(new PtyStartOptions { CommandLine = "cmd.exe", Columns = 0 }));
        }

        [Fact]
        public void MissingExecutableFailsToStart()
        {
            using PseudoConsoleSession session = new PseudoConsoleSession(new PtyStartOptions { CommandLine = "agshell-no-such-program.exe" });

            Assert.Throws<System.ComponentModel.Win32Exception>(() => session.Start());
        }

        private sealed class OutputCollector
        {
            private readonly object sync = new object();
            private readonly StringBuilder text = new StringBuilder();

            public string Text
            {
                get
                {
                    lock (sync)
                    {
                        return text.ToString();
                    }
                }
            }

            public void Append(string chunk)
            {
                lock (sync)
                {
                    text.Append(chunk);
                }
            }

            public async Task WaitForAsync(string expected, TimeSpan timeout)
            {
                DateTime deadline = DateTime.UtcNow + timeout;
                while (!Text.Contains(expected, StringComparison.Ordinal))
                {
                    if (DateTime.UtcNow > deadline)
                    {
                        Assert.Fail("Output did not contain '" + expected + "'. Output was: " + Text);
                    }

                    await Task.Delay(20);
                }
            }
        }
    }
}
