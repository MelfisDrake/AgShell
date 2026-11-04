import assert from "node:assert/strict";
import fs from "node:fs";
import vm from "node:vm";
import test from "node:test";
import ts from "typescript";

function harness() {
  let state;
  let receive;
  const commands = [];
  const projections = [];
  const modules = {
    "./bridge": { command: c => commands.push(c), send: p => projections.push(p), onHostMessage: f => { receive = f; } },
    "./actions": { toggleSplit: () => commands.push("split") },
    "./shortcuts": { bindingLabel: b => b?.label, runCustom: n => commands.push(n) },
    "./store": { currentState: () => state, sessionName: s => s.name, switcherDetail: s => s.cwd, uiActionsEnabled: s => !s.pick && !s.ask && !s.zoomed && !s.dashboard },
    "./overlays": { anyOverlayHidden: s => !!s.overlay?.hidden, anyOverlayShown: s => !!s.overlay && !s.overlay.hidden, hasHideableOverlay: s => !!s.overlay },
    "./appearance": { menuColors: () => ({}) }, "./i18n": { t: s => s },
  };
  const source = fs.readFileSync(new URL("../src/toolbar.ts", import.meta.url), "utf8");
  const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
  const scope = { exports: {}, require: name => modules[name], document: { documentElement: {} }, getComputedStyle: () => ({ getPropertyValue: () => "#abcdef" }) };
  vm.runInNewContext(compiled, scope);
  const api = scope.exports;
  api.startToolbar(() => {});
  return { api, commands, projections, set: s => { state = s; }, choose: id => receive({ type: "toolbarAction", id }) };
}
const session = (id, options = {}) => ({ id, name: id, cwd: "/" + id, status: "idle", statusColor: null, statusShape: null, remoteHost: null, paneOverlays: {}, ...options });
function model() {
  return { theme: { background: "#112233", foreground: "#ddeeff", selectionBackground: null }, settings: { toolbarMode: "normal", hiddenInterfaceElements: [], attentionButtonEnabled: true }, workspaces: [{ name: "Work", sessions: [session("current"), session("hidden"), session("last"), session("other")] }], selectedSessionId: "current", navigableSessionIds: ["current", "last", "other"], recentSessionIds: ["current", "hidden", "last", "other"], attention: [], keymap: { actions: {}, commands: [], mostUsed: [] }, pick: null, ask: null, dashboard: null, zoomed: false, sidebar: { visible: true } };
}
const plain = value => JSON.parse(JSON.stringify(value));
test("MRU excludes selected and filtered sessions; choosing rechecks current eligibility", () => {
  const h = harness(); const state = model(); h.set(state);
  const recent = h.api.toolbarItems(state).find(x => x.id === "recent");
  assert.deepEqual(plain(recent.rows.map(x => x.id)), ["recent:last", "recent:other"]);
  h.choose("recent:last");
  assert.equal(h.commands[0].sessionId, "last");
  state.navigableSessionIds = ["current"];
  h.choose("recent:other");
  assert.equal(h.commands.length, 1);
});
test("Hidden, dashboard and zoom do not expose the full action cluster", () => {
  const h = harness(); const state = model(); state.settings.toolbarMode = "hidden";
  assert.equal(h.api.toolbarItems(state).length, 0);
  state.settings.toolbarMode = "normal"; state.dashboard = {};
  assert.deepEqual(plain(h.api.toolbarItems(state).map(i => i.id)), ["dashboard-exit"]);
  state.zoomed = true;
  assert.deepEqual(plain(h.api.toolbarItems(state).map(i => i.id)), ["zoom"]);
});
test("Commands preserve file order within usage groups and refuse stale choices under ask/pick or held UI", () => {
  const h = harness(); const state = model(); h.set(state);
  state.keymap.commands = ["Z", "B", "A", "C", "D", "E"].map(name => ({ name, shortcut: "ctrl+1" }));
  state.keymap.mostUsed = ["A", "Z"];
  const rows = h.api.toolbarItems(state).find(i => i.id === "commands").rows;
  assert.deepEqual(plain(rows.map(i => i.id)), ["custom:Z", "custom:A", "custom-separator", "custom:B", "custom:C", "custom:D", "custom:E"]);
  h.choose("custom:Z"); assert.equal(h.commands.length, 1);
  for (const key of ["ask", "pick"]) {
    state[key] = {}; h.choose("custom:Z"); assert.equal(h.commands.length, 1); state[key] = null;
  }
  h.api.postToolbar(state, true); h.choose("custom:Z"); assert.equal(h.commands.length, 1);
});
test("Cross-window attention keeps covered targets disabled and blocked bell filled", () => {
  const h = harness(); const state = model(); h.set(state);
  state.attention = [{ windowId: "w", sessionId: "s", name: "Waiting", subtitle: "remote window", enabled: false, status: "blocked", statusShape: "diamond", statusColor: "#123456", remoteHost: "remote" }];
  const bell = h.api.toolbarItems(state).find(i => i.id === "attention");
  assert.equal(bell.glyph, "bell-filled"); assert.equal(bell.rows[0].enabled, false);
  h.choose(bell.rows[0].id); assert.equal(h.commands.length, 0);
});
test("Visibility toggles preserve singleton group flow and split state includes axis and collapsed pane", () => {
  const h = harness(); const state = model();
  state.settings.hiddenInterfaceElements = ["scratch", "overlayToggle", "customCommands", "dashboard"];
  const selected = state.workspaces[0].sessions[0]; selected.hasSplit = true; selected.splitAxis = "horizontal"; selected.splitShown = false; selected.focusedPane = "scratch"; selected.scratchShown = true; selected.splitFocused = true;
  const items = h.api.toolbarItems(state);
  assert.equal(items.some(i => i.separator), false);
  assert.equal(items.find(i => i.id === "split").glyph, "split-bottom");
  selected.splitShown = true;
  assert.equal(h.api.toolbarItems(state).find(i => i.id === "split").glyph, "split-both-horizontal");
});

test("DOM gate survives an empty Hidden projection and refuses stale cross-window choices", () => {
  const h = harness(); const state = model(); h.set(state);
  state.attention = [{ windowId: "w", sessionId: "s", name: "Waiting", subtitle: "", enabled: true, status: "blocked", statusColor: null, statusShape: null, remoteHost: null }];
  h.api.postToolbar(state, false);
  h.choose("attention:w:s"); assert.equal(h.commands.length, 1);
  state.settings.toolbarMode = "hidden";
  h.api.postToolbar(state, true);
  assert.equal(h.projections.at(-1).held, true);
  assert.equal(h.projections.at(-1).items.length, 0);
  state.settings.toolbarMode = "normal";
  h.choose("attention:w:s"); assert.equal(h.commands.length, 1);
  h.api.postToolbar(state, false);
  assert.equal(h.projections.at(-1).held, false);
  h.choose("attention:w:s"); assert.equal(h.commands.length, 2);
  state.attention[0].enabled = false;
  h.choose("attention:w:s"); assert.equal(h.commands.length, 2);
  state.attention = [];
  h.choose("attention:w:s"); assert.equal(h.commands.length, 2);
});
