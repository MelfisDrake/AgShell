import { useEffect, useRef, useState } from "react";
import { ClipboardPromptView, command } from "./bridge";
import { t } from "./i18n";

/**
 * agterm's ClipboardPromptController sheet: Allow is the default button, and the box remembers the choice for this
 * direction until the app quits.
 */
export function ClipboardPrompt({ prompt }: { prompt: ClipboardPromptView }) {
  const [remember, setRemember] = useState(false);
  const allowRef = useRef<HTMLButtonElement>(null);
  const dialogRef = useRef<HTMLDivElement>(null);
  const read = prompt.access === "read";

  useEffect(() => {
    allowRef.current?.focus();
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.isComposing) {
        return;
      }
      if (event.key === "Tab") {
        const controls = Array.from(dialogRef.current?.querySelectorAll<HTMLElement>("button, input") ?? []);
        if (controls.length === 0) {
          return;
        }
        event.preventDefault();
        event.stopImmediatePropagation();
        const current = controls.indexOf(document.activeElement as HTMLElement);
        const next = current < 0 ? (event.shiftKey ? controls.length - 1 : 0)
          : (current + (event.shiftKey ? -1 : 1) + controls.length) % controls.length;
        controls[next].focus();
        return;
      }
      if (event.key !== "Escape") {
        return;
      }
      event.preventDefault();
      event.stopImmediatePropagation();
      command({ command: "clipboardPrompt", id: prompt.id, allow: false, remember: false });
    };
    window.addEventListener("keydown", onKeyDown, true);
    return () => window.removeEventListener("keydown", onKeyDown, true);
  }, [prompt.id]);

  const answer = (allow: boolean) => command({ command: "clipboardPrompt", id: prompt.id, allow, remember });

  return (
    <div className="ask-backdrop centered">
      <div className="ask gui clipboard-prompt" role="alertdialog" aria-modal="true" ref={dialogRef}>
        <div className="ask-title">{read ? t("Разрешить программе прочитать буфер обмена?") : t("Разрешить программе записать в буфер обмена?")}</div>
        <div className="ask-message">
          {read
            ? t("Программа в терминале пытается прочитать содержимое буфера обмена, а там могут быть пароли и другие скопированные данные.")
            : t("Программа в терминале пытается заменить содержимое буфера обмена.")}
        </div>
        <label className="clipboard-prompt-remember">
          <input type="checkbox" checked={remember} onChange={(event) => setRemember(event.target.checked)} />
          {t("Не спрашивать до конца сеанса")}
        </label>
        <div className="ask-buttons">
          <button type="button" className="ask-button" onClick={() => answer(false)}>
            {t("Запретить")}
          </button>
          <button type="button" className="ask-button active" ref={allowRef} onClick={() => answer(true)}>
            {t("Разрешить")}
          </button>
        </div>
      </div>
    </div>
  );
}
