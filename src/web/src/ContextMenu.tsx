import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { CloudIcon } from "./icons";

export interface MenuItem {
  label: string;
  onSelect?: () => void;
  items?: MenuItem[];
  danger?: boolean;
  disabled?: boolean;
  separatorBefore?: boolean;
  /** A remote row's machine, shown after the label with agterm's cloud. */
  remoteHost?: string | null;
}

interface Props {
  x: number;
  y: number;
  items: MenuItem[];
  onClose: () => void;
}

export function ContextMenu({ x, y, items, onClose }: Props) {
  const ref = useRef<HTMLDivElement>(null);
  // As agterm's NSMenu: one highlighted item per open level, the keys act on the deepest level the user is in.
  const [path, setPath] = useState<number[]>([]);
  const [level, setLevel] = useState(0);
  const [openDepth, setOpenDepth] = useState(0);
  const current = useRef({ path, level });
  current.current = { path, level };

  useEffect(() => {
    const itemsAt = (path: number[], depth: number): MenuItem[] => {
      let list = items;
      for (let d = 0; d < depth; d++) {
        list = list[path[d]]?.items ?? [];
      }
      return list;
    };
    const step = (list: MenuItem[], from: number, delta: number): number => {
      for (let n = 1; n <= list.length; n++) {
        const index = (((from + delta * n) % list.length) + list.length) % list.length;
        if (!list[index].disabled) {
          return index;
        }
      }
      return -1;
    };
    const enter = (path: number[], depth: number) => {
      const list = itemsAt(path, depth + 1);
      const first = step(list, -1, 1);
      if (first >= 0) {
        setPath([...path.slice(0, depth + 1), first]);
        setLevel(depth + 1);
        setOpenDepth(depth + 1);
      }
    };
    const onPointerDown = (event: PointerEvent) => {
      if (!ref.current?.contains(event.target as Node)) {
        onClose();
      }
    };
    const onKeyDown = (event: KeyboardEvent) => {
      const { path, level } = current.current;
      const list = itemsAt(path, level);
      const index = path.length > level ? path[level] : -1;
      const item = index >= 0 ? list[index] : undefined;
      if (event.key === "Escape") {
        onClose();
      } else if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        const next = step(list, index < 0 && event.key === "ArrowUp" ? list.length : index, event.key === "ArrowDown" ? 1 : -1);
        if (next >= 0) {
          setPath([...path.slice(0, level), next]);
          setOpenDepth(level);
        }
      } else if (event.key === "ArrowRight") {
        if (item?.items) {
          enter(path, level);
        }
      } else if (event.key === "ArrowLeft") {
        if (level > 0) {
          setPath(path.slice(0, level));
          setLevel(level - 1);
          setOpenDepth(level - 1);
        }
      } else if (event.key === "Enter" || event.key === " ") {
        if (item?.items) {
          enter(path, level);
        } else if (item) {
          item.onSelect?.();
          onClose();
        }
      } else if (event.key.length === 1 && !event.ctrlKey && !event.altKey && !event.metaKey) {
        const letter = event.key.toLocaleLowerCase();
        for (let n = 1; n <= list.length; n++) {
          const candidate = (index + n + list.length) % list.length;
          if (!list[candidate].disabled && list[candidate].label.toLocaleLowerCase().startsWith(letter)) {
            setPath([...path.slice(0, level), candidate]);
            setOpenDepth(level);
            break;
          }
        }
      } else {
        return;
      }
      event.preventDefault();
      event.stopPropagation();
    };
    document.addEventListener("pointerdown", onPointerDown, true);
    document.addEventListener("keydown", onKeyDown, true);
    window.addEventListener("blur", onClose);
    return () => {
      document.removeEventListener("pointerdown", onPointerDown, true);
      document.removeEventListener("keydown", onKeyDown, true);
      window.removeEventListener("blur", onClose);
    };
  }, [items, onClose]);

  const hover = (depth: number, index: number | null, opens: boolean) => {
    setPath((path) => (index === null ? path.slice(0, depth) : [...path.slice(0, depth), index]));
    setLevel(depth);
    setOpenDepth(opens ? depth + 1 : depth);
  };

  return (
    <div ref={ref} className="menu-layer">
      <MenuPanel x={x} y={y} items={items} depth={0} path={path} openDepth={openDepth} hover={hover} onClose={onClose} />
    </div>
  );
}

interface PanelProps {
  x: number;
  y: number;
  items: MenuItem[];
  depth: number;
  path: number[];
  openDepth: number;
  hover: (depth: number, index: number | null, opens: boolean) => void;
  onClose: () => void;
}

function MenuPanel({ x, y, items, depth, path, openDepth, hover, onClose }: PanelProps) {
  const ref = useRef<HTMLDivElement>(null);
  const [position, setPosition] = useState({ left: x, top: y });
  const [submenuAnchor, setSubmenuAnchor] = useState({ x: 0, y: 0 });
  const active = path.length > depth ? path[depth] : null;
  const openIndex = openDepth > depth && active !== null && items[active]?.items ? active : null;

  useLayoutEffect(() => {
    const rect = ref.current?.getBoundingClientRect();
    if (!rect) {
      return;
    }
    setPosition({
      left: Math.max(4, Math.min(x, window.innerWidth - rect.width - 4)),
      top: Math.max(4, Math.min(y, window.innerHeight - rect.height - 4)),
    });
  }, [x, y]);

  useLayoutEffect(() => {
    const button = openIndex !== null ? ref.current?.querySelectorAll<HTMLElement>(":scope > div > .menu-item")[openIndex] : null;
    if (button) {
      const rect = button.getBoundingClientRect();
      setSubmenuAnchor({ x: rect.right - 2, y: rect.top - 4 });
    }
  }, [openIndex]);

  return (
    <div ref={ref} className="menu" role="menu" style={{ left: position.left, top: position.top }}>
      {items.map((item, index) => (
        <div key={index}>
          {item.separatorBefore && <div className="menu-separator" />}
          <button
            type="button"
            role="menuitem"
            className={"menu-item" + (item.danger ? " danger" : "") + (active === index && !item.disabled ? " active" : "")}
            disabled={item.disabled}
            onPointerEnter={() => hover(depth, item.disabled ? null : index, !item.disabled && item.items !== undefined)}
            onPointerLeave={() => {
              if (!item.items) {
                hover(depth, null, false);
              }
            }}
            onClick={() => {
              if (item.items) {
                return;
              }
              item.onSelect?.();
              onClose();
            }}
          >
            <span>
              {item.label}
              {item.remoteHost && (
                <span className="remote-host">
                  <CloudIcon />
                  {item.remoteHost}
                </span>
              )}
            </span>
            {item.items && <span className="menu-arrow">›</span>}
          </button>
        </div>
      ))}
      {openIndex !== null && (
        <MenuPanel x={submenuAnchor.x} y={submenuAnchor.y} items={items[openIndex].items!} depth={depth + 1} path={path} openDepth={openDepth} hover={hover} onClose={onClose} />
      )}
    </div>
  );
}
