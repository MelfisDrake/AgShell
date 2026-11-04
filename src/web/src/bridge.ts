export interface SessionView {
  flagged: boolean;
  id: string;
  name: string;
  customName: string | null;
  cwd: string | null;
  profileId: string;
  canReveal: boolean;
  status: AgentStatus;
  blink: boolean;
  statusColor: string | null;
  statusShape: StatusShape | null;
  context: string | null;
  title: string | null;
  ask: AskView | null;
  background: BackgroundView | null;
  remoteHost: string | null;
  /**
   * agterm's reconnect book by pane: a pane of the other machine whose ssh dropped (disconnected, with what ssh said on
   * its last failed try) or ended with a status of its own (held, waiting for a key); a connected pane is absent.
   */
  remoteWaits: Partial<Record<Pane, { state: "disconnected" | "held"; reason: string | null }>>;
  /** The presentation stream: connecting, connected, failed or unsupported (a machine too old to stream). */
  remoteStream: "connecting" | "connected" | "failed" | "unsupported" | null;
  remoteStreamError: string | null;
  /** The split came from the other machine; one opened here later is a local shell. */
  remoteSplit: boolean;
  /** The main pane is the other machine's; false once a split of this machine took its place. */
  remoteMain: boolean;
  /** agterm's ZmxLeadState.covered: live panes another machine leads, nobody leads or that are joining again, covered here until a key or a click takes the lead. */
  coveredPanes: Pane[];
  /** Covered panes whose lead was just asked for or that are joining again: agterm's "Taking over…". */
  takingPanes: Pane[];
  /** Covered panes nobody leads: agterm's "Reconnecting…". */
  unownedPanes: Pane[];
  /** The leader's columns and rows for each covered pane: its terminal takes that size, so its grid reads as the program's. */
  leaderSizes: Partial<Record<Pane, [number, number]>>;
  paneBackgrounds: { left: BackgroundView | null; right: BackgroundView | null; scratch: BackgroundView | null };
  unseen: number;
  statusChangedAt: number | null;
  fontSize: number;
  /** The right pane's and the scratch's own font sizes, as agterm's terminals each keep one. */
  splitFontSize: number;
  scratchFontSize: number;
  /** agterm's session font size: what a new pane or overlay starts with. */
  sessionFontSize: number;
  hasSplit: boolean;
  splitShown: boolean;
  splitFocused: boolean;
  splitAxis: "vertical" | "horizontal";
  splitRatio: number;
  focusedPane: Pane;
  scratchShown: boolean;
  scratchRunning: boolean;
  overlay: OverlayView | null;
  paneOverlays: { left: OverlayView | null; right: OverlayView | null };
}

export type Pane = "left" | "right" | "scratch" | "overlay" | "quick" | "overlay-left" | "overlay-right";

/** The quick terminal belongs to no session; its pane key uses an empty id. */
export const quickSessionId = "00000000-0000-0000-0000-000000000000";

export interface HudView {
  message: string;
  detail: string | null;
  spinner: string | null;
  position: string;
  textColor: string | null;
  fontSize: number | null;
  sticky: boolean;
  frame: boolean;
  markdown: boolean;
}

/** One styled piece of a HUD row: style is agterm's HudMarkdown.Style bits (bold 1, italic 2, strikethrough 4, dim 8, underline 16). */
export interface HudRunView {
  text: string;
  style: number;
  link: string | null;
}

/** agterm's HUD body: the panel's cell grid, the rows to paint, the markdown block's width (0 for a plain message), the panel's size. */
export interface HudPaintView {
  columns: number;
  rows: number;
  blockWidth: number;
  lines: HudRunView[][];
  widthPercent: number;
  heightPercent: number;
  heightPoints: number | null;
}

export interface DashboardView {
  /** coverId: the overlay standing in for the pane's terminal (agterm's dashboardCover), or null. */
  cells: { sessionId: string; pane: Pane; coverId: string | null }[];
  highlight: number;
  cols: number;
  rows: number;
  fontSize: number | null;
}

