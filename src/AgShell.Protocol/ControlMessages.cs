using System.Text.Json;

namespace AgShell.Protocol
{
    /// <summary>One request per connection: a command name, an optional session or workspace target, and its arguments.</summary>
    public sealed record ControlRequest
    {
        public required string Cmd { get; init; }

        public string? Target { get; init; }

        public ControlArgs? Args { get; init; }
    }

    /// <summary>Optional parameters; each command reads only the ones it needs. Names follow agterm's wire format.</summary>
    public sealed record ControlArgs
    {
        public string? Name { get; init; }

        /// <summary>live.presentation, live.answer: the stream's token, which asks for and holds the presenter role.</summary>
        public string? Presenter { get; init; }

        /// <summary>live.answer: what the presenting machine did with a question handed to it.</summary>
        public ControlPresentationFrame? Answer { get; init; }

        /// <summary>live.tree, live.attach: another machine's ssh destination.</summary>
        public string? Host { get; init; }

        /// <summary>session.status: a sound from C:\Windows\Media to play with this write.</summary>
        public string? Sound { get; init; }

        /// <summary>theme.set: the light theme, the same slot as <see cref="Name"/>.</summary>
        public string? Light { get; init; }

        /// <summary>theme.set: the dark theme, or "none" to stop following the Windows theme.</summary>
        public string? Dark { get; init; }

        public string? Cwd { get; init; }

        /// <summary>Several session targets for batch commands; used instead of the request's single target.</summary>
        public List<string>? Targets { get; init; }

        /// <summary>A workspace id, unique prefix or "active"; never a name.</summary>
        public string? Workspace { get; init; }

        public string? WorkspaceName { get; init; }

        public bool? CreateWorkspace { get; init; }

        /// <summary>Shell profile id for a new session (pwsh, powershell, cmd, wsl:&lt;distribution&gt;).</summary>
        public string? Shell { get; init; }

        public bool? Collapsed { get; init; }

        public bool? NoSelect { get; init; }

        public string? Text { get; init; }

        public string? Mode { get; init; }

        public bool? All { get; init; }

        public int? Lines { get; init; }

        public string? To { get; init; }

        /// <summary>An anchor session for placement, or the event cursor for events.read.</summary>
        public string? After { get; init; }

        public string? Before { get; init; }

        public double? SidebarWidth { get; init; }

        /// <summary>Agent status: idle, active, completed or blocked.</summary>
        public string? Status { get; init; }

        public bool? Blink { get; init; }

        public bool? AutoReset { get; init; }

        /// <summary>session status: the glyph color (#rrggbb) and shape for this status only.</summary>
        public string? Color { get; init; }

        public string? Shape { get; init; }

        public string? Title { get; init; }

        public string? Body { get; init; }

        /// <summary>The app run an event cursor belongs to; paired with <see cref="After"/>.</summary>
        public string? Run { get; init; }

        public List<string>? Kinds { get; init; }

        public int? Limit { get; init; }

        /// <summary>Which terminal of the session: left (main), right (split) or scratch.</summary>
        public string? Pane { get; init; }

        /// <summary>Split arrangement: vertical (left/right) or horizontal (top/bottom).</summary>
        public string? Axis { get; init; }

        /// <summary>The main pane's share of a split, 0.05 to 0.95.</summary>
        public double? Ratio { get; init; }

        /// <summary>The program an overlay runs.</summary>
        public string? Command { get; init; }

        public bool? Wait { get; init; }

        /// <summary>Switch the user to the target session.</summary>
        public bool? Follow { get; init; }

        public int? SizePercent { get; init; }

        /// <summary>Return a resized overlay to full size.</summary>
        public bool? Full { get; init; }


        public string? TextColor { get; init; }

        public string? Message { get; init; }

        public string? Detail { get; init; }

        /// <summary>HUD spinner style, or "none".</summary>
        public string? Spinner { get; init; }

        public string? Position { get; init; }

        public double? HideAfter { get; init; }

        public double? FontSize { get; init; }

        public bool? Sticky { get; init; }

        public bool? Frame { get; init; }

        /// <summary>Render a HUD message as markdown.</summary>
        public bool? Markdown { get; init; }

        /// <summary>A local HTML file to show in an overlay.</summary>
        public string? Html { get; init; }

        /// <summary>An http(s) page to show in an overlay; also the target of overlay.navigate.</summary>
        public string? Url { get; init; }

        /// <summary>agterm's javascript: an --html or --url page runs its own scripts.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("javascript")]
        public bool? Js { get; init; }

        public bool? Navigation { get; init; }

        public bool? Chromeless { get; init; }

        /// <summary>A web page keeps its cookies and storage after it closes, in the store browser.clear empties.</summary>
        public bool? Persistent { get; init; }

        /// <summary>With --url, let the page leave its first site, as agterm's --browse.</summary>
        public bool? Browse { get; init; }

        /// <summary>For overlay.reload: the page shown now rather than the one it opened with.</summary>
        public bool? Current { get; init; }

        /// <summary>For overlay.result: the page whose outcome to read, by the id overlay open answered with.</summary>
[System.Text.Json.Serialization.JsonPropertyName("page")]
        public string? PageId { get; init; }

        /// <summary>Dashboard: shrink cell fonts as the grid grows.</summary>
        public bool? AutoSize { get; init; }

        /// <summary>Dashboard: fill it from the most recently used sessions.</summary>
        public bool? Mru { get; init; }

        /// <summary>Dashboard: close the open one.</summary>
        public bool? Close { get; init; }

        /// <summary>agterm's select: session.type may select the session first when its surface is not ready.</summary>
        public bool? Select { get; init; }

        /// <summary>agterm's force: live.kill's explicit confirmation that a running process is destroyed.</summary>
        public bool? Force { get; init; }

        /// <summary>agterm's ratioDelta: a signed nudge of the split divider for session.resize; positive grows the primary pane.</summary>
        public double? RatioDelta { get; init; }

        /// <summary>agterm's value: the answer overlay submit hands back, empty being a real answer, or the initial text of an ask field.</summary>
        public string? Value { get; init; }

        /// <summary>The image file for session.background image.</summary>
        public string? Path { get; init; }

        /// <summary>pick: the choices, the query field's placeholder and prefill, the item highlighted on open.</summary>
        public List<ControlPickItem>? Items { get; init; }

        public string? Prompt { get; init; }

        public string? Query { get; init; }

        public string? Selection { get; init; }

        /// <summary>pick: a query that matches nothing is an answer too.</summary>
        public bool? AllowCustom { get; init; }

        /// <summary>ask: the buttons and their roles; terminal (default) or gui style; where the buttons sit; a fixed width in percent.</summary>
        public List<ControlAskButton>? Buttons { get; init; }

        public string? DefaultButton { get; init; }

        public string? DestructiveButton { get; init; }

        public string? CancelButton { get; init; }

        public string? Style { get; init; }

        public string? Align { get; init; }

        public int? Width { get; init; }

        /// <summary>ask: a text field (line or multiline) with its prefill and hint.</summary>
        public string? Input { get; init; }


        public string? Placeholder { get; init; }

        /// <summary>ask: a radio list, or checkboxes with <see cref="Multiple"/>, its preselection and a free-form line.</summary>
        public List<ControlAskChoice>? Choices { get; init; }

        public bool? Multiple { get; init; }

        public List<string>? Checked { get; init; }

        public string? Other { get; init; }

        /// <summary>session background: how strong it is (0–1), how an image fits and whether it tiles; where it sits is <see cref="Position"/>.</summary>
        public double? Opacity { get; init; }

        public string? Fit { get; init; }

[System.Text.Json.Serialization.JsonPropertyName("repeats")]
        public bool? Repeat { get; init; }

        /// <summary>agterm's --pane-id: the stable pane token of a shell's AGSHELL_PANE_ID; a known one overrides <see cref="Pane"/>.</summary>
[System.Text.Json.Serialization.JsonPropertyName("paneID")]
        public string? PaneId { get; init; }

        /// <summary>The window a command acts in: an id, a unique prefix or active; by default the one holding the target, else the front one.</summary>
        public string? Window { get; init; }

        /// <summary>window move/resize: the frame in the window's own units, and the display counted from 0, the primary first.</summary>
        public int? X { get; init; }

        public int? Y { get; init; }

        public int? Height { get; init; }

        public int? Display { get; init; }

        /// <summary>window new: park it minimized at once.</summary>
        public bool? Minimized { get; init; }
    }

