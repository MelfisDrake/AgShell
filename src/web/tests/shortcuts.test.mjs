import assert from "node:assert/strict";
import fs from "node:fs";
import vm from "node:vm";
import test from "node:test";
import ts from "typescript";

function harness(commands = []) {
  let now = 0;
  let firing = null;
  let allowed = true;
  let nextTimer = 0;
  const timers = new Map();
  const host = [];
  const events = new Map();
  const runs = [];
  const snapshots = [];
  const timeline = [];
  const state = { keymap: { actions: {}, menu: {}, commands }, selectedSessionId: "session", pendingClose: null };
  class Element { matches() { return true; } closest() { return null; } }
  const document = { activeElement: null, hasFocus: () => true, addEventListener: (kind, callback) => events.set(kind, callback) };
  const context = {
    document, HTMLElement: Element, location: { hash: "" },
    crypto: { randomUUID: () => Math.random().toString() }, Date: { now: () => now },
    window: { addEventListener: (kind, callback) => events.set(kind, callback) },
    setTimeout: (callback, delay) => { const id = ++nextTimer; timers.set(id, { callback, at: now + delay }); return id; },
    clearTimeout: id => timers.delete(id),
  };
  const modules = {
    "./bridge": {
      command: value => { runs.push(value); timeline.push("run"); },
      send: value => { snapshots.push(value); timeline.push("snapshot"); },
      onHostMessage: callback => host.push(callback),
    },
    "./store": { currentState: () => state, uiActionsEnabled: () => allowed },
    "./actions": { actionAvailable: () => true, findAction: name => state.keymap.actions[name] ? { run: () => runs.push({ action: name }) } : null, quickActions: {} },
    "./switcher": { switcherActive: () => false },
    "./terminals": { firingTerminal: () => firing },
  };
  function load(name) {
    const source = fs.readFileSync(new URL(`../src/${name}.ts`, import.meta.url), "utf8");
    const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
    const scope = { ...context, exports: {}, require: name => modules[name] };
    vm.runInNewContext(compiled, scope);
    return scope.exports;
  }
  modules["./keybindMatcher"] = load("keybindMatcher");
  const shortcuts = load("shortcuts");
  shortcuts.startShortcutMonitoring();
  return {
    runs, state, shortcuts, snapshots, timeline,
    settings: () => { context.location.hash = "#settings"; },
    quick: () => { context.location.hash = "#quick"; },
    firing: value => { firing = value; },
    allowed: value => { allowed = value; },
    key: (code, options = {}) => shortcuts.handleAppShortcut({ type: "keydown", code, key: code, ctrlKey: false, altKey: false, shiftKey: false, metaKey: false, repeat: false, ...options }),
    up: code => shortcuts.handleAppShortcut({ type: "keyup", code, key: code }),
    tick: milliseconds => { now += milliseconds; for (const [id, timer] of [...timers]) if (timer.at <= now && timers.delete(id)) timer.callback(); },
    deferTimers: milliseconds => { now += milliseconds; },
    host: message => host.forEach(callback => callback(message)),
    field: enabled => { document.activeElement = enabled ? new Element() : null; events.get("focusin")(); },
  };
}

const chord = (code, ctrl = false) => ({ code, ctrl, alt: false, shift: false });
const custom = (name, sequence, repeat = false) => ({ name, bindings: [{ sequence, repeat }] });
const leader = chord("KeyA", true);
const tail = chord("KeyX");
const repeaters = () => [custom("X", [leader, tail], true), custom("Y", [leader, chord("KeyY")], true)];

test("Alt+F4 closes only its current window instead of reaching a terminal or its program", () => {
  for (const page of ["main", "settings", "quick"]) {
    const h = harness();
    if (page !== "main") h[page]();
    h.field(true);
    assert.equal(h.key("F4", { altKey: true }), true);
    assert.equal(h.up("F4"), true);
    const expected = page === "settings" ? { command: "closeSettings" }
      : page === "quick" ? { command: "setQuick", shown: false } : { command: "closeWindow" };
    assert.deepEqual(JSON.parse(JSON.stringify(h.runs)), [expected]);
  }
});

test("leader expires after 1.5 seconds and unmatched keys pass through", () => {
  const h = harness([custom("X", [leader, tail])]);
  assert.equal(h.key("KeyA", { ctrlKey: true }), true);
  h.tick(1501);
  assert.equal(h.key("KeyX"), false);
  assert.equal(h.runs.length, 0);
  h.key("KeyA", { ctrlKey: true });
  assert.equal(h.key("KeyQ"), false);
});

