import { AppState, command, KeyBindingView, KeyChordView, onHostMessage, send, ShortcutSnapshot } from "./bridge";
import { actionAvailable, closeTopmost, findAction, quickActions } from "./actions";
import { currentState, uiActionsEnabled } from "./store";
import { cancelSwitcher, stepSwitcher, switcherActive } from "./switcher";
import { firingTerminal } from "./terminals";
import { KeybindMatcher, sameChord as same } from "./keybindMatcher";

const leaderTimeout = 1500;
const repeatTimeout = 500;
const matcher = new KeybindMatcher();
const consumed = new Set<string>();
const menuOwned = new Set<string>();
const releasedOwnership = new Set<string>();
let timer: ReturnType<typeof setTimeout> | undefined;
let heldRepeatKey: string | null = null;
let deadline: number | null = null;
const origin = crypto.randomUUID();
let sequence = 0;
let hostRevision = -1;
let lastPublished = "";

function publish(reason: "key" | "reset" | "timer"): void {
  const snapshot: ShortcutSnapshot = { pending: matcher.pending, repeatPrefix: matcher.repeatPrefix, heldRepeatKey, consumed: [...consumed], menuOwned: [...menuOwned], deadline };
  const text = JSON.stringify(snapshot);
  if (text === lastPublished) return;
  lastPublished = text;
  send({ type: "shortcutState", origin, sequence: ++sequence, reason, snapshot });
}

function cancelTimer(): void {
  if (timer !== undefined) clearTimeout(timer);
  timer = undefined;
  deadline = null;
}

export function resetShortcutState(): void {
  matcher.reset();
  heldRepeatKey = null;
  cancelTimer();
  publish("reset");
}

function expireAfter(milliseconds: number): void {
  cancelTimer();
  deadline = Date.now() + milliseconds;
  timer = setTimeout(() => {
    matcher.reset();
    heldRepeatKey = null;
    cancelTimer();
    publish("timer");
  }, milliseconds);
}

function closeRepeatWindow(): void {
  if (matcher.repeatPrefix) resetShortcutState();
  heldRepeatKey = null;
  publish("reset");
}

export function startShortcutMonitoring(): void {
  document.addEventListener("focusin", () => {
    if (inTextField() && document.hasFocus()) resetShortcutState();
  });
  window.addEventListener("keyup", (event) => {
    if ((event.target as Element | null)?.closest?.(".xterm")) return;
    if (handleAppShortcut(event)) {
      event.preventDefault();
      event.stopPropagation();
    }
  });
  if (location.hash === "#settings") window.addEventListener("keydown", () => resetShortcutState(), true);
  onHostMessage((message) => {
    if (message.type === "shortcutState") {
      if (message.revision <= hostRevision) return;
      hostRevision = message.revision;
      if (message.origin === origin && message.sequence <= sequence) return;
      message.released?.forEach((key) => releasedOwnership.add(key));
      cancelTimer();
      matcher.pending = message.snapshot.pending;
      matcher.repeatPrefix = message.snapshot.repeatPrefix ?? null;
      heldRepeatKey = message.snapshot.heldRepeatKey ?? null;
      consumed.clear();
      message.snapshot.consumed.forEach((key) => consumed.add(key));
      menuOwned.clear();
      message.snapshot.menuOwned.forEach((key) => menuOwned.add(key));
      if (message.snapshot.deadline != null) expireAfter(Math.max(0, message.snapshot.deadline - Date.now()));
      lastPublished = JSON.stringify({ pending: matcher.pending, repeatPrefix: matcher.repeatPrefix, heldRepeatKey, consumed: [...consumed], menuOwned: [...menuOwned], deadline });
    } else if (message.type === "keymapChanged") resetShortcutState();
    else if (message.type === "menuTracking") closeRepeatWindow();
    else if (message.type === "windowActive" && !message.value && !message.appActive) closeRepeatWindow();
  });
}

