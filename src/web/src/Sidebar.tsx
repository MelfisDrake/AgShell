import { useEffect, useRef, useState } from "react";
import { closeSession, confirmCloseSessions } from "./actions";
import { AppState, command, sendFiles, SessionView, WorkspaceView } from "./bridge";
import { MenuItem } from "./ContextMenu";
import { ChevronIcon, CloudIcon, CloudOffIcon, CloudSplitIcon, FilterIcon, FlagIcon, FolderPlusIcon, PlusIcon, SplitIcon, TerminalIcon, WorkspaceIcon } from "./icons";
import { InlineEdit } from "./InlineEdit";
import { StatusGlyph } from "./StatusGlyph";
import { Editing, onDropPlan, SessionName, sessionName, setEditing } from "./store";
import { t } from "./i18n";
import { bindingLabel } from "./shortcuts";

const dragType = "application/x-agshell-session";
const workspaceDragType = "application/x-agshell-workspace";

interface Props {
  state: AppState;
  editing: Editing | null;
  openMenu: (x: number, y: number, items: MenuItem[]) => void;
}

/** Sessions picked with Ctrl+click and Shift+click, for the batch menu; plain clicks pick one. */
interface MultiSelection {
  ids: string[];
  onRowClick: (session: SessionView, event: React.MouseEvent) => void;
  /** agterm: a right click outside the selection narrows it to the clicked row first. */
  onRowContext: (session: SessionView) => void;
}

interface DropTarget {
  workspaceId: string;
  sessionId: string | null;
  after: boolean;
}

/** Where a dragged workspace lands: before or after a shown workspace, as agterm splits on the header's midpoint. */
interface WorkspaceDropTarget {
  workspaceId: string;
  after: boolean;
}

interface WorkspaceDrag {
  dragged: string | null;
  target: WorkspaceDropTarget | null;
  start: (id: string) => void;
  /** False when the drop would change nothing, so it is not offered. */
  over: (target: WorkspaceDropTarget) => boolean;
  drop: () => void;
}

/** A drag of sessions, the selection's when it starts on a selected row, and of folders from Explorer. */
interface SessionDrag {
  dragged: string[] | null;
  target: DropTarget | null;
  /** The workspace dragged folders would open in. */
  folderTarget: string | null;
  /** The folded workspace the drag opened. */
  springOpen: string | null;
  start: (ids: string[]) => void;
  /** False when the drop would change nothing, so it is not offered. */
  over: (target: DropTarget) => boolean;
  drop: (target: DropTarget) => void;
}

const noDragging: SessionDrag = { dragged: null, target: null, folderTarget: null, springOpen: null, start: () => {}, over: () => false, drop: () => {} };

