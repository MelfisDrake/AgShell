import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { AskView, command } from "./bridge";
import { boundAction } from "./shortcuts";

/** The highlight agterm opens on: the default button, else the first that is not destructive. */
function initialHighlight(ask: AskView): number {
  const byDefault = ask.buttons.findIndex((button) => button.id === ask.defaultButton);
  if (byDefault >= 0) {
    return byDefault;
  }
  const safe = ask.buttons.findIndex((button) => button.id !== ask.destructiveButton);
  return safe >= 0 ? safe : 0;
}

/**
 * agterm's resolvedHotkey, as keymap chords resolve: the letter a Latin layout types, and on any other layout the letter
 * of the key's US place; nil for anything but a to z.
 */
function hotkeyOf(event: KeyboardEvent): string | null {
  const typed = event.key.length === 1 ? event.key.toLowerCase() : "";
  const key = /\p{Script=Latin}|[\x00-\x7f]/u.test(typed) ? typed : event.code.startsWith("Key") ? event.code.slice(3).toLowerCase() : "";
  return /^[a-z]$/.test(key) ? key : null;
}

/** agterm's buttonLabel: the hotkey's first match in the label underlined, else " (X)" after it; "! " before a destructive one. */
function buttonLabel(label: string, hotkey: string | null, bang: boolean): React.ReactNode {
  const at = hotkey ? label.toLowerCase().indexOf(hotkey.toLowerCase()) : -1;
  const body =
    !hotkey ? (
      label
    ) : at >= 0 ? (
      <>
        {label.slice(0, at)}
        <u>{label.slice(at, at + hotkey.length)}</u>
        {label.slice(at + hotkey.length)}
      </>
    ) : (
      <>
        {label} (<u>{hotkey.toUpperCase()}</u>)
      </>
    );
  // One inline run, so the button's flex box does not drop the space after "!".
  return <span>{bang ? <>{"! "}{body}</> : body}</span>;
}

/** agterm's maxTextBytes: a field holds at most 64 KB of UTF-8, cut at a character. */
const maxTextBytes = 65536;
const encoder = new TextEncoder();

function clampText(text: string): string {
  if (encoder.encode(text).length <= maxTextBytes) {
    return text;
  }
  let bytes = 0;
  let kept = "";
  for (const character of new Intl.Segmenter().segment(text)) {
    bytes += encoder.encode(character.segment).length;
    if (bytes > maxTextBytes) {
      break;
    }
    kept += character.segment;
  }
  return kept;
}

/** agterm's list markers: SF Symbols in the GUI style (accent when checked, secondary otherwise), text in the terminal one. */
function Marker({ gui, multiple, checked }: { gui: boolean; multiple: boolean; checked: boolean }) {
  if (!gui) {
    return <span className="ask-mark">{multiple ? (checked ? "[x]" : "[ ]") : checked ? "(•)" : "( )"}</span>;
  }
  return (
    <svg className={"ask-mark" + (checked ? " checked" : "")} viewBox="0 0 16 16" aria-hidden="true">
      {multiple && checked && (
        <>
          <rect x="1.5" y="1.5" width="13" height="13" rx="3" fill="currentColor" />
          <path d="M4.6 8.2l2.2 2.2 4.6-4.8" fill="none" stroke="var(--menu-bg)" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
        </>
      )}
      {multiple && !checked && <rect x="2" y="2" width="12" height="12" rx="2.5" fill="none" stroke="currentColor" strokeWidth="1.2" />}
      {!multiple && <circle cx="8" cy="8" r="6.2" fill="none" stroke="currentColor" strokeWidth="1.2" />}
      {!multiple && checked && <circle cx="8" cy="8" r="3.6" fill="currentColor" />}
    </svg>
  );
}

/** agterm's AskDraft: what the user has done in a question so far, kept outside the dialog so a session switch keeps it. */
interface AskDraft {
  text: string;
  selected: string[];
  /** The radio item the free-form row took over from, restored when its text goes blank. */
  previous: string[];
  cursor: number;
  other: string;
  otherOn: boolean;
}

