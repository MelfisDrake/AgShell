using AgShell.Live;
using Xunit;

namespace AgShell.Live.Tests
{
    /// <summary>zmx's parseOsc7Cwd and toOsc7Cwd tests (src/util.zig), with a Windows path.</summary>
    public class Osc7CwdTests
    {
        [Theory]
        [InlineData("file://myhost/private/tmp", "myhost", "/private/tmp", true)]
        [InlineData("file://myhost/tmp/zmx%20spaced%20dir", "myhost", "/tmp/zmx spaced dir", true)]
        [InlineData("kitty-shell-cwd://myhost/private/tmp", "myhost", "/private/tmp", true)]
        [InlineData("file://otherhost/home/me", "myhost", "/home/me", false)]
        [InlineData("file:///private/tmp", "myhost", "/private/tmp", true)]
        [InlineData("file://localhost/private/tmp", "myhost", "/private/tmp", true)]
        [InlineData("file://myhost.local/private/tmp", "myhost", "/private/tmp", true)]
        [InlineData("file://myhost/private/tmp", "myhost.lan", "/private/tmp", true)]
        [InlineData("file://MyHost/private/tmp", "myhost", "/private/tmp", true)]
        [InlineData("/private/tmp", "myhost", "/private/tmp", true)]
        [InlineData("file://myhost/C:/Users/me%20too", "myhost", @"C:\Users\me too", true)]
        [InlineData(@"C:\Users\me", "myhost", @"C:\Users\me", true)]
        public void AUsableValueGivesItsPath(string value, string hostname, string path, bool isLocal)
        {
            Assert.Equal((path, isLocal), Osc7Cwd.Parse(value, hostname));
        }

        [Theory]
        [InlineData("")]
        [InlineData("some/dir")]
        [InlineData("http://myhost/private/tmp")]
        [InlineData("file://myhost")]
        public void AnUnusableValueGivesNothing(string value)
        {
            Assert.Null(Osc7Cwd.Parse(value, "myhost"));
        }

        [Theory]
        [InlineData("/private/tmp", "file://myhost/private/tmp")]
        [InlineData("/tmp/zmx spaced dir", "file://myhost/tmp/zmx%20spaced%20dir")]
        [InlineData("/tmp/100%", "file://myhost/tmp/100%25")]
        [InlineData("/tmp/a-b_c.d~e", "file://myhost/tmp/a-b_c.d~e")]
        [InlineData(@"C:\Users\me", "file://myhost/C:/Users/me")]
        public void APlainPathBecomesAnOsc7Uri(string path, string uri)
        {
            Assert.Equal(uri, Osc7Cwd.ToUri(path, "myhost"));
        }

        [Theory]
        [InlineData("/private/tmp")]
        [InlineData("/tmp/zmx spaced dir")]
        [InlineData("/tmp/100%")]
        [InlineData("/tmp/a-b_c.d~e")]
        [InlineData("/tmp/quote'and\"dquote")]
        [InlineData(@"C:\Users\Мой каталог")]
        public void TheUriParsesBackToThePath(string path)
        {
            Assert.Equal((path, true), Osc7Cwd.Parse(Osc7Cwd.ToUri(path, "myhost"), "myhost"));
        }
    }
}
