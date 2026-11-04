import { AppState, command, onHostMessage, quickSessionId, SessionView } from "./bridge";
import { changePageZoom } from "./pages";
import { currentState, focusedKey, setEditing, toggleSearch, setPalette, togglePalette } from "./store";
import { t, tr } from "./i18n";
import { hasHideableOverlay, shownOverlay } from "./overlays";

/**
 * An entry of the action palette. Its id is agterm's action name, which keymap.conf binds keys to. Like agterm, an
 * action that cannot run right now stays listed and greyed out, and one that means nothing now is not listed.
 */
export interface Action {
  id: string;
  /** agterm's title, or one that follows the state (Flag / Unflag Session). */
  title: string | ((state: AppState) => string);
  enabled: (state: AppState) => boolean;
  run: (state: AppState) => void;
  /** agterm's isVisible: listed in the palette at all; an action not listed does not run either. */
  visible?: (state: AppState) => boolean;
  /**
   * agterm's modal cover: what a terminal zoom, the dashboard, a pick or an ask blocks. All by default; none for the
   * font sizes, the reloads, the terminal zoom, Close Session and the window rows; the dashboard is blocked only by a
   * zoom or a pick, which leaves it the way out of its own grid; Swap Panes only by a pick.
   */
  cover?: "none" | "zoomOrPick" | "pick";
  /** Not a row of agterm's action palette: it runs from its keys or the menu bar only. */
  unlisted?: true;
}

/** agterm's isEnabled: listed, not under a modal cover, and able to run. */
export function actionAvailable(action: Action, state: AppState): boolean {
  if (action.visible && !action.visible(state)) {
    return false;
  }
  const picker = state.pick !== null || state.ask !== null;
  const covered =
    action.cover === "none" ? false : action.cover === "pick" ? picker : action.cover === "zoomOrPick" ? state.zoomed || picker : state.zoomed || state.dashboard !== null || picker;
  return !covered && action.enabled(state);
}

export function actionTitle(action: Action, state: AppState): string {
  return t(typeof action.title === "string" ? action.title : action.title(state));
}

function selected(state: AppState): SessionView | null {
  return state.workspaces.flatMap((workspace) => workspace.sessions).find((session) => session.id === state.selectedSessionId) ?? null;
}

function sessions(state: AppState): SessionView[] {
  return state.workspaces.flatMap((workspace) => workspace.sessions);
}

/** The sessions navigation reaches: the flagged ones in the flagged view, else those the focus filter leaves shown. */
export function navigable(state: AppState): SessionView[] {
  const all = sessions(state);
  return state.navigableSessionIds.map((id) => all.find((session) => session.id === id)).filter((session): session is SessionView => session !== undefined);
}

let toggleSidebar: () => void = () => {};

export function setSidebarToggle(handler: () => void): void {
  toggleSidebar = handler;
}

/**
 * agterm's SessionAskInput.wantsFocus: a session's question takes the keys while it is on screen (not under a zoom, the
 * dashboard or, for a pane's question, the scratch or a hidden split) and, for a pane's question, while its pane has the
 * focus. The window's own pick or question comes first.
 */
export function sessionAskHoldsKeys(state: AppState, session: SessionView): boolean {
  const ask = session.ask;
  if (!ask || state.zoomed || state.dashboard || state.pick || state.ask) {
    return false;
  }
  if (ask.pane === null) {
    return true;
  }
  const shown = !session.scratchShown && (ask.pane === "left" || session.splitShown);
  return shown && session.focusedPane === ask.pane;
}

/**
 * Closing peels layers like agterm's ⌘W: the window's ask or pick, the session's ask, the quick terminal, zoom, the
 * dashboard, the overlay, the scratch, the focused pane's overlay, and only then the session. From a key, as agterm's
 * closeActiveSessionOrWindow, a window with none of these and no session left closes itself; the palette row does not.
 * From the quick panel the active window peels its asks and pick, then hands the panel's own rung back.
 */
