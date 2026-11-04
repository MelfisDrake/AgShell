namespace AgShell.Core
{
    /// <summary>
    /// libghostty's showDesktopNotification limits, app-wide: one terminal notification a second, and the same title and body
    /// once in five seconds; the rest are dropped. A dropped one leaves the last allowed as it was.
    /// </summary>
    public sealed class NotificationRateLimit
    {
        private TimeSpan? last;
        private string? lastContent;

        public bool Allow(string title, string body, TimeSpan now)
        {
            string content = title + "\0" + body;
            if (last is TimeSpan previous && (now - previous < TimeSpan.FromSeconds(1) || (content == lastContent && now - previous < TimeSpan.FromSeconds(5))))
            {
                return false;
            }

            last = now;
            lastContent = content;
            return true;
        }
    }
}
