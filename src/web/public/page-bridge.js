// Runs inside every local HTML page shown in an agshell overlay, even when the page's own JavaScript is off.
// It turns data-agshell buttons and forms into agshell commands and gives pages opened with --js agshell.request().
// The page is a top-level page in its own browser, as agterm's, and talks to agshell through that browser's channel.
(() => {
  const webview = window.chrome && window.chrome.webview;
  if (!webview || window.parent !== window || window.agshell) {
    return;
  }
  const pending = new Map();
  let sequence = 0;
  const post = (message) => webview.postMessage({ agshellPage: message });

  // As agterm's request: the second argument is the envelope, not the arguments themselves.
  const request = (cmd, { target, args } = {}) =>
    new Promise((resolve, reject) => {
      const id = ++sequence;
      pending.set(id, { resolve, reject });
      post({ type: "request", id, cmd, target: target ?? null, args: args ?? null });
    });
  Object.defineProperty(window, "agshell", { value: Object.freeze({ request }), enumerable: false });

  // History, reload, zoom, title and agshell's own keys are the browser's: only replies come this way.
  webview.addEventListener("message", (event) => {
    const message = event.data && event.data.agshellPage;
    if (message && message.type === "reply") {
      const waiter = pending.get(message.id);
      pending.delete(message.id);
      if (waiter) {
        message.ok ? waiter.resolve(message.result ?? {}) : waiter.reject(new Error(message.error));
      }
    }
  });

  const show = (element, text) => {
    const selector = element.getAttribute("data-agshell-into");
    const into = selector ? document.querySelector(selector) : null;
    if (into) {
      into.textContent = text;
    }
  };
  const shown = (result) => (result && typeof result.text === "string" ? result.text : JSON.stringify(result ?? {}));

  // agterm's form reading: a key is refused when it would take two values, because keeping either one silently drops the other.
  const formArgs = (form, args, submitter) => {
    const seen = new Set();
    for (const control of form.elements) {
      const name = control.name;
      if (!name || control.matches(":disabled")) {
        continue;
      }
      const type = (control.type || "").toLowerCase();
      if (["submit", "button", "reset", "image"].includes(type) && control !== submitter) {
        continue;
      }
      if (type === "file") {
        throw new Error(`file inputs are not supported: ${name}`);
      }
      if (type === "radio" && !control.checked) {
        continue;
      }
      let value;
      if (type === "checkbox") {
        value = control.checked;
      } else if (type === "number") {
        if (control.value === "") {
          continue;
        }
        value = control.valueAsNumber;
        if (!Number.isFinite(value)) {
          throw new Error(`not a number: ${name}`);
        }
      } else if (control instanceof HTMLSelectElement && control.multiple) {
        const picked = Array.from(control.selectedOptions);
        if (picked.length > 1) {
          throw new Error(`more than one value for ${name}`);
        }
        if (picked.length === 0) {
          continue;
        }
        value = picked[0].value;
      } else {
        value = control.value;
      }
      if (seen.has(name)) {
        throw new Error(`more than one value for ${name}`);
      }
      seen.add(name);
      args[name] = value;
    }
    return args;
  };

  const send = (element, form, submitter) => {
    const options = {};
    const target = element.getAttribute("data-agshell-target");
    if (target !== null) {
      options.target = target;
    }
    try {
      const base = element.getAttribute("data-agshell-args");
      let args = base === null ? undefined : JSON.parse(base);
      if (form) {
        args = formArgs(form, args ?? {}, submitter);
      }
      if (args !== undefined) {
        options.args = args;
      }
    } catch (error) {
      show(element, error.message);
      return;
    }
    request(element.getAttribute("data-agshell"), options).then(
      (result) => show(element, shown(result)),
      (error) => show(element, error.message),
    );
  };

  // Both listen in the capture phase and cancel the default before anything waits, so a tagged form never navigates. A button
  // inside a tagged form belongs to the form's submit; a tagged button elsewhere must be type="button", or it would also submit
  // an untagged form it sits in.
  document.addEventListener(
    "submit",
    (event) => {
      const form = event.target;
      if (!(form instanceof HTMLFormElement) || !form.hasAttribute("data-agshell")) {
        return;
      }
      event.preventDefault();
      send(form, form, event.submitter);
    },
    true,
  );
  document.addEventListener(
    "click",
    (event) => {
      const element = event.target instanceof Element ? event.target.closest("[data-agshell]") : null;
      if (!element || element instanceof HTMLFormElement || element.closest("form[data-agshell]")) {
        return;
      }
      if (element instanceof HTMLButtonElement && element.type !== "button") {
        return;
      }
      event.preventDefault();
      send(element, null, null);
    },
    true,
  );
})();