/** What a page shows now, as its browser reports it: agterm's load state and current page. */
export interface PageInfo {
  state: "loading" | "loaded" | "failed";
  error: string | null;
  page: string | null;
  title: string | null;
  canGoBack: boolean;
  canGoForward: boolean;
  /** The site a web page is kept to now: where it opened, or where navigate --url took it. */
  site?: string | null;
}

export interface PageView {
  source: string;
  isFile: boolean;
  /** A file page another machine handed over: its file is there, so there is nothing to open or reveal here. */
  remoteFile: boolean;
  js: boolean;
  navigation: boolean;
  chromeless: boolean;
  browse: boolean;
}

export interface OverlayView {
  id: string;
  kind: "program" | "hud" | "page";
  /** The pane a HUD is drawn over; null over the whole session. A HUD always sits in the session's own slot. */
  hudPane?: Pane | null;
  page: PageView | null;
  /** A program overlay's command; none for a page, a HUD, or a job another machine runs here through ssh. */
  command: string | null;
  sizePercent: number | null;
  backgroundColor: string | null;
  exited: boolean;
  hud: HudView | null;
  /** A HUD's panel as the app laid it out from the metrics the page reported (agterm's HudLayout). */
  paint: HudPaintView | null;
  /** agterm's hidden overlay: out of sight, running, still holding its slot. */
  hidden: boolean;
  /** agterm's point-sized axes of a floating panel, in place of the percent's; capped at 80% of the session. */
  widthPoints: number | null;
  heightPoints: number | null;
}

export type AgentStatus = "idle" | "active" | "completed" | "blocked";

export type StatusShape = "circle" | "square" | "triangle" | "diamond" | "capsule" | "star";

export interface BackgroundView {
  kind: "image" | "text" | "color";
  url: string | null;
  text: string | null;
  color: string | null;
  opacity: number;
  fit: "contain" | "cover" | "stretch" | "none";
  position: string;
  repeat: boolean;
}

export interface PickItemView {
  id: string;
  label: string;
  subtitle: string | null;
  status: AgentStatus | null;
  statusColor: string | null;
  statusShape: StatusShape | null;
}

export interface PickView {
  id: string;
  items: PickItemView[];
  prompt: string | null;
  query: string | null;
  selection: string | null;
  allowCustom: boolean;
}

export interface AskButtonView {
  id: string;
  label: string;
  hotkey: string | null;
}

export interface AskView {
  id: string;
  title: string;
  message: string | null;
  buttons: AskButtonView[];
  defaultButton: string | null;
  destructiveButton: string | null;
  cancelButton: string | null;
  style: "terminal" | "gui";
  align: "left" | "center" | "right";
  width: number | null;
  input: "line" | "multiline" | null;
  inputValue: string | null;
  placeholder: string | null;
  choices: { id: string; label: string; subtitle: string | null }[] | null;
  multiple: boolean;
  checked: string[] | null;
  other: string | null;
  pane: "left" | "right" | null;
  /** The session a GUI question is anchored to; null for one over the whole window. */
  sessionId: string | null;
}

export interface WorkspaceView {
  id: string;
  name: string;
  collapsed: boolean;
  focused: boolean;
  sessions: SessionView[];
}

export interface ProfileView {
  id: string;
  name: string;
}

export interface KeyChordView {
  meta?: boolean;
  ctrl: boolean;
  alt: boolean;
  shift: boolean;
  code: string;
}

export interface KeyBindingView {
  sequence: KeyChordView[];
  repeat: boolean;
  label: string;
}

export interface KeymapView {
  actions: Record<string, KeyBindingView[]>;
  menu: Record<string, KeyBindingView | null>;
  /** A command's shortcut is its bindings as written in keymap.conf, which is how agterm shows a custom command's keys. */
  commands: { name: string; shortcut: string; bindings: KeyBindingView[] }[];
  /** agterm's most-used group: the most-run commands, once the keymap holds more than it shows. */
  mostUsed: string[];
}

export interface ShortcutSnapshot {
  pending: KeyChordView[];
  repeatPrefix: KeyChordView[] | null;
  heldRepeatKey: string | null;
  consumed: string[];
  menuOwned: string[];
  deadline: number | null;
}

