import { setLanguage, t } from "./i18n";
import { useSyncExternalStore } from "react";
import { AppState, command, InputSource, onHostMessage, send, SessionView } from "./bridge";
import { applyChrome } from "./appearance";
import { deliverPageInfo, forgetPage, handlePageKey, setPageZoom } from "./pages";
import { cancelSwitcher, endSwitcherHold } from "./switcher";
import {
  applyTerminalAppearance,
  clearSearch,
  paneKey,
  pasteInto,
  promoteSplit,
  readText,
  readSelection,
  terminalAction,
  commitComposition,
  takeComposition,
  reattach,
  searchResults,
  searchTerminal,
  SearchResults,
  swapPanes,
  writeOutput,
} from "./terminals";

export interface Editing {
  kind: "workspace" | "session";
  id: string;
}

export type PaletteMode = "sessions" | "actions" | "attention" | "commands" | "themes";

/** agterm's Attach Remote picker: the machines remotes.conf names, then one machine's sessions. */
/**
 * agterm's session search: each session has its own bar and query, on the pane it was opened in even when focus moves,
 * and it is there again when the session is shown again. The snapshot carries the selected session's.
 */
export interface SearchState {
  key: string;
  needle: string;
}

interface Snapshot {
  state: AppState | null;
  editing: Editing | null;
  palette: PaletteMode | null;
  search: SearchState | null;
  /** agterm's first-launch welcome, offering the skill and the status hooks. */
  welcome: boolean;
  lists: { themes: string[]; fonts: string[]; sounds: string[]; inputSources: InputSource[] } | null;
}

let snapshot: Snapshot = { state: null, editing: null, palette: null, search: null, welcome: false, lists: null };
const listeners = new Set<() => void>();

function update(next: Partial<Snapshot>): void {
  snapshot = { ...snapshot, ...next };
  listeners.forEach((listener) => listener());
}

/**
 * Names that title ticks changed since the last full state, which carries them all again. As in agterm, a program that
 * animates its title (Codex does it several times a second) re-renders only the labels showing that session's name.
 */
const labels = new Map<string, string>();
const labelListeners = new Set<() => void>();

function applyLabel(sessionId: string, name: string): void {
  if (labels.get(sessionId) === name) {
    return;
  }
  labels.set(sessionId, name);
  labelListeners.forEach((listener) => listener());
}

function subscribeLabels(listener: () => void): () => void {
  labelListeners.add(listener);
  return () => labelListeners.delete(listener);
}

export function sessionName(session: SessionView): string {
  return labels.get(session.id) ?? session.name;
}

export function SessionName({ session }: { session: SessionView }): string {
  return useSyncExternalStore(subscribeLabels, () => sessionName(session));
}

/** agterm's subtitleDetail: the focused pane's own title when it says more than the name, else its directory. */
export function subtitleDetail(session: SessionView): string {
  return session.title && session.title !== sessionName(session) ? session.title : (session.cwd ?? "");
}

/** agterm's switcherDetail: subtitleDetail led by the machine an attached session lives on. */
export function switcherDetail(session: SessionView): string {
  return session.remoteHost ? `${session.remoteHost} · ${subtitleDetail(session)}` : subtitleDetail(session);
}

/** agterm's uiActionsEnabled: nothing modal covers the window, no terminal zoom, dashboard, pick or ask. */
export function uiActionsEnabled(state: AppState): boolean {
  return !state.zoomed && state.dashboard === null && state.pick === null && state.ask === null;
}

export function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function currentState(): AppState | null {
  return snapshot.state;
}

export function useStore(): Snapshot {
  return useSyncExternalStore(subscribe, () => snapshot);
}

const dropPlanListeners = new Set<(token: number, noop: string[]) => void>();

/** The host's answer to a drag: which drop places change nothing. Returns the unsubscribe. */
export function onDropPlan(listener: (token: number, noop: string[]) => void): () => void {
  dropPlanListeners.add(listener);
  return () => dropPlanListeners.delete(listener);
}

export function setEditing(editing: Editing | null): void {
  update({ editing });
}

/** Picks a machine of remotes.conf and asks it, over ssh, for its sessions. */
export function setPalette(palette: PaletteMode | null): void {
  if (palette === "themes") {
    command({ command: "requestLists" });
  } else if (snapshot.palette === "themes") {
    // Leaving the theme picker drops the preview; a committed theme is already saved by then.
    command({ command: "previewTheme", name: null });
  }
  update({ palette });
}

