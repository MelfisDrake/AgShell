using AgShell.Protocol;

namespace AgShell.Core
{
    public sealed class Session
    {
        public Session(Guid id, string profileId, string? cwd, string? customName)
        {
            Id = id;
            ProfileId = profileId;
            Cwd = cwd;
            CustomName = customName;
        }

        public Guid Id { get; }

        public string ProfileId { get; internal set; }

        public string? CustomName { get; internal set; }

        public string? Cwd { get; internal set; }

        /// <summary>In the user's flagged working set, which the sidebar can show across workspaces; persisted.</summary>
        public bool Flagged { get; internal set; }

        /// <summary>The program the main pane ran when agshell last quit, typed into the fresh shell on restore.</summary>
        public string? RestoreCommand { get; internal set; }

        /// <summary>The same for the split pane.</summary>
        public string? SplitRestoreCommand { get; internal set; }

        /// <summary>
        /// agterm's session restore pin: the line the main pane re-runs on every launch instead of what it ran at quit, an
        /// empty string for a plain shell; null leaves it to the capture. Sticky and persisted.
        /// </summary>
        public string? RestorePin { get; internal set; }

        /// <summary>The same for the split pane.</summary>
        public string? SplitRestorePin { get; internal set; }

        /// <summary>
        /// agterm's wasRestored: the session was rebuilt from a snapshot (at launch, a reopened window, Recent Closed)
        /// rather than created now; a restored session runs its command again only in re-run mode. Not persisted.
        /// </summary>
        public bool WasRestored { get; internal set; }

        /// <summary>
        /// agterm's pendingForegroundCommand: the capture this launch replays in the main pane, taken once. Only the app's
        /// launch arms it, never a window reopened later; never saved, so a save before the pane starts cannot write the
        /// capture back into the file the launch stripped.
        /// </summary>
        public string? PendingRestoreCommand { get; internal set; }

        /// <summary>The same for the split pane, armed for a hidden split too: it replays if that split is shown.</summary>
        public string? PendingSplitRestoreCommand { get; internal set; }

        /// <summary>
        /// agterm's pendingRestoreCommand: the pin as it was at launch, the only pin a starting pane reads, so a pin set
        /// over the pipe during this run never runs in it. Taken once; not persisted.
        /// </summary>
        public string? PendingRestorePin { get; internal set; }

        /// <summary>The same for the split pane.</summary>
        public string? PendingSplitRestorePin { get; internal set; }

        /// <summary>
        /// agterm's session new --command: the program the main pane runs instead of a shell, argv-style; the session closes
        /// when it exits. Persisted, so re-run restores run it again.
        /// </summary>
        public string? Command { get; internal set; }

        /// <summary>--wait: when the command exits the session stays, its last output readable, until a key.</summary>
        public bool CommandWait { get; internal set; }

        /// <summary>agterm's splitInitialCommand: the command and --wait a swap moved into the right pane, which follow it.</summary>
        public string? SplitCommand { get; internal set; }

        public bool SplitCommandWait { get; internal set; }

        /// <summary>
        /// agterm's remote attach: the machine (an ssh destination) whose live daemons run this session, and the session's id
        /// there. Its panes are that machine's shells; the row is never saved. Null for a session of this machine.
        /// </summary>
        public string? RemoteHost { get; internal set; }

        public Guid RemoteSessionId { get; internal set; }

        /// <summary>
        /// Which of the other machine's panes each pane here shows (Main is its left, Split its right), as agterm binds a replica
        /// to its shell: it follows swaps here, the other machine's renames, and the panes that go. A pane not in it is a shell
        /// of this machine.
        /// </summary>
        internal Dictionary<PaneRole, PaneRole> RemoteBinding { get; } = new Dictionary<PaneRole, PaneRole>();

        /// <summary>The other machine's stable id of the pane each pane here shows, learned from its layout; it moves with the binding.</summary>
        internal Dictionary<PaneRole, string> RemotePaneIds { get; } = new Dictionary<PaneRole, string>();

        /// <summary>The presentation protocol the other machine speaks; null when too old to stream, and nothing is mirrored.</summary>
        public int? RemotePresentation { get; internal set; }

        /// <summary>The row's status came from the other machine, so a lost stream takes it away again.</summary>
        public bool RemoteStatusBridged { get; internal set; }

        /// <summary>That status was set by a pane with no counterpart here (the other machine's scratch, or a split closed here): no key here clears it.</summary>
        public bool RemoteStatusUnowned { get; internal set; }