/** One row of agterm's attention list across every open window. */
export interface AttentionEntryView {
  windowId: string;
  sessionId: string;
  name: string;
  remoteHost: string | null;
  status: AgentStatus;
  statusColor: string | null;
  statusShape: StatusShape | null;
  subtitle: string;
  /** Its window is not under a terminal zoom, dashboard, pick or ask. */
  enabled: boolean;
}

export interface ThemeView {
  name: string;
  dark: boolean;
  /** agterm's terminalThemeIsDark: the sidebar with its tint, which picks the disclosure triangle's gray. */
  sidebarDark: boolean;
  background: string;
  foreground: string;
  cursor: string | null;
  cursorText: string | null;
  selectionBackground: string | null;
  selectionForeground: string | null;
  palette: (string | null)[];
}

export interface SettingsView {
  fontFamily: string | null;
  terminalFontFamilies: string[];
  terminalFontSize: number;
  terminalBackgroundOpacity: number;
  fontSize: number;
  /** null is no theme, the terminal's own colors (agterm's "default ghostty"). */
  theme: string | null;
  darkTheme: string | null;
  followSystemAppearance: boolean;
  systemDark: boolean;
  /** The Windows accent color, agterm's accentColor. */
  systemAccent: string;
  cursorStyle: "block" | "bar" | "underline" | "block_hollow";
  cursorBlink: boolean;
  /** The stored choices, null for agterm's Default. */
  cursorStyleSetting: "block" | "bar" | "underline" | null;
  cursorBlinkSetting: boolean | null;
  notificationsEnabled: boolean;
  notificationBadgeEnabled: boolean;
  taskbarFlash: "off" | "once" | "untilFocused";
  activeStatusColorHex: string | null;
  blockedStatusColorHex: string | null;
  completedStatusColorHex: string | null;
  statusReset: "firstKey" | "enter" | "never";
  rightClickPaste: boolean;
  clipboardWrite: boolean;
  /** ghostty.conf's keys for the terminals. */
  copyOnSelect: "true" | "false" | "clipboard";
  mouseHideWhileTyping: boolean;
  cursorClickToMove: boolean;
  terminalPadding: { left: number; right: number; top: number; bottom: number };
  scrollback: number;
  newSessionDirectory: "home" | "currentSession" | "custom";
  newSessionCustomDirectory: string | null;
  newSessionPlacement: "end" | "afterCurrent";
  confirmCloseSession: boolean;
  closeGraceUndoEnabled: boolean;
  autoFollowAttention: "off" | "s5" | "s10" | "s30" | "s60" | "m5";
  autoFollowStayOnActive: boolean;
  notificationSoundName: "default" | "im" | "mail" | "reminder" | "sms" | "none";
  blockedStatusSoundName: string | null;
  hiddenInterfaceElements: string[];
  hiddenByDefaultElements: string[];
  shownInterfaceElements: string[];
  toolbarMode: "compact" | "normal" | "hidden";
  attentionButtonEnabled: boolean;
  /** remotes.conf lists a machine: attach_remote is offered. */
  remotesAvailable: boolean;
  activeStatusShape: StatusShape;
  blockedStatusShape: StatusShape;
  completedStatusShape: StatusShape;
  quickTerminalSizePercent: number;
  /** The stored size, null for the default. */
  quickTerminalSizeSetting: number | null;
  backgroundOpacity: number;
  backgroundBlur: boolean;
  backdropSupported: boolean;
  /** Transparency effects are off in the Windows settings. */
  transparencyOff: boolean;
  /** agterm: a window in full screen is opaque, whatever the opacity setting. */
  fullscreen: boolean;
  restoreMode: "none" | "rerun" | "live";
  /** The language setting; uiLanguage is the one it resolves to now. */
  language: "system" | "ru" | "en";
  uiLanguage: "ru" | "en";
  flaggedViewLayout: "flat" | "plain" | "tree";
  workspaceRowClickExpands: boolean;
  /** The system's double-click interval in ms. */
  doubleClickTime: number;
  mouseScrollMultiplier: number;
  /** agterm's terminalInputSource: the keyboard layout held in terminals, null for off. */
  terminalInputSource: string | null;
  autoHideSidebarInactiveWindows: boolean;
  htmlOverlayZoom: number;
  configDirectory: string | null;
  /** The folder keymap.conf and hooks.conf are read from, the default one included. */
  configFolder: string;
  /** Live restore was chosen but could not start at launch. */
  restoreUnavailableReason: string | null;
  /** agterm's Reset Live Sessions… menu visibility: live both as configured and as this launch runs. */
  liveResetAvailable: boolean;
  denylistPath: string;
  linkOpenMode: "browser" | "overlay";
  inactivePaneMuteStrength: number;
  sidebarBackgroundShift: number;
  sidebarFontSize: number;
  interfaceFontSize: number;
  themesPath: string;
  keymapDiagnostics: string[];
  hookDiagnostics: string[];
}