/** agterm's palette launchers: the same key closes the palette it opened, another mode's key switches to that one. */
export function togglePalette(palette: PaletteMode): void {
  setPalette(snapshot.palette === palette ? null : palette);
}

export function closeWelcome(): void {
  update({ welcome: false });
}

const searches = new Map<string, SearchState>();

function sessionOf(key: string): string {
  return key.split(":")[0];
}

/** The selected session's search into the snapshot, after a change to it or to the selection. */
function publishSearch(state: AppState | null = snapshot.state): Partial<Snapshot> {
  return { search: state?.selectedSessionId ? searches.get(state.selectedSessionId) ?? null : null };
}

export function openSearch(key: string | null): void {
  if (key) {
    const existing = searches.get(sessionOf(key));
    const before = existing?.key ?? null;
    searches.set(sessionOf(key), { key, needle: existing?.key === key ? existing.needle : "" });
    update(publishSearch());
    reportSearch(before, key);
  }
}

// The host steps session search next/prev in the pane whose bar is open.
function reportSearch(before: string | null, after: string | null): void {
  if (before === after) {
    return;
  }
  if (before) {
    command({ command: "searchShown", key: before, shown: false });
  }
  if (after) {
    command({ command: "searchShown", key: after, shown: true });
  }
}

/** The pane key of the selected session's focused pane. */
export function focusedKey(): string | null {
  const state = snapshot.state;
  const session = state?.workspaces.flatMap((workspace) => workspace.sessions).find((s) => s.id === state.selectedSessionId);
  return session ? paneKey(session.id, session.focusedPane) : null;
}

export function setSearchNeedle(needle: string, key: string | null = snapshot.search?.key ?? null): void {
  const search = key ? searches.get(sessionOf(key)) : undefined;
  if (search) {
    searches.set(sessionOf(search.key), { ...search, needle });
    update(publishSearch());
  }
}

/** agterm's toggleSearch: Find again while the session's search bar is open closes it. */
export function toggleSearch(key: string | null): void {
  if (key && searches.has(sessionOf(key))) {
    closeSearch(sessionOf(key));
  } else {
    openSearch(key);
  }
}

/** Closes a session's search bar: the selected session's when none is named. */
export function closeSearch(sessionId: string | null = snapshot.state?.selectedSessionId ?? null): void {
  const search = sessionId ? searches.get(sessionId) : undefined;
  if (search) {
    clearSearch(search.key);
    searches.delete(sessionOf(search.key));
    update(publishSearch());
    reportSearch(search.key, null);
  }
}

/**
 * agterm's clearSearch on pane teardown: a bar whose session or pane went keeps no stuck search. A swap carries the bar
 * with its terminal to the other side; a promotion clears it.
 */
function pruneSearches(state: AppState): void {
  const sessions = new Map(state.workspaces.flatMap((workspace) => workspace.sessions).map((session) => [session.id, session]));
  for (const [sessionId, search] of [...searches]) {
    const session = sessions.get(sessionId);
    const pane = search.key.split(":")[1];
    if (!session || (pane === "right" && !session.hasSplit) || (pane === "scratch" && !session.scratchShown)) {
      searches.delete(sessionId);
      reportSearch(search.key, null);
    }
  }
}

function swapSearch(sessionId: string): void {
  const search = searches.get(sessionId);
  const pane = search?.key.split(":")[1];
  if (search && (pane === "left" || pane === "right")) {
    searches.set(sessionId, { ...search, key: `${sessionId}:${pane === "left" ? "right" : "left"}` });
    update(publishSearch());
  }
}

// The control API's session.search: open the bar, set a needle, step or close, answering with "N of M".
/** The search the host drives, answered as "count index", -1 for none: no query run yet, or no match chosen. */
async function searchFromHost(key: string, needle: string | null, step: "next" | "prev" | "close" | null): Promise<string> {
  if (step === "close") {
    if (searches.get(sessionOf(key))?.key === key) {
      closeSearch(sessionOf(key));
    } else {
      clearSearch(key);
    }
    return "-1 -1";
  }
  openSearch(key);
  if (needle !== null) {
    setSearchNeedle(needle, key);
  }
  const current = searches.get(sessionOf(key))?.needle ?? "";
  if (!current) {
    return "-1 -1";
  }
  const results = await searchTerminal(key, current, step ?? "incremental");
  return `${results.count} ${results.index}`;
}