function chordOf(event: KeyboardEvent): KeyChordView {
  const named: Record<string, string> = { " ": "Space", Spacebar: "Space" };
  const key = named[event.key] ?? event.key;
  const isNamed = /^(?:Arrow(?:Left|Right|Up|Down)|F(?:[1-9]|1[0-9]|20)|Tab|Enter|Escape|Backspace|Delete|Home|End|Insert|PageUp|PageDown|Space)$/.test(key);
  return { meta: event.metaKey, ctrl: event.ctrlKey, alt: event.altKey, shift: event.shiftKey, code: isNamed ? key : event.code };
}

interface Bound {
  binding: KeyBindingView;
  run: () => void;
  menu: boolean;
}

function bound(state: AppState): Bound[] {
  const list: Bound[] = [];
  if (location.hash === "#settings") {
    for (const [name, binding] of Object.entries(state.keymap.menu)) {
      if (!binding || name === "undo_close" || name === "toggle_fullscreen") continue;
      const close = name === "close_session" && binding.sequence.length === 1 && same(binding.sequence[0], { ctrl: true, alt: false, shift: true, code: "KeyW" });
      list.push({ binding, menu: true, run: () => command(close ? { command: "closeSettings" } : { command: "settingsMenuAction", name }) });
    }
    return list;
  }
  for (const [name, bindings] of Object.entries(state.keymap.actions)) {
    if (location.hash === "#quick") {
      // The panel's own actions act on it; the rest go to the active window, as agterm's menu does.
      const run = quickActions[name] ?? (() => command({ command: "quickAction", name }));
      bindings.forEach((binding) => list.push({ binding, run, menu: state.keymap.menu[name]?.label === binding.label }));
      continue;
    }
    // agterm's undo-close monitor: only while a close can be undone and never in a text field, whose own undo the key keeps.
    if (name === "undo_close" && (state.pendingClose === null || inTextField())) {
      continue;
    }
    const action = findAction(name);
    if (action) {
      // agterm's key monitor runs close_session as the menu does: with nothing left to close, the window closes.
      const run = name === "close_session" ? () => closeTopmost(true) : () => action.run(state);
      bindings.forEach((binding) => list.push({ binding, run: () => actionAvailable(action, state) && run(), menu: state.keymap.menu[name]?.label === binding.label }));
    }
  }
  for (const custom of state.keymap.commands) {
    custom.bindings.forEach((binding) => list.push({ binding, run: () => runCustom(custom.name, true), menu: false }));
  }
  return list;
}

/**
 * agterm's stock menu chords ⌘,, ⌘Q and ⌘M, with Shift for the last two since Ctrl+Q and Ctrl+M belong to the terminal.
 * Quit and Minimize go to a map that binds the same chord (stockMenuChords); Settings keeps its chord, which that list
 * leaves out.
 */
const stockChords: { chord: KeyChordView; syntax: string; id: string; yields: boolean }[] = [
  { chord: { ctrl: false, alt: true, shift: false, code: "F4" }, syntax: "alt+f4", id: "close_window", yields: false },
  { chord: { ctrl: true, alt: false, shift: false, code: "Comma" }, syntax: "ctrl+,", id: "open_settings", yields: false },
  { chord: { ctrl: true, alt: false, shift: true, code: "KeyQ" }, syntax: "ctrl+shift+q", id: "quit", yields: true },
  { chord: { ctrl: true, alt: false, shift: true, code: "KeyM" }, syntax: "ctrl+shift+m", id: "minimize", yields: true },
];

/** The stock chord an item shows in the menu, unless a map has taken it. */
export function stockChordLabel(state: AppState, id: string): string | undefined {
  const stock = stockChords.find((entry) => entry.id === id);
  return stock && !(stock.yields && claimed(state, stock.chord)) ? chordLabel(stock.chord) : undefined;
}

function claimed(state: AppState, chord: KeyChordView): boolean {
  return Object.values(state.keymap.menu).some((binding) => binding && same(binding.sequence[0], chord));
}

export function stockChordSyntax(state: AppState, id: string): string | undefined {
  const stock = stockChords.find((entry) => entry.id === (id === "host:minimize" ? "minimize" : id));
  return stock && !(stock.yields && claimed(state, stock.chord)) ? stock.syntax : undefined;
}

