import { AppState, command, MenuNode, MenuView, onHostMessage, RecentClosedView, send } from "./bridge";
import { actionAvailable, actionTitle, closeTopmost, findAction } from "./actions";
import { anyOverlayShown } from "./overlays";
import { menuColors } from "./appearance";
import { currentState, focusedKey, subscribe, uiActionsEnabled } from "./store";
import { chordLabel, stockChordLabel, stockChordSyntax } from "./shortcuts";
import { getSelection, terminalAction } from "./terminals";
import { currentLanguage, t } from "./i18n";

/** agterm's menu key equivalent: the action's single-chord binding; a leader sequence cannot be one. */
function menuKeys(state: AppState, id: string): string | undefined {
  const stock = stockChordLabel(state, id === "host:minimize" ? "minimize" : id);
  if (stock !== undefined || ["open_settings", "quit", "host:minimize"].includes(id)) {
    return stock;
  }
  const chord = state.keymap.menu[id];
  return chord ? chordLabel(chord.sequence[0]) : undefined;
}

/** An item running an action, enabled by the predicate its palette row and keys use; the title is agterm's menu label. */
function action(state: AppState, id: string, title: string, options: { keys?: boolean; checked?: boolean } = {}): MenuNode {
  const found = findAction(id);
  return {
    kind: "item",
    id: "action:" + id,
    title,
    keys: options.keys === false ? undefined : menuKeys(state, id),
    chord: options.keys === false ? undefined : stockChordSyntax(state, id) ?? state.keymap.menu[id]?.label,
    enabled: found !== undefined && actionAvailable(found, state),
    checked: options.checked,
  };
}

const separator: MenuNode = { kind: "separator" };

/** An action item titled as its palette row, where agterm's menu label is the palette's title. */
function paletteAction(state: AppState, id: string): MenuNode {
  const found = findAction(id);
  return action(state, id, found ? actionTitle(found, state) : id);
}

function selectedSession(state: AppState) {
  return state.workspaces.flatMap((workspace) => workspace.sessions).find((session) => session.id === state.selectedSessionId) ?? null;
}

/** agterm's modalActive: a terminal zoom, the dashboard or a pick or ask covers the window; items without a palette row read it. */
function modal(state: AppState): boolean {
  return !uiActionsEnabled(state);
}

/** An action item agterm gates on modalActive alone (the window items), with the action's own visibility. */
function windowAction(state: AppState, id: string, title: string): MenuNode {
  const node = action(state, id, title);
  const found = findAction(id);
  return node.kind === "item" ? { ...node, enabled: !modal(state) && (found?.visible?.(state) ?? true) } : node;
}

function ago(time: number): string {
  const minutes = Math.round((Date.now() - time) / 60000);
  return minutes < 1 ? t("только что") : minutes < 60 ? t("{n} мин назад", { n: minutes }) : minutes < 1440 ? t("{n} ч назад", { n: Math.round(minutes / 60) }) : new Date(time).toLocaleDateString(currentLanguage());
}

/** agterm's recentTitle, "title - subtitle", with the time it was closed, which agshell keeps. */
function recentItem(item: RecentClosedView): MenuNode {
  const title = item.subtitle ? `${item.title} - ${item.subtitle}` : item.title;
  return { kind: "item", id: "recent:" + item.id, title: `${title} · ${ago(item.closedAt)}`, enabled: true };
}

/** agterm's File ▸ Open Recent: the sessions, then the workspaces, under their titles, and Clear Menu. */
function openRecent(state: AppState): MenuNode {
  const sessions = state.recentClosed.filter((item) => item.kind !== "workspace");
  const workspaces = state.recentClosed.filter((item) => item.kind === "workspace");
  const items: MenuNode[] =
    state.recentClosed.length === 0
      ? [{ kind: "item", id: "none", title: t("Нет недавних"), enabled: false }]
      : [
          ...(sessions.length > 0 ? [{ kind: "header" as const, title: t("Сессии") }, ...sessions.map(recentItem)] : []),
          ...(workspaces.length > 0 ? [{ kind: "header" as const, title: t("Workspaces") }, ...workspaces.map(recentItem)] : []),
          separator,
          { kind: "item", id: "clear-recent", title: t("Очистить меню"), enabled: true },
        ];
  return { kind: "submenu", id: "open-recent", title: t("Недавно закрытые"), enabled: state.recentClosed.length > 0 && !modal(state), items };
}

