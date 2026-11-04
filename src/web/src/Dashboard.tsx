import { CSSProperties, useEffect, useLayoutEffect, useReducer, useRef, useState } from "react";
import { AppState, command, DashboardView, OverlayView, Pane, SessionView } from "./bridge";
import { statusFill, textOn } from "./appearance";
import { DocRichTextIcon, PaneLeadIcon, TerminalIcon } from "./icons";
import { MiddleTruncated } from "./MiddleTruncated";
import { identityOf, onPageInfo, pageInfo } from "./pages";
import { SessionName } from "./store";
import { paneKey } from "./terminals";
import { t } from "./i18n";
import { handleAppShortcut } from "./shortcuts";

/** agterm's gridSpacing: between the cells and around them. */
const spacing = 12;

/** A cell's box in the grid, its fractions of the free width and height plus the gaps before it. */
function cellBox(index: number, dashboard: DashboardView): { left: string; top: string; width: string; height: string } {
  const col = index % dashboard.cols;
  const row = Math.floor(index / dashboard.cols);
  const width = `(100% - ${(dashboard.cols + 1) * spacing}px) / ${dashboard.cols}`;
  const height = `(100% - ${(dashboard.rows + 1) * spacing}px) / ${dashboard.rows}`;
  return {
    left: `calc(${spacing + col * spacing}px + ${col} * ${width})`,
    top: `calc(${spacing + row * spacing}px + ${row} * ${height})`,
    width: `calc(${width})`,
    height: `calc(${height})`,
  };
}

/**
 * Where each shown pane's terminal goes, keyed by pane; covered panes show a label instead. The terminal fills its cell
 * as a Ghostty surface does, its text kept off the edges by the window padding, and the cell's rounding clips it.
 */
export function dashboardRects(dashboard: DashboardView, sessions: SessionView[]): Map<string, CSSProperties> {
  const rects = new Map<string, CSSProperties>();
  dashboard.cells.forEach((cell, index) => {
    if (coverOf(cell, sessions)) {
      return;
    }
    const box = cellBox(index, dashboard);
    const pad = (side: string) => `var(--terminal-padding-${side}, 0px)`;
    rects.set(paneKey(cell.sessionId, cell.pane), {
      left: `calc(${box.left} + ${pad("left")})`,
      top: `calc(${box.top} + ${pad("top")})`,
      width: `calc(${box.width} - ${pad("left")} - ${pad("right")})`,
      height: `calc(${box.height} - ${pad("top")} - ${pad("bottom")})`,
      right: "auto",
      bottom: "auto",
      clipPath: `inset(calc(-1 * ${pad("top")}) calc(-1 * ${pad("right")}) calc(-1 * ${pad("bottom")}) calc(-1 * ${pad("left")}) round 6px)`,
    });
  });
  return rects;
}

/** The overlay the host named as the cell's cover (agterm's dashboardCover), as the session shows it now. */
function coverOf(cell: { sessionId: string; coverId: string | null }, sessions: SessionView[]): OverlayView | null {
  const session = sessions.find((s) => s.id === cell.sessionId);
  if (!session || !cell.coverId) {
    return null;
  }
  return [session.overlay, session.paneOverlays.left, session.paneOverlays.right].find((overlay) => overlay?.id === cell.coverId) ?? null;
}

/**
 * agterm's DashboardOverlayCover: what stands in for a pane under a page or program, opaque. The source line is the
 * app's own, cut in the middle; the page's own title stays a dimmed line apart, cut at its end.
 */
function Cover({ overlay }: { overlay: OverlayView }) {
  const info = overlay.page ? pageInfo(overlay.id) : undefined;
  const source = overlay.page ? identityOf(overlay.page, info) : overlay.command;
  return (
    <div className="dashboard-cover overlay">
      <div className="dashboard-cover-icon">{overlay.page ? <DocRichTextIcon /> : <TerminalIcon />}</div>
      <div className="dashboard-cover-kind">{overlay.page ? t("HTML overlay") : t("Overlay с программой")}</div>
      {source && (
        <div className="dashboard-cover-source">
          <MiddleTruncated text={source} />
        </div>
      )}
      {info?.title && <div className="dashboard-cover-title">{info.title}</div>}
    </div>
  );
}

/** agterm's pane marker: which pane of a split the cell hosts, on the split's axis; nothing without a split. */
function paneIndicator(session: SessionView, pane: Pane): string {
  if (pane === "right") {
    return session.splitAxis === "horizontal" ? " ▼" : " ▶";
  }
  if (!session.hasSplit) {
    return "";
  }
  return session.splitAxis === "horizontal" ? " ▲" : " ◀";
}