test("a failed tail is retried as a new leader", () => {
  const h = harness([custom("X", [leader, tail]), custom("Z", [chord("KeyB", true), chord("KeyZ")])]);
  h.key("KeyA", { ctrlKey: true });
  assert.equal(h.key("KeyB", { ctrlKey: true }), true);
  h.key("KeyZ");
  assert.equal(h.runs[0].name, "Z");
});

test("ordinary consumed autorepeat and keyUp do not fire twice", () => {
  const h = harness([custom("single", [chord("KeyK", true)])]);
  h.key("KeyK", { ctrlKey: true });
  assert.equal(h.key("KeyK", { ctrlKey: true, repeat: true }), true);
  assert.equal(h.runs.length, 1);
  assert.equal(h.up("KeyK"), true);
  assert.equal(h.up("KeyK"), false);
});

test("a held repeat tail survives the initial autorepeat delay", () => {
  const h = harness(repeaters());
  h.key("KeyA", { ctrlKey: true }); h.up("KeyA"); h.key("KeyX");
  h.tick(2000);
  h.key("KeyX", { repeat: true });
  assert.equal(h.runs.length, 2);
  h.up("KeyX"); h.tick(501);
  assert.equal(h.key("KeyX"), false);
});

test("repeat prefix accepts a sibling tail until 500ms after release", () => {
  const h = harness(repeaters());
  h.key("KeyA", { ctrlKey: true }); h.up("KeyA"); h.key("KeyX"); h.up("KeyX");
  h.tick(499); h.key("KeyY"); h.up("KeyY");
  assert.deepEqual(h.runs.map(run => run.name), ["X", "Y"]);
  h.tick(501);
  assert.equal(h.key("KeyY"), false);
});

test("release of an old tail does not shorten a newly armed leader", () => {
  const h = harness([...repeaters(), custom("Z", [chord("KeyB", true), chord("KeyZ")])]);
  h.key("KeyA", { ctrlKey: true }); h.key("KeyX");
  h.key("KeyB", { ctrlKey: true }); h.up("KeyX"); h.tick(800); h.key("KeyZ");
  assert.equal(h.runs.at(-1).name, "Z");
});

test("Escape consumes a pending leader but passes through after a repeat", () => {
  const h = harness(repeaters());
  h.key("KeyA", { ctrlKey: true });
  assert.equal(h.key("Escape"), true); assert.equal(h.up("Escape"), true);
  h.key("KeyA", { ctrlKey: true }); h.key("KeyX");
  assert.equal(h.key("Escape"), false);
  h.key("KeyY"); assert.equal(h.runs.length, 1);
});

test("text fields never run custom commands and clear a pending leader", () => {
  const h = harness([custom("single", [chord("KeyK", true)]), ...repeaters()]);
  h.key("KeyA", { ctrlKey: true }); h.field(true);
  assert.equal(h.key("KeyK", { ctrlKey: true }), false);
  h.field(false); assert.equal(h.key("KeyX"), false);
  assert.equal(h.runs.length, 0);
});

test("reload resets the matcher while ownership lasts through release", () => {
  const h = harness(repeaters());
  h.key("KeyA", { ctrlKey: true }); h.host({ type: "keymapChanged" });
  assert.equal(h.up("KeyA"), true); assert.equal(h.key("KeyX"), false);
});

test("menu tracking and app deactivation close repeat, intra-app resign does not", () => {
  const h = harness(repeaters());
  h.key("KeyA", { ctrlKey: true }); h.key("KeyX");
  h.host({ type: "windowActive", value: false, appActive: true }); h.key("KeyY");
  assert.equal(h.runs.length, 2);
  h.host({ type: "menuTracking" }); assert.equal(h.key("KeyY"), false);
  h.key("KeyA", { ctrlKey: true }); h.key("KeyX");
  h.host({ type: "windowActive", value: false, appActive: false }); assert.equal(h.key("KeyY"), false);
});

test("named synthetic events work without code, physical letters ignore produced text", () => {
  const h = harness([custom("F", [chord("F9")]), custom("K", [chord("KeyK", true)])]);
  assert.equal(h.key("", { key: "F9" }), true); assert.equal(h.up("F9"), true);
  h.key("KeyK", { key: "л", ctrlKey: true });
  assert.deepEqual(h.runs.map(run => run.name), ["F", "K"]);
});

test("ownership and a held repeat prefix transfer to another window", () => {
  const first = harness(repeaters());
  const second = harness(repeaters());
  first.key("KeyA", { ctrlKey: true }); first.up("KeyA"); first.key("KeyX");
  const message = first.snapshots.at(-1);
  second.host({ ...message, revision: 1 });
  assert.equal(second.up("KeyX"), true);
  second.tick(200); second.key("KeyY");
  assert.equal(second.runs[0].name, "Y");
});

