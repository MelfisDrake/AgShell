using AgShell.Core;
using Xunit;

namespace AgShell.Core.Tests
{
    public class RestartReplayTests
    {
        private static readonly HashSet<string> NoDenylist = new HashSet<string>();

        private static RestartReplay.Observation Program(string executable, string commandLine, string? directory = @"C:\work")
        {
            return new RestartReplay.Observation((executable, commandLine, directory), null);
        }

        [Fact]
        public void AShellAtItsPromptIsRefusedAsHoldingThePaneNotAsUnreadable()
        {
            Assert.Equal("no replayable foreground program: pwsh holds the pane" + RestartReplay.Hint,
                RestartReplay.Resolve(new RestartReplay.Observation(null, "pwsh.exe"), NoDenylist).Error);
            Assert.Equal("no replayable foreground program: bash holds the pane" + RestartReplay.Hint,
                RestartReplay.Resolve(new RestartReplay.Observation(null, "-bash"), NoDenylist).Error);
        }

        [Fact]
        public void AForegroundThatCannotBeReadIsRefusedAsUnreadable()
        {
            Assert.Equal("the pane's foreground program cannot be read" + RestartReplay.Hint,
                RestartReplay.Resolve(new RestartReplay.Observation(null, null), NoDenylist).Error);
            Assert.Equal("the pane's foreground program cannot be read" + RestartReplay.Hint,
                RestartReplay.Resolve(Program("vim.exe", " "), NoDenylist).Error);
        }

        [Fact]
        public void TheOtherRefusalsUseAgtermsWords()
        {
            Assert.Equal("no replayable foreground program: sh holds the pane" + RestartReplay.Hint,
                RestartReplay.Resolve(Program("/bin/sh", "sh -i"), NoDenylist).Error);
            Assert.Equal("sleep is on the restore denylist" + RestartReplay.Hint,
                RestartReplay.Resolve(Program("/bin/sleep", "sleep 300"), new HashSet<string> { "sleep" }).Error);
            Assert.Equal("the foreground program's arguments cannot be replayed" + RestartReplay.Hint,
                RestartReplay.Resolve(Program("vim.exe", "vim \u001b"), NoDenylist).Error);
            Assert.Equal("the foreground program's command line is too long to replay (max 4096 bytes)" + RestartReplay.Hint,
                RestartReplay.Resolve(Program("vim.exe", "vim " + new string('a', 4096)), NoDenylist).Error);
            Assert.Equal("the foreground program's working directory is unavailable" + RestartReplay.Hint,
                RestartReplay.Resolve(Program("vim.exe", "vim notes.txt", null), NoDenylist).Error);
        }

        [Fact]
        public void AProgramIsReplayedInItsDirectory()
        {
            Assert.Equal(("vim notes.txt", @"C:\work", (string?)null), RestartReplay.Resolve(Program("vim.exe", "vim notes.txt"), NoDenylist));
        }
    }
}