export function Sidebar({ state, editing, openMenu }: Props) {
  // agterm's helpHint: the button's name with the keys its action has now.
  const hint = (base: string, action: string) => {
    const keys = bindingLabel(state.keymap.actions[action]);
    return keys ? `${base} (${keys})` : base;
  };
  const [dragged, setDragged] = useState<string[] | null>(null);
  const [dropTarget, setDropTarget] = useState<DropTarget | null>(null);
  const [folderTarget, setFolderTarget] = useState<string | null>(null);
  const [draggedWorkspace, setDraggedWorkspace] = useState<string | null>(null);
  const [workspaceTarget, setWorkspaceTarget] = useState<WorkspaceDropTarget | null>(null);
  const [width, setWidth] = useState<number | null>(null);
  const currentWidth = width ?? state.sidebar.width;
  const [picked, setPicked] = useState<string[]>([]);
  const [anchor, setAnchor] = useState<string | null>(null);
  // agterm's replaceSidebarSelection: a selection made anywhere but a row click (keys, palette, CLI, a follow) drops the batch.
  const rowSelection = useRef<string | null>(null);
  useEffect(() => {
    if (state.selectedSessionId !== rowSelection.current) {
      setPicked([]);
      setAnchor(null);
    }
    rowSelection.current = null;
  }, [state.selectedSessionId]);

  const shown = (element: string) => !state.settings.hiddenInterfaceElements.includes(element);
  const allIds = new Set(state.workspaces.flatMap((w) => w.sessions.map((s) => s.id)));
  const order =
    state.sidebarMode === "flagged"
      ? state.navigableSessionIds
      : state.workspaces.filter((w) => state.visibleWorkspaceIds.includes(w.id) && !w.collapsed).flatMap((w) => w.sessions.map((s) => s.id));
  // agterm's sidebar selection: only rows the sidebar shows, and none while the active session is not among them.
  const visibleIds = new Set(state.navigableSessionIds);
  const multi: MultiSelection = {
    ids: state.selectedSessionId && visibleIds.has(state.selectedSessionId) ? picked.filter((id) => allIds.has(id) && visibleIds.has(id)) : [],
    onRowClick: (session, event) => {
      if (event.ctrlKey) {
        const base = picked.length > 0 ? picked : state.selectedSessionId ? [state.selectedSessionId] : [];
        pick(base.includes(session.id) ? base.filter((id) => id !== session.id) : [...base, session.id], session.id);
        setAnchor(session.id);
        return;
      }
      const from = anchor ?? state.selectedSessionId;
      if (event.shiftKey && from && order.includes(from) && order.includes(session.id)) {
        const [start, end] = [order.indexOf(from), order.indexOf(session.id)].sort((x, y) => x - y);
        pick(order.slice(start, end + 1), session.id);
        return;
      }
      setPicked([]);
      setAnchor(session.id);
      select(session.id);
    },
    onRowContext: (session) => {
      if (multi.ids.includes(session.id) || (multi.ids.length === 0 && session.id === state.selectedSessionId)) {
        return;
      }
      setPicked([]);
      setAnchor(session.id);
      select(session.id);
    },
  };
  function select(sessionId: string) {
    rowSelection.current = sessionId;
    command({ command: "selectSession", sessionId, reveal: true });
  }
  // agterm's selection change: the clicked row becomes the active one when it is selected, else the active one stays
  // while still selected, else the last selected takes over.
  function pick(ids: string[], clicked: string) {
    setPicked(ids);
    const current = state.selectedSessionId;
    const active = ids.includes(clicked) ? clicked : current && ids.includes(current) ? current : ids[ids.length - 1];
    if (active && active !== current) {
      select(active);
    }
  }

  const profileItems = (workspaceId?: string): MenuItem[] =>
    state.profiles.map((profile) => ({
      label: profile.name,
      onSelect: () => command({ command: "newSession", workspaceId, profileId: profile.id }),
    }));

  // agterm's validateDrop refuses a drop that changes nothing. The host tells which places those are when a drag starts,
  // and again as the tree changes under it, as agterm reads the live tree on every move.
  const [plan, setPlan] = useState<Set<string> | null>(null);
  const planToken = useRef(0);
  useEffect(() => onDropPlan((token, noop) => token === planToken.current && setPlan(new Set(noop))), []);
  const askPlan = (request: { sessionIds?: string[]; workspaceId?: string }) => {
    planToken.current += 1;
    command({ command: "dropPlan", token: planToken.current, ...request });
  };
  useEffect(() => {
    if (dragged) {
      askPlan({ sessionIds: dragged });
    } else if (draggedWorkspace) {
      askPlan({ workspaceId: draggedWorkspace });
    }
  }, [state.workspaces, state.visibleWorkspaceIds]);
  const noop = (key: string) => plan?.has(key) ?? false;

  // agterm's spring-loaded expansion: a folded workspace a drag rests on opens after 0.65 s for this drag only. Leaving it,
  // a cancel or a refused drop folds it back; a drop that lands keeps it open, as a reveal the host keeps.
  const [springOpen, setSpringOpen] = useState<string | null>(null);
  const springPending = useRef<{ id: string; timer: number } | null>(null);
  const keepSpring = useRef(false);
  const dropped = useRef(false);
  const clearSpringPending = () => {
    window.clearTimeout(springPending.current?.timer);
    springPending.current = null;
  };
  const cancelSpring = () => {
    clearSpringPending();
    setSpringOpen(null);
  };
  const scheduleSpring = (id: string | null) => {
    const workspace = id ? state.workspaces.find((w) => w.id === id) : undefined;
    if (!id || !workspace || state.sidebarMode !== "tree") {
      cancelSpring();
      return;
    }
    if (springOpen === id) {
      return;
    }
    if (!workspace.collapsed) {
      cancelSpring();
      return;
    }
    if (springPending.current?.id === id) {
      return;
    }
    cancelSpring();
    springPending.current = {
      id,
      timer: window.setTimeout(() => {
        springPending.current = null;
        setSpringOpen(id);
      }, 650),
    };
  };
  useEffect(() => {
    if (keepSpring.current) {
      keepSpring.current = false;
      setSpringOpen(null);
    }
  }, [state]);

  const finishDrag = (landed: boolean) => {
    clearSpringPending();
    if (landed && springOpen) {
      keepSpring.current = true;
    } else {
      setSpringOpen(null);
    }
    setDragged(null);
    setDropTarget(null);
    setDraggedWorkspace(null);
    setWorkspaceTarget(null);
    setFolderTarget(null);
    setPlan(null);
  };
  const leaveSidebar = () => {
    cancelSpring();
    setDropTarget(null);
    setWorkspaceTarget(null);
    setFolderTarget(null);
  };

  const sessionDrag: SessionDrag = {
    dragged,
    target: dropTarget,
    folderTarget,
    springOpen,
    start: (ids) => {
      dropped.current = false;
      setPlan(null);
      setDragged(ids);
      askPlan({ sessionIds: ids });
    },
    over: (target) => {
      if (noop(target.sessionId ? `${target.sessionId}:${target.after ? "after" : "before"}` : target.workspaceId)) {
        setDropTarget(null);
        cancelSpring();
        return false;
      }
      if (target.workspaceId !== dropTarget?.workspaceId || target.sessionId !== dropTarget.sessionId || target.after !== dropTarget.after) {
        setDropTarget(target);
      }
      scheduleSpring(target.workspaceId);
      return true;
    },
    drop: (target) => {
      if (dragged && !noop(target.sessionId ? `${target.sessionId}:${target.after ? "after" : "before"}` : target.workspaceId)) {
        command({ command: "dropSessions", sessionIds: dragged, workspaceId: target.workspaceId, sessionId: target.sessionId, after: target.after, springOpen: springOpen ?? undefined });
        dropped.current = true;
        finishDrag(true);
      }
    },
  };

  // agterm's workspace reorder: the slot among the shown workspaces; the host maps it onto the full list.
  const shownWorkspaces = state.workspaces.filter((w) => state.visibleWorkspaceIds.includes(w.id));
  const slotOf = (target: WorkspaceDropTarget) => shownWorkspaces.findIndex((w) => w.id === target.workspaceId) + (target.after ? 1 : 0);
  const workspaceDrag: WorkspaceDrag = {
    dragged: draggedWorkspace,
    target: workspaceTarget,
    start: (id) => {
      dropped.current = false;
      setPlan(null);
      setDraggedWorkspace(id);
      askPlan({ workspaceId: id });
    },
    over: (target) => {
      cancelSpring();
      if (noop(`slot:${slotOf(target)}`)) {
        setWorkspaceTarget(null);
        return false;
      }
      if (target.workspaceId !== workspaceTarget?.workspaceId || target.after !== workspaceTarget.after) {
        setWorkspaceTarget(target);
      }
      return true;
    },
    drop: () => {
      if (draggedWorkspace && workspaceTarget) {
        command({ command: "dropWorkspace", workspaceId: draggedWorkspace, slot: slotOf(workspaceTarget) });
        dropped.current = true;
        finishDrag(true);
      }
    },
  };

  /** agterm: below or above every workspace row the slot still counts, so a workspace drops past the last one. */
  const workspaceSlotTarget = (tree: HTMLElement, y: number): WorkspaceDropTarget | null => {
    const headers = Array.from(tree.querySelectorAll(":scope > section.workspace > .workspace-header"));
    const slot = headers.filter((header) => {
      const rect = header.getBoundingClientRect();
      return y > rect.top + rect.height / 2;
    }).length;
    if (shownWorkspaces.length === 0) {
      return null;
    }
    return slot === 0 ? { workspaceId: shownWorkspaces[0].id, after: false } : { workspaceId: shownWorkspaces[slot - 1].id, after: true };
  };

  // agterm's folder drop: the row's workspace, else the one the focus filter narrows to, else the current one; refused in
  // the flagged view. Whether a file is a folder shows only after the drop, so plain files are turned down by the host.
  const focusedWorkspaces = state.workspaces.filter((w) => w.focused);
  const folderWorkspace = (element: EventTarget) => {
    const row = element instanceof Element ? (element.closest("[data-workspace-id]") as HTMLElement | null)?.dataset.workspaceId : undefined;
    return row ?? (state.focusEnabled && focusedWorkspaces.length === 1 ? focusedWorkspaces[0].id : null) ?? state.currentWorkspaceId ?? null;
  };
  const isFiles = (event: React.DragEvent) => event.dataTransfer.types.includes("Files");

  const startResize = (event: React.PointerEvent<HTMLDivElement>) => {
    const startX = event.clientX;
    const startWidth = currentWidth;
    const handle = event.currentTarget;
    handle.setPointerCapture(event.pointerId);
    let latest = startWidth;
    const onMove = (move: PointerEvent) => {
      latest = Math.max(160, Math.min(560, startWidth + move.clientX - startX));
      setWidth(latest);
    };
    const onUp = () => {
      handle.removeEventListener("pointermove", onMove);
      handle.removeEventListener("pointerup", onUp);
      command({ command: "setSidebar", width: latest });
      setWidth(null);
    };
    handle.addEventListener("pointermove", onMove);
    handle.addEventListener("pointerup", onUp);
  };

  return (
    <aside
      className="sidebar"
      style={{ width: currentWidth }}
      onDragOver={(event) => {
        if (!isFiles(event)) {
          return;
        }
        event.preventDefault();
        event.stopPropagation();
        if (state.sidebarMode !== "tree") {
          event.dataTransfer.dropEffect = "none";
          return;
        }
        event.dataTransfer.dropEffect = "copy";
        const id = folderWorkspace(event.target);
        if (id !== folderTarget) {
          setFolderTarget(id);
        }
        scheduleSpring(id);
      }}
      onDragLeave={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
          leaveSidebar();
        }
      }}
      onDrop={(event) => {
        if (!isFiles(event)) {
          return;
        }
        event.preventDefault();
        event.stopPropagation();
        const row = event.target instanceof Element ? (event.target.closest("[data-workspace-id]") as HTMLElement | null)?.dataset.workspaceId : undefined;
        if (state.sidebarMode === "tree" && event.dataTransfer.files.length > 0) {
          sendFiles({ type: "dropFolders", workspaceId: row, springOpen: springOpen ?? undefined }, event.dataTransfer.files);
          finishDrag(true);
        } else {
          finishDrag(false);
        }
      }}
    >
      {state.sidebarMode === "flagged" && state.settings.flaggedViewLayout === "tree" ? (
        <div className="tree">
          <div className="sidebar-caption">{t("Помеченные")}</div>
          {state.navigableSessionIds.length === 0 && <div className="sidebar-empty">{t("Нет помеченных сессий.\nПравый клик по сессии → Пометить.")}</div>}
          {state.workspaces
            .filter((workspace) => workspace.sessions.some((session) => session.flagged))
            .map((workspace) => (
              <WorkspaceGroup
                key={workspace.id}
                workspace={workspace}
                only={workspace.sessions.filter((session) => session.flagged)}
                noDrag
                state={state}
                editing={editing}
                drag={noDragging}
                openMenu={openMenu}
                profileItems={profileItems}
                multi={multi}
              />
            ))}
        </div>
      ) : state.sidebarMode === "flagged" ? (
        <div className="tree">
          <div className="sidebar-caption">{t("Помеченные")}</div>
          {state.navigableSessionIds.length === 0 && <div className="sidebar-empty">{t("Нет помеченных сессий.\nПравый клик по сессии → Пометить.")}</div>}
          {state.navigableSessionIds.map((id) => {
            const workspace = state.workspaces.find((w) => w.sessions.some((s) => s.id === id))!;
            const session = workspace.sessions.find((s) => s.id === id)!;
            return (
              <SessionRow
                key={id}
                workspace={workspace}
                session={session}
                suffix={state.settings.flaggedViewLayout === "flat" ? ` : ${workspace.name}` : undefined}
                noDrag
                multi={multi}
                selected={id === state.selectedSessionId}
                state={state}
                editing={editing}
                drag={noDragging}
                openMenu={openMenu}
                profileItems={profileItems}
              />
            );
          })}
        </div>
      ) : (
      <div
        className="tree"
        onDragEnd={() => {
          if (!dropped.current) {
            finishDrag(false);
          }
          dropped.current = false;
        }}
        onDragOver={(event) => {
          if (!draggedWorkspace || event.target !== event.currentTarget) {
            return;
          }
          const target = workspaceSlotTarget(event.currentTarget, event.clientY);
          if (target && workspaceDrag.over(target)) {
            event.preventDefault();
          }
        }}
        onDrop={(event) => {
          if (draggedWorkspace && event.target === event.currentTarget) {
            event.preventDefault();
            workspaceDrag.drop();
          }
        }}
      >
        {shownWorkspaces.map((workspace) => (
          <WorkspaceGroup
            key={workspace.id}
            workspace={workspace}
            state={state}
            editing={editing}
            drag={sessionDrag}
            openMenu={openMenu}
            profileItems={profileItems}
            multi={multi}
            workspaceDrag={workspaceDrag}
          />
        ))}
      </div>
      )}
      {/* agterm's bottom bar: the two add controls on the left, the two view toggles on the right. */}
      <div className="sidebar-footer">
        {shown("newWorkspace") && (
          <button type="button" className="icon-button" title={hint(t("Новый workspace"), "new_workspace")} onClick={() => command({ command: "newWorkspace" })}>
            <FolderPlusIcon />
          </button>
        )}
        {shown("newSession") && (
          <button
            type="button"
            className="icon-button"
            title={hint(t("Новая сессия"), "new_session")}
            onClick={(event) => {
              // agterm's add menu: a session where new ones go, or in a directory picked for it. The shell to use is
              // AgShell's own item: Windows has several.
              const rect = event.currentTarget.getBoundingClientRect();
              openMenu(rect.left, rect.top - 8 - 28 * 3, [
                { label: t("Новая сессия"), onSelect: () => command({ command: "newSession" }) },
                { label: t("Новая сессия с шеллом"), items: profileItems() },
                { label: t("Открыть каталог…"), onSelect: () => command({ command: "openDirectory" }) },
              ]);
            }}
          >
            <PlusIcon />
          </button>
        )}
        <span className="footer-spacer" />
        {shown("focusFilter") && (
          <button
            type="button"
            className="icon-button"
            title={hint(state.focusEnabled ? t("Показать все workspace") : t("Показать только отмеченные workspace"), "toggle_workspace_filter")}
            disabled={!state.workspaces.some((w) => w.focused)}
            onClick={() => command({ command: "setFocusEnabled" })}
          >
            <FilterIcon filled={state.focusEnabled} />
          </button>
        )}
        {shown("flaggedView") && (
          <button
            type="button"
            className="icon-button"
            title={hint(state.sidebarMode === "flagged" ? t("Показать все сессии") : t("Показать помеченные сессии"), "toggle_flagged_view")}
            disabled={state.sidebarMode === "tree" && !state.workspaces.some((w) => w.sessions.some((s) => s.flagged))}
            onClick={() => command({ command: "setSidebarMode" })}
          >
            <FlagIcon filled={state.sidebarMode === "flagged"} />
          </button>
        )}
      </div>
      <div className="resize-handle" onPointerDown={startResize} />
    </aside>
  );
}