export function closeTopmost(orWindow = false, fromQuick = false): void {
  const state = currentState();
  const session = state ? selected(state) : null;
  if (state?.ask) {
    command({ command: "askAnswer", id: state.ask.id });
  } else if (state?.pick) {
    command({ command: "pickAnswer", id: state.pick.id });
  } else if (session?.ask && sessionAskHoldsKeys(state!, session)) {
    command({ command: "askAnswer", id: session.ask.id });
  } else if (fromQuick) {
    command({ command: "quickClose" });
  } else if (state?.zoomed) {
    command({ command: "setZoom", zoomed: false });
  } else if (state?.dashboard) {
    command({ command: "closeDashboard" });
  } else if (shownOverlay(session?.overlay)) {
    command({ command: "closeOverlay", sessionId: session!.id });
  } else if (session?.scratchShown) {
    command({ command: "setScratch", sessionId: session.id, shown: false });
  } else if (session && (session.focusedPane === "left" || session.focusedPane === "right") && shownOverlay(session.paneOverlays[session.focusedPane])) {
    command({ command: "closeOverlay", sessionId: session.id, slot: session.focusedPane === "left" ? "overlay-left" : "overlay-right" });
  } else if (session) {
    closeSession();
  } else if (orWindow) {
    command({ command: "closeWindow" });
  }
}

function hasHiddenOverlay(session: SessionView): boolean {
  return [session.overlay, session.paneOverlays.left, session.paneOverlays.right].some((overlay) => overlay?.hidden === true);
}

/** agterm's closeConfirmationText: when the sessions really close and how they come back, then their hidden overlays. */
function closeConfirmationText(state: AppState, closing: SessionView[]): string {
  const plural = closing.length > 1;
  const base = state.settings.closeGraceUndoEnabled
    ? plural
      ? t("Сессии закроются после короткого окна для отмены.")
      : t("Сессия закроется после короткого окна для отмены.")
    : plural
      ? t("Сессии закроются сразу, их можно снова открыть из меню «Файл ▸ Недавно закрытые».")
      : t("Сессия закроется сразу, её можно снова открыть из меню «Файл ▸ Недавно закрытые».");
  if (!closing.some(hasHiddenOverlay)) {
    return base;
  }
  return base + " " + (plural ? t("Их скрытые overlay закроются вместе с ними.") : t("Её скрытые overlay закроются вместе с ней."));
}

/** With the setting on, asks once before closing several sessions together. */
export function confirmCloseSessions(ids: string[]): boolean {
  const state = currentState();
  if (!state?.settings.confirmCloseSession) {
    return true;
  }
  const closing = sessions(state).filter((session) => ids.includes(session.id));
  return window.confirm(t("Закрыть сессии ({n})?", { n: ids.length }) + "\n" + closeConfirmationText(state, closing));
}

/** Closes a session from the window; with the setting on, as agterm, only after the user confirms. The API never asks. */
export function closeSession(sessionId?: string): void {
  const state = currentState();
  const id = sessionId ?? state?.selectedSessionId;
  if (!state || !id) {
    return;
  }
  const session = sessions(state).find((candidate) => candidate.id === id);
  if (state.settings.confirmCloseSession && !window.confirm(t("Закрыть сессию «{name}»?", { name: session?.name ?? "" }) + "\n" + closeConfirmationText(state, session ? [session] : []))) {
    return;
  }
  command({ command: "closeSession", sessionId: id });
}

const always = () => true;
const hasSelection = (state: AppState) => selected(state) !== null;
/** As agterm, folding all workspaces needs workspace rows: the tree, or the flagged view laid out as one. */
const hasWorkspaceRows = (state: AppState) => state.sidebarMode === "tree" || state.settings.flaggedViewLayout === "tree";
const hasSplit = (state: AppState) => selected(state)?.hasSplit ?? false;
const hasMarked = (state: AppState) => state.workspaces.some((workspace) => workspace.focused);
const currentWorkspace = (state: AppState) => state.workspaces.find((workspace) => workspace.id === state.currentWorkspaceId) ?? null;
/** agterm's canStepWorkspaces: the ordinary tree, among the visible workspaces, somewhere else to go. */
const canStepWorkspaces = (state: AppState) => {
  if (state.sidebarMode !== "tree" || state.visibleWorkspaceIds.length === 0) {
    return false;
  }
  return state.currentWorkspaceId === null || !state.visibleWorkspaceIds.includes(state.currentWorkspaceId) || state.visibleWorkspaceIds.length > 1;
};
const topBottom = (state: AppState) => selected(state)?.splitAxis === "horizontal";

/**
 * agterm's htmlPageOwnsKeys for keys that reach the interface: the session's topmost page covers it while the keyboard
 * sits outside every terminal, as on the sidebar. An open dashboard hides every page. A page that has the keyboard
 * itself sends its keys through the host and zooms there.
 */
