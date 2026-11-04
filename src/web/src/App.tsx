import { useCallback, useEffect, useLayoutEffect, useState } from "react";
import { command, send, sendFiles } from "./bridge";
import { ContextMenu, MenuItem } from "./ContextMenu";
import { AskDialog, forgetAskDrafts } from "./AskDialog";
import { ClipboardPrompt } from "./ClipboardPrompt";
import { Palette } from "./Palette";
import { Picker } from "./Picker";
import { sessionAskHoldsKeys, setSidebarToggle } from "./actions";
import { handleAppShortcut } from "./shortcuts";
import { Sidebar } from "./Sidebar";
import { paneKey, terminalAt } from "./terminals";
import { setEditing, setPalette, useStore } from "./store";
import { useSwitcher } from "./switcher";
import { SessionSwitcher } from "./SessionSwitcher";
import { TerminalPane } from "./TerminalPane";
import { postToolbar, startToolbar } from "./toolbar";
import { Welcome } from "./Welcome";

interface MenuState {
  x: number;
  y: number;
  items: MenuItem[];
}

/** The element's box in the window, measured after every render and as it resizes; null while there is none. */
function useElementRect(selector: string | null): DOMRect | null {
  const [rect, setRect] = useState<DOMRect | null>(null);
  const measure = () => {
    const next = (selector ? document.querySelector(selector) : null)?.getBoundingClientRect() ?? null;
    setRect((current) =>
      current && next && current.left === next.left && current.top === next.top && current.width === next.width && current.height === next.height ? current : next,
    );
  };
  useLayoutEffect(measure);
  useLayoutEffect(() => {
    const element = selector ? document.querySelector(selector) : null;
    if (!element) {
      return;
    }
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    window.addEventListener("resize", measure);
    return () => {
      observer.disconnect();
      window.removeEventListener("resize", measure);
    };
  });
  return rect;
}

