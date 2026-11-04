const common = { width: 14, height: 14, viewBox: "0 0 16 16", fill: "none", stroke: "currentColor", strokeWidth: 1.4, strokeLinecap: "round" as const, strokeLinejoin: "round" as const };

export function ChevronIcon({ open }: { open: boolean }) {
  return (
    <svg {...common} style={{ transform: open ? "rotate(90deg)" : "none", transition: "transform 120ms" }} aria-hidden="true">
      <path d="M6 4l4 4-4 4" />
    </svg>
  );
}

/** agterm's terminal / terminal.fill: the filled form marks a flagged row. */
export function TerminalIcon({ filled = false }: { filled?: boolean }) {
  return (
    <svg {...common} aria-hidden="true">
      {filled ? (
        <>
          <mask id="terminal-fill-cutout">
            <rect x="0" y="0" width="16" height="16" fill="white" />
            <path d="M5 6.5l2 1.5-2 1.5M8.5 10h2.5" stroke="black" />
          </mask>
          <rect x="2" y="3" width="12" height="10" rx="1.5" fill="currentColor" mask="url(#terminal-fill-cutout)" />
        </>
      ) : (
        <>
          <rect x="2" y="3" width="12" height="10" rx="1.5" />
          <path d="M5 6.5l2 1.5-2 1.5M8.5 10h2.5" />
        </>
      )}
    </svg>
  );
}

/** agterm's doc.richtext: a page of text with a picture in it. */
export function DocRichTextIcon() {
  return (
    <svg {...common} aria-hidden="true">
      <rect x="3" y="1.5" width="10" height="13" rx="1.5" />
      <rect x="5" y="4" width="3" height="3" rx="0.5" />
      <path d="M9.5 4.5h1.5M9.5 6.5h1.5M5 9h6M5 11.5h6" />
    </svg>
  );
}

/** agterm's square.grid.2x2, drawn heavier for a workspace in the focus set. */
export function WorkspaceIcon({ bold = false }: { bold?: boolean }) {
  return (
    <svg {...common} strokeWidth={bold ? 2.2 : common.strokeWidth} aria-hidden="true">
      <rect x="2.5" y="2.5" width="4.5" height="4.5" rx="1" />
      <rect x="9" y="2.5" width="4.5" height="4.5" rx="1" />
      <rect x="2.5" y="9" width="4.5" height="4.5" rx="1" />
      <rect x="9" y="9" width="4.5" height="4.5" rx="1" />
    </svg>
  );
}

export function PlusIcon() {
  return (
    <svg {...common} aria-hidden="true">
      <path d="M8 3.5v9M3.5 8h9" />
    </svg>
  );
}

export function FolderPlusIcon() {
  return (
    <svg {...common} aria-hidden="true">
      <path d="M2 4.5c0-.6.4-1 1-1h3l1.5 1.5H13c.6 0 1 .4 1 1V12c0 .6-.4 1-1 1H3c-.6 0-1-.4-1-1z" />
      <path d="M8 7v4M6 9h4" />
    </svg>
  );
}

export function FlagIcon({ filled }: { filled?: boolean }) {
  return (
    <svg {...common} aria-hidden="true">
      <path d="M4 14V2.5M4 3h7.5l-1.5 3 1.5 3H4" fill={filled ? "currentColor" : "none"} />
    </svg>
  );
}

/** Filled while the filter applies, as agterm's square.grid.2x2.fill. */
export function FilterIcon({ filled = false }: { filled?: boolean }) {
  return (
    <svg {...common} aria-hidden="true">
      <path d="M2.5 3.5h11l-4.2 5v4l-2.6 1v-5z" fill={filled ? "currentColor" : "none"} />
    </svg>
  );
}

export function SidebarIcon() {
  return (
    <svg {...common} aria-hidden="true">
      <rect x="2" y="3" width="12" height="10" rx="1.5" />
      <path d="M6 3v10" />
    </svg>
  );
}

export function CloudIcon() {
  return (
    <svg {...common} aria-hidden="true">
      <path d="M4.5 12.5h7.25a2.75 2.75 0 0 0 .3-5.48A4 4 0 0 0 4.3 7.6 2.5 2.5 0 0 0 4.5 12.5z" />
    </svg>
  );
}

