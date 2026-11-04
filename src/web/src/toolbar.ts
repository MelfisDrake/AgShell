import { AppState, command, onHostMessage, send } from "./bridge";
import { toggleSplit } from "./actions";
import { bindingLabel, handleAppShortcut, runCustom } from "./shortcuts";
import { currentState, sessionName, switcherDetail, uiActionsEnabled } from "./store";
import { anyOverlayHidden, anyOverlayShown, hasHideableOverlay } from "./overlays";
import { menuColors } from "./appearance";
import { t } from "./i18n";

export interface ToolbarItem {
  id: string;
  glyph?: string;
  title?: string;
  subtitle?: string;
  shortcut?: string;
  remote?: boolean;
  color?: string;
  shape?: string;
  enabled?: boolean;
  separator?: boolean;
  rows?: ToolbarItem[];
}

/** Presentation only, like the native menu projection. Choices recheck the latest AppModel snapshot. */
export function toolbarItems(state: AppState, held = false): ToolbarItem[] {
  if (state.settings.toolbarMode === "hidden") return [];
  const hint = (title: string, action: string) => {
    const keys = bindingLabel(state.keymap.actions[action]);
    return keys ? `${title} (${keys})` : title;
  };
  if (state.zoomed) return [{ id: "zoom", glyph: "unzoom", title: hint(t("Выйти из zoom"), "toggle_terminal_zoom"), enabled: !held }];
  if (state.dashboard) return [{ id: "dashboard-exit", glyph: "close", title: t("Закрыть дашборд"), enabled: !held }];
  const shown = (key: string) => !state.settings.hiddenInterfaceElements.includes(key);
  const all = state.workspaces.flatMap(w => w.sessions.map(session => ({ workspace: w, session })));
  const selected = all.find(e => e.session.id === state.selectedSessionId)?.session;
  const modal = state.pick !== null || state.ask !== null || held;
  const status = (entry: { status: string; statusColor: string | null; statusShape: string | null }) => {
    const key = entry.status === "blocked" ? "blocked" : entry.status === "active" ? "active" : "completed";
    const style = getComputedStyle(document.documentElement);
    return entry.status === "idle" ? {} : {
      color: entry.statusColor ?? style.getPropertyValue(`--status-${key}`).trim(),
      shape: entry.statusShape ?? state.settings[`${key}StatusShape`],
    };
  };
  const navigable = new Set(state.navigableSessionIds);
  const recent = state.recentSessionIds.filter(id => id !== state.selectedSessionId && navigable.has(id)).slice(0, 10)
    .flatMap(id => {
      const entry = all.find(e => e.session.id === id);
      return entry ? [{ id: `recent:${id}`, title: sessionName(entry.session), subtitle: `${entry.workspace.name} · ${switcherDetail(entry.session)}`, remote: entry.session.remoteHost !== null, enabled: !modal, ...status(entry.session) }] : [];
    });
  const attention = state.attention.map(entry => ({ id: `attention:${entry.windowId}:${entry.sessionId}`, title: entry.name, subtitle: entry.subtitle, remote: entry.remoteHost !== null, enabled: entry.enabled && !modal, ...status(entry) }));
  const mostUsed = new Set(state.keymap.mostUsed);
  const leading = state.keymap.commands.filter(c => mostUsed.has(c.name));
  const rest = state.keymap.commands.filter(c => !mostUsed.has(c.name));
  const custom = (c: AppState["keymap"]["commands"][number]): ToolbarItem => ({ id: `custom:${c.name}`, title: c.name, shortcut: c.shortcut ?? "", enabled: !!selected && uiActionsEnabled(state) && !held });
  const commands = [...leading.map(custom), ...(leading.length ? [{ id: "custom-separator", separator: true }] : []), ...rest.map(custom)];
  const horizontal = selected?.splitAxis === "horizontal";
  const glyph = !selected?.hasSplit ? "none" : selected.splitShown ? horizontal ? "both-horizontal" : "both" : selected.splitFocused ? horizontal ? "bottom" : "right" : horizontal ? "top" : "left";
  const a: ToolbarItem[] = [];
  const b: ToolbarItem[] = [];
  const c: ToolbarItem[] = [];
  if (shown("recentSessions")) a.push({ id: "recent", glyph: "recent", title: t("Недавние сессии (Ctrl+Tab)"), enabled: recent.length > 0 && !modal, rows: recent });
  if (state.settings.attentionButtonEnabled) a.push({ id: "attention", glyph: state.attention.some(s => s.status === "blocked") ? "bell-filled" : "bell", title: hint(attention.length ? t("Показать сессии, ждущие внимания") : t("Никто не ждёт внимания"), "show_attention"), color: state.attention.some(e => e.status === "blocked") ? getComputedStyle(document.documentElement).getPropertyValue("--status-blocked").trim() : undefined, enabled: attention.length > 0 && !modal, rows: attention });
  if (shown("scratch")) b.push({ id: "scratch", glyph: selected?.scratchShown ? "scratch-filled" : "scratch", title: hint(selected?.scratchShown ? t("Скрыть scratch-терминал") : t("Показать scratch-терминал"), "toggle_scratch"), enabled: !!selected && !modal });
  if (shown("split")) b.push({ id: "split", glyph: `split-${glyph}`, title: hint(selected?.splitShown ? t("Скрыть сплит") : selected?.hasSplit ? t("Показать сплит") : t("Сплит справа"), horizontal ? "toggle_horizontal_split" : "toggle_split"), enabled: !!selected && !modal });
  if (shown("overlayToggle")) b.push({ id: "overlay", glyph: selected && anyOverlayHidden(selected) ? "overlay-hidden" : "overlay", title: hint(selected && anyOverlayShown(selected) ? t("Скрыть overlay") : t("Показать overlay"), "toggle_overlay"), enabled: !!selected && hasHideableOverlay(selected) && !modal });
  if (shown("dashboard")) c.push({ id: "dashboard", glyph: "dashboard", title: hint(t("Дашборд"), "dashboard"), enabled: !!selected && !modal });
  if (shown("quickTerminal")) c.push({ id: "quick", glyph: "quick", title: hint("Quick terminal", "quick_terminal"), enabled: !modal });
  if (shown("customCommands")) c.push({ id: "commands", glyph: "commands", title: hint(t("Пользовательские команды"), "custom_command_palette"), enabled: commands.some(e => !e.separator) && !!selected && uiActionsEnabled(state) && !held, rows: commands });
  return [...(shown("sidebarToggle") ? [{ id: "sidebar", glyph: "sidebar", title: hint(t("Боковая панель"), "toggle_sidebar"), enabled: !modal }] : []), ...a,
    ...(a.length >= 2 && b.length >= 2 ? [{ id: "separator-a", separator: true }] : []), ...b,
    ...((b.length >= 2 && c.length >= 2) || (b.length === 0 && a.length >= 2 && c.length >= 2) ? [{ id: "separator-b", separator: true }] : []), ...c];
}

