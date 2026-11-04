namespace AgShell.Core
{
    /// <summary>Where a page navigation would land: agterm's HtmlNavigationTarget.</summary>
    public enum PageNavigationTarget
    {
        MainFrame,
        Subframe,
        NewWindow,
    }

    public enum PageNavigationDecision
    {
        Allow,
        Cancel,
        OpenExternal,
    }

    /// <summary>
    /// agterm's HtmlNavigationPolicy: a file page stays within the files it was given, a web page's main frame within its
    /// site, or any http(s) site with --browse. Only a user's click hands a web address to the browser, and even that asks
    /// first, since a page can fake one.
    /// </summary>
    public static class PageNavigation
    {
        /// <param name="origin">The site a web page shows: where it opened, or where navigate --url took it.</param>
        /// <param name="ownFile">For a file page, whether an address is one of the files its host serves it.</param>
        public static PageNavigationDecision Decide(string url, PageNavigationTarget target, bool userActivated, PageSpec page, string origin, Func<Uri, bool>? ownFile)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                return PageNavigationDecision.Cancel;
            }

            bool web = uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
            // A local page's files are served under an https host of its own, which is no address for a browser.
            bool own = page.IsFile && string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase);
            if (target == PageNavigationTarget.NewWindow)
            {
                return web && !own && userActivated ? PageNavigationDecision.OpenExternal : PageNavigationDecision.Cancel;
            }

            if (uri.Scheme == "about")
            {
                return PageNavigationDecision.Allow;
            }

            if (page.IsFile)
            {
                if (own)
                {
                    return ownFile?.Invoke(uri) == true ? PageNavigationDecision.Allow : PageNavigationDecision.Cancel;
                }

                return web && userActivated ? PageNavigationDecision.OpenExternal : PageNavigationDecision.Cancel;
            }

            if (!web)
            {
                return PageNavigationDecision.Cancel;
            }

            if (page.Browse || string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase))
            {
                return PageNavigationDecision.Allow;
            }

            if (target == PageNavigationTarget.Subframe && !page.Chromeless)
            {
                return PageNavigationDecision.Allow;
            }

            return userActivated ? PageNavigationDecision.OpenExternal : PageNavigationDecision.Cancel;
        }
    }
}