    /// <summary>
    /// agterm's presentation snapshot: a session's status, context and HUD, which a machine that attached the session shows
    /// on its row. Pane names are the session's own: left, right, scratch.
    /// </summary>
    public sealed record ControlPresentation
    {
        /// <summary>Absent while idle.</summary>
        public ControlPresentationStatus? Status { get; init; }

        public string? Context { get; init; }

        public ControlPresentationHud? Hud { get; init; }

        /// <summary>True when the stream that asked holds the presenter role.</summary>
        public bool? Presenter { get; init; }

        /// <summary>The question handed to that presenter, while it waits.</summary>
        public ControlPresentationAsk? Ask { get; init; }

        public ControlPresentationLayout? Layout { get; init; }

        /// <summary>Status sounds handed to that presenter since its last read, to play instead of this machine.</summary>
        public List<ControlPresentationSound>? Sounds { get; init; }

        /// <summary>The overlays handed to that presenter: those running, and the last one that ended.</summary>
        public List<ControlPresentationOverlay>? Overlays { get; init; }

        /// <summary>The pages handed to that presenter: those open, and those just closed here.</summary>
        public List<ControlPresentationPage>? Pages { get; init; }
    }

    /// <summary>
    /// An overlay program the session's machine hands to the presenter: the presenter shows it in a terminal of its own,
    /// running ssh back to agshellctl session overlay run-job, which runs the program once on the session's machine.
    /// </summary>
    public sealed record ControlPresentationOverlay
    {
        public required string Job { get; init; }

        public int Owner { get; init; }

        /// <summary>agterm's PresentationPane: the pane's identity for one pane of a split; absent over the whole session.</summary>
        [System.Text.Json.Serialization.JsonConverter(typeof(PresentationPaneConverter))]
        public string? Pane { get; init; }

        public int? SizePercent { get; init; }

        public string? BackgroundColor { get; init; }

        public bool Wait { get; init; }

        /// <summary>The presenter selects the session as it shows the overlay (--follow).</summary>
        public bool Follow { get; init; }

        /// <summary>Set once the job ended without an exit status: launch-failed, canceled or unknown.</summary>
        public string? Failure { get; init; }

        /// <summary>Set once the program exited.</summary>
        public int? ExitCode { get; init; }

        /// <summary>Closed on the session's machine while its surface held the result: the presenter takes it down.</summary>
        public bool Dismissed { get; init; }
    }

    /// <summary>
    /// agterm's PresentationLayout: the origin's panes by identity, the one in the main place, the axis and whether the split
    /// shows. Membership and arrangement, independent of which panes the viewer has realized.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(PresentationLayoutConverter))]
    public sealed record ControlPresentationLayout
    {
        public required List<string> Panes { get; init; }

        public required string Primary { get; init; }

        public string? Axis { get; init; }

        public bool Shown { get; init; }

        /// <summary>agterm's invalid layout: what a layout that could not be read decodes to.</summary>
        public static readonly ControlPresentationLayout Invalid = new ControlPresentationLayout { Panes = new List<string>(), Primary = Guid.Empty.Wire() };

        /// <summary>agterm's isValid: one or two distinct panes, the primary among them, a split shown only with a readable axis.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsValid => Panes.Count is 1 or 2 && Panes.Distinct().Count() == Panes.Count && Panes.Contains(Primary)
            && (Panes.Count == 1 ? !Shown : Axis is "vertical" or "horizontal");

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasSplit => Panes.Count == 2;

        /// <summary>The pane in the main place.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string MainId => Primary;

        /// <summary>The other pane, in the split's place; null without a split.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? SplitId => HasSplit ? Panes.FirstOrDefault(pane => pane != Primary) : null;

        /// <summary>By value, panes in order included, as agterm's Equatable layout: only a change is published.</summary>
        public bool Equals(ControlPresentationLayout? other)
        {
            return other is not null && Panes.SequenceEqual(other.Panes) && Primary == other.Primary && Axis == other.Axis && Shown == other.Shown;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(string.Join(",", Panes), Primary, Axis, Shown);
        }
    }

    /// <summary>A status sound for the presenter to play; no name is the presenter's own blocked sound.</summary>
    public sealed record ControlPresentationSound
    {
        public string? Name { get; init; }
    }

    /// <summary>
    /// agterm's OverlayLaunchContext: what agshellctl session overlay run-job runs, as the session's window would start the
    /// overlay. Command is the whole command line, the session's shell around the program (wsl.exe for a WSL session), where
    /// agterm's helper always runs the program through /bin/sh.
    /// </summary>
    public sealed record ControlOverlayJob
    {
        public required string Command { get; init; }

        public string? Cwd { get; init; }

        public Dictionary<string, string?>? Environment { get; init; }
    }

    /// <summary>The kinds of an overlay job connection's lines, agterm's OverlayJobFrame.</summary>
    public static class OverlayJobKinds
    {
        /// <summary>To the helper, first: what to run.</summary>
        public const string Context = "context";

        /// <summary>To the helper: stop the program.</summary>
        public const string Cancel = "cancel";

        /// <summary>From the helper: the program is running.</summary>
        public const string Started = "started";

        public const string Exited = "exited";

        public const string Canceled = "canceled";

        public const string LaunchFailed = "launch-failed";
    }

    /// <summary>One line of an overlay job's connection, after the claim's ok answer.</summary>
    public sealed record ControlOverlayJobFrame
    {
        public required string Kind { get; init; }

        public ControlOverlayJob? Context { get; init; }

        /// <summary>exited: the program's status.</summary>
        public int? Code { get; init; }

        /// <summary>launch-failed: why.</summary>
        public string? Reason { get; init; }
    }

    /// <summary>A question the session's machine hands to the machine presenting it, which draws it and sends the answer back.</summary>
    public sealed record ControlPresentationAsk
    {
        public required string Id { get; init; }

        /// <summary>The presenter generation it was handed under; the answer carries it back, so a late one is told apart.</summary>
        public int Owner { get; init; }

        public required string Title { get; init; }

        public string? Message { get; init; }

        public required List<ControlAskButton> Buttons { get; init; }

        public string? DefaultButton { get; init; }

        public string? DestructiveButton { get; init; }

        public string? CancelButton { get; init; }

        public string Style { get; init; } = "terminal";

        public string Align { get; init; } = "right";

        public int? Width { get; init; }

        public string? Input { get; init; }

        public string? InputValue { get; init; }

        public string? Placeholder { get; init; }

        public List<ControlAskChoice>? Choices { get; init; }

        public bool Multiple { get; init; }

        public List<string>? Checked { get; init; }

        public string? Other { get; init; }

        /// <summary>agterm's PresentationPane: the pane's identity for a question over one pane; absent over the whole session.</summary>
        [System.Text.Json.Serialization.JsonConverter(typeof(PresentationPaneConverter))]
        public string? Pane { get; init; }
    }

    public sealed record ControlPresentationStatus
    {
        public required string Status { get; init; }

        public bool Blink { get; init; }

        public string? Color { get; init; }

        public string? Shape { get; init; }

        /// <summary>agterm's PresentationPane: the pane whose agent set it, by identity, or scratch.</summary>
        [System.Text.Json.Serialization.JsonConverter(typeof(PresentationPaneConverter))]
        public string? Pane { get; init; }

        /// <summary>Unix seconds on the origin's clock.</summary>
        public double? ChangedAt { get; init; }
    }

    public sealed record ControlPresentationHud
    {
        /// <summary>Changes when a new panel is put up; an update keeps it.</summary>
        public required string Id { get; init; }

        public required string Message { get; init; }

        public string? Detail { get; init; }

        public string? Spinner { get; init; }

        public string Position { get; init; } = "center";

        public string? TextColor { get; init; }

        public double? FontSize { get; init; }

        public bool Sticky { get; init; }

        public bool Frame { get; init; } = true;

        public bool Markdown { get; init; }

        public int? SizePercent { get; init; }

        public string? BackgroundColor { get; init; }

        /// <summary>agterm's PresentationPane: the pane's identity for a panel over one pane of a split; absent over the whole session.</summary>
        [System.Text.Json.Serialization.JsonConverter(typeof(PresentationPaneConverter))]
        public string? Pane { get; init; }

        /// <summary>Seconds until the origin hides it; absent for one that stays.</summary>
        public double? Remaining { get; init; }