        /// <summary>The row's panes stand for the other machine's the other way round: a swap made here, or a rename there.</summary>
        public bool RemotePanesSwapped => RemoteBinding.GetValueOrDefault(PaneRole.Main, PaneRole.Main) == PaneRole.Split || RemoteBinding.GetValueOrDefault(PaneRole.Split, PaneRole.Split) == PaneRole.Main;


        /// <summary>The HUD the other machine put up on the row, which a lost stream takes down.</summary>
        public string? RemoteHudId { get; internal set; }

        /// <summary>agterm's session context: what the session is for (a PR, a task), shown in the title bar. Persisted.</summary>
        public string? Context { get; internal set; }

        /// <summary>A remote row's context as its machine has it. One set here wins over it; cleared, this shows again.</summary>
        public string? MirroredContext { get; internal set; }

        public string? EffectiveContext => Context ?? MirroredContext;

        /// <summary>agterm's rule for a context: one line of 1 to 256 bytes.</summary>
        public static bool IsValidContext(string? text)
        {
            return text is not null && ContextError(text) is null;
        }

        /// <summary>
        /// agterm's validateContext: why a context is refused, or null. Trimmed first; the line and paragraph separators
        /// break the title bar's one line as control characters do.
        /// </summary>
        public static string? ContextError(string raw)
        {
            string trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                return "context must not be empty (use --clear to remove it)";
            }

            if (System.Text.Encoding.UTF8.GetByteCount(trimmed) > 256)
            {
                return "context must be at most 256 UTF-8 bytes";
            }

            return raw.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029') ? "context must not contain control characters or line breaks" : null;
        }

        /// <summary>agterm's session background, behind every pane without one of its own. Persisted.</summary>
        public ControlBackground? Background { get; internal set; }

        /// <summary>
        /// agterm's pane ids: a stable token per terminal (Main, Split, Scratch), in its shell's AGSHELL_PANE_ID, that
        /// follows the terminal when the panes swap or the split takes over. Main and split ones are persisted.
        /// </summary>
        internal readonly Dictionary<PaneRole, string> PaneIdMap = new Dictionary<PaneRole, string>();

        public IReadOnlyDictionary<PaneRole, string> PaneIds => PaneIdMap;

        internal readonly Dictionary<PaneRole, bool> LiveFailureMap = new Dictionary<PaneRole, bool>();

        public bool LiveFailed(PaneRole pane) => LiveFailureMap.ContainsKey(pane);

        /// <summary>A pane's own background (Main, Split or Scratch); the scratch's lasts until that terminal closes.</summary>
        internal readonly Dictionary<PaneRole, ControlBackground> PaneBackgroundMap = new Dictionary<PaneRole, ControlBackground>();

        public IReadOnlyDictionary<PaneRole, ControlBackground> PaneBackgrounds => PaneBackgroundMap;

        /// <summary>As in agterm: the rename, else the title the focused pane's program set, else that pane's directory.</summary>
        public string DisplayName => DisplayNames.ForSession(CustomName, FocusedTitle, FocusedCwd);

        /// <summary>The title the main pane's program set through OSC 0/2, shell titles dropped. Not persisted.</summary>
        public string? Title { get; internal set; }

        public string? SplitTitle { get; internal set; }

        public string? FocusedTitle => HasSplit && SplitFocused ? SplitTitle : Title;

        /// <summary>The directory of the focused pane: the split's while it has focus, shown or hidden-maximized, else the main one's.</summary>
        public string? FocusedCwd => HasSplit && SplitFocused ? SplitCwd ?? Cwd : Cwd;

        /// <summary>The session has a second shell, shown or hidden.</summary>
        public bool HasSplit { get; internal set; }

        public bool SplitShown { get; internal set; }

        public SplitAxis SplitAxis { get; internal set; }

        /// <summary>The main pane's share of the session; null is an even split.</summary>
        public double? SplitRatio { get; internal set; }

        public string? SplitCwd { get; internal set; }

        public bool SplitFocused { get; internal set; }

        /// <summary>The scratch shell covers the session. Not persisted.</summary>
        public bool ScratchShown { get; internal set; }

        /// <summary>agterm's scratchCommand: run once by the scratch's next start instead of a shell.</summary>
        public string? ScratchCommand { get; internal set; }