interface GroupProps {
  workspace: WorkspaceView;
  state: AppState;
  editing: Editing | null;
  drag: SessionDrag;
  openMenu: (x: number, y: number, items: MenuItem[]) => void;
  profileItems: (workspaceId?: string) => MenuItem[];
  multi: MultiSelection;
  workspaceDrag?: WorkspaceDrag;
  /** The flagged view's tree shows only these of the workspace's sessions. */
  only?: SessionView[];
  /** The flagged views are projections, not a tree to rearrange. */
  noDrag?: boolean;
}

// agterm's pendingRowToggle: one deferred row toggle for the whole sidebar, cancelled by a double click.
let pendingRowToggle: number | undefined;

function WorkspaceGroup(props: GroupProps) {
  const { workspace, state, editing, drag, openMenu, profileItems, workspaceDrag: reorder, only } = props;
  const reorderTarget = reorder?.target?.workspaceId === workspace.id ? reorder.target : null;
  const isEditing = editing?.kind === "workspace" && editing.id === workspace.id;
  const headerTarget = (drag.target?.workspaceId === workspace.id && drag.target.sessionId === null) || drag.folderTarget === workspace.id;
  const collapsed = workspace.collapsed && drag.springOpen !== workspace.id;
  // agterm animates a fold the user asks for (the chevron, a row click); a fold from a selection or the model does not.
  const [animating, setAnimating] = useState(false);
  const toggle = () => {
    setAnimating(true);
    command({ command: "setCollapsed", workspaceId: workspace.id, collapsed: !workspace.collapsed });
  };
  // agterm: a row click waits out the double-click interval, so a rename does not fold the row on its way in.
  const rowClick = () => {
    window.clearTimeout(pendingRowToggle);
    pendingRowToggle = window.setTimeout(() => {
      pendingRowToggle = undefined;
      toggle();
    }, state.settings.doubleClickTime);
  };
  // agterm's displayedUnseen: the flagged tree's header counts only the rows it shows.
  const unseen = (only ?? workspace.sessions).reduce((sum, session) => sum + session.unseen, 0);

  const menu: MenuItem[] = [
    { label: t("Переименовать"), onSelect: () => setEditing({ kind: "workspace", id: workspace.id }) },
    { label: t("Копировать имя"), onSelect: () => copyText(workspace.name) },
    { label: t("Новая сессия"), onSelect: () => command({ command: "newSession", workspaceId: workspace.id }) },
    { label: t("Новая сессия с шеллом"), items: profileItems(workspace.id) },
    { label: t("Открыть каталог…"), onSelect: () => command({ command: "openDirectory", workspaceId: workspace.id }) },
    {
      label: state.focusEnabled && workspace.focused && state.workspaces.filter((w) => w.focused).length === 1 ? t("Снять фокус") : t("Фокус"),
      separatorBefore: true,
      onSelect: () => command({ command: "toggleFocusWorkspace", workspaceId: workspace.id }),
    },
    {
      label: workspace.focused ? t("Убрать из фокуса") : t("Добавить в фокус"),
      onSelect: () => command({ command: "setFocusMember", workspaceId: workspace.id, member: !workspace.focused }),
    },
    {
      // agterm's Flag All Sessions: every session of the workspace as one value; unflags only when all are flagged.
      label: workspace.sessions.length > 0 && workspace.sessions.every((session) => session.flagged) ? t("Снять пометку со всех сессий") : t("Пометить все сессии"),
      separatorBefore: true,
      disabled: workspace.sessions.length === 0,
      onSelect: () => command({ command: "setFlag", sessionIds: workspace.sessions.map((session) => session.id), flagged: !workspace.sessions.every((session) => session.flagged) }),
    },
    {
      label: t("Удалить workspace"),
      danger: true,
      separatorBefore: true,
      disabled: !state.canRemoveWorkspace,
      onSelect: () => command({ command: "closeWorkspace", workspaceId: workspace.id }),
    },
  ];

  return (
    <section
      className={
        "workspace" +
        (reorder?.dragged === workspace.id ? " dragging" : "") +
        (reorderTarget ? (reorderTarget.after ? " drop-after" : " drop-before") : "")
      }
      data-workspace-id={workspace.id}
      onDragOver={(event) => {
        if (!reorder?.dragged) {
          return;
        }
        const header = event.currentTarget.querySelector(".workspace-header")!.getBoundingClientRect();
        if (reorder.over({ workspaceId: workspace.id, after: event.clientY > header.top + header.height / 2 })) {
          event.preventDefault();
        }
      }}
      onDrop={(event) => {
        if (reorder?.dragged) {
          event.preventDefault();
          reorder.drop();
        }
      }}
    >
      <div
        className={"workspace-header" + (headerTarget ? " drop-into" : "")}
        draggable={!isEditing && reorder !== undefined}
        onDragStart={(event) => {
          event.dataTransfer.setData(workspaceDragType, workspace.id);
          event.dataTransfer.effectAllowed = "move";
          reorder?.start(workspace.id);
        }}
        onClick={state.settings.workspaceRowClickExpands ? rowClick : undefined}
        onDoubleClick={(event) => {
          event.stopPropagation();
          window.clearTimeout(pendingRowToggle);
          pendingRowToggle = undefined;
          setEditing({ kind: "workspace", id: workspace.id });
        }}
        onContextMenu={(event) => {
          event.preventDefault();
          openMenu(event.clientX, event.clientY, menu);
        }}
        onDragOver={(event) => {
          if (drag.dragged && drag.over({ workspaceId: workspace.id, sessionId: null, after: true })) {
            event.preventDefault();
          }
        }}
        onDrop={(event) => {
          if (drag.dragged) {
            event.preventDefault();
            drag.drop({ workspaceId: workspace.id, sessionId: null, after: true });
          }
        }}
      >
        <span
          className="chevron"
          onClick={(event) => {
            event.stopPropagation();
            toggle();
          }}
        >
          <ChevronIcon open={!collapsed} />
        </span>
        <span className="row-icon">
          <WorkspaceIcon bold={workspace.focused} />
        </span>
        {isEditing ? (
          <InlineEdit
            value={workspace.name}
            onCommit={(name) => {
              setEditing(null);
              command({ command: "renameWorkspace", workspaceId: workspace.id, name });
            }}
            onCancel={() => setEditing(null)}
          />
        ) : (
          <span className="workspace-name" onMouseEnter={titleIfTruncated}>
            {workspace.name}
          </span>
        )}
        {!state.settings.hiddenInterfaceElements.includes("workspaceAddSession") && (
        <button
          type="button"
          className="row-action"
          title={t("Новая сессия в этом workspace")}
          onClick={(event) => {
            event.stopPropagation();
            command({ command: "newSession", workspaceId: workspace.id });
          }}
        >
          <PlusIcon />
        </button>
        )}
        {unseen > 0 && state.settings.notificationBadgeEnabled && <Badge count={unseen} />}
      </div>
      {(!collapsed || animating) && (
        <div
          className={"workspace-sessions" + (collapsed ? " folded" : "") + (animating ? " animating" : "")}
          onTransitionEnd={(event) => {
            if (event.target === event.currentTarget) {
              setAnimating(false);
            }
          }}
        >
          <div className="workspace-sessions-inner">
            {(only ?? workspace.sessions).map((session) => (
              <SessionRow key={session.id} {...props} session={session} selected={session.id === state.selectedSessionId} />
            ))}
          </div>
        </div>
      )}
    </section>
  );
}