let held = false;
let last = "";
export function postToolbar(state: AppState, suppressed: boolean): void {
  held = suppressed;
  const projection = { type: "toolbar" as const, held, items: toolbarItems(state, held), colors: { ...menuColors(state), bar: state.theme.background, popoverHover: (state.theme.selectionBackground ? "#80" + state.theme.selectionBackground.slice(1) : "#33" + state.theme.foreground.slice(1)) }, fontSize: state.settings.interfaceFontSize };
  const json = JSON.stringify(projection);
  if (json !== last) { last = json; send(projection); }
}

export function startToolbar(setOpen: (value: boolean) => void): void {
  onHostMessage(message => {
    if (message.type === "toolbarKey") {
      const state = currentState();
      if (!state || !uiActionsEnabled(state) || held) return;
      const handled = handleAppShortcut(new KeyboardEvent(message.kind, message));
      if (handled && message.kind === "keydown") send({ type: "toolbarKeyHandled", token: message.token });
      return;
    }
    if (message.type === "label") { const state = currentState(); if (state) postToolbar(state, held); }
    if (message.type === "toolbarPopover") setOpen(message.value);
    if (message.type !== "toolbarAction") return;
    const state = currentState();
    if (!state) return;
    const items = toolbarItems(state, held);
    const item = items.flatMap(i => [i, ...(i.rows ?? [])]).find(i => i.id === message.id);
    if (!item?.enabled) return;
    const selected = state.selectedSessionId;
    const id = item.id;
    if (id.startsWith("recent:")) command({ command: "selectSession", sessionId: id.slice(7) });
    else if (id.startsWith("attention:")) { const [, windowId, sessionId] = id.split(":"); command({ command: "selectAttentionEntry", windowId, sessionId }); }
    else if (id.startsWith("custom:")) runCustom(id.slice(7));
    else if (id === "sidebar") command({ command: "setSidebar", visible: !state.sidebar.visible });
    else if (id === "scratch" && selected) command({ command: "setScratch", sessionId: selected, shown: null });
    else if (id === "split") toggleSplit(state, null);
    else if (id === "overlay" && selected) command({ command: "overlayVisibility", sessionId: selected, mode: "toggle" });
    else if (id === "dashboard") command({ command: "openDashboard" });
    else if (id === "dashboard-exit") command({ command: "closeDashboard" });
    else if (id === "zoom") command({ command: "setZoom", zoomed: false });
    else if (id === "quick") command({ command: "setQuick", shown: null });
  });
}
