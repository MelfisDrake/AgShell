namespace AgShell.Core.Tests
{
    public class PageNavigationTests
    {
        private const string FileOrigin = "https://p1.agshell-page";
        private static readonly PageSpec FilePage = new PageSpec { Source = @"C:\pages\a.html", IsFile = true };
        private static readonly PageSpec WebPage = new PageSpec { Source = "https://example.com/start" };

        private static PageNavigationDecision File(string url, PageNavigationTarget target = PageNavigationTarget.MainFrame, bool user = false)
        {
            return PageNavigation.Decide(url, target, user, FilePage, FileOrigin, uri => uri.AbsolutePath != "/outside.html");
        }

        private static PageNavigationDecision Web(string url, PageNavigationTarget target = PageNavigationTarget.MainFrame, bool user = false, PageSpec? page = null)
        {
            return PageNavigation.Decide(url, target, user, page ?? WebPage, "https://example.com", null);
        }

        [Fact]
        public void FilePageStaysWithinItsFiles()
        {
            Assert.Equal(PageNavigationDecision.Allow, File(FileOrigin + "/b.html"));
            Assert.Equal(PageNavigationDecision.Cancel, File(FileOrigin + "/outside.html", user: true));
            Assert.Equal(PageNavigationDecision.Allow, File("about:blank"));
            Assert.Equal(PageNavigationDecision.Cancel, File("file:///C:/pages/b.html", user: true));
            Assert.Equal(PageNavigationDecision.Cancel, File("data:text/html,x", PageNavigationTarget.Subframe));
        }

        [Fact]
        public void OnlyAClickHandsAWebAddressToTheBrowser()
        {
            Assert.Equal(PageNavigationDecision.OpenExternal, File("https://other.org/", user: true));
            Assert.Equal(PageNavigationDecision.Cancel, File("https://other.org/"));
            Assert.Equal(PageNavigationDecision.Cancel, File("https://other.org/", PageNavigationTarget.Subframe));
            Assert.Equal(PageNavigationDecision.Cancel, File("mailto:a@b.c", user: true));
            Assert.Equal(PageNavigationDecision.Cancel, Web("https://other.org/"));
            Assert.Equal(PageNavigationDecision.OpenExternal, Web("https://other.org/", user: true));
        }

        [Fact]
        public void NewWindowsOpenOnlyWebAddressesAfterAClick()
        {
            Assert.Equal(PageNavigationDecision.OpenExternal, Web("https://example.com/x", PageNavigationTarget.NewWindow, user: true));
            Assert.Equal(PageNavigationDecision.Cancel, Web("https://example.com/x", PageNavigationTarget.NewWindow));
            Assert.Equal(PageNavigationDecision.Cancel, Web("javascript:alert(1)", PageNavigationTarget.NewWindow, user: true));
            Assert.Equal(PageNavigationDecision.Cancel, File(FileOrigin + "/b.html", PageNavigationTarget.NewWindow, user: true));
        }

        [Fact]
        public void WebPageKeepsToItsSiteUnlessBrowsing()
        {
            Assert.Equal(PageNavigationDecision.Allow, Web("https://example.com/next"));
            Assert.Equal(PageNavigationDecision.Cancel, Web("file:///C:/x.html", user: true));
            Assert.Equal(PageNavigationDecision.Allow, Web("https://other.org/", page: WebPage with { Browse = true }));
            Assert.Equal(PageNavigationDecision.Cancel, Web("ftp://other.org/", page: WebPage with { Browse = true }));
        }

        [Fact]
        public void AFilePageReadsOnlyADirectoryThatHoldsIt()
        {
            const string home = @"C:\Users\me";
            Assert.Null(PageSpec.GrantError(@"C:\pages\a.html", null, home));
            Assert.Null(PageSpec.GrantError(@"C:\pages\a.html", @"C:\pages\", home));
            Assert.Equal("html file must be an absolute path", PageSpec.GrantError(@"pages\a.html", null, home));
            Assert.Equal("cwd must be an absolute path", PageSpec.GrantError(@"C:\pages\a.html", "pages", home));
            Assert.Equal("cwd must not be a drive root or the home directory", PageSpec.GrantError(@"C:\pages\a.html", @"C:\", home));
            Assert.Equal("cwd must not be a drive root or the home directory", PageSpec.GrantError(@"C:\Users\me\a.html", @"C:\Users\me", home));
            Assert.Equal("cwd must be a directory containing the html file", PageSpec.GrantError(@"C:\pages\a.html", @"C:\pages\a.html", home));
            Assert.Equal("html file is outside cwd", PageSpec.GrantError(@"C:\pagesx\a.html", @"C:\pages", home));
        }

        [Fact]
        public void FramesOfAWebPageLoadAnySiteUnlessChromeless()
        {
            Assert.Equal(PageNavigationDecision.Allow, Web("https://widgets.org/", PageNavigationTarget.Subframe));
            Assert.Equal(PageNavigationDecision.Cancel, Web("https://widgets.org/", PageNavigationTarget.Subframe, page: WebPage with { Chromeless = true }));
        }
    }
}
