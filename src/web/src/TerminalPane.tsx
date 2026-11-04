import { CSSProperties, useEffect, useLayoutEffect, useRef, useState } from "react";
import { sessionAskHoldsKeys } from "./actions";
import { AskDialog } from "./AskDialog";
import { AppState, command, OverlayView, Pane, SessionView } from "./bridge";
import { Dashboard, dashboardRects, LeadCoverContent } from "./Dashboard";
import { scheduleCut } from "./holes";
import { Hud } from "./Hud";
import { PageOverlay } from "./PageOverlay";
import { SearchBar } from "./SearchBar";
import { bindingLabel } from "./shortcuts";
import { SearchState } from "./store";
import { t } from "./i18n";
import { reportOverlayGeometry, shownOverlay } from "./overlays";
import {
  activate,
  attach,
  openTerminal,
  deactivate,
  disposeTerminal,
  ensureTerminal,
  fitActive,
  fitAllIn,
  setBackground,
  setSessionBackground,
  focusTerminal as focusTerminalByKey,
  knownTerminals,
  onPaneFocus,
  paneKey,
  setFontSize,
  setRemoteGates,
} from "./terminals";

interface Props {
  state: AppState;
  search: SearchState | null;
  focusTerminal: boolean;
  /** agshell's own popups are open; native web pages step aside so they can be seen. */
  covered: boolean;
}

/** Fractions of the terminal area. */
interface Rect {
  left: number;
  top: number;
  width: number;
  height: number;
  /** agterm's point-sized axes: a fixed size, centred and capped at 80% of the session, in place of the share. */
  widthPx?: number;
  heightPx?: number;
}

/** What a floating page adds around its content: its frame on every side, and its strip with the rule under it, as agterm adds its 29pt strip. */
const pageFrame = 2;
const pageStrip = 29;

const full: Rect = { left: 0, top: 0, width: 1, height: 1 };
const gap = 3;

function panesOf(session: SessionView): Pane[] {
  const panes: Pane[] = ["left"];
  if (session.hasSplit) {
    panes.push("right");
  }
  if (session.scratchShown || session.scratchRunning) {
    panes.push("scratch");
  }
  if (session.overlay?.kind === "program") {
    panes.push("overlay");
  }
  if (session.paneOverlays.left?.kind === "program") {
    panes.push("overlay-left");
  }
  if (session.paneOverlays.right?.kind === "program") {
    panes.push("overlay-right");
  }
  return panes;
}

const paneOverlaySlot: Record<string, Pane> = { left: "overlay-left", right: "overlay-right" };

/** A terminal's font size: each pane has its own; an overlay starts with the session's, as agterm's. */
function paneFontSize(session: SessionView, pane: Pane): number {
  if (pane === "right") {
    return session.splitFontSize;
  }
  if (pane.startsWith("overlay")) {
    return session.sessionFontSize;
  }
  return pane === "scratch" ? session.scratchFontSize : session.fontSize;
}

/**
 * The pane keystrokes go to: a session overlay program covers everything, then a program over the focused pane,
 * otherwise the focused pane itself.
 */
function inputPane(session: SessionView): Pane {
  if (shownOverlay(session.overlay)?.kind === "program") {
    return "overlay";
  }
  const focused = session.focusedPane;
  if ((focused === "left" || focused === "right") && shownOverlay(session.paneOverlays[focused])?.kind === "program") {
    return paneOverlaySlot[focused];
  }
  return focused;
}

/** agterm's washColorHex: a pane's own colour background, else the theme's. */
function washColor(session: SessionView, pane: Pane): string {
  const background = pane === "left" || pane === "right" || pane === "scratch" ? (session.paneBackgrounds[pane] ?? session.background) : null;
  return background?.kind === "color" && background.color ? background.color : "var(--terminal-bg)";
}

/** agterm's backdrop wash: a floating program or page overlay over the session mutes what stays visible around it. */
function backdropWash(session: SessionView, layout: Map<Pane, Rect>): boolean {
  const overlay = shownOverlay(session.overlay);
  return overlay !== null && overlay.kind !== "hud" && overlay.sizePercent !== null && layout.has("overlay");
}

