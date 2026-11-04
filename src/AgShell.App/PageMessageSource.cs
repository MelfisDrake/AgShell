namespace AgShell.App
{
    internal static class PageMessageSource
    {
        public static bool IsTrusted(string source, string current, string origin, Func<Uri, bool> ownFile)
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? sender) || !Uri.TryCreate(current, UriKind.Absolute, out Uri? document)
                || (sender.Scheme != Uri.UriSchemeHttps && sender.Scheme != Uri.UriSchemeHttp) || sender.UserInfo.Length != 0
                || !string.Equals(sender.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.Equals(sender.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped),
                document.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped), StringComparison.Ordinal)
                && ownFile(sender);
        }
    }
}
