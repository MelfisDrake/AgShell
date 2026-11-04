import assert from "node:assert/strict";
import fs from "node:fs";
import vm from "node:vm";
import test from "node:test";
import ts from "typescript";

function harness() {
  let receive;
  const sent = [];
  const appearances = [];
  const outputs = [];
  const modules = {
    "react": {},
    "./i18n": { setLanguage() {} },
    "./bridge": { onHostMessage: callback => { receive = callback; }, send: message => sent.push(message) },
    "./appearance": { applyChrome() {} },
    "./pages": { setPageZoom() {} },
    "./switcher": {},
    "./terminals": {
      applyTerminalAppearance: state => appearances.push(state),
      paneKey: (id, pane) => `${id}:${pane}`,
      writeOutput: (key, data) => outputs.push({ key, data }),
    },
  };
  const source = fs.readFileSync(new URL("../src/store.ts", import.meta.url), "utf8");
  const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
  const scope = { exports: {}, require: name => modules[name] };
  vm.runInNewContext(compiled, scope);
  scope.exports.startHostConnection();
  return { store: scope.exports, receive, sent, appearances, outputs };
}

test("quick state without a workspace reaches subscribers and keeps receiving shell output", () => {
  const h = harness();
  const states = [];
  h.store.subscribe(() => states.push(h.store.currentState()));
  assert.equal(h.sent[0].type, "ready");
  const state = {
    quickPage: true, settings: { uiLanguage: "en", terminalFontSize: 22 }, theme: {}, keymap: {},
    windowsBuild: 26100, clipboardPrompt: null,
    quick: { shown: true, pinned: true, running: false, fontSize: null },
  };
  h.receive({ type: "state", state });
  assert.equal(states.at(-1), state);
  assert.equal(h.appearances.at(-1), state);
  for (const shown of [false, true]) {
    const next = { ...state, quick: { ...state.quick, shown, running: true } };
    h.receive({ type: "state", state: next });
    assert.equal(states.at(-1), next);
  }
  h.receive({ type: "output", sessionId: "quick-id", pane: "quick", data: "shell output\r\n" });
  assert.equal(h.outputs[0].key, "quick-id:quick");
  assert.equal(h.outputs[0].data, "shell output\r\n");
});

test("settings state without a workspace and regular window state still publish", () => {
  const h = harness();
  const settings = { settingsPage: true, settings: {}, theme: {} };
  h.receive({ type: "state", state: settings });
  assert.equal(h.store.currentState(), settings);
  const windowState = {
    settings: {}, theme: {}, workspaces: [], selectedSessionId: null,
    zoomedPane: null, dashboard: null,
  };
  h.receive({ type: "state", state: windowState });
  assert.equal(h.store.currentState(), windowState);
});