        /// <summary>
        /// When the origin last put it up or updated it, in Unix milliseconds: agterm's publish generation, so an update
        /// with the same content still reaches the viewer and restarts its timer there.
        /// </summary>
        public long? Published { get; init; }
    }

    public static class ControlPresentationKinds
    {
        /// <summary>The presentation protocol this agshell speaks, which live tree reports.</summary>
        public const int Version = 1;

        public const string Snapshot = "snapshot";
        public const string Status = "status";
        public const string Context = "context";
        public const string Hud = "hud";
        public const string Notify = "notify";

        /// <summary>Sent every 10 seconds, so the other side can tell a quiet stream from a dead one.</summary>
        public const string Ping = "ping";

        /// <summary>agterm's ack: the presenter's answer to a ping; one that stops answering is dropped after 30 seconds.</summary>
        public const string Ack = "ack";

        /// <summary>agterm's presenter.acquire: a viewer whose hello asked for presenter mode asks for the role.</summary>
        public const string PresenterAcquire = "presenter.acquire";

        public const string PresenterGranted = "presenter.granted";

        /// <summary>Another stream holds the role: this one mirrors, and asks again only when it reconnects.</summary>
        public const string PresenterRefused = "presenter.refused";

        public const string AskRequest = "ask.request";

        /// <summary>The question ended on the session's machine: the presenter takes it down without answering.</summary>
        public const string AskDismiss = "ask.dismiss";

        /// <summary>From the presenter: the answer, a button id or none for escape.</summary>
        public const string AskResolve = "ask.resolve";

        /// <summary>From the presenter: it could not show the question, or it was cancelled there; the question goes back.</summary>
        public const string AskRejected = "ask.rejected";

        /// <summary>The split changed on the session's machine.</summary>
        public const string Layout = "layout";

        /// <summary>A status sound to play on the presenter: Title names it, absent for its own blocked sound.</summary>
        public const string Sound = "sound";

        public const string OverlayRequest = "overlay.request";

        /// <summary>The overlay's job ended on the session's machine: the presenter takes its terminal down.</summary>
        public const string OverlayClose = "overlay.close";

        /// <summary>From the presenter: it could not show the overlay, which ends launch-failed.</summary>
        public const string OverlayRejected = "overlay.rejected";

        /// <summary>The session's machine resized a session-wide overlay it handed over.</summary>
        public const string OverlayResize = "overlay.resize";

        /// <summary>From the presenter: its surface for the job is gone, which frees the slot held for it.</summary>
        public const string OverlayClosed = "overlay.closed";

        /// <summary>
        /// agterm's hello, first both ways: the viewer names its version, the kinds it speaks and the mode it wants; the
        /// session's machine answers with the version and kinds both speak and the mode offered.
        /// </summary>
        public const string Hello = "hello";

        /// <summary>agterm's ask.form: a presenter that draws a question's text field, choice list and cancel role.</summary>
        public const string AskForm = "ask.form";

        /// <summary>agterm's ask.other: a presenter that draws a choice list's free-form row.</summary>
        public const string AskOther = "ask.other";

        /// <summary>agterm's pageKinds: what a presenter must speak to be handed a page.</summary>
        public static readonly IReadOnlyList<string> PageKinds = new[] { "page.request", "page.rejected", "page.close", "page.closed", "page.command", "page.state" };

        /// <summary>agterm's supportedKinds: what a stream may negotiate.</summary>
        public static readonly IReadOnlyList<string> SupportedKinds = new[] { "status", "hud", "notify", "context", "layout", "sound", AskForm, AskOther }.Concat(PageKinds).ToList();

        /// <summary>agterm's page frames: a page the session's machine hands to the presenter, which shows it there.</summary>
        public const string PageRequest = "page.request";

        /// <summary>A reload, history step or browser hand-off asked for on the session's machine, numbered.</summary>
        public const string PageCommandKind = "page.command";

        /// <summary>The page was closed on the session's machine: the presenter takes it down.</summary>
        public const string PageClose = "page.close";

        /// <summary>From the presenter: it could not show the page, which closes unanswered.</summary>
        public const string PageRejected = "page.rejected";

        /// <summary>From the presenter: the page closed there, which frees its slot here.</summary>
        public const string PageClosed = "page.closed";

        /// <summary>From the presenter: what the page shows now, for tree here.</summary>
        public const string PageState = "page.state";
    }

    /// <summary>
    /// agterm's remote page: a web page the session's machine hands to the machine presenting the session, which loads it
    /// itself, with its own network and storage. Closed is set once it closed on the session's machine.
    /// </summary>
    public sealed record ControlPresentationPage
    {
        public required string Id { get; init; }

        public int Owner { get; init; }

        /// <summary>agterm's PresentationPane: the pane's identity for one pane of a split; absent over the whole session.</summary>
        [System.Text.Json.Serialization.JsonConverter(typeof(PresentationPaneConverter))]
        public string? Pane { get; init; }

        /// <summary>The web page's address, or for a file page the file's path on the session's machine.</summary>
        public required string Url { get; init; }

        /// <summary>A local file of the session's machine, served through session overlay page-serve with Token.</summary>
        public bool IsFile { get; init; }

        /// <summary>The directory the file page may read, on the session's machine; absent for the file alone.</summary>
        public string? Root { get; init; }

        /// <summary>What session overlay page-serve on the session's machine takes to serve this page's files, while it is open.</summary>
        public string? Token { get; init; }

        public bool Js { get; init; }

        public bool Navigation { get; init; }

        public bool Chromeless { get; init; }

        public bool Persistent { get; init; }

        public bool Browse { get; init; }

        public int? SizePercent { get; init; }

        public string? BackgroundColor { get; init; }

        public bool Follow { get; init; }

        /// <summary>The visibility asked for on the session's machine.</summary>
        public bool Hidden { get; init; }

        public bool Closed { get; init; }

        /// <summary>Every command asked for so far, by number; the presenter runs those past the last it saw.</summary>
        public List<ControlPresentationPageCommand>? Commands { get; init; }
    }

    public sealed record ControlPresentationPageCommand
    {
        public int Seq { get; init; }

        /// <summary>reload, reload-original, back, forward, browser, or agterm's hide, show and resize.</summary>
        public required string Action { get; init; }

        /// <summary>resize: the session-wide page's new size; null for full.</summary>
        public int? SizePercent { get; init; }
    }

    /// <summary>A page as the presenter shows it: agterm's htmlOverlays fields, reported back to the session's machine.</summary>
    public sealed record ControlPresentationPageState
    {
        public string State { get; init; } = "loading";

        public string? Error { get; init; }

        /// <summary>agterm's address: the page's logical address on the session's machine.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("address")]
        public string? Page { get; init; }

        public string? Title { get; init; }

        public bool? CanGoBack { get; init; }

        public bool? CanGoForward { get; init; }

        public bool Hidden { get; init; }
    }

    /// <summary>
    /// One line of agshellctl live present: a snapshot first, then what changed. Absent fields are idle, none, closed. On the
    /// wire agterm's envelope (<see cref="PresentationFrameConverter"/>): gen is the stream's generation on the session's
    /// machine, rev counts its frames, so a viewer drops what an earlier connection left in flight.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(PresentationFrameConverter))]
    public sealed record ControlPresentationFrame
    {
        public required string Kind { get; init; }

        public int Gen { get; init; }

        public int Rev { get; init; }

        public ControlPresentation? Snapshot { get; init; }

        public ControlPresentationStatus? Status { get; init; }

        public string? Context { get; init; }

        public ControlPresentationHud? Hud { get; init; }

        public string? Title { get; init; }

        public string? Body { get; init; }

        public ControlPresentationAsk? Ask { get; init; }

        public ControlPresentationOverlay? Overlay { get; init; }

        public ControlPresentationLayout? Layout { get; init; }

        public ControlPresentationPage? Page { get; init; }

        public ControlPresentationPageCommand? PageCommand { get; init; }

        public ControlPresentationPageState? PageState { get; init; }

        /// <summary>hello: the protocol version.</summary>
        public int? Version { get; init; }

        /// <summary>hello: the kinds the side speaks, or both speak in the answer.</summary>
        public List<string>? Kinds { get; init; }

        /// <summary>hello: presenter or mirror, asked for by the viewer and offered in the answer.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("mode")]
        public string? PresentationMode { get; init; }

        /// <summary>ask.dismiss, ask.resolve, ask.rejected: the question and the generation it was handed under.</summary>
        public string? Id { get; init; }

        public int? Owner { get; init; }

