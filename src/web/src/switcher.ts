import { useSyncExternalStore } from "react";
import { command, send } from "./bridge";
import { currentState, subscribe, uiActionsEnabled } from "./store";

// The Ctrl+Tab switcher: hold Ctrl and press Tab to walk the most recently used sessions, release Ctrl to switch.
// The order is frozen when it opens, so walking it never reorders it.

const maxCandidates = 10;

export interface SwitcherState {
  candidates: string[];
  index: number;
}

let current: SwitcherState | null = null;
const listeners = new Set<() => void>();

function set(next: SwitcherState | null): void {
  if ((current === null) !== (next === null)) {
    // Windows takes Ctrl+Esc for Start before the window sees it; the host holds Esc for the switcher while it is open.
    send({ type: "switcherHold", value: next !== null });
  }
  current = next;
  listeners.forEach((listener) => listener());
}

export function useSwitcher(): SwitcherState | null {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    () => current,
  );
}

export function switcherActive(): boolean {
  return current !== null;
}

/** agterm's canSwitch: no switching while something modal covers the window. */
function canSwitch(): boolean {
  const state = currentState();
  return state !== null && uiActionsEnabled(state);
}

/** Ctrl+Tab: refused under a modal, where it closes an open switcher and is eaten all the same. */
export function stepSwitcher(reverse: boolean): void {
  if (!canSwitch()) {
    cancelSwitcher();
    return;
  }
  if (current) {
    const count = current.candidates.length;
    set({ ...current, index: (((current.index + (reverse ? -1 : 1)) % count) + count) % count });
    return;
  }

  const state = currentState();
  if (!state) {
    return;
  }
  const live = new Set(state.navigableSessionIds);
  const candidates = state.recentSessionIds.filter((id) => live.has(id)).slice(0, maxCandidates);
  if (candidates.length > 1) {
    set({ candidates, index: 1 });
  }
}

export function commitSwitcher(): void {
  if (!current) {
    return;
  }
  const target = current.candidates[current.index];
  set(null);
  command({ command: "selectSession", sessionId: target });
}

export function cancelSwitcher(): void {
  if (current) {
    set(null);
  }
}

/** Ctrl let go: the walk ends on the row it reached, or on nothing under a modal. */
export function endSwitcherHold(): void {
  if (!current) {
    return;
  }
  if (canSwitch()) {
    commitSwitcher();
  } else {
    cancelSwitcher();
  }
}

/** The keys whose press or release is a modifier change: agterm's flagsChanged. */
const modifierKeys = new Set(["Control", "Shift", "Alt", "Meta", "CapsLock"]);

export function startSwitcherListeners(): void {
  // As agterm's flags monitor: a modifier change ends the walk once Ctrl is up, and the window losing focus leaves
  // the switcher open until the next one.
  const onModifier = (event: KeyboardEvent) => {
    if (!current || !modifierKeys.has(event.key)) {
      return;
    }
    if (!canSwitch()) {
      cancelSwitcher();
    } else if (!event.ctrlKey) {
      commitSwitcher();
    }
  };
  window.addEventListener("keydown", onModifier, true);
  window.addEventListener("keyup", onModifier, true);
  // As agterm's, a dashboard opening over the window takes the keyboard from an open switcher.
  subscribe(() => {
    if (current && currentState()?.dashboard) {
      cancelSwitcher();
    }
  });
}
