namespace AgShell.Protocol.Tests
{
    public class KeystrokesTests
    {
        [Fact]
        public void FinalReturnAfterTextIsPaced()
        {
            Assert.Equal(new Keystrokes("make test", true), Keystrokes.From("make test\n"));
            Assert.Equal(new Keystrokes("a\rb", true), Keystrokes.From("a\r\nb\r\n"));
        }

        [Fact]
        public void TextWithoutFinalNewlineOrOnlyReturnsIsSentAtOnce()
        {
            Assert.Equal(new Keystrokes("echo hi", false), Keystrokes.From("echo hi"));
            Assert.Equal(new Keystrokes("\r\r", false), Keystrokes.From("\n\n"));
        }
    }
}