/** Where each visible pane of the selected session goes; a pane that is not listed is hidden. */
function layoutOf(session: SessionView, ratio: number): Map<Pane, Rect> {
  const layout = new Map<Pane, Rect>();
  const overlay = shownOverlay(session.overlay)?.kind === "hud" ? null : shownOverlay(session.overlay);
  if (overlay) {
    // A floating overlay sits centred over the session, which stays visible around it; a full one replaces it.
    const size = (overlay.sizePercent ?? 100) / 100;
    const floats = overlay.sizePercent !== null;
    const frame = overlay.page ? pageFrame : 0;
    const strip = overlay.page && !overlay.page.chromeless ? pageStrip : 0;
    layout.set("overlay", {
      left: (1 - size) / 2,
      top: (1 - size) / 2,
      width: size,
      height: size,
      widthPx: floats && overlay.widthPoints !== null ? overlay.widthPoints + frame : undefined,
      heightPx: floats && overlay.heightPoints !== null ? overlay.heightPoints + frame + strip : undefined,
    });
    if (overlay.sizePercent === null) {
      return layout;
    }
  }
  if (session.scratchShown) {
    layout.set("scratch", full);
  } else if (session.splitShown) {
    if (session.splitAxis === "horizontal") {
      layout.set("left", { left: 0, top: 0, width: 1, height: ratio });
      layout.set("right", { left: 0, top: ratio, width: 1, height: 1 - ratio });
    } else {
      layout.set("left", { left: 0, top: 0, width: ratio, height: 1 });
      layout.set("right", { left: ratio, top: 0, width: 1 - ratio, height: 1 });
    }
  } else {
    // agterm: a hidden split shows the pane that has focus, maximized.
    layout.set(session.hasSplit && session.focusedPane === "right" ? "right" : "left", full);
  }
  // A pane's own overlay covers exactly that pane, wherever the pane is on screen.
  for (const pane of ["left", "right"] as const) {
    const rect = layout.get(pane);
    const paneOverlay = shownOverlay(session.paneOverlays[pane]);
    if (rect && paneOverlay && paneOverlay.kind !== "hud") {
      layout.set(paneOverlaySlot[pane], rect);
    }
  }
  return layout;
}

// Inner edges keep half a divider's width of space on each side.
function placement(rect: Rect): Partial<CSSProperties> {
  const right = rect.left + rect.width;
  const bottom = rect.top + rect.height;
  const across = rect.widthPx !== undefined ? `calc((100% - min(${rect.widthPx}px, 80%)) / 2)` : null;
  const down = rect.heightPx !== undefined ? `calc((100% - min(${rect.heightPx}px, 80%)) / 2)` : null;
  return {
    left: across ?? `calc(${rect.left * 100}% + ${rect.left > 0 ? gap : 0}px)`,
    top: down ?? `calc(${rect.top * 100}% + ${rect.top > 0 ? gap : 0}px)`,
    right: across ?? `calc(${(1 - right) * 100}% + ${right < 1 ? gap : 0}px)`,
    bottom: down ?? `calc(${(1 - bottom) * 100}% + ${bottom < 1 ? gap : 0}px)`,
    width: "auto",
    height: "auto",
  };
}

