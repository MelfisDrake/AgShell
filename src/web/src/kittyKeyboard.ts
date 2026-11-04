// The Kitty keyboard protocol as Ghostty, under agterm, implements it (src/input/key_encode.zig, kitty.zig and
// terminal/kitty/key.zig): the flag stacks programs push and pop, and the encoding of key events while flags are set.
// xterm.js has no such protocol, so the terminal's key handler asks this module first.

/** Ghostty's FlagStack: eight entries in a ring, the oldest evicted on overflow. */
export class KittyFlagStack {
  private flags = [0, 0, 0, 0, 0, 0, 0, 0];
  private index = 0;

  current(): number {
    return this.flags[this.index];
  }

  /** CSI = flags ; mode u: 1 sets, 2 adds bits, 3 removes them. */
  set(mode: number, value: number): void {
    const flags = value & 31;
    if (mode === 2) {
      this.flags[this.index] |= flags;
    } else if (mode === 3) {
      this.flags[this.index] &= ~flags;
    } else if (mode === 1) {
      this.flags[this.index] = flags;
    }
  }

  push(value: number): void {
    this.index = (this.index + 1) % 8;
    this.flags[this.index] = value & 31;
  }

  pop(count: number): void {
    if (count >= 8) {
      this.reset();
      return;
    }
    for (let n = 0; n < count; n++) {
      this.flags[this.index] = 0;
      this.index = (this.index + 7) % 8;
    }
  }

  reset(): void {
    this.flags = [0, 0, 0, 0, 0, 0, 0, 0];
    this.index = 0;
  }
}

const DISAMBIGUATE = 1;
const REPORT_EVENTS = 2;
const REPORT_ALTERNATES = 4;
const REPORT_ALL = 8;
const REPORT_ASSOCIATED = 16;

interface Entry {
  code: number;
  final: string;
  modifier: boolean;
}

/** Ghostty's kitty.zig table, by the physical key's KeyboardEvent.code. */
const entries: Record<string, Entry> = (() => {
  const table: Record<string, Entry> = {};
  const add = (codes: string[], code: number, final: string, modifier = false) => codes.forEach((name) => (table[name] = { code, final, modifier }));
  add(["Escape"], 27, "u");
  add(["Enter"], 13, "u");
  add(["Tab"], 9, "u");
  add(["Backspace"], 127, "u");
  add(["Insert"], 2, "~");
  add(["Delete"], 3, "~");
  add(["ArrowLeft"], 1, "D");
  add(["ArrowRight"], 1, "C");
  add(["ArrowUp"], 1, "A");
  add(["ArrowDown"], 1, "B");
  add(["PageUp"], 5, "~");
  add(["PageDown"], 6, "~");
  add(["Home"], 1, "H");
  add(["End"], 1, "F");
  add(["CapsLock"], 57358, "u", true);
  add(["ScrollLock"], 57359, "u");
  add(["NumLock"], 57360, "u", true);
  add(["PrintScreen"], 57361, "u");
  add(["Pause"], 57362, "u");
  const fkeys: [number, string][] = [[1, "P"], [1, "Q"], [13, "~"], [1, "S"], [15, "~"], [17, "~"], [18, "~"], [19, "~"], [20, "~"], [21, "~"], [23, "~"], [24, "~"]];
  fkeys.forEach(([code, final], index) => add([`F${index + 1}`], code, final));
  for (let n = 13; n <= 25; n++) {
    add([`F${n}`], 57376 + n - 13, "u");
  }
  for (let n = 0; n <= 9; n++) {
    add([`Numpad${n}`], 57399 + n, "u");
  }
  add(["NumpadDecimal"], 57409, "u");
  add(["NumpadDivide"], 57410, "u");
  add(["NumpadMultiply"], 57411, "u");
  add(["NumpadSubtract"], 57412, "u");
  add(["NumpadAdd"], 57413, "u");
  add(["NumpadEnter"], 57414, "u");
  add(["NumpadEqual"], 57415, "u");
  add(["NumpadComma"], 57416, "u");
  add(["ShiftLeft"], 57441, "u", true);
  add(["ShiftRight"], 57447, "u", true);
  add(["ControlLeft"], 57442, "u", true);
  add(["ControlRight"], 57448, "u", true);
  add(["MetaLeft"], 57444, "u", true);
  add(["MetaRight"], 57450, "u", true);
  add(["AltLeft"], 57443, "u", true);
  add(["AltRight"], 57449, "u", true);
  return table;
})();

/** The PC-101 US key each physical key carries: Ghostty's key.codepoint(), the base-layout key of alternates. */
const usLayout: Record<string, string> = (() => {
  const table: Record<string, string> = { Space: " ", Minus: "-", Equal: "=", BracketLeft: "[", BracketRight: "]", Backslash: "\\", Semicolon: ";", Quote: "'", Backquote: "`", Comma: ",", Period: ".", Slash: "/", IntlBackslash: "\\" };
  for (let c = 97; c <= 122; c++) {
    table[`Key${String.fromCharCode(c).toUpperCase()}`] = String.fromCharCode(c);
  }
  for (let d = 0; d <= 9; d++) {
    table[`Digit${d}`] = String(d);
  }
  return table;
})();

let layout: Map<string, string> | null = null;