export interface AppState {
  workspaces: WorkspaceView[];
  selectedSessionId: string | null;
  /** The name given to the window; null for one known by its place. */
  windowName: string | null;
  zoomed: boolean;
  /** agterm's zoom target: the session and surface filling the window, a background session's too. */
  zoomedSessionId: string | null;
  zoomedPane: Pane | null;
  dashboard: DashboardView | null;
  /** zoomed and fontSize come only to the quick panel's own page. */
  quick: { shown: boolean; pinned: boolean; running: boolean; zoomed?: boolean; fontSize?: number | null };
  recentSessionIds: string[];
  attention: AttentionEntryView[];
  windows: { id: string; name: string; open: boolean }[];
  currentWindowId: string;
  pick: PickView | null;
  ask: AskView | null;
  clipboardPrompt: ClipboardPromptView | null;
  currentWorkspaceId: string | null;
  canRemoveWorkspace: boolean;
  windowsBuild: number;
  /** The settings window's state: settings and theme only, and a plain window the backdrop never shows through. */
  settingsPage?: boolean;
  quickPage?: boolean;
  profiles: ProfileView[];
  defaultProfileId: string;
  sidebar: { visible: boolean; width: number };
  keymap: KeymapView;
  settings: SettingsView;
  theme: ThemeView;
  pendingClose: { id: string; title: string } | null;
  sidebarMode: "tree" | "flagged";
  focusEnabled: boolean;
  visibleWorkspaceIds: string[];
  navigableSessionIds: string[];
  pendingSessionIds: string[];
  recentClosed: RecentClosedView[];
}

export interface RecentClosedView {
  id: string;
  kind: "session" | "workspace";
  title: string;
  subtitle: string | null;
  closedAt: number;
}

/** A row of the window's menu bar: an action, a divider, a section title or a submenu. */
export type MenuNode =
  | { kind: "item"; id: string; title: string; keys?: string; chord?: string; enabled: boolean; checked?: boolean }
  | { kind: "separator" }
  | { kind: "header"; title: string }
  | { kind: "submenu"; id: string; title: string; enabled: boolean; items: MenuNode[] };

export interface MenuView {
  title: string;
  items: MenuNode[];
}

export interface MenuColors {
  bar: string;
  text: string;
  highlight: string;
  dark: boolean;
}

