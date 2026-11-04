import { useEffect, useMemo, useRef, useState } from "react";
import { actionAvailable, actions, actionTitle } from "./actions";
import { AgentStatus, AppState, command, StatusShape } from "./bridge";
import { fuzzyRank } from "./fuzzy";
import { bindingLabel, runCustom } from "./shortcuts";
import { Glyph } from "./StatusGlyph";
import { SearchIcon } from "./icons";
import { MiddleTruncated } from "./MiddleTruncated";
import { PaletteMode, sessionName, setPalette, subtitleDetail, uiActionsEnabled, useStore } from "./store";
import { currentLanguage, t, tr } from "./i18n";

interface Item {
  id: string;
  title: string;
  subtitle?: string;
  shortcut?: string;
  badge?: string;
  status?: AgentStatus;
  statusColor?: string | null;
  statusShape?: StatusShape | null;
  blink?: boolean;
  enabled: boolean;
  /** Picking it opens the next step of a picker instead of closing the palette. */
  run: () => void;
}

const placeholders: Record<PaletteMode, string> = {
  sessions: tr("Перейти к сессии…"),
  actions: tr("Выполнить действие…"),
  attention: tr("Сессии, ждущие внимания…"),
  commands: tr("Выполнить команду…"),
  themes: tr("Выбрать тему…"),
};

/** agterm's theme preview debounce. */
const themePreviewDelay = 70;

/** The theme slot the picker edits: the dark one while Windows is dark and the terminal follows it; null is no theme. */
function currentThemeName(state: AppState): string | null {
  const { settings } = state;
  return settings.followSystemAppearance && settings.systemDark ? (settings.darkTheme ?? settings.theme) : settings.theme;
}

/** agterm's theme row ids; the host takes an empty name for "default ghostty", no theme. */
const defaultThemeId = "theme:__default__";

function themeId(name: string | null): string {
  return name === null ? defaultThemeId : "theme:" + name;
}

function themeName(id: string): string {
  return id === defaultThemeId ? "" : id.slice("theme:".length);
}


function commandItems(state: AppState, badge: boolean): Item[] {
  return state.keymap.commands.map((custom) => ({
    id: "custom:" + custom.name,
    title: custom.name,
    shortcut: custom.shortcut || undefined,
    badge: badge ? "custom" : undefined,
    enabled: uiActionsEnabled(state) && state.selectedSessionId !== null,
    run: () => runCustom(custom.name),
  }));
}

/** agterm's "Move Session to …": one per workspace but the selected session's own, the session going to its end. */
function moveItems(state: AppState): Item[] {
  const owner = state.workspaces.find((workspace) => workspace.sessions.some((session) => session.id === state.selectedSessionId));
  const sessionId = state.selectedSessionId;
  if (!owner || !sessionId) {
    return [];
  }
  return state.workspaces
    .filter((workspace) => workspace.id !== owner.id)
    .map((workspace) => ({
      id: "move-" + workspace.id,
      title: t("Переместить сессию в {name}", { name: workspace.name }),
      enabled: true,
      run: () => command({ command: "moveSession", sessionId, workspaceId: workspace.id, index: workspace.sessions.length }),
    }));
}

function buildItems(mode: PaletteMode, state: AppState, themes: string[]): Item[] {
  const all = state.workspaces.flatMap((workspace) =>
    workspace.sessions.map((session) => ({ session, workspace })),
  );
  const sessionItem = ({ session, workspace }: (typeof all)[number]): Item => ({
    id: session.id,
    title: sessionName(session),
    subtitle: `${workspace.name} · ${subtitleDetail(session)}`,
    status: session.status !== "idle" ? session.status : undefined,
    statusColor: session.statusColor,
    statusShape: session.statusShape,
    blink: session.blink,
    enabled: true,
    run: () => command({ command: "selectSession", sessionId: session.id, reveal: true }),
  });

  switch (mode) {
    case "sessions":
      // Like agterm, the session palette follows the sidebar's flagged view and focus filter; attention does not.
      return all.filter(({ session }) => state.navigableSessionIds.includes(session.id)).map(sessionItem);
    case "attention":
      // agterm's attention list: every open window's, in the host's order (blocked, active, completed, newest first).
      return state.attention.map((entry) => ({
        id: entry.sessionId,
        title: entry.name,
        subtitle: entry.subtitle,
        status: entry.status,
        statusColor: entry.statusColor,
        statusShape: entry.statusShape,
        enabled: entry.enabled,
        run: () => setTimeout(() => command({ command: "selectAttentionEntry", windowId: entry.windowId, sessionId: entry.sessionId })),
      }));
    case "actions":
      // As in agterm: the actions worth listing now, an "Open Window" per closed window, a "Move Session to" per other
      // workspace, and the keymap commands, marked as custom.
      return [
        ...actions
          .filter((action) => !action.unlisted && (!action.visible || action.visible(state)))
          .map((action) => ({
            id: action.id,
            title: actionTitle(action, state),
            shortcut: bindingLabel(state.keymap.actions[action.id]),
            enabled: actionAvailable(action, state),
            run: () => action.run(state),
          })),
        ...state.windows
          .filter((window) => !window.open)
          .map((window) => ({
            id: "open-window-" + window.id,
            title: t("Открыть окно: {name}", { name: window.name }),
            enabled: true,
            run: () => command({ command: "selectWindow", windowId: window.id }),
          })),
        ...moveItems(state),
        ...commandItems(state, true),
      ];
    case "commands":
      return commandItems(state, false);
    case "themes": {
      // agterm's theme rows: "default ghostty" (sorted with the rest), left out while following Windows, whose pair
      // needs two named themes; the theme in use badged.
      const current = currentThemeName(state);
      const names: (string | null)[] = state.settings.followSystemAppearance ? themes : [null, ...themes];
      return names.map((name) => ({
        id: themeId(name),
        title: name ?? "default ghostty",
        badge: name === current ? "current" : undefined,
        enabled: true,
        run: () => command({ command: "previewTheme", name: name ?? "", commit: true }),
      }));
    }
  }
}