interface RowProps extends GroupProps {
  session: SessionView;
  selected: boolean;
  /** The flagged view names the workspace too. */
  suffix?: string;
}

function SessionRow({ session, workspace, state, editing, drag, openMenu, selected, suffix, multi, noDrag }: RowProps) {
  const picked = multi.ids.includes(session.id);
  const batch = picked && multi.ids.length > 1 ? multi.ids : null;
  // agterm scrolls the newly selected row into view.
  const rowRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (selected) {
      rowRef.current?.scrollIntoView({ block: "nearest" });
    }
  }, [selected]);
  const isEditing = editing?.kind === "session" && editing.id === session.id;
  const target = drag.target?.sessionId === session.id ? drag.target : null;
  const others = state.workspaces.filter((w) => w.id !== workspace.id);
  // agterm: a flagged row's icon is filled in the tree only, where not every row is flagged; a cloud keeps its form.
  const flaggedFill = state.sidebarMode === "tree" && session.flagged;

  // agterm's row menu: the status items only where they apply, a separator after them, then the rest in its order.
  const statusItems = (chosen: SessionView[]): MenuItem[] => {
    const many = chosen.length > 1;
    const items: MenuItem[] = [];
    if (chosen.some((s) => s.status !== "idle")) {
      items.push({ label: many ? t("Сбросить статусы") : t("Сбросить статус"), onSelect: () => chosen.forEach((s) => s.status !== "idle" && command({ command: "clearStatus", sessionId: s.id })) });
    }
    if (chosen.some((s) => s.status === "idle")) {
      items.push({ label: t("Отметить для внимания"), onSelect: () => chosen.forEach((s) => s.status === "idle" && command({ command: "markAttention", sessionId: s.id })) });
    }
    return items;
  };

  const batchMenu = (ids: string[]): MenuItem[] => {
    const chosen = state.workspaces.flatMap((w) => w.sessions).filter((s) => ids.includes(s.id));
    const allFlagged = chosen.every((s) => s.flagged);
    // agterm offers a workspace any of the sessions is not in yet.
    const targets = state.workspaces.filter((w) => chosen.some((s) => !w.sessions.some((x) => x.id === s.id)));
    const items: MenuItem[] = [];
    if (targets.length > 0) {
      items.push({
        label: t("Перенести в"),
        items: targets.map((w) => ({
          label: w.name,
          onSelect: () => command({ command: "moveSessions", sessionIds: ids, workspaceId: w.id }),
        })),
      });
    }
    items.push(
      { label: allFlagged ? t("Снять пометку с сессий") : t("Пометить сессии"), onSelect: () => command({ command: "setFlag", sessionIds: ids, flagged: !allFlagged }) },
      { label: t("Закрыть сессии ({n})", { n: ids.length }), danger: true, onSelect: () => confirmCloseSessions(ids) && command({ command: "closeSessions", sessionIds: ids }) },
    );
    return withStatus(statusItems(chosen), items);
  };

  const withStatus = (status: MenuItem[], rest: MenuItem[]): MenuItem[] =>
    status.length > 0 && rest.length > 0 ? [...status, { ...rest[0], separatorBefore: true }, ...rest.slice(1)] : [...status, ...rest];

  const menu: MenuItem[] = batch ? batchMenu(batch) : withStatus(statusItems([session]), [
    { label: t("Переименовать"), onSelect: () => setEditing({ kind: "session", id: session.id }) },
    { label: t("Копировать имя"), onSelect: () => copyText(sessionName(session)) },
    { label: t("Дублировать сессию"), onSelect: () => command({ command: "duplicateSession", sessionId: session.id }) },
    ...(others.length > 0
      ? [
          {
            label: t("Перенести в"),
            items: others.map((w) => ({
              label: w.name,
              onSelect: () => command({ command: "moveSession", sessionId: session.id, workspaceId: w.id, index: w.sessions.length }),
            })),
          },
        ]
      : []),
    { label: session.flagged ? t("Снять пометку") : t("Пометить"), onSelect: () => command({ command: "setFlag", sessionIds: [session.id], flagged: !session.flagged }) },
    { label: t("Открыть в проводнике"), disabled: !session.canReveal, onSelect: () => command({ command: "revealSession", sessionId: session.id }) },
    { label: t("Закрыть сессию"), danger: true, onSelect: () => closeSession(session.id) },
  ]);

  return (
    <div
      ref={rowRef}
      className={
        "session-row" +
        (selected ? " selected" : "") +
        (picked ? " picked" : "") +
        (drag.dragged?.includes(session.id) ? " dragging" : "") +
        (target ? (target.after ? " drop-after" : " drop-before") : "")
      }
      draggable={!isEditing && !noDrag}
      onDragStart={(event) => {
        // agterm: a drag from a selected row carries the whole selection.
        const ids = batch ?? [session.id];
        event.dataTransfer.setData(dragType, ids.join("\n"));
        event.dataTransfer.effectAllowed = "move";
        drag.start(ids);
      }}
      onDragOver={(event) => {
        if (!drag.dragged) {
          return;
        }
        const rect = event.currentTarget.getBoundingClientRect();
        if (drag.over({ workspaceId: workspace.id, sessionId: session.id, after: event.clientY > rect.top + rect.height / 2 })) {
          event.preventDefault();
        }
      }}
      onDrop={(event) => {
        if (drag.dragged && target) {
          event.preventDefault();
          drag.drop(target);
        }
      }}
      data-workspace-id={workspace.id}
      onClick={(event) => multi.onRowClick(session, event)}
      onDoubleClick={() => setEditing({ kind: "session", id: session.id })}
      onContextMenu={(event) => {
        event.preventDefault();
        multi.onRowContext(session);
        openMenu(event.clientX, event.clientY, menu);
      }}
    >
      <span className="session-icon" title={rowNotice(session)}>
        {session.remoteHost ? (
          rowNotice(session) ? (
            <CloudOffIcon split={session.hasSplit} />
          ) : session.hasSplit ? (
            <CloudSplitIcon />
          ) : (
            <CloudIcon />
          )
        ) : session.hasSplit ? (
          <SplitIcon axis={session.splitAxis} glyph={!flaggedFill ? "none" : session.splitAxis === "horizontal" ? "both-horizontal" : "both"} />
        ) : (
          <TerminalIcon filled={flaggedFill} />
        )}
      </span>
      {isEditing ? (
        <InlineEdit
          value={session.customName ?? sessionName(session)}
          onCommit={(name) => {
            setEditing(null);
            command({ command: "renameSession", sessionId: session.id, name });
          }}
          onCancel={() => setEditing(null)}
        />
      ) : (
        <span className="session-name" onMouseEnter={titleIfTruncated}>
          <SessionName session={session} />
          {suffix}
        </span>
      )}
      <StatusGlyph session={session} settings={state.settings} />
      {session.unseen > 0 && state.settings.notificationBadgeEnabled && <Badge count={session.unseen} />}
    </div>
  );
}