test("letter menu chords retain menu autorepeat, F-key menu chords fire once", () => {
  const h = harness();
  const binding = { label: "ctrl+k", sequence: [chord("KeyK", true)], repeat: false };
  h.state.keymap.actions.rename_session = [binding]; h.state.keymap.menu.rename_session = binding;
  h.key("KeyK", { ctrlKey: true }); h.key("KeyK", { ctrlKey: true, repeat: true });
  assert.equal(h.runs.length, 2); h.up("KeyK");
  const functionKey = { label: "f9", sequence: [chord("F9")], repeat: false };
  h.state.keymap.actions.rename_session = [functionKey]; h.state.keymap.menu.rename_session = functionKey;
  h.key("F9"); h.key("F9", { repeat: true }); assert.equal(h.runs.length, 3);
});

test("a monitor tail that equals a menu chord never turns into menu autorepeat", () => {
  const h = harness([custom("monitor", [leader, chord("KeyK", true)])]);
  const binding = { label: "ctrl+k", sequence: [chord("KeyK", true)], repeat: false };
  h.state.keymap.actions.rename_session = [binding]; h.state.keymap.menu.rename_session = binding;
  h.key("KeyA", { ctrlKey: true }); h.key("KeyK", { ctrlKey: true }); h.key("KeyK", { ctrlKey: true, repeat: true });
  assert.equal(h.runs.length, 1); assert.equal(h.runs[0].name, "monitor");
});

test("ownership is published before an action can change windows", () => {
  const h = harness([custom("single", [chord("KeyK", true)])]);
  h.key("KeyK", { ctrlKey: true });
  assert.deepEqual(h.timeline, ["snapshot", "run"]);
  assert.equal(h.snapshots[0].snapshot.consumed.includes("KeyK"), true);
});

test("a stale cross-window snapshot cannot restore an old pending prefix", () => {
  const first = harness(repeaters());
  const second = harness(repeaters());
  first.key("KeyA", { ctrlKey: true }); const old = first.snapshots.at(-1);
  first.key("KeyX"); const latest = first.snapshots.at(-1);
  second.host({ ...latest, revision: 2 }); second.host({ ...old, revision: 1 });
  second.up("KeyX"); second.key("KeyY");
  assert.equal(second.runs[0].name, "Y");
});

test("a background-delayed timer cannot keep an expired leader alive", () => {
  const h = harness([custom("X", [leader, tail])]);
  h.key("KeyA", { ctrlKey: true }); h.deferTimers(10000);
  assert.equal(h.key("KeyX"), false); assert.equal(h.runs.length, 0);
});


test("keybind uses firing scratch and overlay context while palette resolves in host", () => {
  const h = harness();
  h.state.workspaces = [{ sessions: [{ id: "actual", scratchShown: false, focusedPane: "right" }] }];
  h.firing({ sessionId: "actual", pane: "scratch", selection: "scratch text" });
  h.shortcuts.runCustom("C", true);
  assert.equal(h.runs.at(-1).pane, "scratch");
  assert.equal(h.runs.at(-1).sessionId, "actual");
  assert.equal(h.runs.at(-1).selection, "scratch text");
  for (const [surface, expected] of [["overlay-left", "left"], ["overlay-right", "right"], ["overlay", "right"]]) {
    h.firing({ sessionId: "actual", pane: surface, selection: surface });
    h.shortcuts.runCustom("C", true);
    assert.equal(h.runs.at(-1).pane, expected);
  }
  h.state.workspaces[0].sessions[0].scratchShown = true;
  h.shortcuts.runCustom("C", true);
  assert.equal(h.runs.at(-1).pane, "scratch");
  h.shortcuts.runCustom("C");
  assert.equal(h.runs.at(-1).source, "palette");
  assert.equal(h.runs.at(-1).pane, undefined);
  assert.equal(h.runs.at(-1).selection, undefined);
  assert.equal(h.runs.at(-1).sessionId, "session");
});

test("palette gates do not disable the independent keybind route", () => {
  const h = harness();
  h.allowed(false);
  h.shortcuts.runCustom("C");
  assert.equal(h.runs.length, 0);
  h.shortcuts.runCustom("C", true);
  assert.equal(h.runs.length, 1);
  h.allowed(true); h.state.selectedSessionId = null;
  h.shortcuts.runCustom("C");
  assert.equal(h.runs.length, 1);
});