/** agterm's File menu: windows, workspaces, sessions and the config files, then the system's Close. */
function fileMenu(state: AppState): MenuView {
  return {
    title: t("Файл"),
    items: [
      windowAction(state, "new_window", t("Новое окно")),
      {
        kind: "submenu",
        id: "open-window",
        title: t("Открыть окно"),
        enabled: !modal(state),
        items: state.windows.map((window) => ({ kind: "item", id: "window:" + window.id, title: window.name, enabled: true, checked: window.open })),
      },
      windowAction(state, "rename_window", t("Переименовать окно…")),
      windowAction(state, "delete_window", t("Удалить окно")),
      separator,
      action(state, "new_workspace", t("Новый workspace")),
      action(state, "rename_workspace", t("Переименовать workspace")),
      action(state, "delete_workspace", t("Удалить workspace")),
      separator,
      action(state, "new_session", t("Новая сессия")),
      action(state, "open_directory", t("Открыть каталог…")),
      action(state, "attach_remote", t("Подключиться к сессии на другой машине…")),
      openRecent(state),
      action(state, "reopen_recent", t("Вернуть последнее закрытое")),
      action(state, "rename_session", t("Переименовать сессию")),
      action(state, "duplicate_session", t("Дублировать сессию")),
      action(state, "reveal_session", t("Показать в проводнике")),
      action(state, "close_session", t("Закрыть сессию")),
      // agterm's Reopen Closed Item shows no keys: its ⌘Z is the key monitor's, kept for text fields' own undo.
      action(state, "undo_close", t("Вернуть закрытое"), { keys: false }),
      action(state, "clear_status", t("Сбросить статус")),
      separator,
      action(state, "edit_keymap", t("Открыть keymap.conf…")),
      action(state, "reload_keymap", t("Перечитать keymap.conf")),
      action(state, "edit_hooks", t("Открыть hooks.conf…")),
      action(state, "reload_hooks", t("Перечитать hooks.conf")),
      action(state, "edit_ghostty_config", t("Открыть ghostty.conf…")),
      action(state, "reload_ghostty_config", t("Перечитать ghostty.conf")),
      separator,
      { kind: "item", id: "host:close-window", title: t("Закрыть окно"), keys: "Alt+F4", chord: "alt+f4", enabled: true },
    ],
  };
}

/** A field of the interface has the keyboard: a terminal's own input area is not one. */
function textField(): HTMLInputElement | HTMLTextAreaElement | null {
  const element = document.activeElement;
  return (element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement) && !element.closest(".xterm") ? element : null;
}

function fieldSelection(field: HTMLInputElement | HTMLTextAreaElement): string {
  return field.value.slice(field.selectionStart ?? 0, field.selectionEnd ?? 0);
}

/**
 * The system's Edit menu as AppKit validates it: Cut and Delete need a selection in a text field, Copy a selection in
 * it or in the terminal, Paste a field or a terminal (the host checks the clipboard), Select All either.
 */
function editMenu(state: AppState): MenuView {
  const field = textField();
  const key = focusedKey();
  const fieldSelected = field !== null && fieldSelection(field) !== "";
  const terminal = field === null && key !== null;
  return {
    title: t("Правка"),
    items: [
      { kind: "item", id: "edit:cut", title: t("Вырезать"), keys: "Ctrl+X", chord: "ctrl+x", enabled: fieldSelected },
      { kind: "item", id: "edit:copy", title: t("Копировать"), keys: field ? "Ctrl+C" : "Ctrl+Shift+C", chord: field ? "ctrl+c" : "ctrl+shift+c", enabled: fieldSelected || (terminal && getSelection(key) !== "") },
      { kind: "item", id: "edit:paste", title: t("Вставить"), keys: field ? "Ctrl+V" : "Ctrl+Shift+V", chord: field ? "ctrl+v" : "ctrl+shift+v", enabled: field !== null || terminal },
      { kind: "item", id: "edit:delete", title: t("Удалить"), enabled: fieldSelected },
      { kind: "item", id: "edit:select-all", title: t("Выделить всё"), keys: field ? "Ctrl+A" : "Ctrl+Shift+A", chord: field ? "ctrl+a" : "ctrl+shift+a", enabled: field !== null || terminal },
      separator,
      { kind: "item", id: "host:emoji", title: t("Эмодзи и символы"), keys: "Win+.", chord: "super+.", enabled: field !== null || terminal },
    ],
  };
}

