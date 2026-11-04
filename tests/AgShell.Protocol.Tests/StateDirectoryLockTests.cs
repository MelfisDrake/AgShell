using AgShell.Protocol;

namespace AgShell.Protocol.Tests
{
    public class StateDirectoryLockTests
    {
        [Fact]
        public void OneOwnerAtATimeAndTheProbeSeesItWithoutTakingIt()
        {
            string directory = Path.Combine(Path.GetTempPath(), "agshell-lock-" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.Null(StateDirectoryLock.Held(directory));
                Assert.Equal(StateDirectoryClaim.Owned, StateDirectoryLock.Acquire(directory, out StateDirectoryLock? owner, out _));
                Assert.Equal(0, new FileInfo(StateDirectoryLock.PathIn(directory)).Length);
                Assert.True(StateDirectoryLock.Held(directory));
                Assert.Equal(StateDirectoryClaim.HeldByAnother, StateDirectoryLock.Acquire(directory, out StateDirectoryLock? second, out _));
                Assert.Null(second);
                owner!.Dispose();
                Assert.True(File.Exists(StateDirectoryLock.PathIn(directory)));
                Assert.False(StateDirectoryLock.Held(directory));
                Assert.Equal(StateDirectoryClaim.Owned, StateDirectoryLock.Acquire(directory, out StateDirectoryLock? next, out _));
                next!.Dispose();
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
