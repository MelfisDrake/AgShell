import assert from "node:assert/strict";
import fs from "node:fs";
import vm from "node:vm";
import test from "node:test";
import ts from "typescript";

function harness() {
  const effects = [];
  const sent = [];
  const listeners = new Set();
  const document = { activeElement: null };
  const controls = ["remember", "deny", "allow"].map(name => ({ name, focus() { document.activeElement = this; } }));
  let refs = 0;
  const modules = {
    react: {
      useEffect: effect => effects.push(effect),
      useRef: () => ({ current: refs++ === 0 ? controls[2] : { querySelectorAll: () => controls } }),
      useState: () => [true, () => {}],
    },
    "react/jsx-runtime": { jsx: () => null, jsxs: () => null },
    "./bridge": { command: value => sent.push(value) },
    "./i18n": { t: value => value },
  };
  const window = {
    addEventListener: (kind, callback, capture) => {
      assert.equal(kind, "keydown");
      assert.equal(capture, true);
      listeners.add(callback);
    },
    removeEventListener: (kind, callback, capture) => {
      assert.equal(kind, "keydown");
      assert.equal(capture, true);
      listeners.delete(callback);
    },
  };
  const source = fs.readFileSync(new URL("../src/ClipboardPrompt.tsx", import.meta.url), "utf8");
  const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX } }).outputText;
  const scope = { window, document, exports: {}, require: name => modules[name] };
  vm.runInNewContext(compiled, scope);
  scope.exports.ClipboardPrompt({ prompt: { id: "fixture", access: "read" } });
  const cleanup = effects.map(effect => effect());
  return {
    sent,
    controls,
    document,
    fire(key, isComposing = false, shiftKey = false) {
      let prevented = false;
      let stopped = false;
      const event = { key, isComposing, shiftKey, preventDefault: () => { prevented = true; }, stopImmediatePropagation: () => { stopped = true; } };
      for (const listener of listeners) listener(event);
      return { prevented, stopped };
    },
    unmount: () => cleanup.forEach(clean => clean?.()),
  };
}

test("Escape denies once, ignores a checked remember box and stops the terminal key", () => {
  const h = harness();
  assert.deepEqual(h.fire("Escape"), { prevented: true, stopped: true });
  assert.deepEqual(JSON.parse(JSON.stringify(h.sent)), [{ command: "clipboardPrompt", id: "fixture", allow: false, remember: false }]);
  h.unmount();
  h.fire("Escape");
  assert.equal(h.sent.length, 1);
});

test("Tab stays in the clipboard modal in both directions, without answering or reaching the sidebar", () => {
  const h = harness();
  assert.equal(h.document.activeElement, h.controls[2]);
  for (const index of [0, 1, 2, 0]) {
    assert.deepEqual(h.fire("Tab"), { prevented: true, stopped: true });
    assert.equal(h.document.activeElement, h.controls[index]);
  }
  for (const index of [2, 1, 0, 2]) {
    assert.deepEqual(h.fire("Tab", false, true), { prevented: true, stopped: true });
    assert.equal(h.document.activeElement, h.controls[index]);
  }
  h.document.activeElement = { name: "sidebar" };
  h.fire("Tab");
  assert.equal(h.document.activeElement, h.controls[0]);
  assert.equal(h.sent.length, 0);
  h.unmount();
  assert.deepEqual(h.fire("Tab"), { prevented: false, stopped: false });
});

test("other keys and an Escape owned by composition are left alone", () => {
  const h = harness();
  assert.deepEqual(h.fire("Enter"), { prevented: false, stopped: false });
  assert.deepEqual(h.fire("Escape", true), { prevented: false, stopped: false });
  assert.equal(h.sent.length, 0);
  h.unmount();
});
