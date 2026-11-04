namespace AgShell.Protocol.Tests
{
    public class DebugStateDirectoryTests
    {
        private const string Standard = @"C:\Users\u\AppData\Roaming\agshell";

        [Fact]
        public void ADebugLaunchWithoutADirectoryTakesTheSibling()
        {
            Assert.Equal(@"C:\Users\u\AppData\Roaming\agshell-debug", DebugStateDirectory.Adopted(null, Standard));
            Assert.Equal(@"C:\Users\u\AppData\Roaming\agshell-debug", DebugStateDirectory.Adopted(" ", Standard));
            Assert.Null(DebugStateDirectory.Adopted(@"D:\test-state", Standard));
        }

        [Fact]
        public void TheSiblingReadsTheStandardConfigAndAnyOtherDirectoryItsOwn()
        {
            Assert.Equal(Standard, DebugStateDirectory.ConfigDirectory(@"C:\Users\u\AppData\Roaming\AGSHELL-DEBUG\", Standard));
            Assert.Equal(@"D:\test-state", DebugStateDirectory.ConfigDirectory(@"D:\test-state", Standard));
            Assert.Equal(Standard, DebugStateDirectory.ConfigDirectory(Standard, Standard));
        }
    }
}
