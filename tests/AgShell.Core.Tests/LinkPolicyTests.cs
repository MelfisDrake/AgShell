using AgShell.Core;
using Xunit;

namespace AgShell.Core.Tests
{
    public class LinkPolicyTests
    {
        private static readonly string[] Hosts = { "localhost", "box" };

        private static LinkRoute Route(string raw, bool overlay = false, LinkOrigin origin = LinkOrigin.Pane)
        {
            return LinkPolicy.Route(raw, overlay, origin, path => path.StartsWith("/home/", StringComparison.Ordinal) ? @"\\wsl.localhost\Ubuntu" + path.Replace('/', '\\') : null, Hosts);
        }

        [Fact]
        public void WebAndMailOpenOtherSchemesAreIgnored()
        {
            Assert.Equal(LinkAction.Browser, Route("https://example.com/a").Action);
            Assert.Equal(LinkAction.Browser, Route("mailto:a@b.c").Action);
            Assert.Equal(LinkAction.Browser, Route("ftp://h/x").Action);
            Assert.Equal(LinkAction.Ignore, Route("javascript:alert(1)").Action);
            Assert.Equal(LinkAction.Ignore, Route("ms-settings:display").Action);
            Assert.Equal(LinkAction.Ignore, Route("not a link").Action);
        }

        [Fact]
        public void OverlayModeTakesPageLinksOfPanesOnly()
        {
            Assert.Equal(LinkAction.Overlay, Route("https://example.com", overlay: true).Action);
            Assert.Equal(LinkAction.Overlay, Route("http://devbox:3000", overlay: true, origin: LinkOrigin.Scratch).Action);
            Assert.Equal(LinkAction.Browser, Route("http://example.com", overlay: true).Action);
            Assert.Equal(LinkAction.Browser, Route("https://example.com", overlay: true, origin: LinkOrigin.Quick).Action);
            Assert.Equal(LinkAction.Browser, Route("https://example.com", overlay: true, origin: LinkOrigin.ProgramOverlay).Action);
            Assert.Equal(LinkAction.Browser, Route("mailto:a@b.c", overlay: true).Action);
        }

        [Fact]
        public void LocalFilesAreShownNeverOpened()
        {
            Assert.Equal(new LinkRoute(LinkAction.Reveal, @"C:\src\a.exe"), Route("file:///C:/src/a.exe"));
            Assert.Equal(new LinkRoute(LinkAction.Reveal, @"C:\src\a.txt"), Route("file://BOX/C:/src/x/../a.txt"));
            Assert.Equal(new LinkRoute(LinkAction.Reveal, @"\\wsl.localhost\Ubuntu\home\me\f"), Route("file://box/home/me/f"));
            Assert.Equal(LinkAction.Ignore, Route("file://other/C:/x").Action);
            Assert.Equal(LinkAction.Ignore, Route("file:////server/share/x").Action);
            Assert.Equal(LinkAction.Ignore, Route("file:///etc/passwd").Action);
        }
    }
}