test("Win is an independent modifier, not a Ctrl alias", () => {
  const h = harness([custom("Win", [{ ...chord("KeyK", true), meta: true }])]);
  assert.equal(h.key("KeyK", { ctrlKey: true }), false);
  assert.equal(h.key("KeyK", { ctrlKey: true, metaKey: true }), true);
  assert.equal(h.runs.at(-1).name, "Win");
});


test("Ctrl+Win+1 is available independently of reserved Ctrl+1 pane focus", () => {
  const h = harness([custom("Win one", [{ ...chord("Digit1", true), meta: true }])]);
  assert.equal(h.key("Digit1", { ctrlKey: true, metaKey: true }), true);
  assert.equal(h.runs.at(-1).name, "Win one");
});


test("Settings routes stock and menu chords but never custom, leaders or pane monitors", () => {
  const h = harness([custom("single", [chord("KeyK", true)]), custom("leader", [leader, tail])]);
  h.settings();
  h.state.keymap.menu.undo_close = { sequence: [{ ...chord("KeyZ", true), shift: true }], label: "ctrl+shift+z" };
  h.state.keymap.menu.toggle_fullscreen = { sequence: [chord("F11")], label: "f11" };
  assert.equal(h.key("KeyZ", { ctrlKey: true, shiftKey: true }), false);
  assert.equal(h.key("F11"), false);
  h.state.keymap.menu.toggle_sidebar = { sequence: [chord("KeyB", true)], label: "ctrl+b" };
  h.key("KeyM", { ctrlKey: true, shiftKey: true }); h.up("KeyM");
  h.key("KeyQ", { ctrlKey: true, shiftKey: true }); h.up("KeyQ");
  h.key("KeyB", { ctrlKey: true }); h.up("KeyB");
  assert.deepEqual(h.runs.map(r => r.name), ["minimize", "quit", "toggle_sidebar"]);
  assert.equal(h.key("KeyK", { ctrlKey: true }), false);
  assert.equal(h.key("KeyA", { ctrlKey: true }), false);
  assert.equal(h.key("KeyX"), false);
  assert.equal(h.key("Digit1", { ctrlKey: true }), false);
  assert.equal(h.key("Tab", { key: "Tab", ctrlKey: true }), false);
  assert.equal(h.runs.length, 3);
});

test("Settings menu remaps take stock chords and only default menu Close closes Settings", () => {
  const h = harness(); h.settings();
  h.state.keymap.menu.new_session = { sequence: [{ ...chord("KeyM", true), shift: true }], label: "ctrl+shift+m" };
  h.state.keymap.menu.close_session = { sequence: [{ ...chord("KeyW", true), shift: true }], label: "ctrl+shift+w" };
  h.key("KeyM", { ctrlKey: true, shiftKey: true }); h.up("KeyM");
  h.key("KeyW", { ctrlKey: true, shiftKey: true }); h.up("KeyW");
  h.key("KeyW", { ctrlKey: true }); h.up("KeyW");
  h.state.keymap.menu.close_session = { sequence: [chord("KeyE", true)], label: "ctrl+e" };
  h.key("KeyE", { ctrlKey: true });
  assert.deepEqual(h.runs.map(r => [r.command, r.name]), [
    ["settingsMenuAction", "new_session"], ["closeSettings", undefined],
    ["closeSettings", undefined], ["settingsMenuAction", "close_session"],
  ]);
});

test("native reconciliation lets the first Chromium repeat through after a release outside the app", () => {
  const h = harness(repeaters());
  h.key("KeyA", { ctrlKey: true }); h.up("KeyA"); h.key("KeyX");
  h.host({ type: "windowActive", value: false, appActive: false });
  h.host({ type: "shortcutState", origin: "native", sequence: 0, revision: 100,
    snapshot: { pending: [], repeatPrefix: null, heldRepeatKey: null, consumed: [], menuOwned: [], deadline: null } });
  assert.equal(h.key("KeyX", { repeat: true }), false);
  assert.equal(h.up("KeyX"), false);
  assert.equal(h.runs.length, 1);
});


test("the first falsely repeated function shortcut after verified native release fires once", () => {
  const h = harness([custom("F8", [chord("F8")])]);
  h.key("F8");
  h.host({ type: "shortcutState", origin: "native", sequence: 0, revision: 100, released: ["F8"],
    snapshot: { pending: [], repeatPrefix: null, heldRepeatKey: null, consumed: [], menuOwned: [], deadline: null } });
  assert.equal(h.key("F8", { repeat: true }), true);
  assert.equal(h.key("F8", { repeat: true }), true);
  h.up("F8");
  assert.equal(h.runs.length, 2);
});
