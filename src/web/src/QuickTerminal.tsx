import { useEffect, useLayoutEffect, useRef } from "react";
import { AppState, quickSessionId } from "./bridge";
import { ClipboardPrompt } from "./ClipboardPrompt";
import { useStore } from "./store";
import { activate, attach, deactivate, disposeTerminal, ensureTerminal, fitActive, focusTerminal, paneKey, setFontSize } from "./terminals";

const quickKey = paneKey(quickSessionId, "quick");

/** The quick panel's page: its window is the panel, so the terminal fills it. */
export function QuickApp() {
  const { state } = useStore();
  return state ? <QuickTerminal state={state} /> : <div className="app loading" />;
}

/** agterm's quick terminal: one for the app, in a panel of its own; hiding it keeps its shell alive. */
function QuickTerminal({ state }: { state: AppState }) {
  const hostRef = useRef<HTMLDivElement>(null);
  const { shown, running } = state.quick;
  const fontSize = state.quick.fontSize ?? state.settings.terminalFontSize;

  useLayoutEffect(() => {
    if (!shown && !running) {
      disposeTerminal(quickKey);
      return;
    }
    const element = ensureTerminal(quickSessionId, "quick");
    if (element.parentElement !== hostRef.current) {
      hostRef.current!.appendChild(element);
    }
    element.classList.toggle("active", shown);
    attach(quickKey);
    if (shown) {
      activate(quickKey, true);
      focusTerminal(quickKey);
    } else {
      deactivate(quickKey);
    }
  }, [shown, running]);

  useLayoutEffect(() => {
    if (shown || running) {
      setFontSize(quickKey, fontSize);
    }
  }, [shown, running, fontSize]);

  // The terminal takes the keys back once a clipboard prompt is answered.
  useEffect(() => {
    if (shown && !state.clipboardPrompt) {
      focusTerminal(quickKey);
    }
  }, [state.clipboardPrompt?.id]);

  useEffect(() => {
    const observer = new ResizeObserver(() => {
      if (shown) {
        fitActive(quickKey);
      }
    });
    observer.observe(hostRef.current!);
    return () => observer.disconnect();
  }, [shown]);

  return (
    <div className="quick-window">
      <div className="quick-host" ref={hostRef} />
      {state.clipboardPrompt && <ClipboardPrompt key={state.clipboardPrompt.id} prompt={state.clipboardPrompt} />}
    </div>
  );
}
