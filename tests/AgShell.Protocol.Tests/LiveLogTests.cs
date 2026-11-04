namespace AgShell.Protocol.Tests
{
    public class LiveLogTests
    {
        [Fact]
        public void APanesDaemonLogsUnderItsStatesDirectoryByItsPipe()
        {
            string state = Path.Combine(Path.GetTempPath(), "agshell-live-log-" + Guid.NewGuid().ToString("N"));
            string pipe = ControlEndpoint.LivePanePipe(state, "6C55D514-E8EF-4A11-A15E-21B4ECC1E2DF");
            string directory = LiveLog.Directory(state);
            try
            {
                LiveLog.ForDaemon(pipe).Info("daemon started");

                Assert.Equal(Path.Combine(Path.GetTempPath(), ControlEndpoint.PipeName(state) + "-live", "logs"), directory);
                Assert.Matches(@"^\[\d+\] \[info\] \(default\): daemon started\n$", File.ReadAllText(Path.Combine(directory, pipe + ".log")));
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(directory)!, true);
            }
        }

        [Fact]
        public void APipeThatIsNotAPanesLogsNowhere()
        {
            string state = Path.Combine(Path.GetTempPath(), "agshell-live-log-" + Guid.NewGuid().ToString("N"));
            string directory = LiveLog.Directory(state);

            LiveLog.ForDaemon(ControlEndpoint.LivePipePrefix(state) + "test-" + Guid.NewGuid().ToString("N")).Info("lost");

            Assert.False(Directory.Exists(directory));
        }

        [Fact]
        public void ALogPastItsLimitStartsEmptyAgain()
        {
            string path = Path.Combine(Path.GetTempPath(), "agshell-live-log-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                LiveLog log = new LiveLog(path, maxBytes: 200);
                for (int line = 0; line < 10; line++)
                {
                    log.Debug("filler line " + line);
                }

                string text = File.ReadAllText(path);
                Assert.True(new FileInfo(path).Length < 260);
                Assert.Contains("filler line 9", text);
                Assert.DoesNotContain("filler line 0", text);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void APruneRemovesTheLogsOfEarlierBootsOnly()
        {
            string state = Path.Combine(Path.GetTempPath(), "agshell-live-log-" + Guid.NewGuid().ToString("N"));
            string directory = LiveLog.Directory(state);
            Directory.CreateDirectory(directory);
            try
            {
                string old = Path.Combine(directory, "old.log");
                string fresh = Path.Combine(directory, "fresh.log");
                File.WriteAllText(old, "old");
                File.WriteAllText(fresh, "fresh");
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64) - TimeSpan.FromMinutes(1));

                LiveLog.Prune(state);

                Assert.False(File.Exists(old));
                Assert.True(File.Exists(fresh));
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(directory)!, true);
            }
        }
    }
}