export function App() {
  const { state, editing, palette, search, welcome } = useStore();
  const switcher = useSwitcher();
  const [menu, setMenu] = useState<MenuState | null>(null);
  const [popover, setPopover] = useState(false);
  useEffect(() => startToolbar(setPopover), []);
  const closeMenu = useCallback(() => setMenu(null), []);
  const openMenu = useCallback((x: number, y: number, items: MenuItem[]) => setMenu({ x, y, items }), []);
  // agterm: the zoom layer and the dashboard both cover the sidebar.
  const sidebarVisible = (state?.sidebar.visible ?? true) && !state?.zoomed && !state?.dashboard;
  const selectedSession = state?.workspaces.flatMap((w) => w.sessions).find((s) => s.id === state.selectedSessionId);
  const focusedKey = selectedSession ? paneKey(selectedSession.id, selectedSession.focusedPane) : null;
  const modal = state?.pick != null || state?.ask != null || state?.clipboardPrompt != null || welcome;

  useLayoutEffect(() => {
    if (state) postToolbar(state, modal);
  }, [state, modal]);

  // agterm: a pick closes the palette, which is not mounted while the pick holds the window.
  const pickId = state?.pick?.id ?? null;
  useEffect(() => {
    if (pickId) {
      setPalette(null);
    }
  }, [pickId]);

  // agterm: a GUI ask closes the palette too.
  const askId = state?.ask?.id ?? null;
  useEffect(() => {
    if (askId) {
      setPalette(null);
    }
  }, [askId]);

  const pendingAsks = [state?.ask?.id, ...(state?.workspaces.flatMap((w) => w.sessions.map((s) => s.ask?.id)) ?? [])].filter(Boolean).join(" ");
  useEffect(() => {
    forgetAskDrafts(new Set(pendingAsks.split(" ")));
  }, [pendingAsks]);

  // agterm's askAnchorFrame: the terminal area without an anchor or for its session, the pane for a pane's question;
  // none while the anchored session is under a zoom or the dashboard.
  const guiAsk = state?.ask ?? null;
  const askSelector = !guiAsk
    ? null
    : guiAsk.sessionId === null
      ? ".terminal-pane"
      : state?.zoomed || state?.dashboard
        ? null
        : guiAsk.pane
          ? ".ask-anchor"
          : ".terminal-pane";
  const askFrame = useElementRect(askSelector);

  const sidebarSetting = state?.sidebar.visible ?? true;
  useEffect(() => {
    setSidebarToggle(() => command({ command: "setSidebar", visible: !sidebarSetting }));
  }, [sidebarSetting]);

  useEffect(() => {
    // Terminals handle their own shortcuts; this covers focus anywhere else.
    const onKeyDown = (event: KeyboardEvent) => {
      if ((event.target as Element | null)?.closest?.(".xterm")) {
        return;
      }
      if (handleAppShortcut(event)) {
        event.preventDefault();
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, []);

  // agterm's suppressAutoFollow: auto-follow waits while the user is busy in a palette, a rename or a title bar list; the
  // host itself holds it under the dashboard, a pick or an ask.
  const busy = palette !== null || editing !== null || popover;
  useEffect(() => {
    send({ type: "autoFollowSuppressed", value: busy });
  }, [busy]);

  // agterm's beginEditing does nothing for a row that is not on screen, and a rename ends with its row.
  useLayoutEffect(() => {
    if (editing !== null && !document.querySelector(".sidebar .inline-edit")) {
      setEditing(null);
    }
  });

  // Explorer drops: anything on a terminal is typed in as paths (folders on the sidebar are the sidebar's own). Text and links
  // dragged from elsewhere go into a terminal as agterm pastes them. Every other drop is swallowed, so a stray file or
  // link never navigates the window away from agshell.
  useEffect(() => {
    const isFiles = (event: DragEvent) => event.dataTransfer?.types.includes("Files") ?? false;
    const isText = (event: DragEvent) => ["text/uri-list", "text/plain"].some((type) => event.dataTransfer?.types.includes(type));
    const onDragOver = (event: DragEvent) => {
      if (isFiles(event) || isText(event)) {
        event.preventDefault();
        event.dataTransfer!.dropEffect = "copy";
      }
    };
    const onDrop = (event: DragEvent) => {
      if (!isFiles(event)) {
        if (isText(event)) {
          event.preventDefault();
          const terminal = event.target instanceof Element ? terminalAt(event.target) : null;
          if (terminal) {
            const uris = (event.dataTransfer!.getData("text/uri-list") || "").split(/\r?\n/).filter((line) => line && !line.startsWith("#"));
            send({ type: "dropText", ...terminal, text: event.dataTransfer!.getData("text/plain"), uris });
          }
        }
        return;
      }
      event.preventDefault();
      const target = event.target as Element | null;
      const files = event.dataTransfer!.files;
      if (!target || files.length === 0) {
        return;
      }
      const terminal = terminalAt(target);
      if (terminal) {
        sendFiles({ type: "dropPaths", ...terminal }, files);
      }
    };
    window.addEventListener("dragover", onDragOver);
    window.addEventListener("drop", onDrop);
    return () => {
      window.removeEventListener("dragover", onDragOver);
      window.removeEventListener("drop", onDrop);
    };
  }, []);

  if (!state) {
    return <div className="app loading" />;
  }

  return (
    <div
      className="app"
      style={
        {
          // agterm's palette and picker dim from under the title bar and centre over the terminal, beside the sidebar.
          "--chrome-top": "0px",
          "--sidebar-offset": sidebarVisible ? `${state.sidebar.width}px` : "0px",
        } as React.CSSProperties
      }
    >
      {sidebarVisible && <Sidebar state={state} editing={editing} openMenu={openMenu} />}
      <div className="main-column">
        <TerminalPane
          state={state}
          search={search}
          covered={modal || palette !== null || menu !== null || switcher !== null || popover}
          focusTerminal={!modal && !(selectedSession && sessionAskHoldsKeys(state, selectedSession)) && editing === null && menu === null && palette === null && !popover && search?.key !== focusedKey}
        />
      </div>
      {state.pick && <Picker key={state.pick.id} pick={state.pick} state={state} />}
      {welcome && <Welcome />}
      {state.ask && (
        // agterm: the whole window ignores clicks while the question is up; only its anchor is dimmed, the dialog centred on it.
        <div className="ask-backdrop" onMouseDown={(event) => event.preventDefault()}>
          {askFrame && (
            <div className="ask-frame" style={{ left: askFrame.left, top: askFrame.top, width: askFrame.width, height: askFrame.height }}>
              <AskDialog key={state.ask.id} ask={state.ask} active={!state.pick} />
            </div>
          )}
        </div>
      )}
      {state.clipboardPrompt && <ClipboardPrompt key={state.clipboardPrompt.id} prompt={state.clipboardPrompt} />}
      {menu && <ContextMenu x={menu.x} y={menu.y} items={menu.items} onClose={closeMenu} />}
      <SessionSwitcher state={state} />
      {palette && !state.pick && <Palette mode={palette} state={state} />}
    </div>
  );
}