/** agterm's slashed cloud: the row's status stream is not up. */
export function CloudOffIcon({ split }: { split?: boolean }) {
  return (
    <svg {...common} aria-hidden="true">
      <path d="M4.5 12.5h7.25a2.75 2.75 0 0 0 .3-5.48A4 4 0 0 0 4.3 7.6 2.5 2.5 0 0 0 4.5 12.5z" />
      {split && <path d="M8 7.5v5" />}
      <path d="M2.5 2.5l11 11" />
    </svg>
  );
}

/** agterm's rectangle.on.rectangle.slash, light at 28 points: the cover of a pane another client leads. */
export function PaneLeadIcon() {
  return (
    <svg {...common} width={28} height={28} strokeWidth={0.8} aria-hidden="true">
      <path d="M5 4.5V3.5a1 1 0 0 1 1-1h7.5a1 1 0 0 1 1 1V9a1 1 0 0 1-1 1h-1" />
      <rect x="1.5" y="6" width="9.5" height="7.5" rx="1" />
      <path d="M1.5 1.5l13 13" />
    </svg>
  );
}

/** agterm's mark for a remote row that brought its split. */
export function CloudSplitIcon() {
  return (
    <svg {...common} aria-hidden="true">
      <path d="M4.5 12.5h7.25a2.75 2.75 0 0 0 .3-5.48A4 4 0 0 0 4.3 7.6 2.5 2.5 0 0 0 4.5 12.5z" />
      <path d="M8 7.5v5" />
    </svg>
  );
}

export function ClockIcon() {
  return (
    <svg {...common} aria-hidden="true">
      <path d="M2.8 8a5.2 5.2 0 1 0 1.5-3.7" />
      <path d="M2.5 2.8v2.4h2.4M8 5.2V8l2 1.3" />
    </svg>
  );
}

export function BellIcon({ filled }: { filled?: boolean }) {
  return (
    <svg {...common} aria-hidden="true">
      <path d="M4 11.5V7.3a4 4 0 0 1 8 0v4.2l1 1H3z" fill={filled ? "currentColor" : "none"} />
      <path d="M6.8 14h2.4" />
    </svg>
  );
}

/** agterm's rectangle / rectangle.inset.filled: filled while the scratch is shown. */
export function ScratchIcon({ shown = false }: { shown?: boolean }) {
  return (
    <svg {...common} aria-hidden="true">
      <rect x="2" y="3" width="12" height="10" rx="1.5" />
      {shown && <rect x="4" y="5" width="8" height="6" rx="0.8" fill="currentColor" stroke="none" />}
    </svg>
  );
}

/** Struck through while overlays are hidden, as agterm's rectangle.on.rectangle.slash. */
export function OverlayIcon({ hidden = false }: { hidden?: boolean }) {
  return (
    <svg {...common} aria-hidden="true">
      <rect x="2" y="3" width="12" height="10" rx="1.5" />
      <rect x="4.5" y="5.5" width="7" height="5" rx="0.8" />
      {hidden && <path d="M2.5 14.5 13.5 1.5" />}
    </svg>
  );
}

/**
 * agterm's split glyph in its seven states: no split (an empty two-pane outline), both panes shown (filled, on the
 * split's axis), or one pane shown while the other is hidden (that half filled). The sidebar's split row draws it
 * empty on its axis, or filled when flagged, as agterm's rectangle.split.2x1 / 1x2 and their .fill.
 */
export type SplitGlyph = "none" | "both" | "both-horizontal" | "left" | "right" | "top" | "bottom";