function runStock(id: string): void {
  if (id === "close_window") {
    command(location.hash === "#settings" ? { command: "closeSettings" }
      : location.hash === "#quick" ? { command: "setQuick", shown: false } : { command: "closeWindow" });
    return;
  }
  if (location.hash === "#settings") {
    command({ command: "settingsMenuAction", name: id });
    return;
  }
  if (id === "minimize") {
    if (location.hash !== "#quick") {
      command({ command: "minimizeWindow" });
    }
    return;
  }
  if (location.hash === "#quick") {
    command({ command: "quickAction", name: id });
    return;
  }
  const state = currentState();
  const action = findAction(id);
  if (state && action && actionAvailable(action, state)) {
    action.run(state);
  }
}

/** A field of the interface has the keyboard: a terminal's own input area is not one. */
function inTextField(): boolean {
  const element = document.activeElement;
  return element instanceof HTMLElement && (element.matches("input, textarea") || element.isContentEditable) && !element.closest(".xterm");
}

/** Keybinds carry their firing surface; palette and sessionless calls resolve the active pane in the host. */
export function runCustom(name: string, keybind = false): void {
  const state = currentState();
  if (!state || (!keybind && (!uiActionsEnabled(state) || state.selectedSessionId === null))) return;
  const source = keybind ? "keybind" : "palette";
  if (location.hash === "#quick") {
    command({ command: "runCustom", name, source });
    return;
  }
  const firing = keybind ? firingTerminal() : null;
  if (firing && firing.pane !== "quick") {
    const session = state.workspaces.flatMap((workspace) => workspace.sessions).find((session) => session.id === firing.sessionId);
    const pane = firing.pane.startsWith("overlay")
      ? session?.scratchShown ? "scratch" : firing.pane === "overlay-left" ? "left" : firing.pane === "overlay-right" ? "right" : session?.focusedPane === "right" ? "right" : "left"
      : firing.pane;
    command({ command: "runCustom", name, source, sessionId: firing.sessionId, pane, selection: firing.selection });
  } else {
    command({ command: "runCustom", name, source, sessionId: state.selectedSessionId ?? undefined });
  }
}

/** Which of these actions a single key is bound to in keymap.conf, if any. */
export function boundAction(event: KeyboardEvent, ids: string[]): string | undefined {
  const state = currentState();
  const chord = chordOf(event);
  return ids.find((id) => (state?.keymap.actions[id] ?? []).some((binding) => binding.sequence.length === 1 && same(binding.sequence[0], chord)));
}

const modifierCodes = new Set(["ControlLeft", "ControlRight", "ShiftLeft", "ShiftRight", "AltLeft", "AltRight", "MetaLeft", "MetaRight"]);

/** Consumed presses keep their repeats and releases; a live repeat tail may fire again. */
export function handleAppShortcut(event: KeyboardEvent): boolean {
  if (deadline !== null && Date.now() >= deadline) {
    matcher.reset();
    heldRepeatKey = null;
    cancelTimer();
    publish("timer");
  }
  const chord = chordOf(event);
  const key = event.code || chord.code;
  if (event.type === "keyup") {
    releasedOwnership.delete(key);
    if (heldRepeatKey === key) {
      heldRepeatKey = null;
      if (matcher.repeatPrefix) expireAfter(repeatTimeout);
    }
    const owned = consumed.delete(key);
    menuOwned.delete(key);
    publish("key");
    return owned;
  }
  if (event.type !== "keydown" || modifierCodes.has(event.code) || ["Control", "Alt", "Shift", "Meta"].includes(event.key)) return false;
  const state = currentState();
  const released = releasedOwnership.delete(key);
  if (event.repeat && !released) {
    if (!consumed.has(key)) return false;
    let result: ShortcutAction | false = false;
    if (state && matcher.repeatTail(chord, bound(state))) result = feed(event, state, chord, key);
    else if (state && menuOwned.has(key)) {
      const binding = bound(state).find((entry) => entry.menu && entry.binding.sequence.length === 1 && same(entry.binding.sequence[0], chord));
      const stock = stockChords.find((entry) => same(entry.chord, chord));
      if (binding) result = { run: binding.run, menu: true };
      else if (stock) result = { run: () => runStock(stock.id), menu: true };
    }
    publish("key");
    if (result) result.run?.();
    return true;
  }
  consumed.delete(key);
  menuOwned.delete(key);
  const result = state ? feed(event, state, chord, key) : false;
  if (result) {
    consumed.add(key);
    if (result.menu && !/^F(?:[1-9]|1[0-9]|20)$/.test(chord.code)) menuOwned.add(key);
  }
  publish("key");
  if (result) result.run?.();
  return result !== false;
}