export type HostMessage =
  | { type: "toolbarKey"; token: number; kind: "keydown" | "keyup"; code: string; key: string; ctrlKey: boolean; shiftKey: boolean; altKey: boolean; metaKey: boolean; repeat: boolean }
  | { type: "toolbarPopover"; value: boolean }
  | { type: "toolbarAction"; id: string }
  | { type: "state"; state: AppState }
  | { type: "output"; sessionId: string; pane: Pane; data: string }
  | { type: "restart"; sessionId: string; pane: Pane }
  | { type: "swap"; sessionId: string }
  | { type: "promote"; sessionId: string }
  | ({ type: "pageInfo"; overlayId: string } & PageInfo)
  | { type: "pageGone"; overlayId: string }
  | { type: "label"; sessionId: string; name: string }
  | { type: "welcome" }
  | { type: "windowActive"; value: boolean; appActive?: boolean }
  | { type: "keymapChanged" }
  | { type: "menuTracking" }
  | { type: "shortcutState"; origin: string; sequence: number; revision: number; snapshot: ShortcutSnapshot; released?: string[] | null }
  | { type: "dropPlan"; token: number; noop: string[] }
  | { type: "forwardKey"; code: string; key: string; ctrlKey: boolean; shiftKey: boolean; altKey: boolean }
  | { type: "ctrlReleased" }
  | { type: "switcherEscape" }
  | { type: "readText"; requestId: number; sessionId: string; pane: Pane; all: boolean; lines: number | null }
  | { type: "readSelection"; requestId: number; sessionId: string; pane: Pane }
  | { type: "terminal"; requestId: number; sessionId: string; pane: Pane; action: string; text: string | null }
  | { type: "lists"; themes: string[]; fonts: string[]; sounds: string[]; inputSources: InputSource[] }
  | { type: "paste"; sessionId: string; pane: Pane; text: string; clipboard?: boolean; selection?: boolean }
  | { type: "search"; requestId: number; sessionId: string; pane: Pane; needle: string | null; step: "next" | "prev" | "close" | null }
  | { type: "quickAction"; name: string }
  | { type: "menuAction"; id: string; text?: string }
  | { type: "menuRefresh" };

/** The key press behind terminal input, for agterm's keystroke classes (interrupt, submit, typing). */
export interface KeyStroke {
  key: string;
  code: string;
  ctrl: boolean;
  alt: boolean;
  shift: boolean;
  meta: boolean;
}

/** agterm's clipboard prompt: a program in a terminal asks to read or to set the clipboard. */
export interface ClipboardPromptView {
  id: string;
  access: "read" | "write";
}

