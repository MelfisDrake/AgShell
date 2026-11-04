using System.Text.Json.Serialization;
using AgShell.Protocol;

namespace AgShell.Core
{
    public sealed record StateSnapshot
    {
        public const int CurrentVersion = 1;
        private readonly List<Guid> sessionRecency = new List<Guid>();

        // agterm decodes the version with no default: a file without one does not load.
        [JsonRequired]
        public int Version { get; init; } = CurrentVersion;

        public List<WorkspaceSnapshot> Workspaces { get; init; } = new List<WorkspaceSnapshot>();

        [JsonConverter(typeof(LossyConverter<Guid?>))]
        public Guid? SelectedSessionId { get; init; }

        /// <summary>Session ids, most recently selected first; the Ctrl+Tab order survives a restart.</summary>
        [JsonConverter(typeof(LossyListConverter<Guid>))]
        public List<Guid> SessionRecency
        {
            get => sessionRecency;
            init => sessionRecency = value ?? new List<Guid>();
        }

        [JsonConverter(typeof(LossyConverter<WindowPlacement?>))]
        public WindowPlacement? Window { get; init; }

        [JsonConverter(typeof(LossyConverter<SidebarSettings?>))]
        public SidebarSettings? Sidebar { get; init; }

        [JsonConverter(typeof(LossyConverter<string?>))]
        public string? SidebarMode { get; init; }

        [JsonConverter(typeof(LossyConverter<List<Guid>?>))]
        public List<Guid>? FocusedWorkspaceIds { get; init; }

        [JsonConverter(typeof(LossyConverter<bool>))]
        public bool FocusEnabled { get; init; }
    }

    public sealed record WorkspaceSnapshot
    {
        public Guid Id { get; init; }

        public string Name { get; init; } = string.Empty;

        [JsonConverter(typeof(LossyConverter<bool>))]
        public bool Collapsed { get; init; }

        public List<SessionSnapshot> Sessions { get; init; } = new List<SessionSnapshot>();
    }

    public sealed record SessionSnapshot
    {
        public Guid Id { get; init; }

        // A bad profile reads as unset and the session opens in the default one, not the whole file going aside.
        [JsonConverter(typeof(LossyConverter<string>))]
        public string ProfileId { get; init; } = string.Empty;

        [JsonConverter(typeof(LossyConverter<string?>))]
        public string? Name { get; init; }

        public string? Cwd { get; init; }

        [JsonConverter(typeof(LossyConverter<double?>))]
        public double? FontSize { get; init; }

        [JsonConverter(typeof(LossyConverter<double?>))]
        public double? SplitFontSize { get; init; }

        [JsonConverter(typeof(LossyConverter<bool>))]
        public bool HasSplit { get; init; }

        [JsonConverter(typeof(LossyConverter<bool>))]
        public bool SplitShown { get; init; }

        [JsonConverter(typeof(LossyConverter<string?>))]
        public string? SplitAxis { get; init; }

        [JsonConverter(typeof(LossyConverter<double?>))]
        public double? SplitRatio { get; init; }

        [JsonConverter(typeof(LossyConverter<string?>))]
        public string? SplitCwd { get; init; }


        [JsonConverter(typeof(LossyConverter<bool>))]
        public bool Flagged { get; init; }

        [JsonConverter(typeof(LossyConverter<string?>))]
        public string? RestoreCommand { get; init; }

        [JsonConverter(typeof(LossyConverter<string?>))]
        public string? SplitRestoreCommand { get; init; }

        [JsonConverter(typeof(LossyConverter<string?>))]
        public string? RestorePin { get; init; }

        [JsonConverter(typeof(LossyConverter<string?>))]
        public string? SplitRestorePin { get; init; }

        [JsonConverter(typeof(LossyConverter<string?>))]
        public string? Context { get; init; }

        [JsonConverter(typeof(LossyConverter<string?>))]
        public string? Command { get; init; }

        /// <summary>The left and right panes' ids, so a shell kept by a live daemon keeps its own.</summary>
        [JsonConverter(typeof(LossyConverter<Dictionary<string, string>?>))]
        public Dictionary<string, string>? PaneIds { get; init; }

        [JsonConverter(typeof(LossyConverter<List<string>?>))]
        public List<string>? LiveFailedPanes { get; init; }

        [JsonConverter(typeof(LossyConverter<bool>))]
        public bool CommandWait { get; init; }

        [JsonConverter(typeof(LossyConverter<string?>))]
        public string? SplitCommand { get; init; }

        [JsonConverter(typeof(LossyConverter<bool>))]
        public bool SplitCommandWait { get; init; }

        [JsonConverter(typeof(LossyConverter<ControlBackground?>))]
        public ControlBackground? Background { get; init; }

        /// <summary>The left and right panes' own backgrounds; a scratch's is not kept.</summary>
        [JsonConverter(typeof(LossyConverter<Dictionary<string, ControlBackground>?>))]
        public Dictionary<string, ControlBackground>? PaneBackgrounds { get; init; }
    }

    public sealed record WindowPlacement
    {
        public double Left { get; init; }

        public double Top { get; init; }

        public double Width { get; init; }

        public double Height { get; init; }

        public bool Maximized { get; init; }
    }

    public sealed record SidebarSettings
    {
        public bool Visible { get; init; } = true;

        /// <summary>agterm's sidebarWidth: points, 160 to 560, 220 by default.</summary>
        public double Width { get; init; } = 220;
    }
}