const drafts = new Map<string, AskDraft>();

/** agterm's AskDraft(form:): the prefill, the checked ids (a radio list's first item when none is), the cursor on the first checked. */
function initialDraft(ask: AskView): AskDraft {
  const ids = (ask.choices ?? []).map((choice) => choice.id);
  const checked = ask.checked ?? [];
  const selected = ask.multiple ? ids.filter((id) => checked.includes(id)) : [checked[0] ?? ids[0]].filter((id): id is string => id !== undefined);
  return { text: clampText(ask.inputValue ?? ""), selected, previous: [], cursor: Math.max(0, ids.findIndex((id) => selected.includes(id))), other: "", otherOn: false };
}

/** Drops the drafts of questions no longer pending. */
export function forgetAskDrafts(pending: Set<string>) {
  for (const id of drafts.keys()) {
    if (!pending.has(id)) {
      drafts.delete(id);
    }
  }
}

/**
 * agterm's ask: a title, a message, buttons, and optionally a text field or a choice list. Terminal style is drawn in the
 * terminal's font and colors over its session; GUI style is a panel over the window. Both answer the same way: Return
 * presses the highlighted button, a hotkey presses its button, Esc (or the cancel button) escapes.
 */
export function AskDialog({ ask, active, focusRequest = 0 }: { ask: AskView; active: boolean; focusRequest?: number }) {
  const [highlight, setHighlight] = useState(() => initialHighlight(ask));
  const [draft, setDraft] = useState<AskDraft>(() => drafts.get(ask.id) ?? initialDraft(ask));
  const update = (change: (current: AskDraft) => Partial<AskDraft>) =>
    setDraft((current) => {
      const next = { ...current, ...change(current) };
      drafts.set(ask.id, next);
      return next;
    });
  const { text, selected, previous, cursor, other, otherOn } = draft;
  const setText = (value: string) => update(() => ({ text: value }));
  const setCursor = (value: number | ((current: number) => number)) =>
    update((current) => ({ cursor: typeof value === "function" ? value(current.cursor) : value }));
  const rootRef = useRef<HTMLDivElement>(null);
  const buttonsRef = useRef<HTMLDivElement>(null);
  // agterm's multiline editor: its laid-out height, soft wraps included, between five and fifteen lines.
  useLayoutEffect(() => {
    const field = fieldRef.current;
    if (ask.input !== "multiline" || !field) {
      return;
    }
    const style = getComputedStyle(field);
    const line = parseFloat(style.lineHeight) || parseFloat(style.fontSize) * 1.2;
    const padding = parseFloat(style.paddingTop) + parseFloat(style.paddingBottom);
    const border = parseFloat(style.borderTopWidth) + parseFloat(style.borderBottomWidth);
    field.style.height = "auto";
    const lines = Math.min(15, Math.max(5, Math.ceil((field.scrollHeight - padding) / line - 0.01)));
    field.style.height = `${lines * line + padding + border}px`;
  });
  // agterm's ViewThatFits over AskButtonLayout: the buttons share the widest one's width in a row, or stand in a column
  // when the row does not fit.
  useLayoutEffect(() => {
    const group = buttonsRef.current;
    const row = group?.parentElement;
    if (!group || !row) {
      return;
    }
    const fit = () => {
      group.classList.remove("column");
      if (group.scrollWidth > row.clientWidth + 0.5) {
        group.classList.add("column");
      }
    };
    fit();
    const observer = new ResizeObserver(fit);
    observer.observe(row);
    return () => observer.disconnect();
  });
  const fieldRef = useRef<HTMLInputElement & HTMLTextAreaElement>(null);
  const otherRef = useRef<HTMLInputElement>(null);
  const choices = ask.choices ?? [];
  const rows = choices.length + (ask.other !== null ? 1 : 0);
  const onOther = ask.other !== null && cursor === choices.length;

  useEffect(() => {
    if (!active) {
      return;
    }
    if (ask.input && fieldRef.current) {
      // The caret goes after the prefill, ready to continue it.
      const field = fieldRef.current;
      field.focus();
      field.setSelectionRange(field.value.length, field.value.length);
    } else if (onOther) {
      otherRef.current?.focus();
    } else {
      rootRef.current?.focus();
    }
  }, [active, ask.id, onOther, focusRequest]);

  const answer = (index: number) => {
    const button = ask.buttons[index];
    if (!button || button.id === ask.cancelButton) {
      command({ command: "askAnswer", id: ask.id });
      return;
    }
    if (ask.input) {
      command({ command: "askAnswer", id: ask.id, buttonId: button.id, text });
    } else if (ask.choices) {
      command({ command: "askAnswer", id: ask.id, buttonId: button.id, selected, text: otherOn ? other : undefined });
    } else {
      command({ command: "askAnswer", id: ask.id, buttonId: button.id });
    }
  };

  const toggle = (id: string) => {
    if (ask.multiple) {
      update((current) => ({ selected: current.selected.includes(id) ? current.selected.filter((item) => item !== id) : [...current.selected, id] }));
    } else {
      update(() => ({ selected: [id], otherOn: false, previous: [] }));
    }
  };

  const toggleOther = () => {
    if (other.trim().length === 0) {
      return;
    }
    if (ask.multiple) {
      update((current) => ({ otherOn: !current.otherOn }));
    } else if (!otherOn) {
      update((current) => ({ otherOn: true, previous: current.selected, selected: [] }));
    }
  };

  const typeOther = (value: string) => {
    const on = value.trim().length > 0;
    update(() => ({
      other: value,
      otherOn: on,
      ...(on && !otherOn && !ask.multiple ? { previous: selected, selected: [] } : {}),
      ...(!on && otherOn && !ask.multiple ? { selected: previous } : {}),
    }));
  };

  const onKeyDown = (event: React.KeyboardEvent) => {
    // agterm's key catcher takes only keys without Ctrl or Alt; the window's own chords and the menu get the rest.
    const closes = event.key === "Escape" || boundAction(event.nativeEvent, ["close_session"]) !== undefined;
    const chord = (event.ctrlKey || event.altKey) && !closes && !(event.key === "Enter" && ask.input === "multiline");
    if (!chord) {
      event.stopPropagation();
    }
    // agterm leaves Return and Esc to an input method while it composes.
    if (chord || event.nativeEvent.isComposing) {
      return;
    }
    const count = ask.buttons.length;
    const move = (step: number) => setHighlight((current) => (current + step + count) % count);
    if (closes) {
      event.preventDefault();
      command({ command: "askAnswer", id: ask.id });
      return;
    }
    if (event.key === "Tab") {
      event.preventDefault();
      move(event.shiftKey ? -1 : 1);
      return;
    }
    if (event.key === "Enter") {
      const multiline = ask.input === "multiline";
      if (!multiline || event.ctrlKey) {
        event.preventDefault();
        answer(highlight);
      }
      return;
    }
    // A text field, and the free-form row's line, keep their own keys.
    if (ask.input || event.target === otherRef.current) {
      return;
    }
    const list = ask.choices !== null;
    if (event.key === "ArrowLeft" || (!list && event.key === "ArrowUp")) {
      event.preventDefault();
      move(-1);
    } else if (event.key === "ArrowRight" || (!list && event.key === "ArrowDown")) {
      event.preventDefault();
      move(1);
    } else if (list && event.key === "ArrowDown") {
      event.preventDefault();
      setCursor((current) => Math.min(rows - 1, current + 1));
    } else if (list && event.key === "ArrowUp") {
      event.preventDefault();
      setCursor((current) => Math.max(0, current - 1));
    } else if (list && event.key === " ") {
      event.preventDefault();
      if (!onOther) {
        toggle(choices[cursor].id);
      }
    } else {
      const letter = hotkeyOf(event.nativeEvent);
      const index = letter ? ask.buttons.findIndex((button) => button.hotkey === letter) : -1;
      if (index >= 0) {
        event.preventDefault();
        answer(index);
      }
    }
  };

  // agterm's panelWidth: --width is that share of the anchor, held to 10..100; without it the panel's own width, a field
  // taking all it may have.
  const style = ask.width ? { width: `${Math.min(100, Math.max(10, ask.width))}%`, maxWidth: "none" } : undefined;
  const mark = (checked: boolean) => <Marker gui={ask.style === "gui"} multiple={ask.multiple} checked={checked} />;
  return (
    <div className={"ask " + ask.style + (ask.input ? " input" : "")} style={style} ref={rootRef} tabIndex={-1} onKeyDown={onKeyDown} onMouseDown={(event) => event.stopPropagation()}>
      <div className="ask-title">{ask.title}</div>
      {ask.message && <div className="ask-message">{ask.message}</div>}
      {ask.input === "line" && (
        <input className="ask-field" ref={fieldRef} spellCheck={false} value={text} placeholder={ask.placeholder ?? undefined} onChange={(event) => setText(clampText(event.target.value))} />
      )}
      {ask.input === "multiline" && (
        <textarea
          className="ask-field"
          ref={fieldRef}
          spellCheck={false}
          rows={5}
          value={text}
          placeholder={ask.placeholder ?? undefined}
          onChange={(event) => setText(clampText(event.target.value))}
        />
      )}
      {ask.choices && (
        <div className="ask-choices">
          {choices.map((choice, index) => (
            <div
              key={choice.id}
              className={"ask-choice" + (index === cursor ? " cursor" : "")}
              onClick={() => {
                setCursor(index);
                toggle(choice.id);
              }}
            >
              <span className="ask-choice-head">
                {mark(selected.includes(choice.id))}
                <span className="ask-choice-text">{choice.label}</span>
              </span>
              {choice.subtitle && (
                <span className="ask-choice-subtitle">
                  <span className="ask-mark-spacer" aria-hidden="true">{mark(false)}</span>
                  <span>{choice.subtitle}</span>
                </span>
              )}
            </div>
          ))}
          {ask.other !== null && (
            <div className={"ask-choice other" + (onOther ? " cursor" : "")} onClick={() => setCursor(choices.length)}>
              {/* agterm: the free-form row's marker is dimmed until its text is not blank, then toggles like any item. */}
              <span
                className={"ask-other-mark" + (other.trim().length > 0 ? "" : " disabled")}
                onClick={(event) => {
                  event.stopPropagation();
                  setCursor(choices.length);
                  toggleOther();
                }}
              >
                {mark(otherOn)}
              </span>
              <input
                className="ask-other"
                ref={otherRef}
                spellCheck={false}
                value={other}
                placeholder={ask.other}
                onChange={(event) => typeOther(clampText(event.target.value))}
                onKeyDown={(event) => {
                  if (event.key === "ArrowUp") {
                    event.preventDefault();
                    event.stopPropagation();
                    setCursor(choices.length - 1);
                    rootRef.current?.focus();
                  } else if (event.key === "ArrowDown" || event.key === " ") {
                    event.stopPropagation();
                  }
                }}
              />
            </div>
          )}
        </div>
      )}
      <div className={"ask-buttons " + ask.align}>
        <div className="ask-button-group" ref={buttonsRef}>
          {ask.buttons.map((button, index) => (
            <button
              key={button.id}
              type="button"
              className={"ask-button" + (index === highlight ? " active" : "") + (button.id === ask.destructiveButton ? " destructive" : "")}
              onClick={() => answer(index)}
            >
              {buttonLabel(button.label, button.hotkey, ask.style === "terminal" && button.id === ask.destructiveButton)}
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}