/** agterm's name tooltip: the full name, only while the row cuts it short. */
function titleIfTruncated(event: React.MouseEvent<HTMLElement>) {
  const element = event.currentTarget;
  element.title = element.scrollWidth > element.clientWidth ? (element.textContent ?? "") : "";
}

/** agterm's BadgeView: the unseen count, capped at 99+. */
function Badge({ count }: { count: number }) {
  return <span className="badge">{count > 99 ? "99+" : count}</span>;
}

/**
 * agterm's rowNotice: while the row's status stream is not up, its cloud is slashed and says why; a machine too old to
 * stream gets nothing, since nothing here changes it.
 */
function rowNotice(session: SessionView): string | undefined {
  if (!session.remoteHost) {
    return undefined;
  }
  switch (session.remoteStream) {
    case "connecting":
      return t("Подключаюсь к {host} за статусом, контекстом, уведомлениями, диалогами и overlay", { host: session.remoteHost ?? "" });
    case "failed":
      return t("Связь с {host} потеряна ({reason}), повтор идёт. Закройте сессию и подключите её заново, чтобы повторить сейчас.", { host: session.remoteHost ?? "", reason: session.remoteStreamError ?? t("поток оборвался") });
    default:
      return undefined;
  }
}

function copyText(text: string): void {
  navigator.clipboard.writeText(text).catch(() => {});
}