function pageOwnsKeys(state: AppState, session: SessionView): boolean {
  if (state.dashboard || document.activeElement?.closest(".xterm")) {
    return false;
  }
  const cover = shownOverlay(session.overlay);
  if (cover && cover.kind !== "hud") {
    return cover.kind === "page";
  }
  if (session.scratchShown || (session.focusedPane !== "left" && session.focusedPane !== "right")) {
    return false;
  }
  return shownOverlay(session.paneOverlays[session.focusedPane])?.kind === "page";
}

/** A font size action: the terminal's font, or as agterm's the pages' zoom while a page owns the keys. */
function fontAction(action: string, delta: number | null): (state: AppState) => void {
  return (state) => {
    // agterm: while zoomed the focused terminal is the zoomed one, a program over a pane counting as that pane.
    const zoomed = state.zoomedPane ? sessions(state).find((candidate) => candidate.id === state.zoomedSessionId) : undefined;
    if (zoomed && state.zoomedPane) {
      const pane = state.zoomedPane === "overlay-left" ? "left" : state.zoomedPane === "overlay-right" ? "right" : state.zoomedPane === "overlay" ? zoomed.focusedPane : state.zoomedPane;
      command({ command: "changeFont", sessionId: zoomed.id, delta, pane });
      return;
    }
    const session = selected(state);
    if (!session) {
      return;
    }
    if (pageOwnsKeys(state, session)) {
      changePageZoom(action);
      return;
    }
    // agterm: the font keys change the terminal in focus; a program over a pane is that pane's.
    command({ command: "changeFont", sessionId: session.id, delta, pane: session.focusedPane });
  };
}

/** agterm's fullOverlayVisible: a shown program or page over the whole session hides its panes. */
function fullOverlayShown(session: SessionView): boolean {
  const overlay = shownOverlay(session.overlay);
  return overlay !== null && overlay.kind !== "hud" && overlay.sizePercent === null;
}

/**
 * agterm's cover rungs of the split actions from the window: under a full overlay nothing happens, a shown scratch is
 * only dismissed, so the panes are not rearranged or a shell closed out of sight. The API acts behind either cover.
 */
function splitStep(session: SessionView, run: () => void): void {
  if (fullOverlayShown(session)) {
    return;
  }
  if (session.scratchShown) {
    command({ command: "setScratch", sessionId: session.id, shown: false });
    return;
  }
  run();
}

/** agterm's toggleSplit from a key, the palette or the title bar button; null keeps the current axis, vertical at first. */
export function toggleSplit(state: AppState, axis: "vertical" | "horizontal" | null): void {
  const session = selected(state);
  const picker = state.pick !== null || state.ask !== null;
  if (!session || state.zoomed || state.dashboard !== null || picker) {
    return;
  }
  splitStep(session, () => command({ command: "setSplit", sessionId: session.id, axis: axis ?? (session.hasSplit ? session.splitAxis : "vertical") }));
}

function onSelected(run: (session: SessionView) => void): (state: AppState) => void {
  return (state) => {
    const session = selected(state);
    if (session) {
      run(session);
    }
  };
}