/** agterm's app menu: About, Settings, and Reset Live Sessions… beside Quit, the reset shown only in live mode. */
function appMenu(state: AppState): MenuView {
  return {
    title: "AgShell",
    items: [
      action(state, "about", t("О программе AgShell")),
      separator,
      action(state, "open_settings", t("Настройки…")),
      separator,
      ...(state.settings.liveResetAvailable ? [action(state, "reset_live_sessions", t("Сбросить живые сессии…"))] : []),
      action(state, "quit", t("Выйти из AgShell")),
    ],
  };
}

/** agterm's View menu: font and theme, the sidebar, flags and focus, the panes and covers, then the system's Full Screen. */
function viewMenu(state: AppState): MenuView {
  const session = selectedSession(state);
  const current = state.workspaces.find((workspace) => workspace.id === state.currentWorkspaceId) ?? null;
  const marked = state.workspaces.filter((workspace) => workspace.focused);
  const soleFocus = state.focusEnabled && marked.length === 1 && marked[0].id === state.currentWorkspaceId;
  return {
    title: t("Вид"),
    items: [
      paletteAction(state, "increase_font_size"),
      paletteAction(state, "decrease_font_size"),
      paletteAction(state, "reset_font_size"),
      paletteAction(state, "select_theme"),
      separator,
      action(state, "toggle_sidebar", state.sidebar.visible ? t("Скрыть боковую панель") : t("Показать боковую панель")),
      paletteAction(state, "expand_workspaces"),
      paletteAction(state, "collapse_workspaces"),
      action(state, "toggle_workspace_collapse", current?.collapsed ? t("Развернуть workspace") : t("Свернуть workspace")),
      paletteAction(state, "toggle_flagged_view"),
      paletteAction(state, "toggle_flag"),
      paletteAction(state, "clear_flagged"),
      action(state, "focus_workspace", soleFocus ? t("Снять фокус с workspace") : t("Фокус на текущем workspace")),
      paletteAction(state, "add_workspace_to_focus"),
      paletteAction(state, "toggle_workspace_filter"),
      paletteAction(state, "clear_focus"),
      paletteAction(state, "toggle_split"),
      paletteAction(state, "toggle_horizontal_split"),
      paletteAction(state, "swap_panes"),
      action(state, "toggle_scratch", session?.scratchShown ? t("Скрыть scratch") : t("Показать scratch")),
      action(state, "toggle_overlay", !session || anyOverlayShown(session) ? t("Скрыть overlay") : t("Показать overlay")),
      paletteAction(state, "toggle_search"),
      paletteAction(state, "quick_terminal"),
      paletteAction(state, "toggle_terminal_zoom"),
      separator,
      paletteAction(state, "toggle_fullscreen"),
    ],
  };
}

/** agterm's Navigate menu: the palettes and the dashboard, stepping between sessions, workspaces and windows, the panes. */
function navigateMenu(state: AppState): MenuView {
  return {
    title: t("Навигация"),
    items: [
      action(state, "session_palette", t("Перейти к сессии")),
      action(state, "command_palette", t("Палитра действий")),
      action(state, "custom_command_palette", t("Пользовательские команды")),
      action(state, "show_attention", t("Перейти к ждущим внимания…")),
      paletteAction(state, "dashboard"),
      separator,
      paletteAction(state, "previous_session"),
      paletteAction(state, "next_session"),
      paletteAction(state, "previous_attention_session"),
      paletteAction(state, "next_attention_session"),
      paletteAction(state, "first_session"),
      paletteAction(state, "last_session"),
      paletteAction(state, "previous_workspace"),
      paletteAction(state, "next_workspace"),
      paletteAction(state, "previous_window"),
      paletteAction(state, "next_window"),
      separator,
      paletteAction(state, "focus_left_pane"),
      paletteAction(state, "focus_right_pane"),
    ],
  };
}