/**
 * agterm's handleZoomTargetChange: entering the zoom, or moving it to another surface, closes the window's palette and
 * ends the search of the selected session and of the zoomed one, so no bar comes back after the zoom.
 */
function closeChromeOnZoom(before: AppState | null, after: AppState): void {
  const target = after.zoomedPane ? `${after.zoomedSessionId}:${after.zoomedPane}` : null;
  const previous = before?.zoomedPane ? `${before.zoomedSessionId}:${before.zoomedPane}` : null;
  if (target === null || target === previous) {
    return;
  }
  setPalette(null);
  closeSearch(after.selectedSessionId);
  if (after.zoomedSessionId !== after.selectedSessionId) {
    closeSearch(after.zoomedSessionId);
  }
}

/** agterm's handleDashboardOpenChange: a dashboard opening closes the window's palette and ends the selected session's search. */
function closeChromeOnDashboard(before: AppState | null, after: AppState): void {
  if (after.dashboard !== null && !before?.dashboard) {
    setPalette(null);
    closeSearch(after.selectedSessionId);
  }
}

export function startHostConnection(): void {
  onHostMessage((message) => {
    switch (message.type) {
      case "state":
        setLanguage(message.state.settings.uiLanguage);
        applyChrome(message.state);
        applyTerminalAppearance(message.state);
        setPageZoom(message.state.settings.htmlOverlayZoom);
        if (message.state.settingsPage || message.state.quickPage) {
          // Standalone settings and quick pages carry no sessions.
          update({ state: message.state });
          break;
        }

        labels.clear();
        pruneSearches(message.state);
        closeChromeOnZoom(snapshot.state, message.state);
        closeChromeOnDashboard(snapshot.state, message.state);
        update({ state: message.state, ...publishSearch(message.state) });
        break;
      case "lists":
        update({ lists: { themes: message.themes, fonts: message.fonts, sounds: message.sounds, inputSources: message.inputSources } });
        break;
      case "paste":
        pasteInto(paneKey(message.sessionId, message.pane), message.text, message.clipboard === true, message.selection === true);
        break;
      case "output":
        writeOutput(paneKey(message.sessionId, message.pane), message.data);
        break;
      case "restart":
        reattach(paneKey(message.sessionId, message.pane));
        break;
      case "swap":
        swapPanes(message.sessionId);
        swapSearch(message.sessionId);
        break;
      case "promote":
        closeSearch(message.sessionId);
        promoteSplit(message.sessionId);
        break;
      case "label":
        applyLabel(message.sessionId, message.name);
        break;
      case "welcome":
        update({ welcome: true });
        break;
      case "dropPlan":
        dropPlanListeners.forEach((listener) => listener(message.token, message.noop));
        break;
      case "windowActive":
        document.documentElement.classList.toggle("window-inactive", !message.value);
        break;
      case "pageInfo":
        deliverPageInfo(message.overlayId, message);
        break;
      case "pageGone":
        forgetPage(message.overlayId);
        break;
      case "forwardKey":
        handlePageKey(message);
        break;
      case "ctrlReleased":
        endSwitcherHold();
        break;
      case "switcherEscape":
        cancelSwitcher();
        break;
      case "terminal": {
        if (message.action === "commitComposition") {
          void commitComposition(paneKey(message.sessionId, message.pane)).then(() => send({ type: "reply", requestId: message.requestId, text: "" }));
          break;
        }
        if (message.action === "takeComposition") {
          void takeComposition(paneKey(message.sessionId, message.pane)).then((text) => send({ type: "reply", requestId: message.requestId, text }));
          break;
        }
        const text = terminalAction(paneKey(message.sessionId, message.pane), message.action, message.text);
        send(text === null ? { type: "reply", requestId: message.requestId, text: "", missing: true } : { type: "reply", requestId: message.requestId, text });
        break;
      }
      case "readSelection":
        {
          const text = readSelection(paneKey(message.sessionId, message.pane));
          send(text === null ? { type: "reply", requestId: message.requestId, missing: true } : { type: "reply", requestId: message.requestId, text });
        }
        break;
      case "readText":
        {
          const text = readText(paneKey(message.sessionId, message.pane), message.all, message.lines);
          send(text === null ? { type: "reply", requestId: message.requestId, missing: true } : { type: "reply", requestId: message.requestId, text });
        }
        break;
      case "search":
        searchFromHost(paneKey(message.sessionId, message.pane), message.needle, message.step).then((text) => send({ type: "reply", requestId: message.requestId, text }));
        break;
    }
  });
  send({ type: "ready" });
}