export const actions: Action[] = [
  { id: "new_session", title: tr("Новая сессия"), enabled: always, run: () => command({ command: "newSession" }) },
  { id: "new_workspace", title: tr("Новый workspace"), enabled: always, run: () => command({ command: "newWorkspace" }) },
  { id: "new_window", title: tr("Новое окно"), enabled: always, run: () => command({ command: "newWindow" }), cover: "none", unlisted: true },
  { id: "attach_remote", title: tr("Подключиться к сессии на другой машине…"), enabled: (state) => state.settings.remotesAvailable, run: () => command({ command: "requestRemotes" }) },
  {
    id: "previous_window",
    title: tr("Предыдущее окно"),
    enabled: (state) => state.windows.filter((item) => item.open).length > 1,
    run: () => command({ command: "relativeWindow", offset: -1 }),
  },
  {
    id: "next_window",
    title: tr("Следующее окно"),
    enabled: (state) => state.windows.filter((item) => item.open).length > 1,
    run: () => command({ command: "relativeWindow", offset: 1 }),
  },
  {
    id: "rename_window",
    unlisted: true,
    title: tr("Переименовать окно"),
    enabled: always,
    run: (state) => {
      const current = state.windows.find((item) => item.id === state.currentWindowId);
      const name = window.prompt(t("Имя окна"), current?.name ?? "");
      if (name && name.trim()) {
        command({ command: "renameWindow", name: name.trim() });
      }
    },
    cover: "none",
  },
  {
    id: "delete_window",
    unlisted: true,
    title: tr("Удалить окно"),
    enabled: always,
    visible: (state) => state.windows.length > 1,
    run: () => command({ command: "deleteWindow" }),
    cover: "none",
  },
  {
    id: "duplicate_session",
    title: tr("Дублировать сессию"),
    enabled: hasSelection,
    run: onSelected((session) => command({ command: "duplicateSession", sessionId: session.id })),
  },
  { id: "rename_session", title: tr("Переименовать сессию"), enabled: hasSelection, run: onSelected((session) => setEditing({ kind: "session", id: session.id })) },
  {
    id: "rename_workspace",
    title: tr("Переименовать workspace"),
    enabled: (state) => state.currentWorkspaceId !== null,
    run: (state) => state.currentWorkspaceId && setEditing({ kind: "workspace", id: state.currentWorkspaceId }),
  },
  { id: "close_session", title: tr("Закрыть сессию"), enabled: always, run: () => closeTopmost(), cover: "none" },
  {
    id: "toggle_split",
    title: tr("Вертикальный сплит"),
    enabled: hasSelection,
    run: (state) => toggleSplit(state, "vertical"),
  },
  {
    id: "toggle_horizontal_split",
    title: tr("Горизонтальный сплит"),
    enabled: hasSelection,
    run: (state) => toggleSplit(state, "horizontal"),
  },
  {
    id: "close_split",
    title: tr("Закрыть сплит"),
    enabled: always,
    visible: hasSplit,
    run: onSelected((session) => splitStep(session, () => command({ command: "closeSplit", sessionId: session.id }))),
  },
  {
    id: "swap_panes",
    title: tr("Поменять панели местами"),
    enabled: hasSplit,
    cover: "pick",
    run: onSelected((session) => command({ command: "swapPanes", sessionId: session.id })),
  },
  {
    id: "focus_left_pane",
    title: (state) => (topBottom(state) ? tr("Фокус на верхнюю панель") : tr("Фокус на левую панель")),
    enabled: always,
    visible: hasSplit,
    run: onSelected((session) => command({ command: "focusPane", sessionId: session.id, pane: "left" })),
  },
  {
    id: "focus_right_pane",
    title: (state) => (topBottom(state) ? tr("Фокус на нижнюю панель") : tr("Фокус на правую панель")),
    enabled: always,
    visible: hasSplit,
    run: onSelected((session) => command({ command: "focusPane", sessionId: session.id, pane: "right" })),
  },
  {
    id: "toggle_overlay",
    title: tr("Скрыть или показать overlay"),
    enabled: (state) => (selected(state) ? hasHideableOverlay(selected(state)!) : false),
    run: onSelected((session) => command({ command: "overlayVisibility", sessionId: session.id, mode: "toggle" })),
  },
  {
    id: "toggle_scratch",
    title: tr("Scratch-терминал"),
    enabled: hasSelection,
    run: onSelected((session) => command({ command: "setScratch", sessionId: session.id, shown: null })),
  },
  { id: "dashboard", title: tr("Дашборд"), enabled: always, run: (state) => command({ command: state.dashboard ? "closeDashboard" : "openDashboard" }), cover: "zoomOrPick" },
  { id: "quick_terminal", title: "Quick terminal", enabled: always, run: () => command({ command: "setQuick", shown: null }) },
  // As in agterm, the row is never inert, and the zoom itself does nothing while a pick or an ask is up.
  {
    id: "toggle_terminal_zoom",
    title: tr("Zoom терминала"),
    enabled: always,
    run: (state) => state.pick === null && state.ask === null && command({ command: "setZoom", zoomed: null }),
    cover: "none",
  },
  {
    id: "restart_session",
    unlisted: true,
    title: tr("Перезапустить сессию"),
    // A pane of another machine has no shell here to restart; a split opened here on its row does.
    enabled: (state) => {
      const session = selected(state);
      return session !== null && (session.focusedPane === "right" ? !session.remoteSplit : !session.remoteMain);
    },
    run: onSelected((session) => command({ command: "restartSession", sessionId: session.id })),
  },
  {
    id: "clear_status",
    title: tr("Сбросить статус"),
    enabled: hasSelection,
    run: onSelected((session) => command({ command: "clearStatus", sessionId: session.id })),
  },
  {
    id: "mark_attention",
    unlisted: true,
    title: tr("Отметить для внимания"),
    enabled: (state) => selected(state)?.status === "idle",
    run: onSelected((session) => command({ command: "markAttention", sessionId: session.id })),
  },
  { id: "previous_session", title: tr("Предыдущая сессия"), enabled: hasSelection, run: () => command({ command: "selectRelative", offset: -1 }) },
  { id: "next_session", title: tr("Следующая сессия"), enabled: hasSelection, run: () => command({ command: "selectRelative", offset: 1 }) },
  {
    id: "previous_attention_session",
    title: tr("Предыдущая сессия, ждущая внимания"),
    enabled: hasSelection,
    run: () => command({ command: "selectAttention", forward: false }),
  },
  {
    id: "next_attention_session",
    title: tr("Следующая сессия, ждущая внимания"),
    enabled: hasSelection,
    run: () => command({ command: "selectAttention", forward: true }),
  },
  {
    id: "first_session",
    title: tr("Первая сессия"),
    enabled: (state) => hasSelection(state) && navigable(state).length > 0,
    run: (state) => command({ command: "selectSession", sessionId: navigable(state)[0].id, reveal: true }),
  },
  {
    id: "last_session",
    title: tr("Последняя сессия"),
    enabled: (state) => hasSelection(state) && navigable(state).length > 0,
    run: (state) => command({ command: "selectSession", sessionId: navigable(state)[navigable(state).length - 1].id, reveal: true }),
  },
  {
    id: "previous_workspace",
    title: tr("Предыдущий workspace"),
    enabled: (state) => state.currentWorkspaceId !== null && canStepWorkspaces(state),
    visible: (state) => state.sidebarMode === "tree",
    run: () => command({ command: "selectRelativeWorkspace", offset: -1 }),
  },
  {
    id: "next_workspace",
    title: tr("Следующий workspace"),
    enabled: (state) => state.currentWorkspaceId !== null && canStepWorkspaces(state),
    visible: (state) => state.sidebarMode === "tree",
    run: () => command({ command: "selectRelativeWorkspace", offset: 1 }),
  },
  { id: "session_palette", title: tr("Перейти к сессии…"), enabled: always, run: () => togglePalette("sessions"), unlisted: true },
  { id: "command_palette", title: tr("Палитра действий"), enabled: always, run: () => togglePalette("actions"), unlisted: true },
  {
    id: "custom_command_palette",
    unlisted: true,
    title: tr("Пользовательские команды…"),
    enabled: always,
    run: () => togglePalette("commands"),
  },
  { id: "edit_keymap", title: tr("Открыть keymap.conf"), enabled: always, run: () => command({ command: "editConfig", file: "keymap" }) },
  { id: "edit_hooks", title: tr("Открыть hooks.conf"), enabled: always, run: () => command({ command: "editConfig", file: "hooks" }) },
  { id: "reload_config", title: tr("Перечитать keymap.conf и hooks.conf"), enabled: always, run: () => command({ command: "reloadConfig" }), cover: "none", unlisted: true },
  { id: "edit_ghostty_config", title: tr("Открыть ghostty.conf"), enabled: always, run: () => command({ command: "editConfig", file: "ghostty" }) },
  { id: "reload_ghostty_config", title: tr("Перечитать ghostty.conf"), enabled: always, run: () => command({ command: "reloadConfig", file: "ghostty" }), cover: "none" },
  { id: "reload_keymap", title: tr("Перечитать keymap.conf"), enabled: always, run: () => command({ command: "reloadConfig", file: "keymap" }), cover: "none" },
  { id: "reload_hooks", title: tr("Перечитать hooks.conf"), enabled: always, run: () => command({ command: "reloadConfig", file: "hooks" }), cover: "none" },
  // agterm's Reopen Closed Item: only while a close can still be undone.
  { id: "undo_close", title: tr("Вернуть закрытое"), enabled: always, visible: (state) => state.pendingClose !== null, run: () => command({ command: "undoClose" }) },
  // agterm's Reopen Last Closed Item brings back the latest at once; the list is the File menu's Open Recent.
  { id: "reopen_recent", title: tr("Вернуть последнее закрытое"), enabled: always, visible: (state) => state.recentClosed.length > 0, run: () => command({ command: "reopenRecent" }) },
  {
    id: "toggle_flag",
    title: (state) => (selected(state)?.flagged ? tr("Снять пометку") : tr("Пометить сессию")),
    enabled: hasSelection,
    run: onSelected((session) => command({ command: "setFlag", sessionIds: [session.id] })),
  },
  {
    id: "toggle_flagged_view",
    title: (state) => (state.sidebarMode === "flagged" ? tr("Показать все сессии") : tr("Показать помеченные сессии")),
    enabled: always,
    visible: (state) => state.sidebarMode === "flagged" || sessions(state).some((session) => session.flagged),
    run: () => command({ command: "setSidebarMode" }),
  },
  {
    id: "clear_flagged",
    title: tr("Убрать все пометки"),
    enabled: always,
    visible: (state) => sessions(state).some((session) => session.flagged),
    run: (state) => {
      const count = sessions(state).filter((session) => session.flagged).length;
      if (window.confirm(t("Снять пометку с сессий ({n})? Сами сессии не закрываются.", { n: count }))) {
        command({ command: "clearFlags" });
      }
    },
  },
  {
    id: "focus_workspace",
    title: tr("Фокус на текущем workspace"),
    enabled: (state) => state.currentWorkspaceId !== null,
    run: (state) => state.currentWorkspaceId && command({ command: "toggleFocusWorkspace", workspaceId: state.currentWorkspaceId }),
  },
  {
    id: "add_workspace_to_focus",
    title: tr("Добавить текущий workspace в фокус"),
    enabled: (state) => state.currentWorkspaceId !== null,
    visible: (state) => !currentWorkspace(state)?.focused,
    run: (state) => state.currentWorkspaceId && command({ command: "setFocusMember", workspaceId: state.currentWorkspaceId, member: true }),
  },
  { id: "clear_focus", title: tr("Снять фокус со всех workspace"), enabled: always, visible: hasMarked, run: () => command({ command: "clearFocus" }) },
  {
    id: "toggle_workspace_filter",
    title: tr("Фильтр workspace"),
    enabled: always,
    visible: hasMarked,
    run: () => command({ command: "setFocusEnabled" }),
  },
  { id: "install_command_line_tool", title: tr("Добавить agshellctl в PATH"), enabled: always, run: () => command({ command: "installCommandLineTool" }), unlisted: true },
  { id: "install_agent_hooks", title: tr("Установить хуки статуса агентов (Claude Code, Codex, Pi, OpenCode)"), enabled: always, run: () => command({ command: "installAgent", what: "hooks" }), unlisted: true },
  // The system log's provider: agterm's Logger needs no registration, the Windows event log does (ADR 0011).
  { id: "register_event_log", title: tr("Зарегистрировать журнал событий AgShell"), enabled: always, run: () => command({ command: "registerEventLog" }), unlisted: true },
  { id: "install_agent_skill", title: tr("Установить скилл AgShell для Claude Code и Codex"), enabled: always, run: () => command({ command: "installAgent", what: "skill" }), unlisted: true },
  // agterm's Help ▸ Developer Documentation… and its About panel, which have no keymap action there.
  { id: "developer_documentation", title: tr("Документация для разработчиков…"), enabled: always, run: () => command({ command: "openDocumentation" }), cover: "none", unlisted: true },
  { id: "about", title: tr("О программе AgShell"), enabled: always, run: () => command({ command: "showAbout" }), cover: "none", unlisted: true },
  { id: "open_directory", title: tr("Открыть каталог…"), enabled: always, run: () => command({ command: "openDirectory" }) },
  { id: "select_theme", title: tr("Выбрать тему…"), enabled: always, run: () => setPalette("themes") },
  { id: "open_settings", title: tr("Настройки"), enabled: always, run: () => command({ command: "openSettings" }), cover: "none", unlisted: true },
  // agterm's Reset Live Sessions… beside Quit in the app menu, shown only in live mode.
  {
    id: "reset_live_sessions",
    unlisted: true,
    title: tr("Сбросить живые сессии…"),
    enabled: always,
    visible: (state) => state.settings.liveResetAvailable,
    run: () => command({ command: "resetLiveSessions" }),
    cover: "none",
  },
  // agterm's Quit from the app menu, with its stock chord (shortcuts.ts).
  { id: "quit", title: tr("Выйти из AgShell"), enabled: always, run: () => command({ command: "quit" }), cover: "none", unlisted: true },
  { id: "show_attention", title: tr("Показать сессии, ждущие внимания"), enabled: always, run: () => togglePalette("attention") },
  { id: "toggle_search", title: tr("Найти…"), enabled: hasSelection, run: () => toggleSearch(focusedKey()) },
  {
    id: "increase_font_size",
    title: tr("Увеличить шрифт"),
    enabled: always,
    cover: "none",
    run: fontAction("increase_font_size", 1),
  },
  {
    id: "decrease_font_size",
    title: tr("Уменьшить шрифт"),
    enabled: always,
    cover: "none",
    run: fontAction("decrease_font_size", -1),
  },
  {
    id: "reset_font_size",
    title: tr("Обычный размер шрифта"),
    enabled: always,
    cover: "none",
    run: fontAction("reset_font_size", null),
  },
  { id: "toggle_sidebar", title: tr("Показать или скрыть боковую панель"), enabled: always, run: () => toggleSidebar() },
  { id: "toggle_fullscreen", title: tr("Полноэкранный режим"), enabled: always, run: () => command({ command: "toggleFullscreen" }) },
  { id: "expand_workspaces", title: tr("Развернуть все workspace"), enabled: always, visible: hasWorkspaceRows, run: () => command({ command: "expandWorkspaces" }) },
  { id: "collapse_workspaces", title: tr("Свернуть все workspace, кроме текущего"), enabled: always, visible: hasWorkspaceRows, run: () => command({ command: "collapseWorkspaces" }) },
  {
    id: "toggle_workspace_collapse",
    title: (state) => (currentWorkspace(state)?.collapsed ? tr("Развернуть workspace") : tr("Свернуть workspace")),
    enabled: (state) => state.currentWorkspaceId !== null,
    visible: hasWorkspaceRows,
    run: (state) => {
      const workspace = state.workspaces.find((w) => w.id === state.currentWorkspaceId);
      if (workspace) {
        command({ command: "setCollapsed", workspaceId: workspace.id, collapsed: !workspace.collapsed });
      }
    },
  },
  {
    id: "delete_workspace",
    title: tr("Удалить workspace"),
    enabled: (state) => state.currentWorkspaceId !== null,
    visible: (state) => state.canRemoveWorkspace,
    run: (state) => state.currentWorkspaceId && command({ command: "closeWorkspace", workspaceId: state.currentWorkspaceId }),
  },
  {
    id: "reveal_session",
    unlisted: true,
    title: tr("Открыть каталог сессии в проводнике"),
    enabled: (state) => selected(state)?.canReveal ?? false,
    run: onSelected((session) => command({ command: "revealSession", sessionId: session.id })),
  },
];

