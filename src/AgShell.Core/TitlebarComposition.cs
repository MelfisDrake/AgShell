using System.Globalization;

namespace AgShell.Core
{
    public sealed record TitlebarComposition(string Title, string? Host, string Tail, string Subtitle)
    {
        public static TitlebarComposition Compose(Session? session, Workspace? workspace, string? windowName, AppSettings settings)
        {
            if (settings.EffectiveToolbarMode == "hidden")
            {
                return new TitlebarComposition("", null, "", "");
            }

            IReadOnlyList<string> hidden = settings.EffectiveHiddenInterfaceElements;
            string? workspaceName = !hidden.Contains("workspaceName") && session is not null ? workspace?.Name.Trim() : null;
            if (!string.IsNullOrEmpty(workspaceName))
            {
                int[] characters = StringInfo.ParseCombiningCharacters(workspaceName);
                if (characters.Length > 24)
                {
                    workspaceName = workspaceName[..characters[24]] + "…";
                }
            }

            string title = string.Join(" — ", new[]
            {
                string.IsNullOrEmpty(workspaceName) ? null : workspaceName,
                hidden.Contains("sessionName") ? null : session?.DisplayName ?? "AgShell",
                hidden.Contains("windowName") ? null : windowName,
            }.Where(part => part is not null));
            string? host = hidden.Contains("remoteHost") ? null : session?.RemoteHost;
            string? context = hidden.Contains("sessionContext") ? null : session?.EffectiveContext;
            string detail = session is null ? "" : !string.IsNullOrEmpty(session.FocusedTitle) && session.FocusedTitle != session.DisplayName
                ? session.FocusedTitle : session.FocusedCwd ?? "";
            bool compact = settings.EffectiveToolbarMode == "compact";
            return new TitlebarComposition(title, host,
                compact && !string.IsNullOrEmpty(context) ? (title.Length > 0 || host is not null ? " · " : "") + context : "",
                compact ? "" : context ?? detail);
        }
    }
}