/** The system's Window menu: minimize, zoom, bring all to front, and the open windows, the current one checked. */
function windowMenu(state: AppState): MenuView {
  const open = state.windows.filter((window) => window.open);
  return {
    title: t("Окно"),
    items: [
      { kind: "item", id: "host:minimize", title: t("Свернуть"), keys: menuKeys(state, "host:minimize"), chord: stockChordSyntax(state, "host:minimize"), enabled: true },
      { kind: "item", id: "host:zoom", title: t("Развернуть"), enabled: true },
      separator,
      { kind: "item", id: "host:front", title: t("Все окна — на передний план"), enabled: true },
      separator,
      ...open.map((window): MenuNode => ({ kind: "item", id: "raise:" + window.id, title: window.name, enabled: true, checked: window.id === state.currentWindowId })),
    ],
  };
}

/** agterm's Help menu: the developer documentation and the installers. */
function helpMenu(state: AppState): MenuView {
  return {
    title: t("Справка"),
    items: [
      action(state, "developer_documentation", t("Документация для разработчиков…")),
      separator,
      action(state, "install_command_line_tool", t("Установить agshellctl…")),
      action(state, "install_agent_hooks", t("Установить хуки статуса агентов…")),
      action(state, "install_agent_skill", t("Установить скилл агентов…")),
      separator,
      action(state, "register_event_log", t("Зарегистрировать журнал событий…")),
    ],
  };
}

export function buildMenus(state: AppState): MenuView[] {
  return [appMenu(state), fileMenu(state), editMenu(state), viewMenu(state), navigateMenu(state), windowMenu(state), helpMenu(state)];
}

let posted = "";

function postMenu(force = false): void {
  const state = currentState();
  if (!state) {
    return;
  }
  const message = { type: "menu" as const, menus: buildMenus(state), colors: menuColors(state) };
  const json = JSON.stringify(message);
  if (json !== posted || force) {
    posted = json;
    send(message);
  }
}

/** The Edit items on the field or terminal that had the keyboard; the clipboard is the host's, the page has no gesture. */
function runEdit(what: string, text: string | undefined): void {
  const field = textField();
  const key = focusedKey();
  if (field) {
    field.focus();
    const selection = fieldSelection(field);
    if ((what === "copy" || what === "cut") && selection) {
      send({ type: "menuClipboard", text: selection });
    }
    if (what === "cut" || what === "delete") {
      document.execCommand("delete");
    } else if (what === "paste" && text) {
      document.execCommand("insertText", false, text);
    } else if (what === "select-all") {
      field.select();
    }
    return;
  }
  if (!key) {
    return;
  }
  if (what === "copy") {
    const selection = getSelection(key);
    if (selection) {
      send({ type: "menuClipboard", text: selection });
    }
  } else if (what === "paste" && text) {
    terminalAction(key, "paste", text);
  } else if (what === "select-all") {
    terminalAction(key, "selectAll", null);
  }
}

function runMenuItem(id: string, text: string | undefined): void {
  const state = currentState();
  if (!state) {
    return;
  }
  if (id.startsWith("action:")) {
    const name = id.slice("action:".length);
    const found = findAction(name);
    // agterm's menu Close Session closes the window when nothing is left to close, as its key does.
    if (name === "close_session") {
      closeTopmost(true);
    } else if (found && (actionAvailable(found, state) || (["new_window", "rename_window", "delete_window"].includes(name) && !modal(state)))) {
      found.run(state);
    }
  } else if (id.startsWith("window:") && !modal(state)) {
    command({ command: "selectWindow", windowId: id.slice("window:".length) });
  } else if (id.startsWith("raise:")) {
    command({ command: "selectWindow", windowId: id.slice("raise:".length) });
  } else if (id.startsWith("recent:") && !modal(state)) {
    command({ command: "reopenRecent", itemId: id.slice("recent:".length) });
  } else if (id === "clear-recent") {
    command({ command: "clearRecent" });
  } else if (id.startsWith("edit:")) {
    runEdit(id.slice("edit:".length), text);
  }
}

/** The window's menu bar: the model follows the state, and the host runs an item by its id here. */
export function startMenuBar(): void {
  subscribe(() => postMenu());
  onHostMessage((message) => {
    if (message.type === "menuAction") {
      runMenuItem(message.id, message.text);
    } else if (message.type === "menuRefresh") {
      postMenu(true);
    }
  });
}
