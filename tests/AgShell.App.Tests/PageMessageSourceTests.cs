namespace AgShell.App.Tests
{
    public class PageMessageSourceTests
    {
        [Theory]
        [InlineData("https://p.localhost/a.html", "https://p.localhost/a.html", true)]
        [InlineData("https://p.localhost/a.html#old", "https://p.localhost/a.html#new", true)]
        [InlineData("https://p.localhost/a.html?x=1", "https://p.localhost/a.html?x=1", true)]
        [InlineData("https://p.localhost/a.html?x=1", "https://p.localhost/a.html?x=2", false)]
        [InlineData("https://p.localhost/b.html", "https://p.localhost/a.html", false)]
        [InlineData("about:blank", "about:blank", false)]
        [InlineData("about:srcdoc", "about:srcdoc", false)]
        [InlineData("data:text/html,probe", "data:text/html,probe", false)]
        [InlineData("https://example.invalid/a.html", "https://example.invalid/a.html", false)]
        [InlineData("https://p.localhost:444/a.html", "https://p.localhost:444/a.html", false)]
        [InlineData("http://p.localhost/a.html", "http://p.localhost/a.html", false)]
        [InlineData("https://p.localhost.evil.invalid/a.html", "https://p.localhost.evil.invalid/a.html", false)]
        [InlineData("https://user@p.localhost/a.html", "https://user@p.localhost/a.html", false)]
        [InlineData("https://other.localhost/a.html", "https://p.localhost/a.html", false)]
        [InlineData("https://p.localhost/a.html", "about:blank", false)]
        [InlineData("invalid", "https://p.localhost/a.html", false)]
        public void OnlyTheCurrentGrantedDocumentCanSend(string source, string current, bool expected)
        {
            Assert.Equal(expected, PageMessageSource.IsTrusted(source, current, "https://p.localhost", uri => uri.AbsolutePath == "/a.html"));
        }

        [Fact]
        public void AHostAloneDoesNotGrantAnUnservedResource()
        {
            Assert.False(PageMessageSource.IsTrusted("https://p.localhost/private", "https://p.localhost/private", "https://p.localhost", _ => false));
            Assert.True(PageMessageSource.IsTrusted("http://replica.localhost:1234/a.html", "http://replica.localhost:1234/a.html", "http://replica.localhost:1234", _ => true));
        }
    }
}