        public string? Button { get; init; }

        public string? Text { get; init; }

        public List<string>? Selected { get; init; }

        /// <summary>agterm's PresentationPageChange.reason: why a presenter refused a page, which the session's machine logs.</summary>
        public string? Reason { get; init; }
    }

    /// <summary>live tree: a machine's attachable sessions, where its live daemons are, and the presentation protocol it speaks.</summary>
    public sealed record ControlRemoteTree
    {
        /// <summary>The ssh destination asked, stamped on by the asking side; absent for this machine's own.</summary>
        public string? Host { get; init; }

        public ControlRemoteEndpoint? Endpoint { get; init; }

        /// <summary>The presentation protocol version; absent from an agshell too old to stream.</summary>
        public int? Presentation { get; init; }

        public required List<ControlRemoteSession> Sessions { get; init; }
    }

    /// <summary>A live daemon's pipe, which agshellctl live proxy relays.</summary>
    public sealed record ControlRemoteEndpoint
    {
        public required string Pipe { get; init; }
    }

    /// <summary>A session another machine can attach: its panes are kept by live daemons as &lt;id&gt;:Main and &lt;id&gt;:Split.</summary>
    public sealed record ControlRemoteSession
    {
        public required string Id { get; init; }

        public required string Name { get; init; }

        private readonly string? workspace;
        private readonly string? legacyWorkspace;
        private readonly string? window;
        private readonly string? legacyWindow;

        /// <summary>The names to show; group by the ids, since names repeat. agterm's field names.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("workspaceName")]
        public string Workspace { get => workspace ?? legacyWorkspace ?? string.Empty; init => workspace = value; }

        /// <summary>The name an earlier agshell sent the workspace under; read only, never written.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("workspace")]
        public string? LegacyWorkspace { get => null; init => legacyWorkspace = value; }

        [System.Text.Json.Serialization.JsonPropertyName("workspaceID")]
        public string? WorkspaceId { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("windowName")]
        public string Window { get => window ?? legacyWindow ?? string.Empty; init => window = value; }

        [System.Text.Json.Serialization.JsonPropertyName("window")]
        public string? LegacyWindow { get => null; init => legacyWindow = value; }

        [System.Text.Json.Serialization.JsonPropertyName("windowID")]
        public string? WindowId { get; init; }

        public string? Cwd { get; init; }

        public bool Split { get; init; }

        /// <summary>vertical or horizontal, only with a split.</summary>
        public string? SplitAxis { get; init; }

        /// <summary>Absent while idle, as the far side's tree reports it.</summary>
        public string? Status { get; init; }

        public string? StatusColor { get; init; }

        public string? StatusShape { get; init; }

        public string? Context { get; init; }

        public List<ControlRemotePane>? Panes { get; init; }
    }

    /// <summary>One pane of an attachable session: its role, its daemon's name, and the program it runs, if any.</summary>
    public sealed record ControlRemotePane
    {
        public required string Pane { get; init; }

        public required string Daemon { get; init; }

        /// <summary>The program's argv; absent for a bare shell. An earlier agshell sent one command line instead.</summary>
        [System.Text.Json.Serialization.JsonConverter(typeof(ArgvConverter))]
        public List<string>? Foreground { get; init; }
    }

    /// <summary>An argv array, or a command line from an earlier agshell, read as its program and the rest.</summary>
    public sealed class ArgvConverter : System.Text.Json.Serialization.JsonConverter<List<string>?>
    {
        public override List<string>? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case System.Text.Json.JsonTokenType.Null:
                    return null;
                case System.Text.Json.JsonTokenType.String:
                    string line = (reader.GetString() ?? string.Empty).Trim();
                    if (line.Length == 0)
                    {
                        return null;
                    }

                    int end = line.StartsWith('"') ? line.IndexOf('"', 1) : line.IndexOf(' ');
                    string program = end < 0 ? line.Trim('"') : line[(line.StartsWith('"') ? 1 : 0)..end];
                    string rest = end < 0 ? string.Empty : line[(end + 1)..].Trim();
                    return rest.Length == 0 ? new List<string> { program } : new List<string> { program, rest };
                case System.Text.Json.JsonTokenType.StartArray:
                    List<string> argv = new List<string>();
                    while (reader.Read() && reader.TokenType != System.Text.Json.JsonTokenType.EndArray)
                    {
                        argv.Add(reader.TokenType == System.Text.Json.JsonTokenType.String ? reader.GetString() ?? string.Empty : string.Empty);
                        if (reader.TokenType is System.Text.Json.JsonTokenType.StartArray or System.Text.Json.JsonTokenType.StartObject)
                        {
                            reader.Skip();
                        }
                    }