interface ShortcutAction { run?: () => void; menu: boolean }

function feed(event: KeyboardEvent, state: AppState, chord: KeyChordView, key: string): ShortcutAction | false {
  if (location.hash === "#settings" && event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey && event.code === "KeyW") {
    return { run: () => command({ command: "closeSettings" }), menu: true };
  }
  if (location.hash !== "#settings") {
    if (switcherActive() && event.key === "Escape") {
      resetShortcutState();
      return { run: () => cancelSwitcher(), menu: false };
    }
    if (event.ctrlKey && !event.altKey && event.key === "Tab") {
      return { run: () => stepSwitcher(event.shiftKey), menu: false };
    }
    if (event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey && (event.code === "Digit1" || event.code === "Digit2")) {
      const action = findAction(event.code === "Digit1" ? "focus_left_pane" : "focus_right_pane");
      return { run: () => { if (action && actionAvailable(action, state)) action.run(state); }, menu: false };
    }
  }
  const field = inTextField() || location.hash === "#settings";
  if (field) resetShortcutState();
  if (event.key === "Escape") {
    const armed = matcher.pending.length > 0;
    resetShortcutState();
    return armed ? { menu: false } : false;
  }
  const bindings = bound(state).filter((entry) => !field || entry.menu);
  const wasArmed = matcher.pending.length > 0;
  const stock = !wasArmed ? stockChords.find((entry) => same(entry.chord, chord)) : undefined;
  if (stock && !stock.yields) {
    resetShortcutState();
    return { run: () => runStock(stock.id), menu: true };
  }
  const result = matcher.advance(chord, bindings);
  if (result === "armed") {
    expireAfter(leaderTimeout);
    return { menu: false };
  }
  cancelTimer();
  if (result) {
    heldRepeatKey = matcher.repeatPrefix ? key : null;
    return { run: result.run, menu: result.menu };
  }
  heldRepeatKey = null;
  if (stock) {
    return { run: () => runStock(stock.id), menu: true };
  }
  return false;
}

const keyLabels: Record<string, string> = {
  ArrowLeft: "←",
  ArrowRight: "→",
  ArrowUp: "↑",
  ArrowDown: "↓",
  Backquote: "`",
  Equal: "=",
  Minus: "-",
  BracketLeft: "[",
  BracketRight: "]",
  Backslash: "\\",
  Semicolon: ";",
  Quote: "'",
  Comma: ",",
  Period: ".",
  Slash: "/",
  NumpadAdd: "Num+",
  NumpadSubtract: "Num-",
  NumpadMultiply: "Num*",
  NumpadDivide: "Num/",
  NumpadDecimal: "Num.",
  NumpadEnter: "NumEnter",
};

function keyLabel(code: string): string {
  if (keyLabels[code]) {
    return keyLabels[code];
  }
  const match = /^(?:Key|Digit)(.)$/.exec(code) ?? /^Numpad(\d)$/.exec(code);
  return match ? (code.startsWith("Numpad") ? "Num" : "") + match[1] : code;
}

export function chordLabel(chord: KeyChordView): string {
  return [chord.ctrl && "Ctrl", chord.meta && "Win", chord.alt && "Alt", chord.shift && "Shift", keyLabel(chord.code)].filter(Boolean).join("+");
}

/** Every alternative, with a leader's chords joined by >. */
export function bindingLabel(bindings: KeyBindingView[] | undefined): string | undefined {
  return bindings?.length ? bindings.map((binding) => binding.sequence.map(chordLabel).join(">")).join(" ") : undefined;
}