/**
 * The keys the quick panel's own page runs: agterm's actions that act on the panel while it holds the keyboard, its
 * toggle, zoom and font. The others, close among them, go to the active window (startQuickActions).
 */
export const quickActions: Partial<Record<string, () => void>> = {
  quick_terminal: () => command({ command: "setQuick", shown: null }),
  toggle_terminal_zoom: () => command({ command: "setZoom", zoomed: null }),
  increase_font_size: () => command({ command: "changeFont", sessionId: quickSessionId, delta: 1, pane: "quick" }),
  decrease_font_size: () => command({ command: "changeFont", sessionId: quickSessionId, delta: -1, pane: "quick" }),
  reset_font_size: () => command({ command: "changeFont", sessionId: quickSessionId, delta: null, pane: "quick" }),
};

/**
 * agterm's menu works while the quick panel holds the keyboard: the panel's other actions run in the active window, as its
 * own keys would, except find, which the panel covers (agterm's coverHidesActiveSession).
 */
export function startQuickActions(): void {
  onHostMessage((message) => {
    if (message.type !== "quickAction") {
      return;
    }
    const state = currentState();
    const action = findAction(message.name);
    if (message.name === "close_session") {
      closeTopmost(false, true);
    } else if (state && action && message.name !== "toggle_search" && actionAvailable(action, state)) {
      action.run(state);
    }
  });
}

export function findAction(id: string): Action | undefined {
  return actions.find((action) => action.id === id);
}