        internal readonly Dictionary<PaneRole, SessionOverlay> OverlayMap = new Dictionary<PaneRole, SessionOverlay>();

        internal readonly Dictionary<PaneRole, OverlayResult> OverlayResults = new Dictionary<PaneRole, OverlayResult>();

        /// <summary>What covers the session or its panes, by slot: Overlay, OverlayLeft, OverlayRight.</summary>
        public IReadOnlyDictionary<PaneRole, SessionOverlay> Overlays => OverlayMap;

        /// <summary>The program or HUD over the whole session, or null.</summary>
        public SessionOverlay? Overlay => OverlayMap.GetValueOrDefault(PaneRole.Overlay);

        /// <summary>The last overlay program opened in a slot and its exit code, null while it runs.</summary>
        public OverlayResult? LastOverlay(PaneRole slot)
        {
            return OverlayResults.GetValueOrDefault(slot);
        }

        /// <summary>The exit code of the last session-wide overlay program; null while one runs or before any.</summary>
        public int? LastOverlayExitCode => LastOverlay(PaneRole.Overlay)?.ExitCode;

        /// <summary>
        /// The pane keystrokes go to: the scratch when shown, then the split when it has focus, then the main one. As in
        /// agterm, a hidden split keeps its focus and that pane shows maximized.
        /// </summary>
        public PaneRole FocusedPane => ScratchShown ? PaneRole.Scratch : HasSplit && SplitFocused ? PaneRole.Split : PaneRole.Main;

        /// <summary>agterm's rendersPane: both panes while the split shows, else only the focused one, maximized.</summary>
        public bool RendersPane(PaneRole pane)
        {
            bool splitFocused = HasSplit && SplitFocused;
            return pane switch
            {
                PaneRole.Main => SplitShown || !splitFocused,
                PaneRole.Split => HasSplit && (SplitShown || splitFocused),
                _ => true,
            };
        }

        public AgentStatus Status { get; internal set; }

        /// <summary>
        /// agterm's indicator as written, idle included: a repeated write compares against it and the status event carries it.
        /// What shows reads <see cref="StatusBlink"/>, <see cref="StatusColor"/> and <see cref="StatusShape"/>, empty while idle.
        /// </summary>
        internal bool IndicatorBlink { get; set; }

        internal string? IndicatorColor { get; set; }

        internal string? IndicatorShape { get; set; }

        public bool StatusBlink => IndicatorBlink && Status != AgentStatus.Idle;

        /// <summary>The status clears itself once the session is visited.</summary>
        public bool StatusAutoReset { get; internal set; }

        /// <summary>A glyph color given with this status only (#rrggbb or as written); the next status set without one drops it.</summary>
        public string? StatusColor => Status == AgentStatus.Idle ? null : IndicatorColor;

        /// <summary>The same for the glyph's shape, one of <see cref="AgentStatuses.Shapes"/>.</summary>
        public string? StatusShape => Status == AgentStatus.Idle ? null : IndicatorShape;

        public DateTimeOffset? StatusChangedAt { get; internal set; }

        /// <summary>The pane whose agent set the status: main, split or scratch.</summary>
        public PaneRole StatusPane { get; internal set; }

        /// <summary>agterm's statusPane being set: the write named its pane, so the status event carries it.</summary>
        public bool StatusPaneNamed { get; internal set; }

        public int UnseenCount { get; internal set; }

        /// <summary>
        /// As agterm's terminals, each pane keeps the font size it has: left, right and scratch, and the size goes with its
        /// terminal across a swap and a promotion. A pane missing here has the session's size.
        /// </summary>
        internal readonly Dictionary<PaneRole, double> FontSizeMap = new Dictionary<PaneRole, double>();

        /// <summary>
        /// agterm's session fontSize: the size Ctrl+/- last gave any of the session's panes, which a new split, scratch or
        /// overlay starts with; saved. Null is the default size.
        /// </summary>
        public double? SessionFontSize { get; internal set; }

        /// <summary>The left pane's font size; null is the default.</summary>
        public double? FontSize => FontSizeMap.TryGetValue(PaneRole.Main, out double size) ? size : SessionFontSize;

        public double? SplitFontSize => FontSizeMap.TryGetValue(PaneRole.Split, out double size) ? size : SessionFontSize;

        public double? ScratchFontSize => FontSizeMap.TryGetValue(PaneRole.Scratch, out double size) ? size : SessionFontSize;
    }
}
