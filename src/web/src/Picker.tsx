import { useEffect, useMemo, useRef, useState } from "react";
import { AppState, command, PickItemView, PickView } from "./bridge";
import { fuzzyRank } from "./fuzzy";
import { Glyph } from "./StatusGlyph";
import { SearchIcon } from "./icons";
import { MiddleTruncated } from "./MiddleTruncated";
import { t } from "./i18n";
import { boundAction } from "./shortcuts";
import { paletteStep } from "./Palette";

/** agterm's free-text row: what Return answers with when a custom answer is allowed and nothing matches. */
const customRowId = "pick-custom";

/**
 * agterm's picker for agshellctl pick: the caller's items, matched by label only and listed in the caller's order while
 * the query is empty. Return answers with the highlighted item, or, when a custom answer is allowed and nothing matches,
 * with the row Use "…" standing for the trimmed query; Esc or a click on the backdrop cancels.
 */
export function Picker({ pick, state }: { pick: PickView; state: AppState }) {
  const [query, setQuery] = useState(pick.query ?? "");
  const [selection, setSelection] = useState(0);
  const listRef = useRef<HTMLDivElement>(null);

  const trimmed = query.trim();
  const filtered = useMemo((): PickItemView[] => {
    const matches = trimmed ? fuzzyRank(trimmed, pick.items, (item) => [item.label]) : pick.items;
    return matches.length === 0 && pick.allowCustom && trimmed ? [{ id: customRowId, label: t("Использовать «{value}»", { value: trimmed }), subtitle: null, status: null, statusColor: null, statusShape: null }] : matches;
  }, [pick.items, pick.allowCustom, trimmed]);

  useEffect(() => {
    const index = pick.selection ? filtered.findIndex((item) => item.id === pick.selection) : -1;
    setSelection(Math.max(0, index));
  }, [pick.id]);

  useEffect(() => {
    setSelection((current) => Math.min(current, Math.max(0, filtered.length - 1)));
  }, [filtered.length]);

  useEffect(() => {
    listRef.current?.querySelector(".palette-row.selected")?.scrollIntoView({ block: "nearest" });
  }, [selection]);

  const answer = (itemId?: string, custom?: string) => command({ command: "pickAnswer", id: pick.id, itemId, query: custom });
  const choose = (itemId: string) => (itemId === customRowId ? answer(undefined, trimmed) : answer(itemId));

  const onKeyDown = (event: React.KeyboardEvent<HTMLInputElement>) => {
    const step = paletteStep(event);
    const closes = event.key === "Escape" || boundAction(event.nativeEvent, ["close_session"]) !== undefined;
    if (step === 0 && !closes && (event.ctrlKey || event.altKey || event.metaKey)) {
      return;
    }
    event.stopPropagation();
    if (step !== 0) {
      event.preventDefault();
      if (filtered.length > 0) {
        setSelection((current) => Math.max(0, Math.min(current + step, filtered.length - 1)));
      }
    } else if (event.key === "Tab") {
      event.preventDefault();
    } else if (event.key === "Enter") {
      event.preventDefault();
      if (filtered.length > 0) {
        choose(filtered[selection].id);
      }
    } else if (closes) {
      event.preventDefault();
      answer();
    }
  };

  return (
    <div className="palette-backdrop" onMouseDown={() => answer()}>
      <div
        className="palette"
        onMouseDown={(event) => {
          event.stopPropagation();
          if (!(event.target instanceof HTMLInputElement)) {
            event.preventDefault();
          }
        }}
      >
        <div className="palette-search">
          <SearchIcon />
          <input
            className="palette-input"
            autoFocus
            spellCheck={false}
            placeholder={pick.prompt ?? t("Выбрать…")}
            value={query}
            onChange={(event) => {
              setQuery(event.target.value);
              setSelection(0);
            }}
            onKeyDown={onKeyDown}
          />
        </div>
        {(pick.items.length > 0 || trimmed) && (
          <div className="palette-list" ref={listRef}>
            {filtered.length === 0 && <div className="palette-empty">{t("Ничего не найдено")}</div>}
            {filtered.map((item, index) => (
              <div key={item.id} className={"palette-row" + (index === selection ? " selected" : "")} onClick={() => choose(item.id)}>
                <div className="palette-text">
                  <div className="palette-title">{item.label}</div>
                  {item.subtitle && (
                    <div className="palette-subtitle">
                      <MiddleTruncated text={item.subtitle} />
                    </div>
                  )}
                </div>
                {item.status && item.status !== "idle" && <Glyph status={item.status} settings={state.settings} color={item.statusColor} shape={item.statusShape} />}
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
