import { CSSProperties, useEffect, useLayoutEffect, useRef, useState } from "react";
import { command, HudPaintView, HudRunView, HudView, OverlayView, send } from "./bridge";

// agterm's spinner styles: frames and seconds per frame.
const spinners: Record<string, { frames: string[]; interval: number }> = {
  bar: { frames: ["|", "/", "-", "\\"], interval: 100 },
  braille: { frames: ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"], interval: 80 },
  circle: { frames: ["◐", "◓", "◑", "◒"], interval: 120 },
  blocks: { frames: ["▁", "▃", "▄", "▅", "▆", "▇", "▆", "▅", "▄", "▃"], interval: 80 },
  dot: { frames: ["●", " "], interval: 450 },
};

// agterm's HudPosition.edgeMarginPercent: the share of the pane held clear at an anchored edge, none when sticky.
const edgeMargin = 0.1;

/** -1, 0 or 1: which band of the pane the position puts the panel in, vertically and horizontally. */
function bands(position: string): [number, number] {
  const [vertical, horizontal] = position === "center" ? ["center", "center"] : position.split("-");
  const band = (value: string | undefined, leading: string, trailing: string) => (value === leading ? -1 : value === trailing ? 1 : 0);
  return [band(vertical, "top", "bottom"), band(horizontal, "left", "right")];
}

/** agterm's offset: the panel's start along one axis, moved from the centre by half the room left after it and its edge margin. */
function offset(extent: number, size: number, band: number, margin: number): number {
  const free = Math.max(0, extent * ((1 - size / Math.max(extent, 1)) / 2 - margin));
  return (extent - size) / 2 + band * free;
}

const codePoints = (text: string) => [...text].length;

/** The terminal font's cell at a size, as the panel's text is laid out in it. */
function measureCell(family: string, size: number): { width: number; height: number } {
  const probe = document.createElement("span");
  probe.style.cssText = `position:absolute;visibility:hidden;white-space:pre;font-family:${family};font-size:${size}px;line-height:normal`;
  probe.textContent = "W".repeat(50);
  document.body.appendChild(probe);
  const rect = probe.getBoundingClientRect();
  probe.remove();
  return { width: rect.width / 50, height: Math.ceil(rect.height) };
}

/** agterm's SGR for a run: bold, italic, strikethrough, faint (as xterm.js draws it, at half strength), underline. */
function runStyle(run: HudRunView): CSSProperties {
  const lines = [run.style & 16 ? "underline" : "", run.style & 4 ? "line-through" : ""].filter(Boolean).join(" ");
  return {
    fontWeight: run.style & 1 ? 700 : undefined,
    fontStyle: run.style & 2 ? "italic" : undefined,
    textDecoration: lines || undefined,
    opacity: run.style & 8 ? 0.5 : undefined,
  };
}

/**
 * agterm's HUD: a passive panel over the session, laid out as its hud.sh paints the rows the app wrote — a grid of
 * terminal cells, the rows centred in it as a block, a plain message's lines each centred and a markdown body's at one
 * shared offset, the spinner's glyph and a space before the first row. It takes no focus; Ctrl+click opens a link.
 */
export function Hud({ overlay, hud, paint, fontSize, sessionId, pane }: { overlay: OverlayView; hud: HudView; paint: HudPaintView; fontSize: number; sessionId: string; pane: "left" | "right" }) {
  const spinner = hud.spinner ? spinners[hud.spinner] : undefined;
  const [frame, setFrame] = useState(0);
  useEffect(() => {
    if (!spinner) {
      return;
    }
    const timer = setInterval(() => setFrame((current) => current + 1), spinner.interval);
    return () => clearInterval(timer);
  }, [spinner]);

  const [area, setArea] = useState<{ width: number; height: number } | null>(null);
  const [cell, setCell] = useState<{ width: number; height: number }>({ width: 8, height: 17 });
  const [padding, setPadding] = useState<{ width: number; height: number }>({ width: 0, height: 0 });
  const panel = useRef<HTMLDivElement>(null);
  const sentFloat = useRef("");
  const size = hud.fontSize ?? fontSize;

  // agterm's paneMetrics: the cell of the panel's font, the pane or session it sits in, the terminal's own padding.
  useLayoutEffect(() => {
    const element = panel.current?.parentElement;
    if (!element) {
      return;
    }
    let last = "";
    const measure = () => {
      const root = getComputedStyle(document.documentElement);
      const family = root.getPropertyValue("--terminal-font-family").trim() || '"Cascadia Mono", Consolas, monospace';
      const nextCell = measureCell(family, size);
      const nextPadding = {
        width: parseFloat(root.getPropertyValue("--terminal-padding-left")) || 0,
        height: parseFloat(root.getPropertyValue("--terminal-padding-top")) || 0,
      };
      const nextArea = { width: element.clientWidth, height: element.clientHeight };
      setCell(nextCell);
      setPadding(nextPadding);
      setArea(nextArea);
      const metrics = {
        command: "hudMetrics" as const,
        overlayId: overlay.id,
        cellWidth: nextCell.width,
        cellHeight: nextCell.height,
        paneWidth: nextArea.width,
        paneHeight: nextArea.height,
        paddingWidth: nextPadding.width,
        paddingHeight: nextPadding.height,
      };
      const key = JSON.stringify(metrics);
      if (key !== last) {
        last = key;
        command(metrics);
      }
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, [overlay.id, size]);

  // agterm's HudLinkClick: with Ctrl held a link takes the pointer, and a Ctrl+click opens it.
  const [ctrl, setCtrl] = useState(false);
  useEffect(() => {
    const track = (event: KeyboardEvent | MouseEvent) => setCtrl(event.ctrlKey);
    window.addEventListener("keydown", track, true);
    window.addEventListener("keyup", track, true);
    window.addEventListener("mousemove", track, true);
    return () => {
      window.removeEventListener("keydown", track, true);
      window.removeEventListener("keyup", track, true);
      window.removeEventListener("mousemove", track, true);
    };
  }, []);

  const width = area ? (area.width * paint.widthPercent) / 100 : 0;
  const height = paint.heightPoints ?? (area ? (area.height * paint.heightPercent) / 100 : 0);
  const margin = hud.sticky ? 0 : edgeMargin;
  const [vertical, horizontal] = bands(hud.position);
  const glyph = spinner ? spinner.frames[frame % spinner.frames.length] + " " : "";
  const top = Math.max(0, Math.floor((paint.rows - paint.lines.length) / 2));
  const markdown = paint.blockWidth > 0;
  const style: CSSProperties = {
    left: area ? offset(area.width, width, horizontal, margin) : 0,
    top: area ? offset(area.height, height, vertical, margin) : 0,
    width,
    height,
    padding: `${padding.height}px ${padding.width}px`,
    visibility: area ? undefined : "hidden",
    background: overlay.backgroundColor ?? undefined,
    color: hud.textColor ?? undefined,
    fontSize: size,
    lineHeight: `${cell.height}px`,
  };

  // hud.sh: the glyph goes before the first row of a markdown body, and before the first line with text of a plain one.
  const glyphRow = markdown ? 0 : paint.lines.findIndex((line) => line.some((run) => run.text.length > 0));
  const rowLeft = (line: HudRunView[], index: number) => {
    const pre = index === glyphRow ? glyph : "";
    const text = line.map((run) => run.text).join("");
    const cells = markdown ? Math.floor((paint.columns - paint.blockWidth) / 2) : Math.floor((paint.columns - codePoints(text) - codePoints(pre)) / 2);
    return Math.max(0, cells) * cell.width;
  };

  // The panel as laid out here, for the window the app draws it again in over a raised page (agterm's HUD lies above it).
  useLayoutEffect(() => {
    const element = panel.current;
    if (!element || !area) {
      return;
    }
    const rect = element.getBoundingClientRect();
    const computed = getComputedStyle(element);
    const layout = {
      type: "hudFloat" as const,
      overlayId: overlay.id,
      x: rect.left,
      y: rect.top,
      width: rect.width,
      height: rect.height,
      background: computed.backgroundColor,
      color: computed.color,
      fontFamily: computed.fontFamily.split(",")[0].replace(/["']/g, "").trim(),
      fontSize: size,
      lineHeight: cell.height,
      framed: hud.frame,
      paddingWidth: padding.width,
      paddingHeight: padding.height,
      glyphRow: spinner ? glyphRow : -1,
      frames: spinner?.frames ?? [],
      interval: spinner?.interval ?? 0,
      rows: paint.lines.map((line, index) => ({ x: rowLeft(line, index), y: (top + index) * cell.height, runs: line })),
    };
    // A spinner's frame changes nothing here: the window turns its own glyph.
    const key = JSON.stringify(layout);
    if (key !== sentFloat.current) {
      sentFloat.current = key;
      send(layout);
    }
  });
  useEffect(() => () => send({ type: "hudFloat", overlayId: overlay.id, width: 0 }), [overlay.id]);
  return (
    <div ref={panel} className={"hud" + (hud.frame ? " framed" : "")} style={style}>
      <div className="hud-grid">
        {paint.lines.map((line, index) => {
          const pre = index === glyphRow ? glyph : "";
          return (
            <div key={index} className="hud-row" style={{ top: (top + index) * cell.height, left: rowLeft(line, index) }}>
              {pre}
              {line.map((run, at) =>
                run.link ? (
                  <a
                    key={at}
                    className={"hud-link" + (ctrl ? " armed" : "")}
                    style={runStyle(run)}
                    onMouseDown={(event) => event.preventDefault()}
                    onClick={(event) => {
                      if (event.ctrlKey) {
                        command({ command: "openLink", url: run.link!, sessionId, pane });
                      }
                    }}
                  >
                    {run.text}
                  </a>
                ) : (
                  <span key={at} style={runStyle(run)}>
                    {run.text}
                  </span>
                ),
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}