export type Command =
  | { command: "newSession"; workspaceId?: string; profileId?: string }
  | { command: "newWorkspace" }
  | { command: "duplicateSession"; sessionId?: string }
  | { command: "revealSession"; sessionId: string }
  | { command: "selectSession"; sessionId: string; reveal?: boolean }
  | { command: "clearStatus"; sessionId: string }
  | { command: "changeFont"; sessionId: string; delta: number | null; pane: Pane }
  | { command: "setSplit"; sessionId: string; axis: "vertical" | "horizontal" }
  | { command: "closeSplit"; sessionId: string }
  | { command: "swapPanes"; sessionId: string }
  | { command: "focusPane"; sessionId: string; pane: Pane }
  | { command: "takeLead"; sessionId: string; pane: Pane }
  | { command: "retryRemote"; sessionId: string; pane?: Pane }
  | { command: "setSplitRatio"; sessionId: string; ratio: number }
  | { command: "setScratch"; sessionId: string; shown: boolean | null }
  | { command: "setZoom"; zoomed: boolean | null }
  | { command: "searchShown"; key: string; shown: boolean }
  | { command: "hudMetrics"; overlayId: string; cellWidth: number; cellHeight: number; paneWidth: number; paneHeight: number; paddingWidth: number; paddingHeight: number }
  | { command: "overlayGeometry"; overlayId: string; width: number; height: number }
  | { command: "closeOverlay"; sessionId: string; slot?: Pane }
  | { command: "openDashboard" }
  | { command: "dashboardMove"; direction: "up" | "down" | "left" | "right" }
  | { command: "dashboardActivate"; index: number }
  | { command: "dashboardClick"; index: number }
  | { command: "closeDashboard" }
  | { command: "setQuick"; shown: boolean | null }
  | { command: "quickAction"; name: string }
  | { command: "quickClose" }
  | { command: "markAttention"; sessionId: string }
  | { command: "selectAttention"; forward: boolean }
  | { command: "selectAttentionEntry"; windowId: string; sessionId: string }
  | { command: "openSettings" }
  | { command: "quit" }
  | { command: "minimizeWindow" }
  | { command: "resetLiveSessions" }
  | { command: "settingsMenuAction"; name: string }
  | { command: "closeSettings" }
  | { command: "selectRelative"; offset: number }
  | { command: "selectRelativeWorkspace"; offset: number }
  | { command: "renameSession"; sessionId: string; name: string }
  | { command: "renameWorkspace"; workspaceId: string; name: string }
  | { command: "moveSession"; sessionId: string; workspaceId: string; index: number }
  | { command: "moveSessions"; sessionIds: string[]; workspaceId: string }
  | { command: "dropSessions"; sessionIds: string[]; workspaceId: string; sessionId: string | null; after: boolean; springOpen?: string }
  | { command: "dropWorkspace"; workspaceId: string; slot: number }
  | { command: "dropPlan"; token: number; sessionIds?: string[]; workspaceId?: string }
  | { command: "closeSession"; sessionId?: string }
  | { command: "restartSession"; sessionId: string }
  | { command: "closeWorkspace"; workspaceId: string }
  | { command: "setCollapsed"; workspaceId: string; collapsed: boolean }
  | { command: "setSidebar"; visible?: boolean; width?: number }
  | { command: "runCustom"; name: string; source: "keybind" | "palette"; sessionId?: string; pane?: Pane; selection?: string }
  | { command: "editConfig"; file: "keymap" | "hooks" | "denylist" | "ghostty" }
  | { command: "reloadConfig"; file?: "keymap" | "hooks" | "ghostty" }
  | { command: "previewSound"; kind: "notification" | "status"; name: string }
  | { command: "closeSessions"; sessionIds: string[] }
  | { command: "setFlag"; sessionIds: string[]; flagged?: boolean }
  | { command: "setSidebarMode"; mode?: "tree" | "flagged" }
  | { command: "toggleFocusWorkspace"; workspaceId: string }
  | { command: "setFocusMember"; workspaceId: string; member?: boolean }
  | { command: "setFocusEnabled"; enabled?: boolean }
  | { command: "clearFocus" }
  | { command: "pickAnswer"; id: string; itemId?: string; query?: string }
  | { command: "overlayVisibility"; sessionId: string; slot?: Pane; mode: "hide" | "show" | "toggle" }
  | { command: "askAnswer"; id: string; buttonId?: string; text?: string; selected?: string[] }
  | { command: "clipboardPrompt"; id: string; allow: boolean; remember: boolean }
  | { command: "clearFlags" }
  | { command: "requestRemotes" }
  | { command: "chooseConfigDirectory" }
  | { command: "newWindow" }
  | { command: "selectWindow"; windowId: string }
  | { command: "relativeWindow"; offset: number }
  | { command: "renameWindow"; name: string }
  | { command: "deleteWindow" }
  | { command: "closeWindow" }
  | { command: "expandWorkspaces" }
  | { command: "collapseWorkspaces" }
  | { command: "toggleFullscreen" }
  | { command: "openDirectory"; workspaceId?: string }
  | { command: "clearRecent" }
  | { command: "showAbout" }
  | { command: "openDocumentation" }
  | { command: "installCommandLineTool" }
  | { command: "registerEventLog" }
  | { command: "log"; level: "debug" | "info"; category: string; message: string }
  | { command: "installAgent"; what: "hooks" | "skill" }
  | { command: "undoClose"; closeId?: string }
  | { command: "reopenRecent"; itemId?: string }
  | { command: "updateSettings"; patch: Partial<Record<keyof SettingsView, unknown>> }
  | { command: "previewSettings"; patch: Partial<Record<keyof SettingsView, unknown>> }
  | { command: "commitSettings" }
  | { command: "previewTheme"; name: string | null; commit?: boolean }
  | { command: "requestLists" }
  | { command: "chooseDirectory" }
  | { command: "paste"; sessionId: string; pane: Pane }
  | { command: "pasteSelection"; sessionId: string; pane: Pane }
  | { command: "openLink"; url: string; sessionId: string; pane: Pane };

