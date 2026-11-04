using System.Text;

namespace AgShell.Protocol.Tests
{
    public class CommandStderrFileTests
    {
        [Fact]
        public void ReadsOnlyTheBoundedTailAndRemovesTheFile()
        {
            using CommandStderrFile capture = CommandStderrFile.TryCreate()!;
            Assert.NotNull(capture);
            File.WriteAllText(capture.Path, new string('x', 30000) + "last line", new UTF8Encoding(false));
            byte[] tail = capture.Consume(16384);
            Assert.Equal(16384, tail.Length);
            Assert.EndsWith("last line", Encoding.UTF8.GetString(tail));
            Assert.False(File.Exists(capture.Path));
        }
    }
}