                    return argv;
                default:
                    throw new System.Text.Json.JsonException("foreground is neither a string nor an array");
            }
        }

        public override void Write(System.Text.Json.Utf8JsonWriter writer, List<string>? value, System.Text.Json.JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartArray();
            foreach (string item in value)
            {
                writer.WriteStringValue(item);
            }

            writer.WriteEndArray();
        }
    }

    /// <summary>agterm's window list entry. Geometry and the frame states are omitted for a closed window.</summary>
    public sealed record ControlWindowNode
    {
        public required string Id { get; init; }

        public required string Name { get; init; }

        public bool Open { get; init; }

        public bool Active { get; init; }

        public bool? SidebarVisible { get; init; }

        public ControlGeometry? Geometry { get; init; }

        public bool? Fullscreen { get; init; }

        public bool? Zoomed { get; init; }

        public bool? Minimized { get; init; }

        /// <summary>agterm's autoFollowMs: the auto-follow timeout in milliseconds; null when off.</summary>
        public long? AutoFollowMs { get; init; }
    }

    public sealed record ControlGeometry
    {
        public int X { get; init; }

        public int Y { get; init; }

        public int Width { get; init; }

        public int Height { get; init; }

        public int Display { get; init; }
    }

    /// <summary>agterm's HUD values the CLI checks before sending and the app checks again.</summary>
    public static class ControlHud
    {
        /// <summary>agterm's HudPosition anchors.</summary>
        public static readonly IReadOnlyList<string> Positions = new[] { "top-left", "top-center", "top-right", "center-left", "center", "center-right", "bottom-left", "bottom-center", "bottom-right" };

        /// <summary>agterm's position aliases, top-center and bottom-center.</summary>
        public static readonly IReadOnlyList<string> PositionAliases = new[] { "top", "bottom" };

        /// <summary>agterm's HudSpinner styles; none, the read-back of a static panel, is accepted beside them.</summary>
        public static readonly IReadOnlyList<string> Spinners = new[] { "bar", "braille", "circle", "blocks", "dot" };

        public const string NoSpinner = "none";

        /// <summary>agterm's longest auto-hide: a day.</summary>
        public const double MaxHideAfter = 86_400;

        public const double MinFontSize = 6;

        public const double MaxFontSize = 72;
    }

    public static class ControlUrls
    {
        /// <summary>An absolute address on this machine: 127.0.0.1, ::1 or localhost.</summary>
        public static bool IsLoopback(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && (uri.IsLoopback || uri.Host == "localhost");
        }

        /// <summary>agterm's HtmlSource.webURL: an http or https address.</summary>
        public static bool IsWeb(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
    }

    public static class ControlStatusNames
    {
        /// <summary>agterm's status glyph silhouettes; circle is the default.</summary>
        public static readonly IReadOnlyList<string> Shapes = new[] { "circle", "square", "triangle", "diamond", "capsule", "star" };
    }

    /// <summary>
    /// agterm's BackgroundWatermark, the session background behind the terminal grid: an image (PNG or JPEG), a word or two
    /// of text, or a solid color. <see cref="ImagePath"/> is the image file as Windows reads it.
    /// </summary>
    public sealed record ControlBackground
    {
        /// <summary>agterm's background-image-position values: center and the eight edge and corner anchors.</summary>
        public static readonly IReadOnlyList<string> Positions = new[] { "top-left", "top-center", "top-right", "center-left", "center", "center-right", "bottom-left", "bottom-center", "bottom-right" };

        /// <summary>agterm's background-image-fit values.</summary>
        public static readonly IReadOnlyList<string> Fits = new[] { "contain", "cover", "stretch", "none" };

        /// <summary>agterm's WatermarkConfig.maxTextLength, in characters.</summary>
        public const int MaxTextLength = 256;

        private readonly string? position;

        public required string Kind { get; init; }

        public string? ImagePath { get; init; }

        public string? Text { get; init; }

        public string? ColorHex { get; init; }

        public double? Opacity { get; init; }

        public string? Fit { get; init; }

        /// <summary>A side alone, as an earlier agshell saved it, reads as that side's center.</summary>
        public string? Position
        {
            get => position;
            init => position = value switch
            {
                "top" => "top-center",
                "bottom" => "bottom-center",
                "left" => "center-left",
                "right" => "center-right",
                _ => value,
            };
        }

        public bool? Repeats { get; init; }

        /// <summary>An earlier agshell's names for <see cref="ImagePath"/>, <see cref="ColorHex"/> and <see cref="Repeats"/>, read and never written.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("path")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? LegacyPath
        {
            get => null;
            init => ImagePath ??= value;
        }

        [System.Text.Json.Serialization.JsonPropertyName("color")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? LegacyColor
        {
            get => null;
            init => ColorHex ??= value;
        }

        [System.Text.Json.Serialization.JsonPropertyName("repeat")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public bool? LegacyRepeat
        {
            get => null;
            init => Repeats ??= value;
        }
    }

    /// <summary>One choice of agterm's picker; a trailing status glyph takes session status values.</summary>
    public sealed record ControlPickItem
    {
        public required string Id { get; init; }

        public required string Label { get; init; }

        public string? Subtitle { get; init; }

        public string? Status { get; init; }

        public string? StatusColor { get; init; }

        public string? StatusShape { get; init; }
    }

    /// <summary>pick.result: pending, picked (id, label, index), custom (the query) or cancelled.</summary>
    public sealed record ControlPickResult
    {
        public required string Result { get; init; }

        public string? Id { get; init; }

        public string? Label { get; init; }

        public int? Index { get; init; }

        public string? Query { get; init; }
    }

    public sealed record ControlAskButton
    {
        public required string Id { get; init; }

        public required string Label { get; init; }

        /// <summary>One ASCII letter that presses the button.</summary>
        public string? Hotkey { get; init; }
    }

    public sealed record ControlAskChoice
    {
        public required string Id { get; init; }

        public required string Label { get; init; }

        public string? Subtitle { get; init; }
    }

    /// <summary>ask.result: pending, answered (the button, plus the field's text or the list's ids), escaped or cancelled.</summary>
    public sealed record ControlAskResult
    {
        public required string Result { get; init; }

        public string? Id { get; init; }

        public string? Label { get; init; }

        public int? Index { get; init; }

        public string? Text { get; init; }

        public List<string>? Selected { get; init; }

        /// <summary>Why a question ended without an answer: presentation-lost when it came back from a presenter and could not be shown.</summary>
        public string? Reason { get; init; }
    }

    /// <summary>One terminal of a session: surface:&lt;session&gt;:&lt;pane&gt;, as surface zoom and cursor take it.</summary>
    /// <summary>
    /// agterm's surface node: kind is the user-facing name (left, right, scratch, overlay, overlay-left, overlay-right);
    /// active and visible derive from the session's own flags (overlay, scratch, focused split), not from zoom.
    /// </summary>
    public sealed record ControlSurfaceNode
    {
        public required string Id { get; init; }

        public required string Kind { get; init; }

        public bool Active { get; init; }

        public bool Visible { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("paneID")]
        public string? PaneId { get; init; }

        /// <summary>A live pane's lead: leader, follower (covered here) or unowned; absent when the pane has none.</summary>
        public string? Lead { get; init; }

        /// <summary>agterm's backedByZmx: a left or right pane kept by a live daemon; null for a scratch, an overlay or another machine's pane.</summary>
        public bool? BackedByZmx { get; init; }

        public ControlReconnectNode? Reconnect { get; init; }
    }

    /// <summary>A terminal-style question waiting in a session, and the pane it sits over.</summary>
    public sealed record ControlAskNode
    {
        public required string Id { get; init; }

        public string? Pane { get; init; }

        /// <summary>Handed to the machine presenting the session: it waits here, drawn there.</summary>
        public bool? Remote { get; init; }

        /// <summary>A question another machine handed here to draw.</summary>
        public bool? Replica { get; init; }
    }

    /// <summary>A slot reserved for an overlay that runs on the machine presenting the session.</summary>
    public sealed record ControlRemoteOverlayNode
    {
        /// <summary>left or right; absent for the session-wide slot.</summary>
        public string? Pane { get; init; }

        public int? SizePercent { get; init; }

        /// <summary>The page's id when the slot holds a page shown there.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("pageID")]
        public string? PageId { get; init; }
    }

    public sealed record ControlResponse
    {
        public bool Ok { get; init; }

        public ControlResult? Result { get; init; }

        public string? Error { get; init; }

        public static ControlResponse Success(ControlResult? result = null)
        {
            return new ControlResponse { Ok = true, Result = result };
        }

        public static ControlResponse Failure(string error)
        {
            return new ControlResponse { Ok = false, Error = error };
        }
    }

    public sealed record ControlHtmlOverlayNode
    {
        /// <summary>The page's id, the one overlay result --page reads.</summary>
        public required string Id { get; init; }

        /// <summary>left or right; absent for the session-wide slot.</summary>
        public string? Pane { get; init; }

        public string? File { get; init; }

        public string? Cwd { get; init; }

        public string? Url { get; init; }

        /// <summary>loading, loaded or failed, as the page's view reports its last navigation; failed carries the error.</summary>
        public required string State { get; init; }

        public string? Error { get; init; }

        /// <summary>The address shown now and its title, once the view reports them.</summary>
        public string? Page { get; init; }

        public string? Title { get; init; }

        /// <summary>Where the page's history can move.</summary>
        public bool? CanGoBack { get; init; }

        public bool? CanGoForward { get; init; }

        /// <summary>The pages' zoom, 1 at actual size: one setting for the app, so every page reports the same, as agterm's.</summary>
        public double? Zoom { get; init; }

        public bool? Navigation { get; init; }

        public bool Javascript { get; init; }

        public bool Chromeless { get; init; }

        public bool Persistent { get; init; }

        public bool Browse { get; init; }

        /// <summary>The page is hidden; absent while it shows.</summary>
        public bool? Hidden { get; init; }

        /// <summary>On the session's machine: the page shows on the machine presenting the session.</summary>
        public bool? Remote { get; init; }

        /// <summary>On the presenter: the page came from the session's machine.</summary>
        public bool? Replica { get; init; }
    }

    public sealed record ControlHudNode
    {
        public required string Message { get; init; }

        public string? Detail { get; init; }

        /// <summary>The spinner style, or none for a static panel.</summary>
        public required string Spinner { get; init; }

        public string? BackgroundColor { get; init; }

        public string? TextColor { get; init; }

        /// <summary>The effective share of the pane's width: the one asked for, or as measured where it is drawn.</summary>
        public int? SizePercent { get; init; }

        /// <summary>The share of the pane's height, always measured from the message.</summary>
        public int? HeightPercent { get; init; }

        public required string Position { get; init; }

        /// <summary>left or right while drawn over one pane; absent over the whole session.</summary>
        public string? Pane { get; init; }

        /// <summary>Seconds, 0 for a panel that stays until something closes it.</summary>
        public double HideAfter { get; init; }

        public bool Markdown { get; init; }

        public double? FontSize { get; init; }

        public bool Sticky { get; init; }

        public bool Frame { get; init; }
    }

    /// <summary>agterm's session.restart receipt: the pane's shells, the one that ended and the one that replaced it.</summary>
    public sealed record ControlRestartReceipt
    {
        [System.Text.Json.Serialization.JsonPropertyName("paneID")]
        public string? PaneId { get; init; }

        /// <summary>Absent when the pane's shell had already ended.</summary>
        public int? OldPid { get; init; }

        public int NewPid { get; init; }

        /// <summary>The program asked to run again when no --command was given.</summary>
        public List<string>? ReplayedArgv { get; init; }
    }

    /// <summary>agterm's ControlKeymapDiagnostic: a problem a config file has, by its line; 0 for one about the whole file.</summary>
    public sealed record ControlConfigDiagnostic
    {
        public int Line { get; init; }

        public required string Message { get; init; }

        /// <summary>From agshell's own wording, "line N: message".</summary>
        public static ControlConfigDiagnostic Parse(string text)
        {
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(text, "^line (\\d+): (.*)$", System.Text.RegularExpressions.RegexOptions.Singleline);
            return match.Success ? new ControlConfigDiagnostic { Line = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), Message = match.Groups[2].Value } : new ControlConfigDiagnostic { Message = text };
        }
    }

    /// <summary>
    /// agterm's ControlZmxInventory: the restore policy, whether every pane was accounted for, where the host listens, and a
    /// row per kept shell.
    /// </summary>
    public sealed record ControlLiveInventory
    {
        public required ControlRestoreStatus Restore { get; init; }

        public bool InventoryComplete { get; init; }

        public ControlLiveEndpoint? Endpoint { get; init; }

        /// <summary>agterm's liveReset: a confirmed reset waiting for the quit, and what this launch's reset did.</summary>
        public ControlLiveResetReadback? LiveReset { get; init; }

        public required List<ControlLiveEntry> Entries { get; init; }
    }

    /// <summary>agterm's ControlLiveResetStatus: what live.reset confirmed; outdated is how many sessions predate the recorded daemon build change, omitted when none.</summary>
    public sealed record ControlLiveResetStatus
    {
        public required int Sessions { get; init; }

        public required int Panes { get; init; }

        public required bool Pending { get; init; }

        public int? Outdated { get; init; }
    }

    /// <summary>agterm's ControlLiveResetReadback: the panes a confirmed reset waits to reset at the quit, and the last launch's result.</summary>
    public sealed record ControlLiveResetReadback
    {
        public int? Pending { get; init; }

        public LiveResetOutcome? Last { get; init; }
    }

    /// <summary>agterm's LiveReset.Outcome: how the launch after a reset dealt with the confirmed panes and their sessions.</summary>
    public sealed record LiveResetOutcome
    {
        public required LiveResetPaneCounts Panes { get; init; }

        /// <summary>The panes whose shell outlived the reset: they got no saved command at this launch.</summary>
        public required List<string> Unconfirmed { get; init; }

        public required LiveResetSessionCounts Sessions { get; init; }

        public required bool InventoryFailed { get; init; }
    }

    public sealed record LiveResetPaneCounts
    {
        public required int Confirmed { get; init; }

        public required int Killed { get; init; }

        public required int Gone { get; init; }

        public required int Skipped { get; init; }
    }

    public sealed record LiveResetSessionCounts
    {
        public required int Affected { get; init; }

        public required int Reset { get; init; }

        public required int Partial { get; init; }

        public required int Unconfirmed { get; init; }
    }

    /// <summary>agterm's ControlZmxEndpoint: the host's program, and where it listens (a pipe here, a socket directory there).</summary>
    public sealed record ControlLiveEndpoint
    {
        public required string Executable { get; init; }

        public required string SocketDirectory { get; init; }
    }

    /// <summary>
    /// agterm's ControlZmxEntry: a kept shell, whether a pane claims it (claimed, pendingClose for a session in its undo
    /// window, orphan), that it runs, how many clients show it, its process, and the pane that owns it.
    /// </summary>
    public sealed record ControlLiveEntry
    {
        public required string Daemon { get; init; }

        public required string State { get; init; }

        public required string Observation { get; init; }

        public int? Clients { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("leaderPID")]
        public int? LeaderPid { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("windowID")]
        public string? WindowId { get; init; }

        public string? WindowName { get; init; }

        /// <summary>open or closed.</summary>
        public string? WindowState { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("workspaceID")]
        public string? WorkspaceId { get; init; }

        public string? WorkspaceName { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("sessionID")]
        public string? SessionId { get; init; }

        public string? SessionName { get; init; }

        public string? Pane { get; init; }

        /// <summary>agterm's outdated: the daemon started before the launch's recorded daemon build change; omitted otherwise.</summary>
        public bool? Outdated { get; init; }
    }

    /// <summary>
    /// agterm's parsed keymap and the chords in the applied menu. Windows selectors are the page's dispatch ids.
    /// </summary>
    public sealed record ControlKeymap
    {
        public required string Path { get; init; }

        public required List<ControlKeymapAction> Actions { get; init; }

        public required List<ControlKeymapCommand> Commands { get; init; }

        public required List<ControlConfigDiagnostic> Diagnostics { get; init; }

        public string? GlobalHotkey { get; init; }

        public List<ControlKeymapMenuItem>? Menu { get; init; }
    }

    public sealed record ControlKeymapMenuItem
    {
        public required string Menu { get; init; }

        public required string Title { get; init; }

        public required string Chord { get; init; }

        public string? Selector { get; init; }

        public bool? Enabled { get; init; }
    }

    /// <summary>agterm's ControlKeymapAction: an action's first chord, the rest, whether keymap.conf moved it, and --repeat.</summary>
    public sealed record ControlKeymapAction
    {
        public required string Action { get; init; }

        public string? Chord { get; init; }

        public List<string>? Alternates { get; init; }

        public bool? Overridden { get; init; }

        public bool? Repeats { get; init; }
    }

    /// <summary>agterm's ControlKeymapCommand: a custom command, its key, and how its failure shows.</summary>
    public sealed record ControlKeymapCommand
    {
        public required string Name { get; init; }

        public string? Shortcut { get; init; }

        public bool ErrorHud { get; init; }

        public required string ErrorPosition { get; init; }

        public string? ErrorPane { get; init; }

        public bool Repeats { get; init; }
    }

    /// <summary>agterm's ControlHooks: hooks.conf's path, its problems, and each hook with its live state.</summary>
    public sealed record ControlHooks
    {
        public required string Path { get; init; }

        public required List<ControlConfigDiagnostic> Diagnostics { get; init; }

        public required List<ControlHookEntry> Hooks { get; init; }
    }

    /// <summary>agterm's ControlHookEntry: one hook, and what it is doing now.</summary>
    public sealed record ControlHookEntry
    {
        public required string Kind { get; init; }

        public required string Command { get; init; }

        public int Line { get; init; }

        public int? RunningPid { get; init; }

        public double? ElapsedSeconds { get; init; }

        public int Pending { get; init; }

        public long Dropped { get; init; }

        public string? LastFailure { get; init; }

        /// <summary>Removed from the file while its script still runs.</summary>
        public bool? Retired { get; init; }

        /// <summary>agshell's own counters, beside agterm's fields: how many times it ran and how many of those failed.</summary>
        public int Runs { get; init; }

        public int Failures { get; init; }
    }

    /// <summary>
    /// agterm's ControlRestoreStatus: the mode set for the next launch, the one this launch asked for and the one it got,
    /// whether a restart is needed to apply the setting, and why live fell back when it did.
    /// </summary>
    public sealed record ControlRestoreStatus
    {
        public required string Configured { get; init; }

        public required string RequestedAtLaunch { get; init; }

        public required string Active { get; init; }

        public bool RestartRequired { get; init; }

        public string? UnavailableReason { get; init; }
    }

    /// <summary>agterm's ControlRecentItem: a recently closed session or workspace, with the id recent open takes.</summary>
    public sealed record ControlRecentItem
    {
        public required string Id { get; init; }

        /// <summary>session or workspace.</summary>
        public required string Kind { get; init; }

        public required string Title { get; init; }

        public string? Subtitle { get; init; }

        /// <summary>When it closed, in Unix seconds.</summary>
        public double ClosedAt { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("workspaceID")]
        public required string WorkspaceId { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("sessionIDs")]
        public required List<string> SessionIds { get; init; }
    }

    /// <summary>agterm's ControlRecentOpen: the entry reopened, the window it went to, and whether all of it came back.</summary>
    public sealed record ControlRecentOpen
    {
        public required ControlRecentItem Item { get; init; }

        public string? Window { get; init; }

        public bool Complete { get; init; }
    }

    /// <summary>agterm's ControlCursor: the zero-based column of a surface's cursor.</summary>
    public sealed record ControlCursor
    {
        public int Column { get; init; }
    }

    public sealed record ControlResult
    {
        public ControlRestartReceipt? Restart { get; init; }

        /// <summary>agterm's ratio: the primary pane's share session.resize applied, after clamping or a nudge.</summary>
        public double? Ratio { get; init; }

        /// <summary>The created or affected id.</summary>
        public string? Id { get; init; }

        public ControlTree? Tree { get; init; }

        public string? Text { get; init; }

        /// <summary>agterm's affected: how many things a batch command changed, sessions closed or moved, shells pruned.</summary>
        public int? Affected { get; init; }

        /// <summary>A count, such as the diagnostics a config reload found.</summary>
        public int? Count { get; init; }

        public ControlEventBatch? Events { get; init; }

        /// <summary>An overlay program's exit status; null until it exits.</summary>
        public int? ExitCode { get; init; }

        /// <summary>agterm's pageOutcome: a page's id, where it stands and the value it submitted.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("pageOutcome")]
        public ControlPageResult? Page { get; init; }

        /// <summary>The pane a command acted on (left, right or scratch), where a pane id could have named any.</summary>
        public string? Pane { get; init; }

        public List<ControlWindowNode>? Windows { get; init; }

        /// <summary>live tree: what a machine offers to attach, as agterm's zmx tree.</summary>
        public ControlRemoteTree? Remote { get; init; }

        /// <summary>live.present: what another machine shows of the session on its row.</summary>
        public ControlPresentation? Presentation { get; init; }

        public ControlPickResult? Pick { get; init; }

        /// <summary>agterm's zmx: the inventory of the shells live daemons keep, for live.list.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("zmx")]
        public ControlLiveInventory? Live { get; init; }

        /// <summary>agterm's keymap: the resolved keymap, for keymap.list.</summary>
        public ControlKeymap? Keymap { get; init; }

        /// <summary>agterm's hooks: the hook definitions and their live state, for hooks.list.</summary>
        public ControlHooks? Hooks { get; init; }

        /// <summary>agterm's restore: the restore policy for restore.mode, and the header live list repeats.</summary>
        public ControlRestoreStatus? Restore { get; init; }

        /// <summary>agterm's liveReset: what live.reset confirmed, for its reply.</summary>
        public ControlLiveResetStatus? LiveReset { get; init; }

        /// <summary>agterm's recent: the recently closed entries for recent.list, newest first.</summary>
        public List<ControlRecentItem>? Recent { get; init; }

        /// <summary>agterm's recentOpen: what recent.open reopened and where.</summary>
        public ControlRecentOpen? RecentOpen { get; init; }

        /// <summary>agterm's theme: the theme in force, for theme.list and the echo of theme.set.</summary>
        public string? Theme { get; init; }

        /// <summary>The installed themes, for theme.list.</summary>
        public List<string>? Themes { get; init; }

        /// <summary>The terminal follows the Windows light/dark mode with the light and dark themes.</summary>
        public bool? Sync { get; init; }

        public string? Light { get; init; }

        public string? Dark { get; init; }

        /// <summary>agterm's app: the app serving this pipe, for version.</summary>
        public ControlAppIdentity? App { get; init; }

        /// <summary>agterm's installed: the build on disk, which differs from app once it was replaced under a running app; null when unreadable.</summary>
        public ControlAppIdentity? Installed { get; init; }

        /// <summary>agterm's cursor: the addressed surface's cursor position for surface.cursor.</summary>
        public ControlCursor? Cursor { get; init; }

        /// <summary>The window size window.resize applied, in points.</summary>
        public int? Width { get; init; }

        public int? Height { get; init; }

        /// <summary>The stored sidebar width sidebar.width applied, after clamping.</summary>
        public double? SidebarWidth { get; init; }

        /// <summary>agterm's pageID: the page overlay open --html or --url opened, which overlay result --page reads.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("pageID")]
        public string? PageId { get; init; }

        public ControlAskResult? Ask { get; init; }
    }

    /// <summary>What became of a page overlay: pending, submitted (with the value) or dismissed.</summary>
    public sealed record ControlPageResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("pageID")]
        public required string PageId { get; init; }

        /// <summary>pending, submitted or dismissed.</summary>
        public required string Outcome { get; init; }

        /// <summary>What a submitted page answered: the text itself, or any other JSON value as written. Absent otherwise.</summary>
        public string? Value { get; init; }
    }

    /// <summary>agterm's AppIdentity: a version, and the commit it was built from when one was recorded.</summary>
    public sealed record ControlAppIdentity
    {
        public required string Version { get; init; }

        public string? Commit { get; init; }

        /// <summary>From an informational version, "1.2.3+commit".</summary>
        public static ControlAppIdentity Parse(string informational)
        {
            string[] parts = informational.Split('+', 2);
            return new ControlAppIdentity { Version = parts[0], Commit = parts.Length == 2 && parts[1].Length > 0 && parts[1] != "unknown" ? parts[1] : null };
        }
    }

    public sealed record ControlTree
    {
        public required List<ControlWorkspaceNode> Workspaces { get; init; }

        public bool SidebarVisible { get; init; }

        public double SidebarWidth { get; init; }

        public bool QuickVisible { get; init; }

        /// <summary>"tree", or "flagged" while the sidebar lists flagged sessions.</summary>
        public string SidebarMode { get; init; } = "tree";

        /// <summary>How the flagged view lays its sessions out: flat, plain or tree.</summary>
        public string SidebarFlaggedLayout { get; init; } = "flat";

        /// <summary>Where a clicked web link opens: browser or overlay.</summary>
        public string LinkOpenMode { get; init; } = "browser";

        /// <summary>The zoomed surface (surface:&lt;session&gt;:&lt;pane&gt;), or quick; absent when nothing is zoomed.</summary>
        public string? ZoomedSurface { get; init; }

        /// <summary>The window this tree is of.</summary>
        public string? WindowId { get; init; }

        public string? WindowName { get; init; }

        /// <summary>The picker waiting for an answer, and the window's GUI question; absent when none.</summary>
        public string? PickPending { get; init; }

        public string? AskPending { get; init; }

        /// <summary>The workspace focus filter is on; the marked workspaces are those with <c>focused</c>.</summary>
        /// <summary>agterm's workspaceFilter: the workspace focus filter is applied.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("workspaceFilter")]
        public bool FocusEnabled { get; init; }

        /// <summary>agterm's app: the app serving this pipe, its version without a second request.</summary>
        public ControlAppIdentity? App { get; init; }

        /// <summary>Milliseconds since the last input in the window; null before any.</summary>
        public long? IdleMs { get; init; }

        /// <summary>The window's auto-follow timeout in milliseconds; null when it is off.</summary>
        public long? AutoFollowMs { get; init; }

        /// <summary>The dashboard's font: auto (--auto-size), fixed (--font-size) or untouched; null with no dashboard.</summary>
        public string? DashboardFontMode { get; init; }

        /// <summary>The font size the dashboard cells get; null with no dashboard or an untouched font.</summary>
        public double? DashboardFontSize { get; init; }

        /// <summary>True while the last write of windows.json failed.</summary>
        public bool? IndexUnsaved { get; init; }

        /// <summary>agterm's liveReset: omitted when no reset is pending and this launch consumed none.</summary>
        public ControlLiveResetReadback? LiveReset { get; init; }

        /// <summary>The dashboard's cells as session:left or session:right, while it is open.</summary>
        public List<string>? DashboardMembers { get; init; }

        public string? DashboardHighlighted { get; init; }
    }

    public sealed record ControlWorkspaceNode
    {
        public required string Id { get; init; }

        public required string Name { get; init; }

        /// <summary>The workspace a new session lands in.</summary>
        public bool Active { get; init; }

        /// <summary>As agterm's: true while folded, absent while expanded.</summary>
        public bool? Collapsed { get; init; }

        /// <summary>In the focus filter's marked set: true, else absent.</summary>
        public bool? Focused { get; init; }

        public required List<ControlSessionNode> Sessions { get; init; }
    }

    /// <summary>A remote row's presentation stream: connecting, connected or failed with why, and whether it presents.</summary>
    public sealed record ControlPresentationNode
    {
        public required string State { get; init; }

        /// <summary>presenter or mirror.</summary>
        public required string Mode { get; init; }

        public string? Error { get; init; }
    }

    /// <summary>On a session other machines attached: how many mirror it, and whether one presents it.</summary>
    public sealed record ControlPresentersNode
    {
        public int Mirrors { get; init; }

        public bool? Presenter { get; init; }
    }

    /// <summary>A remote pane waiting to be attached again: the failures in a row and what ssh said on the last try.</summary>
    public sealed record ControlReconnectNode
    {
        public int Failures { get; init; }

        public string? Reason { get; init; }
    }

    public sealed record ControlSessionNode
    {
        /// <summary>The ssh destination of a row attached from another machine.</summary>
        public string? RemoteHost { get; init; }

        public ControlPresentationNode? Presentation { get; init; }

        public ControlPresentersNode? Presenters { get; init; }

        /// <summary>The slots held for overlays the presenter shows; absent when none.</summary>
        public List<ControlRemoteOverlayNode>? RemoteOverlays { get; init; }

        public required string Id { get; init; }

        /// <summary>In the user's flagged working set.</summary>
        public bool Flagged { get; init; }

        public required string Name { get; init; }

        public string? CustomName { get; init; }

        /// <summary>The last directory the shell reported; empty until it reports one.</summary>
        public required string Cwd { get; init; }

        public required string Shell { get; init; }

        public bool Active { get; init; }

        /// <summary>As agterm's: absent while idle.</summary>
        public string? Status { get; init; }

        /// <summary>agterm's statusBlink: the status glyph blinks.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("statusBlink")]
        public bool? Blink { get; init; }

        /// <summary>The pane whose agent set the status, when the write named one: left, right or scratch.</summary>
        public string? StatusPane { get; init; }

        /// <summary>Unix seconds of the last status write, idle included; absent before the first one.</summary>
        public double? StatusChangedAt { get; init; }

        /// <summary>Notifications not yet seen in this session; absent at none.</summary>
        public int? Unseen { get; init; }

        /// <summary>The split is on screen.</summary>
        public bool Split { get; init; }

        /// <summary>The session has a split, shown or hidden.</summary>
        public bool? HasSplit { get; init; }

        public string? SplitAxis { get; init; }

        public double? SplitRatio { get; init; }

        public string? SplitCwd { get; init; }

        /// <summary>The title the main pane's program set, distinct from the derived name; absent when none or a shell's own.</summary>
        public string? Title { get; init; }

        /// <summary>What the session is for, set with session context.</summary>
        public string? Context { get; init; }

        /// <summary>The program session new --command runs in the main pane, and whether it holds open after exiting.</summary>
        public string? Command { get; init; }

        public bool? CommandWait { get; init; }

        /// <summary>The terminal-style question waiting in the session.</summary>
        public ControlAskNode? Ask { get; init; }

        /// <summary>The session's terminals: each pane and overlay, with its surface id and, for a pane, its pane id.</summary>
        public List<ControlSurfaceNode>? Surfaces { get; init; }

        /// <summary>The session's background, and the panes' own ones (left, right, scratch); a pane not listed inherits.</summary>
        public ControlBackground? Background { get; init; }

        public Dictionary<string, ControlBackground>? PaneBackgrounds { get; init; }

        /// <summary>The glyph color and shape given with the current status only.</summary>
        public string? StatusColor { get; init; }

        public string? StatusShape { get; init; }

        /// <summary>The command pinned with session restore for the main pane; an empty string pins a plain shell.</summary>
        public string? RestoreCommand { get; init; }

        /// <summary>The same for the split pane.</summary>
        public string? SplitRestoreCommand { get; init; }

        /// <summary>The pane keystrokes go to: left, right or scratch.</summary>
        /// <summary>agterm's splitFocused: with a split, shown or hidden, whether its right pane holds the keyboard; null without one.</summary>
        public bool? SplitFocused { get; init; }

        /// <summary>The live font size in points of the left pane, the right pane and the scratch, each while its terminal runs.</summary>
        public double? FontSize { get; init; }

        public double? SplitFontSize { get; init; }

        public double? ScratchFontSize { get; init; }

        /// <summary>agterm's foreground: the program the left pane runs now, as its argv; null when a shell holds it or it cannot be read.</summary>
        public List<string>? Foreground { get; init; }

        /// <summary>The left pane's shell when a shell holds it, by name.</summary>
        public string? ForegroundShell { get; init; }

        public List<string>? SplitForeground { get; init; }

        public string? SplitForegroundShell { get; init; }

        /// <summary>agterm's realized: the main pane's terminal exists, so type and text reach it.</summary>
        public bool? Realized { get; init; }

        /// <summary>agterm's backedByZmx: every existing pane is kept by a live daemon; null for a row of another machine.</summary>
        public bool? BackedByZmx { get; init; }

        /// <summary>agterm's splitCommandWait: the right pane holds after its program exits, as a row's split from another machine.</summary>
        public bool? SplitCommandWait { get; init; }

        public bool Scratch { get; init; }

        /// <summary>agterm's overlay: a caller's program or page covers the session; a HUD reports itself in Hud instead.</summary>
        public bool Overlay { get; init; }

        /// <summary>agterm's hud node: the panel in the session-wide slot, absent when none is up.</summary>
        public ControlHudNode? Hud { get; init; }

        /// <summary>agterm's: the floating size of what covers the session, program or page; absent for a full one or a HUD.</summary>
        public int? OverlaySizePercent { get; init; }

        /// <summary>agterm's htmlOverlays: the pages in the session's overlay slots, session-wide first, then left and right.</summary>
        public List<ControlHtmlOverlayNode>? HtmlOverlays { get; init; }

        /// <summary>The panes of a split that have their own overlay: left, right.</summary>
        public List<string>? PaneOverlays { get; init; }

        /// <summary>agterm's overlayWidth/overlayHeight: a floating program's or page's content size in points as laid out.</summary>
        public int? OverlayWidth { get; init; }

        public int? OverlayHeight { get; init; }

        /// <summary>agterm's hiddenOverlays: the slots holding a hidden program or page, as session, left and right.</summary>
        public List<string>? HiddenOverlays { get; init; }
    }

    public static class ControlEventKinds
    {
        public const string Status = "status";
        public const string Notify = "notify";
        public const string SessionCreated = "session.created";
        public const string SessionClosed = "session.closed";
        public const string SessionSelected = "session.selected";
        public const string PaneSplit = "pane.split";
        public const string PaneScratch = "pane.scratch";

        /// <summary>agterm's pane.focused: a session's selection settled on the other split pane.</summary>
        public const string PaneFocused = "pane.focused";

        /// <summary>The sidebar tree or an overlay changed; coalesced, so it says that something moved, not what.</summary>
        public const string TreeChanged = "tree.changed";

        /// <summary>Once per app run, when the control pipe is up and the sessions are restored.</summary>
        public const string AppLaunched = "app.launched";

        /// <summary>Beside session.created and session.closed of a row attached from another machine, with its ssh destination as host.</summary>
        public const string RemoteOpened = "remote.opened";
        public const string RemoteClosed = "remote.closed";

        public static readonly IReadOnlyList<string> All = new[] { Status, Notify, SessionCreated, SessionClosed, SessionSelected, PaneSplit, PaneScratch, PaneFocused, TreeChanged, AppLaunched, RemoteOpened, RemoteClosed };
    }

    public sealed record ControlEventPayload
    {
        public string? Name { get; init; }

        /// <summary>pane.focused: the selected pane's stable token, absent until its terminal exists.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("paneID")]
        public string? PaneId { get; init; }

        public string? Status { get; init; }

        /// <summary>The status before this write, so a reader sees the transition without keeping state.</summary>
        public string? Previous { get; init; }

        /// <summary>A status's own color and shape, when the call gave them, as agterm's status events carry.</summary>
        public string? Color { get; init; }

        public string? Shape { get; init; }

        /// <summary>The pane that wrote the status.</summary>
        public string? Pane { get; init; }

        public bool? Blink { get; init; }

        public string? Title { get; init; }

        public string? Body { get; init; }

        /// <summary>remote.opened, remote.closed: the row's ssh destination.</summary>
        public string? Host { get; init; }

        /// <summary>
        /// A notification's source: control (agshellctl notify), terminal (OSC 9/777) or mirrored (from another machine). Kept
        /// in the app, for the presentation stream to forward only agshellctl notify's; agterm's event carries none.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? Source { get; init; }
    }

    public sealed record ControlEvent
    {
        public long Seq { get; init; }

        /// <summary>Unix seconds.</summary>
        public double Ts { get; init; }

        public required string Kind { get; init; }

        public string? Workspace { get; init; }

        public string? Session { get; init; }

        /// <summary>The window the event happened in; absent for the app's own, such as app.launched.</summary>
        public string? Window { get; init; }

        public required ControlEventPayload Payload { get; init; }
    }

    /// <summary>A page of events. <see cref="Next"/> is the cursor for the following read.</summary>
    public sealed record ControlEventBatch
    {
        public required string Run { get; init; }

        public long Next { get; init; }

        public required List<ControlEvent> Items { get; init; }
    }

    /// <summary>agterm's session.overlay.result errors, shared so the --wait poll matches the server's wording.</summary>
    public static class OverlayResultErrors
    {
        public const string StillRunning = "overlay still running";
        public const string NoResult = "no overlay result";
    }
}
