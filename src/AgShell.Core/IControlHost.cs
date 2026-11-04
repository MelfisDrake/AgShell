using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>What the control commands need from the running app beyond the model: shells, terminals and window chrome.</summary>
    public interface IControlHost
    {
        string Version { get; }

        /// <summary>The version of the app's file on disk, as an informational version; null when it cannot be read.</summary>
        string? InstalledVersion { get; }

        IReadOnlyList<string> ShellIds { get; }

        string DefaultShellId { get; }

        bool SidebarVisible { get; }

        double SidebarWidth { get; }

        bool IsRunning(Guid sessionId, PaneRole pane);

        /// <summary>Ends every shell of a session that is being removed.</summary>
        void StopSession(Guid sessionId);

        /// <summary>Ends one pane's shell, for a split or scratch that is being closed.</summary>
        void StopPane(Guid sessionId, PaneRole pane);

        /// <summary>Replaces a pane's shell with a fresh one, running or not.</summary>
        void RestartSession(Guid sessionId, PaneRole pane);

        /// <summary>
        /// agterm's session.restart: ends the pane's shell, starts a new one and runs <paramref name="command"/> in it, or with
        /// none the pane's foreground program again. Returns the receipt, or the error naming the step that did not happen.
        /// </summary>
        Task<(string? Error, ControlRestartReceipt? Receipt)> RestartPaneAsync(Guid sessionId, PaneRole pane, string? command)
        {
            RestartSession(sessionId, pane);
            return Task.FromResult<(string?, ControlRestartReceipt?)>((null, new ControlRestartReceipt()));
        }

        /// <summary>Exchanges the shells of the main and split panes.</summary>
        void SwapPaneShells(Guid sessionId);

        /// <summary>Writes raw input to a pane's shell.</summary>
        void Write(Guid sessionId, PaneRole pane, string data);

        /// <summary>The terminal's text: the visible screen, or the whole buffer, optionally only its last lines.</summary>
        Task<string> ReadTextAsync(Guid sessionId, PaneRole pane, bool all, int? lines);

        /// <summary>
        /// Drives the search bar of a pane's terminal: opens it, searches <paramref name="needle"/>, steps with "next"/"prev"
        /// or closes it with "close". Returns the "N of M" counter.
        /// </summary>
        Task<string> SearchAsync(Guid sessionId, PaneRole pane, string? needle, string? step);

        /// <summary>The session's pane whose search bar is open, which next and prev step in; null when none is.</summary>
        PaneRole? SearchPane(Guid sessionId) => null;

        /// <summary>A HUD's width and height in percent of its pane, as drawn; null before it is measured.</summary>
        (int Width, int Height)? HudGeometry(string overlayId) => null;

        /// <summary>A floating program's or page's content size as last laid out, a page's strip left out; null before it is.</summary>
        (int Width, int Height)? OverlayGeometry(string overlayId) => null;

        /// <summary>The page came from the machine whose session this presenter shows.</summary>
        bool IsReplicaPage(string overlayId) => false;

        /// <summary>A page overlay as its view reports it; null when this machine shows it nowhere.</summary>
        ControlPageView? PageState(string overlayId) => null;

        /// <summary>Opens the session's directory in Explorer. Returns false when it does not exist.</summary>
        bool Reveal(Guid sessionId);

        void SetSidebar(bool? visible, double? width);

        /// <summary>agterm's reloadKeymap and reloadHooks: rereads the file asked for; returns its diagnostics.</summary>
        IReadOnlyList<string> ReloadConfig(bool keymapFile, bool hooksFile);

        /// <summary>Every binding, custom command and diagnostic of the keymap, as text.</summary>
        ControlKeymap KeymapList();

        /// <summary>Every hook with its queue, counters and last failure, as text.</summary>
        ControlHooks HooksList();

        /// <summary>Runs a custom command for a session; returns an error, or null when it started.</summary>
        string? RunCustomCommand(string name, Guid? sessionId);

        Task<string?> RunCustomCommandAsync(string name, Guid? sessionId) => Task.FromResult(RunCustomCommand(name, sessionId));

        /// <summary>Whether keymap.conf defines a custom command of that name.</summary>
        bool HasCustomCommand(string name);

        /// <summary>The shells the live daemons keep and which session pane claims each, as text.</summary>
        Task<(ControlLiveInventory? Inventory, string? Error)> LiveListAsync();

        /// <summary>Ends the kept shells no pane claims; returns how many.</summary>
        /// <summary>agterm's zmx prune: ends the kept shells nothing claims and no client shows; the error, or a report and the count.</summary>
        Task<(string? Error, string Report, int Affected)> LivePruneAsync();

        /// <summary>Reads, or with <paramref name="mode"/> sets and saves, how sessions come back on launch.</summary>
        /// <summary>Sets the restore mode for the next launch when one is given; the error, or the restore policy now.</summary>
        (string? Error, ControlRestoreStatus Status) RestoreMode(string? mode);

        /// <summary>agterm's saveChecked: writes the window's state now and says whether it reached the disk.</summary>
        bool SaveState();

        /// <summary>Reads, or with <paramref name="layout"/> (flat, plain, tree or toggle) sets and saves, the flagged view's layout.</summary>
        (string? Error, string Layout) FlaggedLayout(string? layout);

        /// <summary>Reads, or with <paramref name="mode"/> (browser or overlay) sets and saves, where clicked links open.</summary>
        (string? Error, string Mode) LinkMode(string? mode);

        /// <summary>
        /// Acts on a pane's terminal: copy returns its selection, selectAll selects the whole buffer, paste pastes
        /// <paramref name="text"/> as the user would. Null when the pane has no terminal yet.
        /// </summary>
        Task<string?> TerminalAsync(Guid sessionId, PaneRole pane, string action, string? text);

        /// <summary>
        /// A path a caller gave, as Windows opens it: a WSL session's /home/… is its distribution's file, /mnt/c/… is C:\.
        /// Null when it cannot be read as a path.
        /// </summary>
        string? LocalPath(Guid sessionId, string path);

        /// <summary>Brings the window to the front, as agterm's --follow raises its window.</summary>
        void Raise();

        /// <summary>A command (--wait) or remote pane that holds its last output until a key after its process exited.</summary>
        bool IsHeld(Guid sessionId, PaneRole pane);

        /// <summary>Ends a command or remote pane held open after its process exited; false when that pane is not held.</summary>
        bool ReleaseHeld(Guid sessionId, PaneRole pane);

        string? ReadClipboard();

        void WriteClipboard(string text);

        /// <summary>agterm's restore capture: notes what every pane runs now, as a quit would. Refused outside re-run mode.</summary>
        (string? Error, int Count) CaptureRestoreNow();

        /// <summary>
        /// agterm's theme read-back: the installed themes; the single theme, null while following Windows or for none; and the
        /// light/dark pair, set only while following. A null theme is Ghostty's built-in one.
        /// </summary>
        (IReadOnlyList<string> Names, string? Current, bool Sync, string? Light, string? Dark) ThemeState();

        /// <summary>
        /// Sets and saves the theme slots as agterm's theme.set does: <paramref name="light"/> is the single (or light) theme
        /// and keeps a dark one; <paramref name="dark"/> sets the dark one, which makes the terminal follow the Windows
        /// theme, and "none" clears it; neither resets to the default. Returns an error, or null with the theme now in force.
        /// </summary>
        string? SetTheme(string? light, string? dark);

        /// <summary>Clears what web pages opened with --persistent have kept; why it could not, or null.</summary>
        Task<string?> ClearBrowserDataAsync();

        /// <summary>The saved browser store is being emptied: a page opened on it now would write into it.</summary>
        bool BrowserClearing { get; }

        /// <summary>Drives a page overlay: reload, back, forward, browser, finder, or navigate to <paramref name="url"/>; why it could not, or null.</summary>
        string? PageCommand(Guid sessionId, PaneRole slot, string action, string? url);

        /// <summary>Takes the HUD down after <paramref name="seconds"/>, if it is still the one with that id; null keeps it up, ending an earlier timer.</summary>
        void ScheduleHudHide(Guid sessionId, string overlayId, double? seconds);

        /// <summary>
        /// Plays the sound a status write asks for, as agterm: the call's own <paramref name="sound"/>, else the settings'
        /// blocked sound for a blocked status, else none.
        /// </summary>
        void StatusSound(AgentStatus status, string? sound);

        /// <summary>
        /// agterm's batch session.close: with the undo grace on, one grouped close the window can undo; off, each closes at
        /// once. Returns how many closed.
        /// </summary>
        int CloseSessionsBatch(IReadOnlyList<Guid> sessionIds);

        /// <summary>agterm's unknown sound: why a sound name cannot be played, or null when it can.</summary>
        string? SoundError(string name);

        /// <summary>agterm's stepHtmlOverlayZoom: the pages' zoom, one for the app, a step up, down or back to 1.</summary>
        void StepPageZoom(string action);

        /// <summary>Shows a desktop notification that opens the session when clicked; false when banners are turned off.</summary>
        bool Notify(Guid sessionId, string title, string body);

        /// <summary>agterm's lead of a live pane: leader, follower, or null when the pane has none.</summary>
        string? PaneLead(Guid sessionId, PaneRole pane);

        /// <summary>
        /// agterm's ZmxLeadState.covered: the pane is a follower, nobody leads it (unowned), or it is joining again and has
        /// not reported its lead yet (reattaching); its own terminal is not the program's.
        /// </summary>
        bool PaneCovered(Guid sessionId, PaneRole pane);

        /// <summary>
        /// agterm's zmx screen of a pane: its daemon's own terminal, the visible screen or with <paramref name="all"/> the
        /// history too. Null when no daemon of this machine keeps the pane's shell or the read failed.
        /// </summary>
        Task<DaemonScreen?> PaneScreenAsync(Guid sessionId, PaneRole pane, bool all);

        /// <summary>
        /// agterm's zmx type into a pane: its daemon writes <paramref name="text"/> to the shell without taking the lead,
        /// each CR a Return key for the program's keyboard mode. False when no daemon of this machine took it.
        /// </summary>
        Task<bool> PaneTypeAsync(Guid sessionId, PaneRole pane, string text);

        /// <summary>
        /// agterm's foreground: the program a pane runs now as its argv, or with only shells there the shell's name; both
        /// null when it cannot be read.
        /// </summary>
        (List<string>? Argv, string? Shell) ForegroundOf(Guid sessionId, PaneRole pane);

        /// <summary>Milliseconds since the last input in the window; null before any.</summary>
        long? IdleMs { get; }

        /// <summary>The auto-follow timeout in milliseconds; null when it is off.</summary>
        long? AutoFollowMs { get; }

        /// <summary>agterm's session lead: this window's size for the pane, the other side covered. Returns an error, or null.</summary>
        string? TakeLead(Guid sessionId, PaneRole pane);

        /// <summary>A remote row's presentation stream, null for a session of this machine.</summary>
        ControlPresentationNode? PresentationOf(Guid sessionId);

        /// <summary>A pane of the other machine waiting to be attached again, null otherwise.</summary>
        ControlReconnectNode? ReconnectOf(Guid sessionId, PaneRole pane);

        /// <summary>How the window would start an overlay program in the slot: its command line, directory and environment.</summary>
        ControlOverlayJob OverlayLaunch(Guid sessionId, PaneRole slot, string command, string? cwd);
    }

    /// <summary>A page as its view reports it: the address and title shown now, its load state, history and the pages' zoom.</summary>
    public sealed record ControlPageView(string? Page, string? Title, string State, string? Error, bool? CanGoBack, bool? CanGoForward, double? Zoom);
}