/**
 * agterm's palette keys: the arrows, or Ctrl+J / Ctrl+K with no other modifier, by physical key so the layout does not
 * matter; the selection stops at either end.
 */
export function paletteStep(event: React.KeyboardEvent): number {
  const ctrlOnly = event.ctrlKey && !event.shiftKey && !event.altKey && !event.metaKey;
  if (event.key === "ArrowDown" || (ctrlOnly && event.code === "KeyJ")) {
    return 1;
  }
  return event.key === "ArrowUp" || (ctrlOnly && event.code === "KeyK") ? -1 : 0;
}

export function Palette({ mode, state }: { mode: PaletteMode; state: AppState }) {
  const [query, setQuery] = useState("");
  const [selection, setSelection] = useState(0);
  const listRef = useRef<HTMLDivElement>(null);
  const { lists } = useStore();
  const themes = lists?.themes ?? [];
  const items = useMemo(() => buildItems(mode, state, themes), [mode, state, themes]);

  const filtered = useMemo(() => {
    const trimmed = query.trim();
    if (!trimmed) {
      // As agterm's, an empty query lists a built-in palette A→Z and leaves attention in its own order.
      return mode === "attention" ? items : [...items].sort((a, b) => a.title.localeCompare(b.title, undefined, { sensitivity: "accent" }));
    }
    return fuzzyRank(trimmed, items, (item) => [item.title, item.subtitle ?? ""]);
  }, [items, query, mode]);

  // As in agterm, switching the mode starts from the first row and keeps what was typed.
  useEffect(() => {
    setSelection(0);
  }, [mode]);

  useEffect(() => {
    setSelection((current) => Math.min(current, Math.max(0, filtered.length - 1)));
  }, [filtered.length]);

  useEffect(() => {
    listRef.current?.querySelector(".palette-row.selected")?.scrollIntoView({ block: "nearest" });
  }, [selection]);

  // The theme picker opens on the theme in use each time it is entered, finding its row in the list as shown, and
  // previews whichever row is selected, as agterm's does.
  const [positioned, setPositioned] = useState(false);
  useEffect(() => {
    if (mode !== "themes") {
      setPositioned(false);
    } else if (themes.length > 0 && !positioned) {
      setSelection(Math.max(0, filtered.findIndex((item) => item.id === themeId(currentThemeName(state)))));
      setPositioned(true);
    }
  }, [mode, themes, positioned]);

  // agterm debounces the preview by 70 ms, so holding an arrow key does not repaint every theme on the way; leaving
  // the picker drops a pending one and reverts at once.
  const previewed = mode === "themes" && positioned ? filtered[selection]?.id : undefined;
  useEffect(() => {
    if (!previewed) {
      return;
    }
    const timer = window.setTimeout(() => command({ command: "previewTheme", name: themeName(previewed) }), themePreviewDelay);
    return () => window.clearTimeout(timer);
  }, [previewed]);

  const runItem = (item: Item | undefined) => {
    if (!item || !item.enabled) {
      return;
    }
    setPalette(null);
    item.run();
  };

  const onKeyDown = (event: React.KeyboardEvent<HTMLInputElement>) => {
    const step = paletteStep(event);
    if (step !== 0) {
      event.preventDefault();
      const count = filtered.length;
      if (count > 0) {
        setSelection((current) => Math.max(0, Math.min(current + step, count - 1)));
      }
    } else if (event.key === "Tab") {
      // The input is the palette's only field, as in agterm: Tab must not walk the focus out under the backdrop.
      event.preventDefault();
    } else if (event.key === "Enter") {
      event.preventDefault();
      runItem(filtered[selection]);
    } else if (event.key === "Escape") {
      event.preventDefault();
      setPalette(null);
    }
  };

  return (
    <div className="palette-backdrop" onMouseDown={() => setPalette(null)}>
      <div
        className="palette"
        onMouseDown={(event) => {
          event.stopPropagation();
          // Clicking a row must not take the focus away from the input.
          if (!(event.target instanceof HTMLInputElement)) {
            event.preventDefault();
          }
        }}
      >
        <div className="palette-search">
          <SearchIcon />
          <input
            className="palette-input"
            autoFocus
            spellCheck={false}
            placeholder={t(placeholders[mode])}
            value={query}
            onChange={(event) => {
              setQuery(event.target.value);
              setSelection(0);
            }}
            onKeyDown={onKeyDown}
          />
        </div>
        <div className="palette-list" ref={listRef}>
          {filtered.length === 0 && <div className="palette-empty">{t("Ничего не найдено")}</div>}
          {filtered.map((item, index) => (
            <div
              key={item.id}
              className={"palette-row" + (index === selection ? " selected" : "") + (item.enabled ? "" : " disabled")}
              onClick={() => runItem(item)}
            >
              <div className="palette-text">
                <div className="palette-title">{item.title}</div>
                {item.subtitle && (
                  <div className="palette-subtitle">
                    <MiddleTruncated text={item.subtitle} />
                  </div>
                )}
              </div>
              {item.badge && <span className="palette-badge">{item.badge}</span>}
              {item.shortcut && <span className="palette-shortcut">{item.shortcut}</span>}
              {item.status && item.status !== "idle" && <Glyph status={item.status} settings={state.settings} blink={item.blink} color={item.statusColor} shape={item.statusShape} />}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
