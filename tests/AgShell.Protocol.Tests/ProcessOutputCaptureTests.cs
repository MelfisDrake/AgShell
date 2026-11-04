using System.Diagnostics;
using System.Text;

namespace AgShell.Protocol.Tests
{
    public class ProcessOutputCaptureTests
    {
        private static ProcessStartInfo Cmd(string line)
        {
            return new ProcessStartInfo("cmd.exe", "/d /s /c \"" + line + "\"") { CreateNoWindow = true };
        }

        [Fact]
        public void BothStreamsAreDrainedSoAFullPipeDoesNotHoldTheExit()
        {
            // Well past a pipe's buffer on each stream, stderr first: read one after the other, this would hang.
            ProcessOutput? output = ProcessOutputCapture.Run(Cmd("for /l %i in (1,1,3000) do @(echo error line %i 1>&2 & echo out line %i)"), TimeSpan.FromSeconds(30));
            Assert.NotNull(output);
            Assert.True(output.Succeeded);
            Assert.Contains("out line 3000", output.Stdout);
            Assert.Contains("error line 3000", output.Stderr);
        }

        [Fact]
        public async Task PastTheTimeoutTheTreeIsKilled()
        {
            Stopwatch clock = Stopwatch.StartNew();
            ProcessOutput? output = await ProcessOutputCapture.RunAsync(Cmd("echo started & ping -n 30 127.0.0.1 >nul"), TimeSpan.FromMilliseconds(500));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
            Assert.NotNull(output);
            Assert.True(output.TimedOut);
            Assert.False(output.Succeeded);
            Assert.Contains("started", output.Stdout);
        }

        [Fact]
        public void TheExitCodeAndStderrComeBack()
        {
            ProcessOutput? output = ProcessOutputCapture.Run(Cmd("echo nope 1>&2 & exit 3"), null);
            Assert.NotNull(output);
            Assert.Equal(3, output.ExitCode);
            Assert.False(output.TimedOut);
            Assert.Equal("nope", output.Stderr.Trim());
        }

        [Fact]
        public async Task AStreamTailKeepsOnlyTheLastBytes()
        {
            byte[] data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 5000).Select(i => i.ToString("D5") + "\n")));
            StreamTail tail = new StreamTail(new MemoryStream(data), 1000);
            await tail.Completion;
            byte[] kept = tail.Snapshot();
            Assert.Equal(data[^1000..], kept);
            Assert.Equal(data[^1000..], await tail.TakeAsync(TimeSpan.Zero));

            StreamTail small = new StreamTail(new MemoryStream(Encoding.ASCII.GetBytes("abc")), 1000);
            await small.Completion;
            Assert.Equal("abc", Encoding.ASCII.GetString(small.Snapshot()));
        }
    }
}
