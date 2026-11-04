import assert from "node:assert/strict";
import fs from "node:fs";
import vm from "node:vm";
import test from "node:test";
import ts from "typescript";

function harness() {
  const sent = [];
  const modules = {
    react: { useEffect() {}, useMemo: f => f(), useRef: () => ({ current: null }), useState: value => [value, () => {}] },
    "react/jsx-runtime": { jsx: (type, props) => ({ type, props }), jsxs: (type, props) => ({ type, props }) },
    "./bridge": { command: value => sent.push(value) },
    "./fuzzy": { fuzzyRank: (_query, items) => items },
    "./StatusGlyph": {}, "./icons": {}, "./MiddleTruncated": {},
    "./i18n": { t: value => value },
    "./shortcuts": { boundAction: event => event.close ? "close_session" : undefined },
    "./Palette": { paletteStep: event => event.step ?? 0 },
  };
  const source = fs.readFileSync(new URL("../src/Picker.tsx", import.meta.url), "utf8");
  const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX } }).outputText;
  const scope = { exports: {}, require: name => modules[name] };
  vm.runInNewContext(compiled, scope);
  const tree = scope.exports.Picker({ pick: { id: "pick", items: [{ id: "alpha", label: "Alpha" }] }, state: {} });
  function findInput(node) {
    if (!node || typeof node !== "object") return;
    if (node?.type === "input") return node;
    for (const child of [node?.props?.children].flat()) {
      const found = findInput(child);
      if (found) return found;
    }
  }
  const input = findInput(tree);
  return { sent, fire(overrides) {
    let stopped = false;
    let prevented = false;
    const event = { key: "q", nativeEvent: {}, stopPropagation: () => { stopped = true; }, preventDefault: () => { prevented = true; }, ...overrides };
    input.props.onKeyDown(event);
    return { stopped, prevented };
  } };
}

test("pick lets menu chords reach the app without submitting or cancelling", () => {
  const h = harness();
  for (const event of [{ key: "Q", ctrlKey: true, shiftKey: true }, { key: ",", ctrlKey: true }, { key: "q", metaKey: true }]) {
    assert.deepEqual(h.fire(event), { stopped: false, prevented: false });
  }
  assert.equal(h.sent.length, 0);
});

test("pick still owns text, selection movement and its close chord", () => {
  const h = harness();
  assert.deepEqual(h.fire({ key: "a" }), { stopped: true, prevented: false });
  assert.deepEqual(h.fire({ key: "j", ctrlKey: true, step: 1 }), { stopped: true, prevented: true });
  assert.equal(h.sent.length, 0);
  assert.deepEqual(h.fire({ key: "W", ctrlKey: true, shiftKey: true, nativeEvent: { close: true } }), { stopped: true, prevented: true });
  assert.deepEqual(JSON.parse(JSON.stringify(h.sent)), [{ command: "pickAnswer", id: "pick" }]);
});