export function SplitIcon({ glyph = "none", axis }: { glyph?: SplitGlyph; axis?: "vertical" | "horizontal" }) {
  const horizontal = axis ? axis === "horizontal" : glyph === "both-horizontal" || glyph === "top" || glyph === "bottom";
  const halves: Record<SplitGlyph, [number, number, number, number][]> = {
    none: [],
    both: [
      [3.5, 4.5, 3.6, 7],
      [8.9, 4.5, 3.6, 7],
    ],
    "both-horizontal": [
      [3.5, 4.5, 9, 2.6],
      [3.5, 8.9, 9, 2.6],
    ],
    left: [[3.5, 4.5, 3.6, 7]],
    right: [[8.9, 4.5, 3.6, 7]],
    top: [[3.5, 4.5, 9, 2.6]],
    bottom: [[3.5, 8.9, 9, 2.6]],
  };
  return (
    <svg {...common} aria-hidden="true">
      <rect x="2" y="3" width="12" height="10" rx="1.5" />
      <path d={horizontal ? "M2 8h12" : "M8 3v10"} />
      {halves[glyph].map(([x, y, width, height]) => (
        <rect key={`${x}-${y}`} x={x} y={y} width={width} height={height} rx="0.6" fill="currentColor" stroke="none" />
      ))}
    </svg>
  );
}

/** agterm's arrow.down.right.and.arrow.up.left: leave the terminal zoom. */
export function UnzoomIcon() {
  return (
    <svg {...common} aria-hidden="true">
      <path d="M13.5 2.5L9.5 6.5M9.5 3.5v3h3M2.5 13.5l4-4M6.5 12.5v-3h-3" />
    </svg>
  );
}

/** agterm's page strip symbols: chevron.left / chevron.right, arrow.clockwise, safari, arrow.up.forward.app, link, minus. */
export function PageStripIcon({ name }: { name: "back" | "forward" | "reload" | "browser" | "external" | "link" | "minus" }) {
  const paths = {
    back: "M10 3.5L5.5 8l4.5 4.5",
    forward: "M6 3.5L10.5 8 6 12.5",
    reload: "M12.6 6.2A5 5 0 1 0 13 9M13 3v3.4H9.6",
    browser: "M8 2.5a5.5 5.5 0 1 0 0 11 5.5 5.5 0 0 0 0-11zM10.6 5.4L9 9 5.4 10.6 7 7z",
    external: "M7 3.5H4.5a1 1 0 0 0-1 1v7a1 1 0 0 0 1 1h7a1 1 0 0 0 1-1V9M9.5 3.5h3v3M12.5 3.5L7.5 8.5",
    link: "M7 9a2.5 2.5 0 0 0 3.5 0l2-2A2.5 2.5 0 0 0 9 3.5l-.8.8M9 7a2.5 2.5 0 0 0-3.5 0l-2 2A2.5 2.5 0 0 0 7 12.5l.8-.8",
    minus: "M4 8h8",
  };
  return (
    <svg {...common} aria-hidden="true">
      <path d={paths[name]} />
    </svg>
  );
}

/** agterm's chevron.up / chevron.down. */
export function ChevronUpDownIcon({ up }: { up: boolean }) {
  return (
    <svg {...common} aria-hidden="true">
      <path d={up ? "M4 10l4-4 4 4" : "M4 6l4 4 4-4"} />
    </svg>
  );
}

export function CloseIcon() {
  return (
    <svg {...common} aria-hidden="true">
      <path d="M4 4l8 8M12 4l-8 8" />
    </svg>
  );
}

export function GridIcon() {
  return (
    <svg {...common} aria-hidden="true">
      <rect x="2.5" y="2.5" width="4.5" height="4.5" rx="0.8" />
      <rect x="9" y="2.5" width="4.5" height="4.5" rx="0.8" />
      <rect x="2.5" y="9" width="4.5" height="4.5" rx="0.8" />
      <rect x="9" y="9" width="4.5" height="4.5" rx="0.8" />
    </svg>
  );
}

export function QuickIcon() {
  return (
    <svg {...common} aria-hidden="true">
      <path d="M2.5 3.5h11M4 6.5l2 1.5-2 1.5M7.5 11h3" />
    </svg>
  );
}

export function CommandIcon() {
  return (
    <svg {...common} aria-hidden="true">
      <path d="M3 4.5l3 3.5-3 3.5M8 12h5" />
    </svg>
  );
}

/** agterm's magnifyingglass in the palette's search field, at the field's own text size. */
export function SearchIcon() {
  return (
    <svg {...common} width="1em" height="1em" aria-hidden="true">
      <circle cx="7" cy="7" r="4.6" />
      <path d="M10.4 10.4l3.4 3.4" />
    </svg>
  );
}