/** The active layout's unshifted character per physical key, as Ghostty gets it from the system keymap. */
export function refreshKeyboardLayout(): void {
  const keyboard = (navigator as Navigator & { keyboard?: { getLayoutMap(): Promise<Map<string, string>> } }).keyboard;
  keyboard
    ?.getLayoutMap()
    .then((map) => {
      layout = map;
    })
    .catch(() => {});
}

function isControl(cp: number): boolean {
  return cp < 0x20 || cp === 0x7f;
}

function codepoints(text: string): number[] {
  return [...text].map((char) => char.codePointAt(0)!);
}

/**
 * Ghostty's kitty(): what the key event sends to the program under the given flags; null when the terminal should send
 * nothing for it (a modifier alone without report-all, a release without report-events).
 */
export function encodeKey(event: KeyboardEvent, flags: number): string | null {
  const release = event.type === "keyup";
  const action = release ? 3 : event.repeat ? 2 : 1;
  if (release) {
    if (!(flags & REPORT_EVENTS)) {
      return null;
    }
    if (!(flags & REPORT_ALL) && ["Enter", "Backspace", "Tab"].includes(event.code)) {
      return null;
    }
  }

  // The text the key makes: a printable character; Ctrl and Alt chords carry their letter here, as Ghostty's utf8 does.
  const altGraph = event.getModifierState("AltGraph");
  const utf8 = [...event.key].length === 1 && !isControl(event.key.codePointAt(0)!) ? event.key : "";
  const unshifted = (layout?.get(event.code) ?? usLayout[event.code] ?? (utf8 ? utf8.toLowerCase() : "")).toLowerCase();
  const unshiftedCp = unshifted ? unshifted.codePointAt(0)! : 0;

  const mods = {
    shift: event.shiftKey,
    alt: event.altKey && !altGraph,
    ctrl: event.ctrlKey && !altGraph,
    super: event.metaKey,
    capsLock: event.getModifierState("CapsLock"),
    numLock: event.getModifierState("NumLock"),
  };
  // Ghostty's binding mods are the effective ones: a modifier the text consumed (Shift turning a into A, AltGr making
  // a character) does not count.
  const shiftConsumed = utf8 !== "" && utf8 !== unshifted;
  const binding = (mods.shift && !shiftConsumed) || mods.alt || mods.ctrl || mods.super;

  const entry: Entry | null = entries[event.code] ?? (unshiftedCp > 0 ? { code: unshiftedCp, final: "u", modifier: false } : null);

  if (!(flags & REPORT_ALL)) {
    // Enter, Tab and Backspace keep their legacy bytes without modifiers, so a shell left in this mode still runs reset.
    if (!binding) {
      if (event.code === "Enter") {
        return "\r";
      }
      if (event.code === "Tab") {
        return "\t";
      }
      if (event.code === "Backspace") {
        return "\x7f";
      }
    }
    if (utf8 && !binding && !release) {
      return utf8;
    }
  }

  if (!entry) {
    return release || !utf8 ? null : utf8;
  }
  if (entry.modifier && !(flags & REPORT_ALL)) {
    return null;
  }

  const modBits = (mods.shift ? 1 : 0) | (mods.alt ? 2 : 0) | (mods.ctrl ? 4 : 0) | (mods.super ? 8 : 0) | (mods.capsLock ? 64 : 0) | (mods.numLock ? 128 : 0);
  const modValue = modBits + 1;
  const eventType = flags & REPORT_EVENTS ? action : 0;

  if (entry.final !== "u" && entry.final !== "~") {
    if (eventType !== 0) {
      return `\x1b[1;${modValue}:${eventType}${entry.final}`;
    }
    return modValue > 1 ? `\x1b[1;${modValue}${entry.final}` : `\x1b[${entry.final}`;
  }

  let key = `${entry.code}`;
  if (flags & REPORT_ALTERNATES && !isControl(entry.code)) {
    const chars = codepoints(utf8);
    let shifted: number | null = null;
    let base: number | null = null;
    const us = usLayout[event.code];
    const baseCp = us ? us.codePointAt(0)! : null;
    if (chars.length > 0) {
      if (chars[0] !== entry.code && mods.shift) {
        shifted = chars[0];
      }
      if (baseCp !== null && baseCp !== entry.code && chars[0] !== baseCp && chars.length === 1) {
        base = baseCp;
      }
    } else if (baseCp !== null && baseCp !== entry.code) {
      base = baseCp;
    }
    if (shifted !== null) {
      key += `:${shifted}`;
    }
    if (base !== null) {
      key += shifted === null ? `::${base}` : `:${base}`;
    }
  }

  let text = "";
  if (flags & REPORT_ASSOCIATED && eventType !== 3 && !(mods.alt || mods.ctrl || mods.super)) {
    text = codepoints(utf8)
      .filter((cp) => !isControl(cp))
      .join(":");
  }

  let sequence = `\x1b[${key}`;
  let emitted = false;
  if (eventType !== 0 && eventType !== 1) {
    sequence += `;${modValue}:${eventType}`;
    emitted = true;
  } else if (modValue > 1) {
    sequence += `;${modValue}`;
    emitted = true;
  }
  if (text) {
    sequence += (emitted ? ";" : ";;") + text;
  }
  return sequence + entry.final;
}

export const kittyFlags = { DISAMBIGUATE, REPORT_EVENTS, REPORT_ALTERNATES, REPORT_ALL, REPORT_ASSOCIATED };
