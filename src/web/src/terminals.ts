import { ITheme, Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { ISearchOptions, SearchAddon } from "@xterm/addon-search";
import { WebglAddon } from "@xterm/addon-webgl";
import { WebLinksAddon } from "@xterm/addon-web-links";
import { AppState, BackgroundView, command, KeyStroke, Pane, send } from "./bridge";
import { encodeKey, KittyFlagStack, refreshKeyboardLayout } from "./kittyKeyboard";
import { OscTerminators } from "./oscTerminators";
import { backgroundOpacity, terminalTheme, withOpacity as colorWithOpacity } from "./appearance";
import { handleAppShortcut } from "./shortcuts";
import { currentState } from "./store";

/** A terminal is keyed by session and pane; the key of a shell changes when panes are swapped or a split is promoted. */
export function paneKey(sessionId: string, pane: Pane): string {
  return `${sessionId}:${pane}`;
}

interface Entry {
  sessionId: string;
  pane: Pane;
  term: Terminal;
  fit: FitAddon;
  element: HTMLDivElement;
  webgl: WebglAddon | null;
  attached: boolean;
  search: SearchAddon;
  results: SearchResults;
  /** A background of its own, for overlays opened with one; null follows the theme. */
  ownBackground: string | null;
  /** agterm's session background: a solid color, or an image or text drawn behind a transparent grid. */
  backdrop: BackgroundView | null;
  /** Ghostty's mode 5522: a paste from the clipboard becomes a Kitty clipboard paste event. */
  pasteEvents: boolean;
  /** The key press the next input comes from; a paste, a drop or a report of the terminal has none. */
  stroke: KeyStroke | "text" | null;
  /** An IME composition is under way in the terminal's textarea. */
  composing: boolean;
  /** Where the composition under way starts in the terminal's textarea, as xterm.js's CompositionHelper records it. */
  compositionStart: number;
  /** A background the program set with OSC 11, drawn at the window's opacity until OSC 111. */
  programBackground: string | null;
  /** Mode 2031: the program asked to be told when the color scheme changes. */
  colorSchemeReports: boolean;
  /** The Kitty keyboard protocol's flag stacks, one for each screen, as Ghostty keeps them. */
  kittyMain: KittyFlagStack;
  kittyAlt: KittyFlagStack;
  /** The last keydown went out through the Kitty encoder: its keypress must not reach xterm.js as well. */
  kittyHandled: boolean;
  /** The terminators of the Kitty clipboard requests in the output, which Ghostty's answers end with. */
  kittyClipboardEnds: OscTerminators;
  cursorOverride: boolean;
  cursorHidden: boolean;
  cursorBlinkOverride: boolean | null;
}

export interface SearchResults {
  index: number;
  count: number;
}

const searchListeners = new Set<() => void>();

const searchOptions: ISearchOptions = {
  decorations: {
    matchBackground: "#5c4a2c",
    matchOverviewRuler: "#8a6a3a",
    activeMatchBackground: "#e3a35a",
    activeMatchColorOverviewRuler: "#e3a35a",
  },
};

const entries = new Map<string, Entry>();

let theme: ITheme = { background: "#1b1a18", foreground: "#e6e1d7" };
let fontFamily = '"Cascadia Mono", Consolas, monospace';
let cursorStyle: "block" | "bar" | "underline" = "block";
let cursorBlink = true;
let hollowCursor = false;
/** agterm: an explicit blink choice holds against a program's DECSET 12; the default leaves it to the program. */
let blinkLocked = false;
let rightClickPaste = true;
let autoHideSidebar = false;
let copyOnSelect: "true" | "false" | "clipboard" = "true";
let systemDark = true;

/**
 * Ghostty's link rule under agterm: a link is underlined and clickable only with Ctrl held, and while the program reads
 * the mouse it is not found at all unless Shift is held too, so the click stays the program's.
 */
/**
 * Sends a key the Kitty way while the program has set flags on the screen in use, as Ghostty's key encoder does; false
 * then keeps xterm.js from sending it too. A composition under way stays the IME's.
 */
function kittyKey(entry: Entry, event: KeyboardEvent): boolean {
  const flags = (entry.term.buffer.active.type === "alternate" ? entry.kittyAlt : entry.kittyMain).current();
  if (flags === 0) {
    return true;
  }
  if (event.type === "keypress") {
    return !entry.kittyHandled;
  }
  if (event.type === "keydown") {
    entry.kittyHandled = false;
  }
  if (event.isComposing || event.key === "Dead" || event.keyCode === 229 || entry.composing) {
    return true;
  }
  const data = encodeKey(event, flags);
  if (event.type === "keydown") {
    entry.kittyHandled = true;
  }
  event.preventDefault();
  if (data) {
    const stroke = event.type === "keydown" ? entry.stroke : null;
    entry.stroke = null;
    send(stroke ? { type: "input", sessionId: entry.sessionId, pane: entry.pane, data, stroke } : { type: "input", sessionId: entry.sessionId, pane: entry.pane, data });
  }
  return false;
}

function linkLive(term: Terminal, event: { ctrlKey: boolean; shiftKey: boolean }): boolean {
  return event.ctrlKey && (term.modes.mouseTrackingMode === "none" || event.shiftKey);
}

/**
 * Puts the hovered link's underline and hand pointer in line with linkLive. xterm.js draws both for every link, OSC 8
 * ones included, and lets them change only through the hovered link's decorations, which its API does not hand out
 * for OSC 8 links; they are reached through the linkifier of the pinned xterm.js 6.0.0.
 */
function showLink(term: Terminal, event: { ctrlKey: boolean; shiftKey: boolean }): void {
  const core = (term as unknown as { _core?: { linkifier?: { _currentLink?: { link?: { decorations?: { underline: boolean; pointerCursor: boolean } } } } } })._core;
  const decorations = core?.linkifier?._currentLink?.link?.decorations;
  if (decorations) {
    const live = linkLive(term, event);
    decorations.underline = live;
    decorations.pointerCursor = live;
  }
}

function screenOf(entry: Entry): HTMLElement | null {
  return entry.term.element?.querySelector(".xterm-screen") ?? null;
}

/** A color as X11 writes it in answers: rgb:rrrr/gggg/bbbb. */
function x11Color(hex: string): string {
  const value = parseInt(hex.replace("#", "").slice(0, 6), 16);
  const part = (byte: number) => byte.toString(16).padStart(2, "0").repeat(2);
  return `rgb:${part((value >> 16) & 255)}/${part((value >> 8) & 255)}/${part(value & 255)}`;
}

let colorProbe: CanvasRenderingContext2D | null = null;

/** An X11 color spec (rgb:r/g/b, #rgb up to #rrrrggggbbbb, or a name) as #rrggbb; null when it is none. */
function parseColorSpec(spec: string): string | null {
  const text = spec.trim();
  const rgb = /^rgb:([0-9a-f]{1,4})\/([0-9a-f]{1,4})\/([0-9a-f]{1,4})$/i.exec(text);
  if (rgb) {
    return "#" + [rgb[1], rgb[2], rgb[3]].map((part) => Math.round((parseInt(part, 16) * 255) / (16 ** part.length - 1)).toString(16).padStart(2, "0")).join("");
  }
  const hex = /^#([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{9}|[0-9a-f]{12})$/i.exec(text);
  if (hex) {
    const digits = hex[1].length / 3;
    return "#" + [0, 1, 2].map((index) => hex[1].slice(index * digits, index * digits + digits).padEnd(2, hex[1][index * digits]).slice(0, 2)).join("").toLowerCase();
  }
  colorProbe ??= document.createElement("canvas").getContext("2d");
  if (!colorProbe || !/^[a-z ]+$/i.test(text)) {
    return null;
  }
  colorProbe.fillStyle = "#000001";
  colorProbe.fillStyle = text.replace(/ /g, "");
  return colorProbe.fillStyle === "#000001" || !colorProbe.fillStyle.startsWith("#") ? null : colorProbe.fillStyle;
}
let mouseHideWhileTyping = false;
// agterm's ghostty-defaults: cursor-click-to-move off, and Ghostty's 10 MB of scrollback.
let altClickMovesCursor = false;
let scrollback = 10000;
let scrollSensitivity = 1;
let translucent = false;
let windowOpacity = 1;
let rendererOpacity = 1;
let solidBackground = "#1b1a18";
let appearanceKey = "";

/** Brings every terminal to the theme, font and cursor of the settings; new terminals start with them. */
export function applyTerminalAppearance(state: AppState): void {
  const { settings } = state;
  const opacity = backgroundOpacity(state);
  const key = JSON.stringify([state.theme, settings.terminalFontFamilies, settings.terminalBackgroundOpacity, settings.cursorStyle, settings.cursorBlink, settings.cursorBlinkSetting, opacity, settings.mouseScrollMultiplier, settings.cursorClickToMove, settings.scrollback]);
  rightClickPaste = settings.rightClickPaste;
  autoHideSidebar = settings.autoHideSidebarInactiveWindows;
  copyOnSelect = settings.copyOnSelect;
  if (settings.systemDark !== systemDark) {
    systemDark = settings.systemDark;
    for (const entry of entries.values()) {
      if (entry.colorSchemeReports) {
        send({ type: "input", sessionId: entry.sessionId, pane: entry.pane, data: `\x1b[?997;${systemDark ? 1 : 2}n` });
      }
    }
  }
  mouseHideWhileTyping = settings.mouseHideWhileTyping;
  // agterm's askFont: the terminal font of the settings, for the terminal-style question.
  document.documentElement.style.setProperty("--terminal-font-family", [...settings.terminalFontFamilies.map((family) => JSON.stringify(family)), '"Cascadia Mono"', "Consolas", "monospace"].join(", "));
  document.documentElement.style.setProperty("--terminal-font-size", `${settings.terminalFontSize}px`);
  const padding = settings.terminalPadding;
  document.documentElement.style.setProperty("--terminal-padding", `${padding.top}px ${padding.right}px ${padding.bottom}px ${padding.left}px`);
  (["top", "right", "bottom", "left"] as const).forEach((side) => document.documentElement.style.setProperty(`--terminal-padding-${side}`, `${padding[side]}px`));
  if (key === appearanceKey) {
    return;
  }
  appearanceKey = key;
  rendererOpacity = settings.terminalBackgroundOpacity;
  // Compose the default background once in CSS; WebGL blends partial canvas alpha incorrectly.
  theme = terminalTheme(state.theme, rendererOpacity < 1 ? 0 : 1);
  solidBackground = state.theme.background;
  windowOpacity = opacity;
  translucent = opacity < 1;
  fontFamily = [...settings.terminalFontFamilies.map((family) => JSON.stringify(family)), '"Cascadia Mono"', "Consolas", "monospace"].join(", ");
  hollowCursor = settings.cursorStyle === "block_hollow";
  cursorStyle = hollowCursor ? "bar" : settings.cursorStyle as typeof cursorStyle;
  cursorBlink = settings.cursorBlink;
  blinkLocked = settings.cursorBlinkSetting !== null;
  // agterm's multiplier is Ghostty's, whose default of 3 is xterm.js's normal speed.
  scrollSensitivity = settings.mouseScrollMultiplier / 3;
  altClickMovesCursor = settings.cursorClickToMove;
  scrollback = settings.scrollback;
  for (const entry of entries.values()) {
    entry.term.options.altClickMovesCursor = altClickMovesCursor;
    entry.term.options.scrollback = scrollback;
    const fontChanged = entry.term.options.fontFamily !== fontFamily;
    entry.term.options.fontFamily = fontFamily;
    entry.term.options.cursorStyle = cursorStyle;
    entry.term.options.cursorBlink = cursorBlink;
    entry.term.options.scrollSensitivity = scrollSensitivity;
    paint(entry, true);
    if (fontChanged && entry.term.element) {
      fitEntry(entry);
    }
  }
}

/**
 * Colors a terminal from the theme, the window's translucency, an overlay's own color and the session background. An
 * image or text backdrop shows through a transparent grid over the opaque theme color, as agterm makes such a pane opaque.
 */
function paint(entry: Entry, themeChanged = false): void {
  const backdrop = entry.backdrop;
  // agterm draws a program's OSC 11 color and an overlay's own color at the window's opacity, as it draws a solid
  // session background; the program's color stands over the others until it resets it.
  const own = entry.programBackground ?? entry.ownBackground;
  const color = own ? withOpacity(own) : backdrop?.kind === "color" && backdrop.color ? withOpacity(backdrop.color) : null;
  const layered = backdrop !== null && backdrop.kind !== "color" && own === null;
  const background = color ?? (layered ? "rgba(0, 0, 0, 0)" : theme.background);
  if (themeChanged || entry.term.options.theme?.background !== background) {
    entry.term.options.theme = { ...theme, background, ...(hollowCursor && !entry.cursorOverride ? { cursor: "#00000000" } : {}) };
  }
  entry.element.style.background = color ?? (layered ? solidBackground : rendererOpacity < 1 ? colorWithOpacity(solidBackground, rendererOpacity) : !translucent ? solidBackground : "");
  entry.element.classList.toggle("renderer-translucent", rendererOpacity < 1);
  const transparent = translucent || layered || rendererOpacity < 1;
  if (entry.term.options.allowTransparency !== transparent) {
    entry.term.options.allowTransparency = transparent;
    // The WebGL renderer takes transparency in when it is created, so a visible terminal gets a fresh one.
    if (entry.webgl) {
      entry.webgl.dispose();
      entry.webgl = null;
      activate(paneKey(entry.sessionId, entry.pane), false);
    }
  }
  entry.element.classList.toggle("layered", layered);
  paintBackdrop(entry, layered ? backdrop : null);
  renderHollowCursor(entry);
}

function renderHollowCursor(entry: Entry): void {
  const screen = screenOf(entry);
  if (!screen) return;
  let cursor = screen.querySelector<HTMLDivElement>(":scope > .ghostty-hollow-cursor");
  if (!hollowCursor || entry.cursorOverride || entry.cursorHidden) {
    cursor?.remove();
    return;
  }
  if (!cursor) {
    cursor = document.createElement("div");
    cursor.className = "ghostty-hollow-cursor";
    screen.appendChild(cursor);
  }
  const buffer = entry.term.buffer.active;
  const row = buffer.baseY + buffer.cursorY - buffer.viewportY;
  const width = screen.clientWidth / entry.term.cols;
  const height = screen.clientHeight / entry.term.rows;
  const cells = buffer.getLine(buffer.baseY + buffer.cursorY)?.getCell(buffer.cursorX)?.getWidth() || 1;
  cursor.hidden = row < 0 || row >= entry.term.rows;
  cursor.style.left = `${buffer.cursorX * width}px`;
  cursor.style.top = `${row * height}px`;
  cursor.style.width = `${cells * width}px`;
  cursor.style.height = `${height}px`;
  cursor.style.borderColor = theme.cursor ?? theme.foreground ?? "white";
  cursor.classList.toggle("blink", (blinkLocked ? cursorBlink : entry.cursorBlinkOverride ?? cursorBlink) && document.activeElement === entry.term.textarea && document.hasFocus());
}

/** A solid backdrop color keeps the window's translucency, as agterm draws it at the settings' opacity. */
function withOpacity(color: string): string {
  if (!translucent) {
    return color;
  }
  const value = parseInt(color.slice(1), 16);
  return `rgba(${(value >> 16) & 255}, ${(value >> 8) & 255}, ${value & 255}, ${windowOpacity})`;
}

const fits: Record<string, string> = { contain: "contain", cover: "cover", stretch: "100% 100%", none: "auto" };

function paintBackdrop(entry: Entry, backdrop: BackgroundView | null): void {
  let layer = entry.element.querySelector<HTMLDivElement>(":scope > .backdrop");
  if (!backdrop) {
    layer?.remove();
    return;
  }
  if (!layer) {
    layer = document.createElement("div");
    layer.className = "backdrop";
    entry.element.prepend(layer);
  }
  const [vertical, horizontal] = backdrop.position === "center" ? ["center", "center"] : anchor(backdrop.position);
  layer.style.opacity = String(backdrop.opacity);
  // agterm's WatermarkRenderer: a text is drawn into a picture and then goes as one, fit, position and repeat included.
  const url = backdrop.kind === "image" ? backdrop.url : renderedText(backdrop.text ?? "", backdrop.color ?? theme.foreground ?? "#ffffff");
  layer.style.backgroundImage = url ? `url("${url}")` : "none";
  layer.style.backgroundSize = fits[backdrop.fit] ?? "contain";
  layer.style.backgroundPosition = `${horizontal} ${vertical}`;
  layer.style.backgroundRepeat = backdrop.repeat ? "repeat" : "no-repeat";
}

const renderedTexts = new Map<string, string | null>();

/**
 * agterm's renderText: the text in bold at 256 px on a transparent picture as big as the glyphs plus 0.3 of the size on
 * each side, so a fit scales it crisply; none past 64 million pixels.
 */
function renderedText(text: string, color: string): string | null {
  const key = color + "\n" + text;
  if (renderedTexts.has(key)) {
    return renderedTexts.get(key)!;
  }
  const size = 256;
  const font = `700 ${size}px "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif`;
  const canvas = document.createElement("canvas");
  const context = canvas.getContext("2d");
  let url: string | null = null;
  if (context) {
    context.font = font;
    const metrics = context.measureText(text);
    const padding = size * 0.3;
    const width = Math.ceil(metrics.width + padding * 2);
    const height = Math.ceil(metrics.fontBoundingBoxAscent + metrics.fontBoundingBoxDescent + padding * 2);
    if (width > 0 && height > 0 && width * height <= 64_000_000) {
      canvas.width = width;
      canvas.height = height;
      context.font = font;
      context.fillStyle = color;
      context.textBaseline = "alphabetic";
      context.fillText(text, padding, padding + metrics.fontBoundingBoxAscent);
      url = canvas.toDataURL("image/png");
    }
  }
  renderedTexts.set(key, url);
  return url;
}

/** "top-left" as [vertical, horizontal]; a single edge centres the other axis. */
function anchor(position: string): [string, string] {
  const parts = position.split("-");
  if (parts.length === 2) {
    return [parts[0], parts[1]];
  }
  return parts[0] === "top" || parts[0] === "bottom" ? [parts[0], "center"] : ["center", parts[0]];
}

/** agterm's firingIsFocused: this very terminal has the page's keyboard focus. */
function hasKeyboard(entry: Entry): boolean {
  return document.hasFocus() && entry.element.contains(document.activeElement);
}

export function firingTerminal(): { sessionId: string; pane: Pane; selection: string } | null {
  for (const entry of entries.values()) {
    if (hasKeyboard(entry)) return { sessionId: entry.sessionId, pane: entry.pane, selection: entry.term.getSelection() };
  }
  return null;
}

export function setSessionBackground(key: string, backdrop: BackgroundView | null): void {
  const entry = entries.get(key);
  if (!entry || JSON.stringify(entry.backdrop) === JSON.stringify(backdrop)) {
    return;
  }
  entry.backdrop = backdrop;
  paint(entry);
}

/**
 * A paste; one from the clipboard goes as a Kitty paste event while the program asked for them (mode 5522), as Ghostty's.
 * As agterm's, a composition under way is committed first, so its text does not land after the pasted one.
 */
export function pasteInto(key: string, text: string, fromClipboard = false, selection = false): void {
  const entry = entries.get(key);
  if (entry && fromClipboard && entry.pasteEvents) {
    send({ type: "kittyPaste", sessionId: entry.sessionId, pane: entry.pane, selection });
  } else if (text && entry) {
    void commitComposition(key).then(() => entry.term.paste(text));
  }
}

/** agterm's commitMarkedText: an IME composition is ended where it stands, and its text goes to the program. */
export async function commitComposition(key: string): Promise<void> {
  const entry = entries.get(key);
  const textarea = entry?.term.textarea;
  if (!entry?.composing || !textarea) {
    return;
  }
  textarea.blur();
  textarea.focus();
  // xterm.js reads the committed text a tick after compositionend.
  await new Promise((resolve) => setTimeout(resolve, 20));
}

/**
 * agterm's pendingComposition and discardComposition: the half-typed text of a composition under way, ended without
 * reaching the program, for the caller to type ahead of its own text through the pane's daemon.
 */
export async function takeComposition(key: string): Promise<string> {
  const entry = entries.get(key);
  const textarea = entry?.term.textarea;
  if (!entry?.composing || !textarea) {
    return "";
  }
  const text = textarea.value.substring(entry.compositionStart);
  // xterm.js sends what the textarea holds past the composition's start once it ends: emptied, it sends nothing.
  textarea.value = "";
  textarea.blur();
  textarea.focus();
  await new Promise((resolve) => setTimeout(resolve, 20));
  return text;
}

/** agterm's session copy, select-all and paste from the API; null when the pane has no terminal yet. */
export function terminalAction(key: string, action: string, text: string | null): string | null {
  const entry = entries.get(key);
  if (!entry) {
    return null;
  }
  switch (action) {
    case "copy":
      return entry.term.getSelection();
    case "cursor":
      return String(entry.term.buffer.active.cursorX);
    case "selectAll":
      entry.term.selectAll();
      return "";
    case "paste":
      pasteInto(key, text ?? "", true);
      return "";
    default:
      return null;
  }
}

// The Kitty protocol's unshifted keys come from the active layout, read again when the window comes back.
refreshKeyboardLayout();
window.addEventListener("focus", refreshKeyboardLayout);

// Coming back to the window counts for the pane that has the keyboard, and only for it.
window.addEventListener("focus", () => {
  const focused = document.activeElement;
  for (const entry of entries.values()) {
    if (focused && entry.element.contains(focused)) {
      send({ type: "paneFocused", sessionId: entry.sessionId, pane: entry.pane });
    }
  }
});

/**
 * agterm's acceptsFirstMouse: the click that brings the window back reaches a terminal only with the left button, and not
 * at all while auto-hide-inactive-sidebars is on. WebView2 keeps WM_MOUSEACTIVATE from the app, so a press that comes
 * right after the window takes focus, with no key between, is taken for that click.
 */
const activationWindow = 100;
let activatedAt = Number.NEGATIVE_INFINITY;
const eatenButtons = new Set<number>();
window.addEventListener("focus", (event) => {
  activatedAt = event.timeStamp;
});
window.addEventListener("blur", () => {
  activatedAt = Number.NEGATIVE_INFINITY;
});
// A press anywhere else in the page is the activating one.
window.addEventListener("mousedown", () => {
  activatedAt = Number.NEGATIVE_INFINITY;
});
window.addEventListener(
  "keydown",
  () => {
    activatedAt = Number.NEGATIVE_INFINITY;
    eatenButtons.clear();
  },
  true,
);

function eatActivatingClick(element: HTMLElement): void {
  const eat = (event: Event) => {
    event.preventDefault();
    event.stopImmediatePropagation();
  };
  element.addEventListener(
    "mousedown",
    (event) => {
      const activating = event.timeStamp - activatedAt <= activationWindow;
      activatedAt = Number.NEGATIVE_INFINITY;
      eatenButtons.delete(event.button);
      if (activating && (autoHideSidebar || event.button !== 0)) {
        eatenButtons.add(event.button);
        eat(event);
      }
    },
    true,
  );
  // The rest of an eaten press goes with it, up to the next press of its button.
  for (const type of ["mouseup", "click", "auxclick", "contextmenu"]) {
    element.addEventListener(
      type,
      (event) => {
        if (event instanceof MouseEvent && eatenButtons.has(event.button)) {
          eat(event);
        }
      },
      true,
    );
  }
}

/**
 * Whether an OSC 9 is one of ConEmu's commands as libghostty's osc9 parser tells them (sleep, xterm emulation, comment,
 * prompt, message box, tab title, progress, wait, GUI macro, run, environment, directory); anything else, an empty text
 * included, is a desktop notification.
 */
export function isConEmuCommand(data: string): boolean {
  const separated = data[1] === ";";
  switch (data[0]) {
    case "1":
      if (data[1] === ";" || data[1] === "2") {
        return true;
      }
      if (data[1] === "0") {
        return data.length === 2 || (data.length >= 4 && data[2] === ";" && "0123".includes(data[3]));
      }
      return data[1] === "1" && data[2] === ";";
    case "2":
    case "3":
    case "6":
    case "7":
    case "8":
    case "9":
      return separated;
    case "4":
      return separated && data.length >= 3 && "01234".includes(data[2]);
    case "5":
      return true;
    default:
      return false;
  }
}

let paneFocusListener: (sessionId: string, pane: Pane) => void = () => {};

/** Called when the user clicks or tabs into a pane's terminal. */
export function onPaneFocus(listener: (sessionId: string, pane: Pane) => void): void {
  paneFocusListener = listener;
}

export function ensureTerminal(sessionId: string, pane: Pane): HTMLDivElement {
  const key = paneKey(sessionId, pane);
  const existing = entries.get(key);
  if (existing) {
    return existing.element;
  }

  const element = document.createElement("div");
  element.className = "terminal-host";
  eatActivatingClick(element);
  const term = new Terminal({
    fontFamily,
    fontSize: 14,
    cursorStyle,
    cursorBlink,
    scrollSensitivity,
    allowTransparency: translucent || rendererOpacity < 1,
    scrollback,
    altClickMovesCursor,
    // The search addon highlights matches through decorations, which are still a proposed API.
    allowProposedApi: true,
    // ConPTY repaints the screen itself on resize; xterm.js must not reflow on top of it or lines get duplicated.
    windowsPty: { backend: "conpty", buildNumber: currentState()?.windowsBuild },
    theme: hollowCursor ? { ...theme, cursor: "#00000000" } : theme,
    // OSC 8 links go the way found links do, any scheme included: the app decides what may open, as agterm's LinkPolicy.
    linkHandler: {
      allowNonHttpProtocols: true,
      activate: (event, text) => {
        if (linkLive(term, event)) {
          command({ command: "openLink", url: text, sessionId, pane });
        }
      },
      // The linkifier makes the decorations settable only after it calls hover.
      hover: (event) => setTimeout(() => showLink(term, event)),
    },
  });
  const fit = new FitAddon();
  term.loadAddon(fit);
  const search = new SearchAddon();
  term.loadAddon(search);
  const entry: Entry = { sessionId, pane, term, fit, element, webgl: null, attached: false, search, results: { index: -1, count: 0 }, ownBackground: null, backdrop: null, pasteEvents: false, stroke: null, composing: false, compositionStart: 0, programBackground: null, colorSchemeReports: false, kittyMain: new KittyFlagStack(), kittyAlt: new KittyFlagStack(), kittyHandled: false, kittyClipboardEnds: new OscTerminators(5522), cursorOverride: false, cursorHidden: false, cursorBlinkOverride: null };
  term.onRender(() => renderHollowCursor(entry));
  term.onScroll(() => renderHollowCursor(entry));
  term.parser.registerCsiHandler({ intermediates: " ", final: "q" }, (params) => {
    entry.cursorOverride = params[0] !== 0;
    queueMicrotask(() => paint(entry, true));
    return false;
  });
  // As in Windows Terminal, Ctrl+click opens a link; where it opens is the settings' choice.
  term.loadAddon(
    new WebLinksAddon(
      (event, uri) => {
        if (linkLive(term, event)) {
          command({ command: "openLink", url: uri, sessionId: entry.sessionId, pane: entry.pane });
        }
      },
      { hover: (event) => setTimeout(() => showLink(term, event)) },
    ),
  );
  // Ghostty's right-click-action: while the program reads the mouse the click is its own, and Shift takes it back.
  element.addEventListener("contextmenu", (event) => {
    if (rightClickPaste) {
      event.preventDefault();
      if (term.modes.mouseTrackingMode === "none" || event.shiftKey) {
        command({ command: "paste", sessionId: entry.sessionId, pane: entry.pane });
      }
    }
  });
  search.onDidChangeResults(({ resultIndex, resultCount }) => {
    entry.results = { index: resultIndex, count: resultCount };
    searchListeners.forEach((listener) => listener());
  });
  entries.set(key, entry);
  paint(entry, true);

  // The link under the pointer lights up as Ctrl (or Shift over a program reading the mouse) goes down or up.
  element.addEventListener("mousemove", (event) => showLink(term, event));
  term.attachCustomKeyEventHandler((event) => {
    if (event.key === "Control" || event.key === "Shift") {
      showLink(term, event);
    }
    // agterm classifies the key, not the bytes it makes: the input that follows a key press carries it.
    if (event.type === "keydown") {
      entry.stroke = { key: event.key, code: event.code, ctrl: event.ctrlKey, alt: event.altKey, shift: event.shiftKey, meta: event.metaKey };
    } else if (event.type === "keyup") {
      entry.stroke = null;
    }
    const gate = remoteGate(entry, event);
    if (gate !== null) {
      return gate;
    }
    // The app's keys and copy and paste come first, as Ghostty's bindings do; the rest may be the Kitty protocol's.
    return handleKey(term, event) && kittyKey(entry, event);
  });
  // agterm clears a session's unseen notifications when its pane takes the keyboard in a window that has it.
  element.addEventListener("focusin", () => {
    if (document.hasFocus()) {
      send({ type: "paneFocused", sessionId: entry.sessionId, pane: entry.pane });
    }
  });
  // Text an IME or dictation commits is typing with no key of its own.
  element.addEventListener("compositionstart", () => {
    entry.composing = true;
    entry.compositionStart = term.textarea?.value.length ?? 0;
  });
  element.addEventListener("compositionend", () => {
    entry.composing = false;
    entry.stroke = "text";
  });
  // Ghostty's copy-on-select: a selection made with the mouse goes to the selection clipboard, which the middle button
  // pastes; while the program reads the mouse the button is the program's, and Shift takes it back.
  element.addEventListener("mouseup", (event) => {
    if (event.button === 0 && term.hasSelection() && copyOnSelect !== "false") {
      const text = term.getSelection();
      send({ type: "selectionCopied", sessionId: entry.sessionId, pane: entry.pane, text });
      // copy-on-select = clipboard puts it on the clipboard as well.
      if (copyOnSelect === "clipboard") {
        navigator.clipboard.writeText(text).catch(() => {});
      }
    }
  });
  // Ghostty's mouse-hide-while-typing: the pointer goes while keys are typed and comes back when the mouse moves.
  element.addEventListener("keydown", () => {
    if (mouseHideWhileTyping) {
      element.classList.add("typing");
    }
  });
  element.addEventListener("mousemove", () => element.classList.remove("typing"));
  element.addEventListener(
    "mousedown",
    (event) => {
      if (event.button === 1 && (term.modes.mouseTrackingMode === "none" || event.shiftKey)) {
        event.preventDefault();
        event.stopImmediatePropagation();
        command({ command: "pasteSelection", sessionId: entry.sessionId, pane: entry.pane });
      }
    },
    true,
  );
  term.onData((data) => {
    const stroke = entry.stroke;
    entry.stroke = null;
    send(stroke ? { type: "input", sessionId: entry.sessionId, pane: entry.pane, data, stroke } : { type: "input", sessionId: entry.sessionId, pane: entry.pane, data });
  });
  // agterm's setAccessibilityValue: a tool that sets the terminal's value through UI Automation (Chromium reports it as a
  // bare input event, which xterm.js ignores) types the text in, or pastes it when it carries a control character, so a
  // line break cannot submit the line nor a tab fire completion.
  element.addEventListener(
    "input",
    (event: Event) => {
      const textarea = event.target;
      if (event instanceof InputEvent || !(textarea instanceof HTMLTextAreaElement)) {
        return;
      }
      event.stopImmediatePropagation();
      const text = textarea.value;
      textarea.value = "";
      entry.stroke = null;
      if (needsPasteRouting(text)) {
        term.paste(text);
      } else if (text) {
        term.input(text, true);
      }
    },
    true,
  );
  // Ctrl+V reaches xterm.js as the browser's paste event; in mode 5522 it becomes a paste event instead.
  element.addEventListener(
    "paste",
    (event) => {
      // A paste is not a key: Ctrl+V's own key press does not count, as in agterm, where the menu takes it first.
      entry.stroke = null;
      if (entry.pasteEvents) {
        event.preventDefault();
        event.stopImmediatePropagation();
        send({ type: "kittyPaste", sessionId: entry.sessionId, pane: entry.pane });
        return;
      }
      // Files copied in Explorer paste as their paths, which the app quotes for the pane's shell, as agterm's.
      const types = event.clipboardData?.types ?? [];
      if (!types.includes("text/plain") && types.includes("Files")) {
        event.preventDefault();
        event.stopImmediatePropagation();
        command({ command: "paste", sessionId: entry.sessionId, pane: entry.pane });
      }
    },
    true,
  );
  term.onResize(({ cols, rows }) => {
    if (entry.attached) {
      send({ type: "resize", sessionId: entry.sessionId, pane: entry.pane, cols, rows });
    }
  });
  // OSC 9 is both ConEmu's command family (9;<n>;..., our cwd is 9;9) and iTerm2's plain-text notification.
  // DECSET/DECRST 12 sets xterm.js's blink option; a locked choice is put back once the sequence has run.
  for (const final of ["h", "l"]) {
    term.parser.registerCsiHandler({ prefix: "?", final }, (params) => {
      if (params.some((param) => param === 25)) {
        entry.cursorHidden = final === "l";
        queueMicrotask(() => renderHollowCursor(entry));
      }
      if (params.some((param) => param === 5522)) {
        entry.pasteEvents = final === "h";
      }
      if (params.some((param) => param === 2031)) {
        entry.colorSchemeReports = final === "h";
      }
      if (params.some((param) => param === 12)) {
        entry.cursorBlinkOverride = final === "h";
        queueMicrotask(() => renderHollowCursor(entry));
      }
      if (blinkLocked && params.some((param) => param === 12)) {
        queueMicrotask(() => {
          term.options.cursorBlink = cursorBlink;
        });
      }
      return false;
    });
  }
  // Ghostty answers DECRQM for mode 5522 (1 set, 2 reset); xterm.js would call it unknown.
  term.parser.registerCsiHandler({ prefix: "?", intermediates: "$", final: "p" }, (params) => {
    if (params[0] !== 5522) {
      return false;
    }
    send({ type: "input", sessionId: entry.sessionId, pane: entry.pane, data: `\x1b[?5522;${entry.pasteEvents ? 1 : 2}$y` });
    return true;
  });
  // The Kitty keyboard protocol's flags: query, push, pop and set, on the stack of the screen in use.
  const kittyStack = () => (term.buffer.active.type === "alternate" ? entry.kittyAlt : entry.kittyMain);
  term.parser.registerCsiHandler({ prefix: "?", final: "u" }, () => {
    send({ type: "input", sessionId: entry.sessionId, pane: entry.pane, data: `\x1b[?${kittyStack().current()}u` });
    return true;
  });
  term.parser.registerCsiHandler({ prefix: ">", final: "u" }, (params) => {
    kittyStack().push(typeof params[0] === "number" ? params[0] : 0);
    return true;
  });
  term.parser.registerCsiHandler({ prefix: "<", final: "u" }, (params) => {
    kittyStack().pop(typeof params[0] === "number" && params[0] > 0 ? params[0] : 1);
    return true;
  });
  term.parser.registerCsiHandler({ prefix: "=", final: "u" }, (params) => {
    kittyStack().set(typeof params[1] === "number" && params[1] > 0 ? params[1] : 1, typeof params[0] === "number" ? params[0] : 0);
    return true;
  });
  const resetCursor = () => {
    entry.cursorHidden = false;
    entry.cursorOverride = false;
    entry.cursorBlinkOverride = null;
    queueMicrotask(() => paint(entry, true));
  };
  term.parser.registerCsiHandler({ intermediates: "!", final: "p" }, () => {
    resetCursor();
    return false;
  });
  term.parser.registerEscHandler({ final: "c" }, () => {
    resetCursor();
    entry.kittyMain.reset();
    entry.kittyAlt.reset();
    entry.pasteEvents = false;
    entry.colorSchemeReports = false;
    if (entry.programBackground) {
      entry.programBackground = null;
      paint(entry);
    }
    screenOf(entry)?.style.removeProperty("cursor");
    return false;
  });
  const reply = (data: string) => send({ type: "input", sessionId: entry.sessionId, pane: entry.pane, data });
  // Ghostty's OSC 11 under agterm: the program's background tints its own pane at the window's opacity, OSC 111 gives
  // the pane its own color back, and a query answers with the color on screen, not the transparent one under it.
  term.parser.registerOscHandler(11, (data) => {
    if (data === "?") {
      reply(`\x1b]11;${x11Color(entry.programBackground ?? entry.ownBackground ?? (entry.backdrop?.kind === "color" ? entry.backdrop.color : null) ?? solidBackground)}\x1b\\`);
      return true;
    }
    const color = parseColorSpec(data);
    if (!color) {
      return true;
    }
    if (color !== entry.programBackground) {
      entry.programBackground = color;
      paint(entry);
    }
    return true;
  });
  term.parser.registerOscHandler(111, () => {
    if (entry.programBackground) {
      entry.programBackground = null;
      paint(entry);
    }
    return true;
  });
  // OSC 22: the mouse pointer shape the program asks for, by its CSS name; an empty one gives the terminal's back.
  term.parser.registerOscHandler(22, (data) => {
    const screen = screenOf(entry);
    if (screen) {
      if (data && CSS.supports("cursor", data)) {
        screen.style.setProperty("cursor", data, "important");
      } else if (!data) {
        screen.style.removeProperty("cursor");
      }
    }
    return true;
  });
  // Ghostty's color scheme reports: CSI ? 996 n asks dark or light, mode 2031 reports every change.
  term.parser.registerCsiHandler({ prefix: "?", final: "n" }, (params) => {
    if (params[0] !== 996) {
      return false;
    }
    reply(`\x1b[?997;${systemDark ? 1 : 2}n`);
    return true;
  });
  term.parser.registerOscHandler(9, (data) => {
    if (data.startsWith("9;")) {
      const path = data.slice(2).replace(/^"(.*)"$/, "$1");
      if (path) {
        send({ type: "cwd", sessionId: entry.sessionId, pane: entry.pane, path });
      }
    } else if (!isConEmuCommand(data)) {
      send({ type: "notify", sessionId: entry.sessionId, pane: entry.pane, focused: hasKeyboard(entry), title: "", body: data });
    }
    return true;
  });
  // Ghostty's rxvt extension: notify;TITLE;BODY, the title's ; required; anything else is not a notification.
  term.parser.registerOscHandler(777, (data) => {
    const kind = data.indexOf(";");
    const title = kind < 0 ? -1 : data.indexOf(";", kind + 1);
    if (kind >= 0 && data.slice(0, kind) === "notify" && title >= 0) {
      send({ type: "notify", sessionId: entry.sessionId, pane: entry.pane, focused: hasKeyboard(entry), title: data.slice(kind + 1, title), body: data.slice(title + 1) });
    }
    return true;
  });
  // OSC 52 and the Kitty clipboard protocol (OSC 5522) go to the app, which checks them, asks the user and answers.
  term.parser.registerOscHandler(52, (data) => {
    send({ type: "osc52", sessionId: entry.sessionId, pane: entry.pane, data });
    return true;
  });
  term.parser.registerOscHandler(5522, (data) => {
    send({ type: "osc5522", sessionId: entry.sessionId, pane: entry.pane, data, bel: entry.kittyClipboardEnds.take() });
    return true;
  });
  term.onTitleChange((title) => send({ type: "title", sessionId: entry.sessionId, pane: entry.pane, title }));
  term.parser.registerOscHandler(7, (data) => {
    const path = pathFromFileUrl(data);
    if (path) {
      send({ type: "cwd", sessionId: entry.sessionId, pane: entry.pane, path });
    }
    return true;
  });

  return element;
}

/** agterm's AccessibilityInsert.needsPasteRouting: a line break of any kind, or any other C0 control or DEL. */
function needsPasteRouting(text: string): boolean {
  return /[\u0000-\u001f\u007f\u0085\u2028\u2029]/.test(text);
}

/** Opens the terminal once its element is in the document, without starting its shell. */
export function openTerminal(key: string): void {
  const entry = entries.get(key);
  if (!entry || entry.term.element || !entry.element.isConnected) {
    return;
  }
  entry.term.open(entry.element);
  entry.term.textarea?.addEventListener("focus", () => paneFocusListener(entry.sessionId, entry.pane));
}

/** Opens the terminal once its element is in the document, then starts the shell with the fitted size. */
export function attach(key: string): void {
  const entry = entries.get(key);
  if (!entry || entry.attached || !entry.element.isConnected) {
    return;
  }
  openTerminal(key);
  fitEntry(entry);
  entry.attached = true;
  send({ type: "attach", sessionId: entry.sessionId, pane: entry.pane, cols: entry.term.cols, rows: entry.term.rows });
}

function exchange(sessionId: string, first: Pane, second: Pane): void {
  const a = entries.get(paneKey(sessionId, first));
  const b = entries.get(paneKey(sessionId, second));
  entries.delete(paneKey(sessionId, first));
  entries.delete(paneKey(sessionId, second));
  if (a) {
    a.pane = second;
    entries.set(paneKey(sessionId, second), a);
  }
  if (b) {
    b.pane = first;
    entries.set(paneKey(sessionId, first), b);
  }
}

/** Exchanges the terminals of a split's two panes and of their overlays, following their shells. */
export function swapPanes(sessionId: string): void {
  exchange(sessionId, "left", "right");
  exchange(sessionId, "overlay-left", "overlay-right");
}

/** The main shell ended and the split's terminal, with its overlay, becomes the session's main one. */
export function promoteSplit(sessionId: string): void {
  disposeTerminal(paneKey(sessionId, "left"));
  disposeTerminal(paneKey(sessionId, "overlay-left"));
  exchange(sessionId, "left", "right");
  exchange(sessionId, "overlay-left", "overlay-right");
}

/** Starts a new shell in an existing terminal after the previous one exited. */
export function reattach(key: string): void {
  const entry = entries.get(key);
  if (!entry) {
    return;
  }
  entry.term.reset();
  entry.cursorHidden = false;
  entry.cursorOverride = false;
  entry.cursorBlinkOverride = null;
  paint(entry, true);
  entry.attached = false;
  // agterm's fresh surface: it attaches at its own size, not the leader's the covered view was pinned to.
  pinnedSizes.delete(key);
  attach(key);
}

// Browsers cap the number of live WebGL contexts, so only the visible terminal gets one.
export function activate(key: string, focus: boolean): void {
  const entry = entries.get(key);
  if (!entry || !entry.term.element) {
    return;
  }
  if (!entry.webgl) {
    logOcclusion(key, true);
    try {
      const webgl = new WebglAddon();
      webgl.onContextLoss(() => {
        webgl.dispose();
        entry.webgl = null;
      });
      entry.term.loadAddon(webgl);
      entry.webgl = webgl;
    } catch {
      entry.webgl = null;
    }
  }
  fitEntry(entry);
  if (focus) {
    entry.term.focus();
  }
}

export function deactivate(key: string): void {
  const entry = entries.get(key);
  if (entry?.webgl) {
    logOcclusion(key, false);
    entry.webgl.dispose();
    entry.webgl = null;
  }
}

/** agterm's renderer-visibility debug line: a terminal's renderer goes on or off as it is shown or hidden. */
function logOcclusion(key: string, visible: boolean): void {
  const cut = key.lastIndexOf(":");
  const message = `occlusion ${visible ? "visible" : "hidden"} session=${key.slice(0, cut)} split=${key.slice(cut + 1) === "right"}`;
  command({ command: "log", level: "debug", category: "renderer-visibility", message });
}

export function fitActive(key: string | null): void {
  const entry = key ? entries.get(key) : undefined;
  if (entry) {
    fitEntry(entry);
  }
}

/**
 * agterm keeps every session's surface mounted at the window's size, so a resize reaches programs in the background at
 * once: every started terminal under the container is fitted, not only the ones on screen.
 */
export function fitAllIn(container: HTMLElement): void {
  for (const entry of entries.values()) {
    if (entry.attached && entry.term.element && container.contains(entry.element)) {
      fitEntry(entry);
    }
  }
}

/** Fits the terminal to its element, unless another machine leads its pane: then it keeps the leader's size. */
function fitEntry(entry: Entry): void {
  const pinned = pinnedSizes.get(paneKey(entry.sessionId, entry.pane));
  if (pinned) {
    if (entry.term.cols !== pinned[0] || entry.term.rows !== pinned[1]) {
      entry.term.resize(pinned[0], pinned[1]);
    }
  } else {
    entry.fit.fit();
  }
}

const pinnedSizes = new Map<string, [number, number]>();
let coveredKeys = new Set<string>();
let waitingKeys = new Set<string>();
/** The key whose keydown took the lead or retried a connection: its repeats and its release are swallowed, as agterm's. */
let latchedCode: string | null = null;

/** agterm's cover and reconnect wait: which panes are led elsewhere (with the leader's size) and which wait for their machine. */
export function setRemoteGates(covered: Map<string, [number, number] | null>, waiting: Set<string>): void {
  coveredKeys = new Set(covered.keys());
  waitingKeys = waiting;
  for (const key of [...pinnedSizes.keys()]) {
    if (!covered.get(key)) {
      pinnedSizes.delete(key);
      const entry = entries.get(key);
      if (entry && entry.element.classList.contains("active")) {
        fitEntry(entry);
      }
    }
  }
  for (const [key, size] of covered) {
    if (size) {
      pinnedSizes.set(key, size);
      const entry = entries.get(key);
      if (entry) {
        fitEntry(entry);
      }
    }
  }
}

function remoteGate(entry: Entry, event: KeyboardEvent): boolean | null {
  if (latchedCode !== null && event.code === latchedCode) {
    if (event.type === "keyup") {
      latchedCode = null;
    }
    event.preventDefault();
    return false;
  }
  const key = paneKey(entry.sessionId, entry.pane);
  const covered = coveredKeys.has(key);
  if (!covered && !waitingKeys.has(key)) {
    return null;
  }
  if (event.type !== "keydown" || ["Shift", "Control", "Alt", "Meta"].includes(event.key)) {
    return null;
  }
  if (handleAppShortcut(event)) {
    event.preventDefault();
    return false;
  }
  if (!event.repeat) {
    latchedCode = event.code;
    command(covered ? { command: "takeLead", sessionId: entry.sessionId, pane: entry.pane } : { command: "retryRemote", sessionId: entry.sessionId, pane: entry.pane });
  }
  event.preventDefault();
  return false;
}

export function focusTerminal(key: string | null): void {
  if (key) {
    entries.get(key)?.term.focus();
  }
}

/**
 * The terminal's text with wrapped rows joined: the visible screen, or the whole buffer, optionally only its last lines.
 * Trailing empty rows are dropped.
 */
/** The pane's text; null when it has no terminal, which the host reports as not realized. */
export function readText(key: string, all: boolean, lines: number | null): string | null {
  const entry = entries.get(key);
  return entry ? textOf(entry.term, all, lines) : null;
}

function textOf(term: Terminal, all: boolean, lines: number | null): string {
  const buffer = term.buffer.active;
  // agterm's readScreenText: the screen as the user sees it, scrolled back or not; all of it with --all or --lines.
  const start = all || lines !== null ? 0 : buffer.viewportY;
  const end = all || lines !== null ? buffer.length : buffer.viewportY + term.rows;
  const result: string[] = [];
  for (let y = start; y < end; y++) {
    const line = buffer.getLine(y);
    if (!line) {
      continue;
    }
    const text = line.translateToString(true);
    if (line.isWrapped && result.length > 0) {
      result[result.length - 1] += text;
    } else {
      result.push(text);
    }
  }
  if (lines === null) {
    return result.join("\n");
  }
  // With --lines, the last lines with something on them: the blank rows below are padding.
  while (result.length > 0 && result[result.length - 1].trim() === "") {
    result.pop();
  }
  return result.slice(-lines).join("\n");
}

/** Paints a terminal's background, for overlays opened with their own color; null restores the theme. */
export function getSelection(key: string): string {
  return entries.get(key)?.term.getSelection() ?? "";
}

export function readSelection(key: string): string | null {
  return entries.get(key)?.term.getSelection() ?? null;
}

export function setBackground(key: string, color: string | null): void {
  const entry = entries.get(key);
  if (!entry) {
    return;
  }
  if (entry.ownBackground === color) {
    return;
  }
  entry.ownBackground = color;
  paint(entry);
}

export function setFontSize(key: string, size: number): void {
  const entry = entries.get(key);
  if (!entry || entry.term.options.fontSize === size) {
    return;
  }
  entry.term.options.fontSize = size;
  if (entry.term.element) {
    fitEntry(entry);
  }
}

/** Searches the terminal and resolves with the "N of M" counter once the addon has counted the matches. */
export function searchTerminal(key: string, needle: string, step: "next" | "prev" | "incremental"): Promise<SearchResults> {
  const entry = entries.get(key);
  if (!entry || !needle) {
    entry?.search.clearDecorations();
    if (entry) {
      entry.results = { index: -1, count: 0 };
      searchListeners.forEach((listener) => listener());
    }
    return Promise.resolve({ index: -1, count: 0 });
  }
  return new Promise((resolve) => {
    let done = false;
    const finish = () => {
      if (!done) {
        done = true;
        searchListeners.delete(finish);
        resolve(entry.results);
      }
    };
    searchListeners.add(finish);
    setTimeout(finish, 500);
    if (step === "prev") {
      entry.search.findPrevious(needle, searchOptions);
    } else {
      entry.search.findNext(needle, { ...searchOptions, incremental: step === "incremental" });
    }
  });
}

export function clearSearch(key: string): void {
  const entry = entries.get(key);
  if (entry) {
    entry.search.clearDecorations();
    entry.term.clearSelection();
    entry.results = { index: -1, count: 0 };
  }
}

export function searchResults(key: string | null): SearchResults {
  return (key ? entries.get(key)?.results : undefined) ?? { index: -1, count: 0 };
}

export function onSearchResults(listener: () => void): () => void {
  searchListeners.add(listener);
  return () => searchListeners.delete(listener);
}

export function writeOutput(key: string, data: string): void {
  const entry = entries.get(key);
  entry?.kittyClipboardEnds.scan(data);
  entry?.term.write(data);
}

export function disposeTerminal(key: string): void {
  const entry = entries.get(key);
  if (!entry) {
    return;
  }
  entries.delete(key);
  entry.term.dispose();
  entry.element.remove();
}

/** The terminal an element sits in, for a drop. */
export function terminalAt(element: Element): { sessionId: string; pane: Pane } | null {
  for (const entry of entries.values()) {
    if (entry.element.contains(element)) {
      return { sessionId: entry.sessionId, pane: entry.pane };
    }
  }
  return null;
}

export function knownTerminals(): string[] {
  return [...entries.keys()];
}

// OSC 7 carries file://host/path, percent-encoded, or kitty-shell-cwd://host/path as it is, the form ghostty's shell
// integration sends; a Windows path arrives as /C:/dir, a share as ///server/share.
function pathFromFileUrl(data: string): string | null {
  const match = /^(file|kitty-shell-cwd):\/\/[^/]*(\/.*)$/i.exec(data);
  if (!match) {
    return null;
  }
  let path = match[2];
  if (match[1].toLowerCase() === "file") {
    try {
      path = decodeURIComponent(path);
    } catch {
      return null;
    }
  }
  if (/^\/[A-Za-z]:/.test(path)) {
    return path.slice(1).replace(/\//g, "\\");
  }
  return path.startsWith("///") ? "\\\\" + path.slice(3).replace(/\//g, "\\") : path;
}

function handleKey(term: Terminal, event: KeyboardEvent): boolean {
  if (handleAppShortcut(event)) {
    event.preventDefault();
    return false;
  }

  if (event.type !== "keydown" || !event.ctrlKey || event.altKey || event.metaKey) {
    return true;
  }

  // As agterm's copy, the selection stays after it is copied.
  if (event.code === "KeyC" && (event.shiftKey || term.hasSelection())) {
    const text = term.getSelection();
    if (text) {
      navigator.clipboard.writeText(text).catch(() => {});
    }
    event.preventDefault();
    return false;
  }

  // agterm's select all is Cmd+A; Ctrl+A belongs to the shell, so Ctrl+Shift+A, as in Windows Terminal.
  if (event.code === "KeyA" && event.shiftKey) {
    term.selectAll();
    event.preventDefault();
    return false;
  }

  if (event.code === "KeyV") {
    // let the browser fire a paste event, which xterm.js turns into input
    return false;
  }

  return true;
}