export type PageMessage =
  | { type: "toolbarKeyHandled"; token: number }
  | { type: "toolbar"; held: boolean; items: import("./toolbar").ToolbarItem[]; colors: MenuColors; fontSize: number }
  | { type: "shortcutState"; origin: string; sequence: number; reason: "key" | "reset" | "timer"; snapshot: ShortcutSnapshot }
  | { type: "menu"; menus: MenuView[]; colors: MenuColors }
  | { type: "menuClipboard"; text: string }
  | { type: "ready" }
  | { type: "attach"; sessionId: string; pane: Pane; cols: number; rows: number }
  | { type: "input"; sessionId: string; pane: Pane; data: string; stroke?: KeyStroke | "text" }
  | { type: "resize"; sessionId: string; pane: Pane; cols: number; rows: number }
  | { type: "cwd"; sessionId: string; pane: Pane; path: string }
  | { type: "title"; sessionId: string; pane: Pane; title: string }
  | { type: "reply"; requestId: number; text?: string; missing?: boolean }
  | { type: "pageOpenExternal"; target: string }
  | { type: "nativePage"; overlayId: string; x: number; y: number; width: number; height: number; visible: boolean; raised: boolean; focus: boolean; radius: number; roundTop: boolean }
  | {
      type: "hudFloat";
      overlayId: string;
      width: number;
      x?: number;
      y?: number;
      height?: number;
      background?: string;
      color?: string;
      fontFamily?: string;
      fontSize?: number;
      lineHeight?: number;
      framed?: boolean;
      paddingWidth?: number;
      paddingHeight?: number;
      glyphRow?: number;
      frames?: string[];
      interval?: number;
      rows?: { x: number; y: number; runs: HudRunView[] }[];
    }
  | { type: "pageNav"; overlayId: string; action: "back" | "forward" | "reload" }
  | { type: "pageZoom"; factor: number }
  | { type: "pageOpenCurrent"; overlayId: string }
  | { type: "pageCopyLink"; overlayId: string }
  | { type: "pageReveal"; overlayId: string }
  | { type: "notify"; sessionId: string; pane: Pane; focused: boolean; title: string | null; body: string }
  | { type: "osc52"; sessionId: string; pane: Pane; data: string }
  | { type: "osc5522"; sessionId: string; pane: Pane; data: string; bel: boolean }
  | { type: "kittyPaste"; sessionId: string; pane: Pane; selection?: boolean }
  | { type: "selectionCopied"; sessionId: string; pane: Pane; text: string }
  | { type: "dropText"; sessionId: string; pane: Pane; text: string; uris: string[] }
  | { type: "autoFollowSuppressed"; value: boolean }
  | { type: "terminalFocus"; value: boolean }
  | { type: "switcherHold"; value: boolean }
  | { type: "paneFocused"; sessionId: string; pane: Pane }
  | ({ type: "command" } & Command);

interface WebViewBridge {
  postMessage(message: unknown): void;
  postMessageWithAdditionalObjects(message: unknown, objects: FileList): void;
  addEventListener(type: "message", listener: (event: MessageEvent) => void): void;
}

declare global {
  interface Window {
    chrome?: { webview?: WebViewBridge };
  }
}

/** A keyboard layout Windows can select: its handle in hex and the name its list shows. */
export interface InputSource {
  id: string;
  name: string;
}

/**
 * Tells the host whether a terminal has the page's keyboard focus, so agterm's terminal input source can hold its layout
 * there; the host also checks that the window is the foreground one.
 */
export function reportTerminalFocus(): void {
  let last = false;
  const report = () => {
    const focused = document.hasFocus() && document.activeElement?.classList.contains("xterm-helper-textarea") === true;
    if (focused !== last) {
      last = focused;
      send({ type: "terminalFocus", value: focused });
    }
  };
  // focusout runs before the next element takes focus, so the check waits for it.
  document.addEventListener("focusin", report, true);
  document.addEventListener("focusout", () => setTimeout(report), true);
  window.addEventListener("focus", report);
  window.addEventListener("blur", report);
}

export function send(message: PageMessage): void {
  window.chrome?.webview?.postMessage(message);
}

/** Hands dropped files to the host, which reads their paths; the page itself never sees them. */
export function sendFiles(message: { type: "dropFolders"; workspaceId?: string; springOpen?: string } | { type: "dropPaths"; sessionId: string; pane: Pane }, files: FileList): void {
  window.chrome?.webview?.postMessageWithAdditionalObjects(message, files);
}

export function command(cmd: Command): void {
  send({ type: "command", ...cmd });
}

export function onHostMessage(handler: (message: HostMessage) => void): void {
  window.chrome?.webview?.addEventListener("message", (event) => handler(event.data as HostMessage));
}
