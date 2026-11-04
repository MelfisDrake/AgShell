import { useState } from "react";
import { command } from "./bridge";
import { closeWelcome } from "./store";
import { t } from "./i18n";

/**
 * agterm's first-launch welcome: the two extras that need an install, both checked, installed on «Установить»; with both
 * unchecked it only closes, as «Позже». They stay in the action palette for later.
 */
export function Welcome() {
  const [skill, setSkill] = useState(true);
  const [hooks, setHooks] = useState(true);

  const install = () => {
    closeWelcome();
    if (skill) {
      command({ command: "installAgent", what: "skill" });
    }
    if (hooks) {
      command({ command: "installAgent", what: "hooks" });
    }
  };

  return (
    <div className="ask-backdrop centered">
      <div
        className="ask gui welcome"
        tabIndex={-1}
        onKeyDown={(event) => {
          event.stopPropagation();
          if (event.key === "Escape") {
            closeWelcome();
          }
        }}
      >
        <div className="ask-title">{t("Добро пожаловать в AgShell")}</div>
        <div className="ask-message">
          {t("У AgShell есть необязательные дополнения. Два из них можно поставить отсюда, все — в любой момент из палитры действий (Ctrl+Shift+P).")}
          {"\n\n"}{t("Скилл учит Claude Code и Codex управлять AgShell через agshellctl.")}
          {"\n\n"}{t("Хуки статуса показывают в боковой панели, что агент работает, ждёт ответа или закончил.")}
          {"\n\n"}{t("Инструмент командной строки добавляет agshellctl в PATH. Установщик уже сделал это, так что он нужен только для zip-версии.")}
        </div>
        <label className="welcome-option">
          <input type="checkbox" checked={skill} onChange={(event) => setSkill(event.target.checked)} />
          {t("Установить скилл")}
        </label>
        <label className="welcome-option">
          <input type="checkbox" checked={hooks} onChange={(event) => setHooks(event.target.checked)} />
          {t("Установить хуки статуса")}
        </label>
        <div className="ask-buttons right">
          <button type="button" className="ask-button" onClick={closeWelcome}>
            {t("Позже")}
          </button>
          <button type="button" className="ask-button active" autoFocus onClick={install}>
            {t("Установить")}
          </button>
        </div>
      </div>
    </div>
  );
}