/**
 * agterm's DashboardCaptionPill on the cell's bottom-right frame line. Idle, the theme's selection colors, the name
 * dimmed on a cell that is not highlighted; with a status, the status color and black or white text by its brightness.
 * A blink keeps the capsule opaque and pulses a wash of the color opposite to the text.
 */
function Caption({ session, pane, highlighted, state }: { session: SessionView; pane: Pane; highlighted: boolean; state: AppState }) {
  const ref = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState<number | undefined>(undefined);
  useLayoutEffect(() => {
    const element = ref.current;
    if (!element) {
      return;
    }
    const measure = () => setWidth(Math.max(0, element.clientWidth - 12));
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  const name = SessionName({ session }) + paneIndicator(session, pane);
  const status = session.status !== "idle" ? session.status : null;
  const fill = status ? statusFill(state, status, session.statusColor) : null;
  const text = fill ? textOn(fill) : null;
  const style = fill && text ? ({ "--chip-fill": fill, "--chip-text": text, "--chip-wash": text === "#000000" ? "#ffffff" : "#000000" } as CSSProperties) : undefined;
  return (
    <div ref={ref} className="dashboard-caption">
      <span className={"dashboard-chip" + (status ? " status" : "") + (status && session.blink ? " blink" : "")} style={style}>
        <span className={status || highlighted ? undefined : "dimmed"}>
          {width !== undefined && <MiddleTruncated text={name} limit={width} />}
        </span>
      </span>
    </div>
  );
}

/**
 * agterm's dashboard: live panes in a grid, view-only; arrows move, Enter or a click jumps in, Esc closes. While a pick,
 * ask, palette, menu or switcher is over it, the keys are theirs (agterm's focusAllowed).
 */
export function Dashboard({ state, dashboard, sessions, covered }: { state: AppState; dashboard: DashboardView; sessions: SessionView[]; covered: boolean }) {
  // A page's title changes in its browser, not in the state: the covers follow it.
  const [, refresh] = useReducer((tick: number) => tick + 1, 0);
  useEffect(() => onPageInfo(refresh), []);
  useEffect(() => {
    if (covered) {
      return;
    }
    // agterm's key catcher eats every key but its own; the app's keys still go their way, as menu keys do there.
    const onKey = (event: KeyboardEvent) => {
      const direction = ({ ArrowUp: "up", ArrowDown: "down", ArrowLeft: "left", ArrowRight: "right" } as const)[event.key as "ArrowUp"];
      if (direction && !event.ctrlKey && !event.altKey) {
        command({ command: "dashboardMove", direction });
      } else if (event.key === "Enter") {
        command({ command: "dashboardActivate", index: dashboard.highlight });
      } else if (event.key === "Escape") {
        command({ command: "closeDashboard" });
      } else {
        handleAppShortcut(event);
      }
      event.preventDefault();
      event.stopPropagation();
    };
    window.addEventListener("keydown", onKey, true);
    return () => window.removeEventListener("keydown", onKey, true);
  }, [dashboard.highlight, covered]);

  return (
    <div className="dashboard-layer">
      {dashboard.cells.map((cell, index) => {
        const session = sessions.find((s) => s.id === cell.sessionId);
        if (!session) {
          return null;
        }
        const cover = coverOf(cell, sessions);
        const highlighted = index === dashboard.highlight;
        return (
          <div
            key={`${cell.sessionId}:${cell.pane}`}
            className={"dashboard-cell" + (highlighted ? " highlighted" : "")}
            style={cellBox(index, dashboard)}
            onClick={() => command({ command: "dashboardClick", index })}
          >
            {cover && <Cover overlay={cover} />}
            {!cover && session.coveredPanes.includes(cell.pane) && (
              <div className="lead-cover dashboard-lead-cover">
                <LeadCoverContent session={session} pane={cell.pane} />
              </div>
            )}
            <Caption session={session} pane={cell.pane} highlighted={highlighted} state={state} />
          </div>
        );
      })}
    </div>
  );
}

/**
 * agterm's PaneLeadCover, on the pane and on its dashboard tile: "Taking over…" while a fresh attach has not reported,
 * "Reconnecting…" for a pane nobody leads, else where it is in use, with the hint until it is on its way.
 */
export function LeadCoverContent({ session, pane }: { session: SessionView; pane: Pane }) {
  const taking = session.takingPanes.includes(pane);
  const title = taking
    ? t("Забираю…")
    : session.unownedPanes.includes(pane)
      ? t("Переподключаю…")
      : session.remoteHost
        ? t("Используется на машине, где запущена")
        : t("Используется с другой машины");
  return (
    <>
      <PaneLeadIcon />
      <span className="lead-cover-title">{title}</span>
      {!taking && <span className="lead-cover-hint">{t("Нажмите любую клавишу, чтобы работать здесь")}</span>}
    </>
  );
}