export function TerminalPane({ state, search, focusTerminal, covered }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const visibleRef = useRef<Set<string>>(new Set());
  const stateRef = useRef(state);
  stateRef.current = state;
  const [dragRatio, setDragRatio] = useState<number | null>(null);
  const [askFocus, setAskFocus] = useState(0);

  const sessions = state.workspaces.flatMap((workspace) => workspace.sessions);
  const selected = sessions.find((session) => session.id === state.selectedSessionId) ?? null;
  const ratio = dragRatio ?? selected?.splitRatio ?? 0.5;
  const dashboard = state.dashboard;
  const dashboardLayout = dashboard ? dashboardRects(dashboard, sessions) : null;
  // agterm's zoom layer: the zoom target alone fills the window, even a background session's surface, a hidden split or
  // scratch or a pane under an overlay.
  const zoomedSession = state.zoomedPane ? (sessions.find((session) => session.id === state.zoomedSessionId) ?? null) : null;
  const shownSession = zoomedSession ?? selected;
  const layout = zoomedSession && state.zoomedPane ? new Map<Pane, Rect>([[state.zoomedPane, full]]) : selected && !dashboard ? layoutOf(selected, ratio) : new Map<Pane, Rect>();
  // agterm's surface zoom: the zoomed terminal takes the keyboard, without moving the split's focus.
  const inputKey = zoomedSession && state.zoomedPane ? paneKey(zoomedSession.id, state.zoomedPane) : selected ? paneKey(selected.id, inputPane(selected)) : null;
  const layoutKey =
    sessions
      .map((s) =>
        [s.id, state.zoomedPane, s.fontSize, s.splitFontSize, s.scratchFontSize, s.sessionFontSize, s.hasSplit, s.splitShown, s.splitAxis, s.focusedPane, s.scratchShown, s.scratchRunning, s.overlay?.id, s.overlay?.sizePercent, s.overlay?.widthPoints, s.overlay?.heightPoints, s.overlay?.backgroundColor, s.overlay?.hidden, s.paneOverlays.left?.id, s.paneOverlays.left?.hidden, s.paneOverlays.right?.id, s.paneOverlays.right?.hidden].join(
          ":",
        ),
      )
      .join(",") + `|${state.pendingSessionIds.join(",")}|${state.selectedSessionId}|${state.zoomedSessionId}|${ratio}|${dashboard ? JSON.stringify(dashboard.cells) + dashboard.fontSize : ""}`;

  useEffect(() => {
    onPaneFocus((sessionId, pane) => {
      const session = stateRef.current.workspaces.flatMap((w) => w.sessions).find((s) => s.id === sessionId);
      if (session && !stateRef.current.zoomed && pane !== "scratch" && session.splitShown && session.focusedPane !== pane) {
        command({ command: "focusPane", sessionId, pane });
      }
    });
  }, []);

  useLayoutEffect(() => {
    const container = containerRef.current!;
    const wanted = new Set(sessions.flatMap((session) => panesOf(session).map((pane) => paneKey(session.id, pane))));
    // A session closed with undo still in reach keeps its terminals, so it comes back with everything it printed.
    const pending = new Set(state.pendingSessionIds);
    for (const key of knownTerminals()) {
      if (!wanted.has(key) && !pending.has(key.split(":")[0])) {
        disposeTerminal(key);
      }
    }

    const visible = new Set<string>();
    const starting: string[] = [];
    for (const session of sessions) {
      for (const pane of panesOf(session)) {
        const key = paneKey(session.id, pane);
        const element = ensureTerminal(session.id, pane);
        // In the dashboard the cells may use a smaller font of their own; it goes back when the dashboard closes.
        const dashboardRect = dashboardLayout?.get(key);
        setFontSize(key, dashboardRect && dashboard?.fontSize ? dashboard.fontSize : paneFontSize(session, pane));
        if (element.parentElement !== container) {
          container.appendChild(element);
        }
        const rect = dashboardLayout ? undefined : session.id === shownSession?.id ? layout.get(pane) : undefined;
        Object.assign(element.style, { clipPath: "" }, dashboardRect ?? placement(rect ?? full));
        element.classList.toggle("active", rect !== undefined || dashboardRect !== undefined);
        element.classList.toggle("scratch", pane === "scratch");
        // agterm's paneDim: the split pane without focus, or the overlay over it, fades its text into its own background;
        // a floating overlay's backdrop wash already covers the panes, so they are not muted twice.
        const paneOf = pane === "overlay-left" ? "left" : pane === "overlay-right" ? "right" : pane;
        const ownOverlay = pane === "overlay-left" ? session.paneOverlays.left : pane === "overlay-right" ? session.paneOverlays.right : null;
        element.classList.toggle(
          "muted",
          !state.zoomed && session.splitShown && (paneOf === "left" || paneOf === "right") && paneOf !== session.focusedPane && rect !== undefined && !backdropWash(session, layout),
        );
        element.style.setProperty("--mute-color", ownOverlay ? (ownOverlay.backgroundColor ?? "var(--terminal-bg)") : washColor(session, paneOf));
        element.classList.toggle("overlay", pane === "overlay" || pane === "overlay-left" || pane === "overlay-right");
        element.classList.toggle("floating", pane === "overlay" && session.overlay?.sizePercent != null);
        const overlayView = pane === "overlay" ? session.overlay : pane === "overlay-left" ? session.paneOverlays.left : pane === "overlay-right" ? session.paneOverlays.right : null;
        setBackground(key, overlayView?.backgroundColor ?? null);
        if (rect || dashboardRect) {
          visible.add(key);
        }
        if (pane === "overlay" && rect && !dashboardRect && session.overlay?.sizePercent != null && session.overlay.kind === "program") {
          const overlayId = session.overlay.id;
          requestAnimationFrame(() => reportOverlayGeometry(overlayId, element.offsetWidth, element.offsetHeight));
        }
        openTerminal(key);
        starting.push(key);
      }
    }

    for (const key of visibleRef.current) {
      if (!visible.has(key)) {
        deactivate(key);
      }
    }
    for (const key of visible) {
      activate(key, false);
    }
    visibleRef.current = visible;
    // A shown terminal gets its renderer before its shell starts, so the shell starts at the size it is drawn at.
    for (const key of starting) {
      attach(key);
    }
  }, [layoutKey]);

  // agterm's cover and reconnect wait: a key there takes the lead or retries, latched; a covered pane keeps the leader's size.
  useEffect(() => {
    const covered = new Map<string, [number, number] | null>();
    const waiting = new Set<string>();
    for (const session of sessions) {
      for (const pane of session.coveredPanes) {
        covered.set(paneKey(session.id, pane), session.leaderSizes[pane] ?? null);
      }
      for (const pane of ["left", "right"] as const) {
        if (session.remoteWaits[pane]) {
          waiting.add(paneKey(session.id, pane));
        }
      }
    }
    setRemoteGates(covered, waiting);
  });

  // agterm's session background: a pane's own, else the session's; overlays keep the color they were opened with.
  useEffect(() => {
    for (const session of sessions) {
      for (const pane of ["left", "right", "scratch"] as const) {
        setSessionBackground(paneKey(session.id, pane), session.paneBackgrounds[pane] ?? session.background);
      }
    }
  });

  // The layers under the pages move and come and go with every layout; their holes follow.
  useLayoutEffect(() => {
    scheduleCut();
  });

  useEffect(() => {
    if (dashboard) {
      // The dashboard is view-only: no terminal takes keys while it is open.
      (document.activeElement as HTMLElement | null)?.blur();
    } else if (focusTerminal && inputKey && visibleRef.current.has(inputKey)) {
      focusTerminalByKey(inputKey);
    }
  }, [focusTerminal, inputKey, layoutKey, dashboard !== null]);

  useEffect(() => {
    let scheduled = false;
    const observer = new ResizeObserver(() => {
      if (scheduled) {
        return;
      }
      scheduled = true;
      requestAnimationFrame(() => {
        scheduled = false;
        fitAllIn(containerRef.current!);
        scheduleCut();
      });
    });
    observer.observe(containerRef.current!);
    return () => observer.disconnect();
  }, []);

  const startDrag = (event: React.PointerEvent<HTMLDivElement>) => {
    if (!selected) {
      return;
    }
    const sessionId = selected.id;
    const horizontal = selected.splitAxis === "horizontal";
    const area = containerRef.current!.getBoundingClientRect();
    const handle = event.currentTarget;
    handle.setPointerCapture(event.pointerId);
    let latest = ratio;
    const onMove = (move: PointerEvent) => {
      const position = horizontal ? (move.clientY - area.top) / area.height : (move.clientX - area.left) / area.width;
      latest = Math.min(0.95, Math.max(0.05, position));
      setDragRatio(latest);
    };
    const onUp = () => {
      handle.removeEventListener("pointermove", onMove);
      handle.removeEventListener("pointerup", onUp);
      command({ command: "setSplitRatio", sessionId, ratio: latest });
      setDragRatio(null);
    };
    handle.addEventListener("pointermove", onMove);
    handle.addEventListener("pointerup", onUp);
  };

  // A session-wide question covers the session, the scratch included; a pane's question hides while the scratch is up.
  const askRect = (session: SessionView): Rect | undefined => {
    const pane = session.ask?.pane;
    return pane ? (session.scratchShown ? undefined : layout.get(pane)) : full;
  };

  // agterm draws a session's ask over its page; the native page goes under the interface while the ask is up.
  const askOver = (session: SessionView, slot: Pane): boolean => {
    const ask = session.id === selected?.id ? session.ask : null;
    return ask !== null && (slot === "overlay" || ask.pane === null || slot === `overlay-${ask.pane}`);
  };

  const shownCover = shownOverlay(selected?.overlay);
  const coveredByOverlay = shownCover !== null && shownCover.kind !== "hud" && shownCover.sizePercent === null;
  const showDivider = selected !== null && selected.splitShown && !selected.scratchShown && !state.zoomed && !coveredByOverlay && !dashboard;
  const dividerStyle: CSSProperties =
    selected?.splitAxis === "horizontal"
      ? { left: 0, right: 0, top: `calc(${ratio * 100}% - ${gap}px)`, height: gap * 2 }
      : { top: 0, bottom: 0, left: `calc(${ratio * 100}% - ${gap}px)`, width: gap * 2 };

  return (
    <main className="terminal-pane">
      {/* Terminals are added to the stack by hand, so React-owned elements live in a layer of their own. */}
      {/* agterm's dashboard backdrop: opaque in the terminal's color, the window's translucency dropped. */}
      {dashboard && <div className="dashboard-backdrop" />}
      <div ref={containerRef} className={"terminal-stack" + (dashboard ? " dashboard" : "")} />
      {showDivider && (
        <div className="split-layer">
          <div
            className={"split-divider " + selected.splitAxis}
            style={dividerStyle}
            onPointerDown={startDrag}
            onDoubleClick={() => command({ command: "setSplitRatio", sessionId: selected.id, ratio: 0.5 })}
          />
        </div>
      )}
      {/* agterm's empty detail: a grey line, no buttons. */}
      {!selected && !dashboard && (
        <div className="empty-state">
          <p>{t("Сессия не выбрана")}</p>
        </div>
      )}
      {/* agterm's zoom layer keeps the cover over a zoomed pane, of whichever session it is. */}
      {shownSession &&
        !dashboard &&
        (state.zoomed || !shownSession.scratchShown) &&
        shownSession.coveredPanes.map((pane) => {
          const rect = layout.get(pane);
          // agterm's cover sits on the terminal, under the pane's own overlay, and steps aside while one is up.
          return (
            rect &&
            !shownOverlay(shownSession.paneOverlays[pane as "left" | "right"]) && (
              <div
                key={pane}
                className="lead-cover"
                style={placement(rect)}
                onMouseDown={(event) => {
                  // As agterm's cover: a click only focuses the pane; the first key there takes the lead.
                  event.preventDefault();
                  command({ command: "focusPane", sessionId: shownSession.id, pane });
                  focusTerminalByKey(paneKey(shownSession.id, pane));
                }}
              >
                <LeadCoverContent session={shownSession} pane={pane} />
              </div>
            )
          );
        })}
      {/* agterm's reconnect note: the line in the terminal says it waits; under each waiting pane, what ssh said last. */}
      {shownSession?.remoteHost &&
        (state.zoomed || !shownSession.scratchShown) &&
        !dashboard &&
        (["left", "right"] as const).map((pane) => {
          const wait = shownSession.remoteWaits[pane];
          const rect = layout.get(pane);
          return (
            wait?.state === "disconnected" &&
            wait.reason &&
            rect && (
              <div key={"reconnect-" + pane} className="remote-note" style={placement(rect)}>
                <span>{"ssh: " + wait.reason}</span>
              </div>
            )
          );
        })}
      {/* agterm's ask anchor: where the window's GUI question anchored to a pane centres and dims; App measures it. */}
      {state.ask?.pane && state.ask.sessionId === selected?.id && !state.zoomed && !dashboard && layout.get(state.ask.pane) && (
        <div className="ask-anchor" style={placement(layout.get(state.ask.pane)!)} />
      )}
      {selected?.ask && !state.zoomed && !dashboard && askRect(selected) && (
        <div
          className="ask-layer"
          style={placement(askRect(selected)!)}
          // agterm's deferMouseToAsk: a click anywhere on the question's area selects its pane, and one beside the dialog
          // gives the dialog the keys rather than the program under it.
          onMouseDownCapture={() => {
            if (selected.ask?.pane && selected.focusedPane !== selected.ask.pane) {
              command({ command: "focusPane", sessionId: selected.id, pane: selected.ask.pane });
            }
          }}
          onMouseDown={(event) => {
            event.preventDefault();
            setAskFocus((current) => current + 1);
          }}
        >
          <AskDialog key={selected.ask.id} ask={selected.ask} active={!covered && sessionAskHoldsKeys(state, selected)} focusRequest={askFocus} />
        </div>
      )}
      {/* agterm's backdropWash: the session around a floating overlay, each region in its own background, faded as one. */}
      {selected && !dashboard && !state.zoomed && backdropWash(selected, layout) && (
        <div className="backdrop-wash">
          <div
            style={{
              position: "absolute",
              inset: 0,
              background: selected.scratchShown ? washColor(selected, "scratch") : selected.background?.kind === "color" && selected.background.color ? selected.background.color : "var(--terminal-bg)",
            }}
          />
          <div className="backdrop-wash-panes">
            {(selected.scratchShown ? [] : (["left", "right"] as const)).map((pane) => {
              const rect = layout.get(pane);
              const paneOverlay = shownOverlay(selected.paneOverlays[pane]);
              return rect ? <div key={pane} style={{ position: "absolute", ...placement(rect), background: paneOverlay?.backgroundColor ?? washColor(selected, pane) }} /> : null;
            })}
          </div>
        </div>
      )}
      {sessions.flatMap((session) => {
        const isSelected = session.id === selected?.id;
        const sessionLayout = isSelected ? layout : new Map<Pane, Rect>();
        const slots: [Pane, OverlayView | null][] = [
          ["overlay", session.overlay],
          ["overlay-left", session.paneOverlays.left],
          ["overlay-right", session.paneOverlays.right],
        ];
        return slots.map(([slot, overlay]) => {
          if (!overlay?.page) {
            return null;
          }
          const rect = sessionLayout.get(slot);
          return (
            <div key={overlay.id} className="page-layer">
              <PageOverlay
                sessionId={session.id}
                slot={slot}
                overlay={overlay}
                page={overlay.page}
                style={placement(rect ?? full)}
                visible={rect !== undefined}
                floating={slot === "overlay" && overlay.sizePercent !== null}
                covered={covered || dashboard !== null || askOver(session, slot)}
                active={
                  isSelected &&
                  (slot === "overlay" || (slot === "overlay-left" && session.focusedPane === "left") || (slot === "overlay-right" && session.focusedPane === "right"))
                }
              />
            </div>
          );
        });
      })}
      {/* agterm's HUD: the session's one panel, over the pane it names or the session; agterm's hudCanMount keeps a pane's
          panel off screen while its pane is not laid out (the fallback to the session is decided when it opens). */}
      {!state.zoomed &&
        selected?.overlay?.hud &&
        (selected.overlay.hudPane ? (
          layout.get(selected.overlay.hudPane) && (
          <div key={selected.overlay.id} className="hud-area">
            <div className="hud-pane" style={placement(layout.get(selected.overlay.hudPane)!)}>
              {selected.overlay.paint && (
                <Hud
                  overlay={selected.overlay}
                  hud={selected.overlay.hud}
                  paint={selected.overlay.paint}
                  fontSize={selected.overlay.hudPane === "right" ? selected.splitFontSize : selected.fontSize}
                  sessionId={selected.id}
                  pane={selected.overlay.hudPane === "right" ? "right" : "left"}
                />
              )}
            </div>
          </div>
          )
        ) : (
          selected.overlay.paint && (
            <div key={selected.overlay.id} className="hud-area">
              <Hud overlay={selected.overlay} hud={selected.overlay.hud} paint={selected.overlay.paint} fontSize={selected.fontSize} sessionId={selected.id} pane="left" />
            </div>
          )
        ))}
      {/* agterm: the session's bar stays on the pane it was opened in; a terminal zoom hides it. */}
      {search && !state.zoomed && <SearchBar key={search.key} search={search} />}
      {dashboard && <Dashboard state={state} dashboard={dashboard} sessions={sessions} covered={covered} />}
      {state.pendingClose && (
        <div className="undo-bar">
          <span>{state.pendingClose.title}</span>
          <button type="button" className="text-button" onClick={() => command({ command: "undoClose", closeId: state.pendingClose!.id })}>
            {t("Отменить")} <kbd>{bindingLabel(state.keymap.actions["undo_close"]) ?? "Ctrl+Shift+Z"}</kbd>
          </button>
        </div>
      )}
    </main>
  );
}
