import { ReactNode, useEffect, useRef, useState } from "react";
import { AppState, command, InputSource, SettingsView, StatusShape } from "./bridge";
import { defaultStatusColors } from "./appearance";
import { useStore } from "./store";
import { t, tr } from "./i18n";
import { Glyph } from "./StatusGlyph";
import { handleAppShortcut } from "./shortcuts";

type Tab = "general" | "appearance" | "interface" | "notifications" | "status" | "keys";

const tabs: { id: Tab; title: string }[] = [
  { id: "general", title: tr("Общие") },
  { id: "appearance", title: tr("Оформление") },
  { id: "interface", title: tr("Интерфейс") },
  { id: "notifications", title: tr("Уведомления") },
  { id: "status", title: tr("Статус агентов") },
  { id: "keys", title: tr("Клавиши") },
];

function set(patch: Partial<Record<keyof SettingsView, unknown>>): void {
  command({ command: "updateSettings", patch });
}

function Row({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <div className="settings-row">
      <div className="settings-label">
        {label}
        {hint && <div className="settings-hint">{hint}</div>}
      </div>
      <div className="settings-control">{children}</div>
    </div>
  );
}

function Select<T extends string>({ value, options, onChange }: { value: T; options: [T, string][]; onChange: (value: T) => void }) {
  return (
    <select className="settings-select" value={value} onChange={(event) => onChange(event.target.value as T)}>
      {options.map(([id, title]) => (
        <option key={id} value={id}>
          {t(title)}
        </option>
      ))}
    </select>
  );
}

function Toggle({ value, onChange }: { value: boolean; onChange: (value: boolean) => void }) {
  return <input type="checkbox" className="settings-toggle" checked={value} onChange={(event) => onChange(event.target.checked)} />;
}

function Stepper({ value, min, max, onChange }: { value: number; min: number; max: number; onChange: (value: number) => void }) {
  return (
    <span className="settings-stepper">
      <button type="button" className="icon-button narrow" disabled={value <= min} onClick={() => onChange(value - 1)}>
        −
      </button>
      <span className="settings-value">{value}</span>
      <button type="button" className="icon-button narrow" disabled={value >= max} onClick={() => onChange(value + 1)}>
        +
      </button>
    </span>
  );
}

function Slider({ value, min = 0, max = 10, suffix = "", label, onChange }: { value: number; min?: number; max?: number; suffix?: string; label?: string; onChange: (value: number) => void }) {
  return (
    <span className="settings-slider">
      <input type="range" min={min} max={max} step={1} value={value} onChange={(event) => onChange(Number(event.target.value))} />
      <span className="settings-value">
        {label ?? value}
        {label === undefined && suffix}
      </span>
    </span>
  );
}

/** agterm's sidebarShiftLabel: "None" at the neutral 5, else the direction and how far. */
function sidebarShiftLabel(shift: number): string {
  const offset = shift - 5;
  if (offset === 0) {
    return t("Нет");
  }

  return offset < 0 ? t("Светлее {n}", { n: -offset }) : t("Темнее {n}", { n: offset });
}

function General({ settings }: { settings: SettingsView }) {
  return (
    <>
      <Row label={t("Язык")}>
        <Select
          value={settings.language}
          options={[
            ["system", t("Как в Windows")],
            ["ru", t("Русский")],
            ["en", "English"],
          ]}
          onChange={(value) => set({ language: value === "system" ? null : value })}
        />
      </Row>
      <h3>{t("Мышь")}</h3>
      <Row label={t("Скорость прокрутки")}>
        <Slider value={settings.mouseScrollMultiplier} min={1} max={10} suffix="x" onChange={(value) => set({ mouseScrollMultiplier: value === 3 ? null : value })} />
      </Row>
      <Row label={t("Правый клик вставляет")}>
        <Toggle value={settings.rightClickPaste} onChange={(value) => set({ rightClickPaste: value })} />
      </Row>
      <Row label={t("Открывать ссылки")}>
        <Select
          value={settings.linkOpenMode}
          options={[
            ["browser", t("в браузере")],
            ["overlay", t("поверх сессии")],
          ]}
          onChange={(value) => set({ linkOpenMode: value })}
        />
      </Row>
      <Row label={t("Клик по строке workspace разворачивает и сворачивает его")}>
        <Toggle value={settings.workspaceRowClickExpands} onChange={(value) => set({ workspaceRowClickExpands: value ? null : false })} />
      </Row>
      <h3>{t("Сессии")}</h3>
      <Row label={t("Новые сессии открываются")} hint={settings.newSessionDirectory === "custom" ? (settings.newSessionCustomDirectory ?? t("не задан")) : undefined}>
        <Select
          value={settings.newSessionDirectory}
          options={[
            ["home", t("в домашнем каталоге")],
            ["currentSession", t("в каталоге текущей сессии")],
            ["custom", t("в своём каталоге")],
          ]}
          onChange={(value) => set({ newSessionDirectory: value })}
        />
        {settings.newSessionDirectory === "custom" && (
          <button type="button" className="text-button" onClick={() => command({ command: "chooseDirectory" })}>
            {t("Выбрать…")}
          </button>
        )}
      </Row>
      <Row label={t("Новые сессии добавляются")}>
        <Select
          value={settings.newSessionPlacement}
          options={[
            ["end", t("в конец workspace")],
            ["afterCurrent", t("после текущей сессии")],
          ]}
          onChange={(value) => set({ newSessionPlacement: value })}
        />
      </Row>
      <Row
        label={t("Восстанавливать сессии")}
        hint={[t("Изменения применятся после перезапуска AgShell."), settings.restoreUnavailableReason].filter(Boolean).join(" ")}
      >
        <Select
          value={settings.restoreMode}
          options={[
            ["none", t("свежие шеллы")],
            ["rerun", t("перезапуск команд")],
            ["live", t("живые сессии")],
          ]}
          onChange={(value) => set({ restoreMode: value })}
        />
        {settings.restoreMode === "rerun" && (
          <button type="button" className="text-button" onClick={() => command({ command: "editConfig", file: "denylist" })}>
            {t("Исключения…")}
          </button>
        )}
      </Row>
      <Row label={t("Подтверждать закрытие сессии")}>
        <Toggle value={settings.confirmCloseSession} onChange={(value) => set({ confirmCloseSession: value })} />
      </Row>
      <Row label={t("Разрешить отмену закрытия сессий и workspace")}>
        <Toggle value={settings.closeGraceUndoEnabled} onChange={(value) => set({ closeGraceUndoEnabled: value })} />
      </Row>
      <Row label={t("Раскладка помеченных")}>
        <Select
          value={settings.flaggedViewLayout}
          options={[
            ["flat", t("плоский список")],
            ["plain", t("плоский список, только имена")],
            ["tree", t("дерево workspace")],
          ]}
          onChange={(value) => set({ flaggedViewLayout: value === "flat" ? null : value })}
        />
      </Row>
      <Row label={t("Программы могут записывать в буфер обмена")} hint={t("OSC 52 и протокол буфера обмена Kitty, в том числе agshellctl clipboard set с другой машины.")}>
        <Toggle value={settings.clipboardWrite} onChange={(value) => set({ clipboardWrite: value })} />
      </Row>
    </>
  );
}

/** agterm's light side for following Windows from no theme. */
const builtinLight = "Builtin Light";

function Appearance({ settings, themes, fonts }: { settings: SettingsView; themes: string[]; fonts: string[] }) {
  // agterm's "default ghostty" is no theme, offered only while not following Windows: its pair needs two named themes.
  const themeOptions = (current: string | null, withDefault: boolean): [string, string][] => {
    const names = current && !themes.includes(current) ? [current, ...themes] : themes;
    return [...(withDefault ? [["", "default ghostty"] as [string, string]] : []), ...names.map((name): [string, string] => [name, name])];
  };
  // agterm's theme pickers: the first is the theme of the mode Windows is in now, the second, while following, the other's.
  const following = settings.followSystemAppearance;
  const dark = settings.systemDark;
  const current = following && dark ? (settings.darkTheme ?? settings.theme) : settings.theme;
  const other = dark ? settings.theme : (settings.darkTheme ?? settings.theme);
  const setCurrent = (value: string) => set(following && dark ? { darkTheme: value } : { theme: value || null });
  const setOther = (value: string) => set(dark ? { theme: value } : { darkTheme: value });
  const translucent = settings.backgroundOpacity < 1;
  return (
    <>
      <h3>{t("Терминал")}</h3>
      <Row label={t("Шрифт")}>
        <Select
          value={settings.fontFamily ?? ""}
          options={[["", t("по умолчанию")], ...fonts.filter((font) => font !== "Cascadia Mono").map((font): [string, string] => [font, font])]}
          onChange={(value) => set({ fontFamily: value || null })}
        />
      </Row>
      <Row label={t("Размер шрифта")}>
        <Stepper value={settings.fontSize} min={8} max={32} onChange={(value) => set({ fontSize: value })} />
      </Row>
      <Row label={t("Форма курсора")}>
        <Select
          value={settings.cursorStyleSetting ?? ""}
          options={[
            ["", t("по умолчанию")],
            ["block", t("блок")],
            ["bar", t("черта")],
            ["underline", t("подчёркивание")],
          ]}
          onChange={(value) => set({ cursorStyle: value || null })}
        />
      </Row>
      <Row label={t("Мигание курсора")}>
        <Select
          value={settings.cursorBlinkSetting === null ? "" : settings.cursorBlinkSetting ? "blink" : "steady"}
          options={[
            ["", t("по умолчанию")],
            ["blink", t("мигает")],
            ["steady", t("не мигает")],
          ]}
          onChange={(value) => set({ cursorBlink: value === "" ? null : value === "blink" })}
        />
      </Row>
      <Row label={t("Тема")}>
        <Select value={current ?? ""} options={themeOptions(current, !following)} onChange={setCurrent} />
      </Row>
      <Row label={t("Следовать теме системы")}>
        <Toggle
          value={following}
          onChange={(value) =>
            // As in agterm, no theme cannot be a side of the pair, so following starts from agterm's light theme; turning it
            // off collapses the pair to the theme on screen.
            set(value ? { followSystemAppearance: true, theme: settings.theme ?? builtinLight, darkTheme: settings.darkTheme ?? settings.theme ?? builtinLight } : { followSystemAppearance: null, theme: dark ? (settings.darkTheme ?? settings.theme) : settings.theme, darkTheme: null })
          }
        />
      </Row>
      {following && (
        <Row
          label={dark ? t("Светлая тема") : t("Тёмная тема")}
          hint={dark ? t("Используется, когда в Windows светлый режим.") : t("Используется, когда в Windows тёмный режим.")}
        >
          <Select value={other ?? ""} options={themeOptions(other, false)} onChange={setOther} />
        </Row>
      )}
      <h3>{t("Окно")}</h3>
      <Row label={t("Панель инструментов")}>
        <Select
          value={settings.toolbarMode}
          options={[
            ["normal", t("обычная")],
            ["compact", t("компактная")],
            ["hidden", t("скрыта")],
          ]}
          onChange={(value) => set({ toolbarMode: value === "compact" ? null : value })}
        />
      </Row>
      <Row
        label={t("Непрозрачность фона")}
        hint={settings.backdropSupported ? undefined : t("Нужна Windows 11 22H2 или новее.")}
      >
        <span className="settings-slider">
          <input
            type="range"
            min={0}
            max={100}
            step={1}
            disabled={!settings.backdropSupported}
            value={Math.round(settings.backgroundOpacity * 100)}
            onChange={(event) => command({ command: "previewSettings", patch: { backgroundOpacity: Number(event.target.value) / 100 } })}
            onPointerUp={() => command({ command: "commitSettings" })}
          />
          <span className="settings-value">{Math.round(settings.backgroundOpacity * 100)}%</span>
        </span>
      </Row>
      <Row
        label={t("Размытие фона")}
        hint={settings.transparencyOff ? t("В настройках Windows выключены эффекты прозрачности; сохранённые непрозрачность и размытие применятся, когда их включат.") : t("Размытие работает при непрозрачности меньше 100%.")}
      >
        <input
          type="checkbox"
          className="settings-toggle"
          checked={settings.backgroundBlur}
          disabled={!translucent || !settings.backdropSupported}
          onChange={(event) => set({ backgroundBlur: event.target.checked })}
        />
      </Row>
      <Row label={t("Оттенок боковой панели")}>
        <Slider value={settings.sidebarBackgroundShift} label={sidebarShiftLabel(settings.sidebarBackgroundShift)} onChange={(value) => set({ sidebarBackgroundShift: value })} />
      </Row>
      <Row label={t("Размер шрифта боковой панели")}>
        <Stepper value={settings.sidebarFontSize} min={9} max={20} onChange={(value) => set({ sidebarFontSize: value })} />
      </Row>
      <Row label={t("Размер шрифта палитр и переключателя")}>
        <Stepper value={settings.interfaceFontSize} min={9} max={20} onChange={(value) => set({ interfaceFontSize: value })} />
      </Row>
      <Row label={t("Приглушение неактивной панели и фона")}>
        <Slider value={settings.inactivePaneMuteStrength} onChange={(value) => set({ inactivePaneMuteStrength: value })} />
      </Row>
    </>
  );
}

const titlebarElements: [string, string][] = [
  ["sidebarToggle", tr("Переключатель боковой панели")],
  ["workspaceName", tr("Имя workspace")],
  ["sessionName", tr("Имя сессии")],
  ["windowName", tr("Имя окна")],
  ["remoteHost", tr("Удалённый хост")],
  ["sessionContext", tr("Контекст сессии")],
  ["recentSessions", tr("Недавние сессии")],
  ["scratch", tr("Scratch-терминал")],
  ["split", tr("Сплит-вид")],
  ["overlayToggle", tr("Скрыть overlay")],
  ["dashboard", tr("Дашборд")],
  ["quickTerminal", "Quick terminal"],
  ["customCommands", tr("Пользовательские команды")],
];

const interfaceElements: [string, string][] = [
  ["newWorkspace", tr("Новый workspace")],
  ["newSession", tr("Новая сессия")],
  ["flaggedView", tr("Помеченные")],
  ["focusFilter", tr("Фильтр workspace")],
  ["workspaceAddSession", tr("Добавление сессии в workspace")],
];

/** Turns an element on or off: one hidden by default is opted into through the shown list, any other leaves the hidden one. */
function setElement(settings: SettingsView, id: string, on: boolean): void {
  const defaults = settings.hiddenByDefaultElements;
  const shown = settings.shownInterfaceElements;
  const hidden = settings.hiddenInterfaceElements.filter((item) => !(defaults.includes(item) && !shown.includes(item)));
  if (defaults.includes(id)) {
    set({ shownInterfaceElements: on ? [...shown, id] : shown.filter((item) => item !== id), hiddenInterfaceElements: hidden.filter((item) => item !== id) });
  } else {
    set({ hiddenInterfaceElements: on ? hidden.filter((item) => item !== id) : [...hidden, id] });
  }
}

/** agterm's two-column section: the element toggles two to a row. */
function ElementGrid({ settings, elements }: { settings: SettingsView; elements: [string, string][] }) {
  const hidden = settings.hiddenInterfaceElements;
  return (
    <div className="settings-grid">
      {elements.map(([id, title]) => (
        <label key={id} className="settings-grid-item">
          <span>{t(title)}</span>
          <Toggle value={!hidden.includes(id)} onChange={(value) => setElement(settings, id, value)} />
        </label>
      ))}
    </div>
  );
}

function Interface({ settings }: { settings: SettingsView }) {
  return (
    <>
      <h3>{t("Строка заголовка")}</h3>
      <ElementGrid settings={settings} elements={titlebarElements} />
      <h3>{t("Боковая панель")}</h3>
      <ElementGrid settings={settings} elements={interfaceElements} />
      <Row label={t("Боковая панель только в активном окне")}>
        <Toggle value={settings.autoHideSidebarInactiveWindows} onChange={(value) => set({ autoHideSidebarInactiveWindows: value ? true : null })} />
      </Row>
      <h3>Quick Terminal</h3>
      <Row label={t("Размер")}>
        <Select
          value={settings.quickTerminalSizeSetting === null ? "" : String(settings.quickTerminalSizeSetting)}
          options={[["", t("по умолчанию")], ...[40, 50, 60, 70, 80, 90].map((size): [string, string] => [String(size), t("{n}% экрана", { n: size })])]}
          onChange={(value) => set({ quickTerminalSizePercent: value === "" ? null : Number(value) })}
        />
      </Row>
    </>
  );
}

function Notifications({ settings }: { settings: SettingsView }) {
  return (
    <>
      <h3>{t("Уведомления")}</h3>
      <Row label={t("Показывать баннеры уведомлений")}>
        <Toggle value={settings.notificationsEnabled} onChange={(value) => set({ notificationsEnabled: value })} />
      </Row>
      <Row label={t("Показывать счётчики уведомлений")}>
        <Toggle value={settings.notificationBadgeEnabled} onChange={(value) => set({ notificationBadgeEnabled: value })} />
      </Row>
      <Row label={t("Мигать кнопкой на панели задач")}>
        <Select
          value={settings.taskbarFlash}
          options={[
            ["off", t("нет")],
            ["once", t("один раз")],
            ["untilFocused", t("пока окно не в фокусе")],
          ]}
          onChange={(value) => set({ taskbarFlash: value })}
        />
      </Row>
      <Row label={t("Звук уведомления")}>
        <Select
          value={settings.notificationSoundName}
          options={[
            ["none", t("без звука")],
            ["default", t("стандартный")],
            ["im", t("сообщение")],
            ["mail", t("почта")],
            ["reminder", t("напоминание")],
            ["sms", "SMS"],
          ]}
          onChange={(value) => {
            set({ notificationSoundName: value });
            // agterm plays a sound as it is chosen.
            command({ command: "previewSound", kind: "notification", name: value });
          }}
        />
      </Row>
      <Row label={t("Показывать индикатор внимания")}>
        <Toggle value={settings.attentionButtonEnabled} onChange={(value) => set({ attentionButtonEnabled: value ? true : null })} />
      </Row>
    </>
  );
}

function StatusColor({ value, fallback, onChange }: { value: string | null; fallback: string; onChange: (value: string | null) => void }) {
  const [draft, setDraft] = useState(value ?? fallback);
  useEffect(() => setDraft(value ?? fallback), [value, fallback]);
  return <input type="color" className="settings-color" value={draft} onChange={(event) => setDraft(event.target.value)} onBlur={() => draft !== (value ?? fallback) && onChange(draft)} />;
}

const shapes: StatusShape[] = ["circle", "square", "triangle", "diamond", "capsule", "star"];

const shapeNames: Record<StatusShape, string> = {
  circle: tr("круг"),
  square: tr("квадрат"),
  triangle: tr("треугольник"),
  diamond: tr("ромб"),
  capsule: tr("капсула"),
  star: tr("звезда"),
};

/** agterm's shape picker: the glyphs themselves, drawn in the status's color, in a menu under the current one. */
function ShapePicker({ status, value, color, settings, onChange }: { status: "active" | "blocked" | "completed"; value: StatusShape; color: string; settings: SettingsView; onChange: (shape: StatusShape) => void }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLSpanElement>(null);
  useEffect(() => {
    if (!open) {
      return;
    }
    const close = (event: PointerEvent) => {
      if (!ref.current?.contains(event.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener("pointerdown", close, true);
    return () => document.removeEventListener("pointerdown", close, true);
  }, [open]);
  return (
    <span ref={ref} className="shape-picker">
      <button type="button" className="shape-picker-button" title={t(shapeNames[value])} onClick={() => setOpen(!open)}>
        <Glyph status={status} settings={settings} shape={value} color={color} />
        <span className="shape-picker-chevron">▾</span>
      </button>
      {open && (
        <span className="shape-picker-menu">
          {shapes.map((shape) => (
            <button
              key={shape}
              type="button"
              className={"shape-picker-item" + (shape === value ? " selected" : "")}
              title={t(shapeNames[shape])}
              onClick={() => {
                setOpen(false);
                onChange(shape);
              }}
            >
              <Glyph status={status} settings={settings} shape={shape} color={color} />
            </button>
          ))}
        </span>
      )}
    </span>
  );
}

/** One status: agterm's row with the color well and the shape picker side by side. */
function GlyphRow({ settings, status, label }: { settings: SettingsView; status: "active" | "blocked" | "completed"; label: string }) {
  const colorKey = `${status}StatusColorHex` as const;
  const shapeKey = `${status}StatusShape` as const;
  const { state } = useStore();
  const fallback = defaultStatusColors(state?.theme.dark ?? true)[status];
  const color = settings[colorKey] ?? fallback;
  return (
    <Row label={label}>
      <StatusColor value={settings[colorKey]} fallback={fallback} onChange={(value) => set({ [colorKey]: value })} />
      <ShapePicker status={status} value={settings[shapeKey]} color={color} settings={settings} onChange={(shape) => set({ [shapeKey]: shape === "circle" ? null : shape })} />
    </Row>
  );
}

function AgentStatus({ settings, sounds }: { settings: SettingsView; sounds: string[] }) {
  const autoFollowOn = settings.autoFollowAttention !== "off";
  return (
    <>
      <h3>{t("Цвета и формы")}</h3>
      <GlyphRow settings={settings} status="active" label="Active" />
      <GlyphRow settings={settings} status="blocked" label="Blocked" />
      <GlyphRow settings={settings} status="completed" label="Completed" />
      <h3>{t("Звук")}</h3>
      <Row label={t("Звук blocked")}>
        <Select
          value={settings.blockedStatusSoundName ?? ""}
          options={[["", t("нет")], ...sounds.map((sound): [string, string] => [sound, sound])]}
          onChange={(value) => {
            set({ blockedStatusSoundName: value || null });
            if (value) {
              command({ command: "previewSound", kind: "status", name: value });
            }
          }}
        />
      </Row>
      <h3>{t("Ввод")}</h3>
      <Row label={t("Сброс статуса")}>
        <Select
          value={settings.statusReset}
          options={[
            ["firstKey", t("от первой клавиши")],
            ["enter", t("от Enter")],
            ["never", t("выключен")],
          ]}
          onChange={(value) => set({ statusReset: value })}
        />
      </Row>
      <h3>{t("Автопереход")}</h3>
      <Row label={t("Переходить к сессиям blocked")}>
        <Select
          value={settings.autoFollowAttention}
          options={[
            ["off", t("выключен")],
            ["s5", t("после 5 с простоя")],
            ["s10", t("после 10 с простоя")],
            ["s30", t("после 30 с простоя")],
            ["s60", t("после 60 с простоя")],
            ["m5", t("после 5 мин простоя")],
          ]}
          onChange={(value) => set({ autoFollowAttention: value })}
        />
      </Row>
      <Row label={t("Уходить с работающей сессии")} hint={t("Действует только при включённом автопереходе.")}>
        <input
          type="checkbox"
          className="settings-toggle"
          checked={!settings.autoFollowStayOnActive}
          disabled={!autoFollowOn}
          onChange={(event) => set({ autoFollowStayOnActive: event.target.checked ? null : true })}
        />
      </Row>
      <div className="settings-actions">
        <button
          type="button"
          className="text-button"
          onClick={() =>
            set({
              activeStatusColorHex: null,
              blockedStatusColorHex: null,
              completedStatusColorHex: null,
              activeStatusShape: null,
              blockedStatusShape: null,
              completedStatusShape: null,
              statusReset: null,
              blockedStatusSoundName: null,
            })
          }
        >
          {t("Сбросить по умолчанию")}
        </button>
      </div>
    </>
  );
}

function Diagnostics({ lines }: { lines: string[] }) {
  return lines.length === 0 ? <div className="settings-hint">{t("Проблем нет.")}</div> : <pre className="settings-diagnostics">{lines.join("\n")}</pre>;
}

function Keys({ settings, inputSources }: { settings: SettingsView; inputSources: InputSource[] }) {
  // Adding a layout is a trip to Windows settings and back, so coming back to the window reloads the list.
  useEffect(() => {
    const reload = () => command({ command: "requestLists" });
    window.addEventListener("focus", reload);
    return () => window.removeEventListener("focus", reload);
  }, []);
  // A saved layout Windows no longer lists still shows, so the stored choice stays visible.
  const saved = settings.terminalInputSource;
  const sources = saved !== null && !inputSources.some((source) => source.id === saved) ? [...inputSources, { id: saved, name: saved + " " + t("(недоступна)") }] : inputSources;
  return (
    <>
      <h3>{t("Раскладка клавиатуры")}</h3>
      <Row label={t("Держать в терминалах")}>
        <select className="settings-select" value={saved ?? ""} onChange={(event) => set({ terminalInputSource: event.target.value === "" ? null : event.target.value })}>
          <option value="">{t("Выключено")}</option>
          {sources.map((source) => (
            <option key={source.id} value={source.id}>
              {source.name}
            </option>
          ))}
        </select>
      </Row>
      <h3>{t("Папка конфигурации")}</h3>
      <div className="settings-actions">
        <span className="settings-path">{settings.configFolder}</span>
        <button type="button" className="text-button" onClick={() => command({ command: "chooseConfigDirectory" })}>
          {t("Выбрать…")}
        </button>
        {settings.configDirectory && (
          <button type="button" className="text-button" onClick={() => set({ configDirectory: null })}>
            {t("Вернуть по умолчанию")}
          </button>
        )}
      </div>
      <div className="settings-hint">{t("Папка с keymap.conf и hooks.conf. При смене оба файла перечитываются.")}</div>
      <Row label={t("Свои темы")} hint={t("Файлы в формате Ghostty; тема с тем же именем заменяет встроенную.")}>
        <span className="settings-path">{settings.themesPath}</span>
      </Row>
      <h3>{t("Диагностика")}</h3>
      <Diagnostics lines={settings.keymapDiagnostics} />
      <div className="settings-actions">
        <button type="button" className="text-button" onClick={() => command({ command: "reloadConfig", file: "keymap" })}>
          {t("Перечитать")}
        </button>
      </div>
      <h3>{t("Хуки")}</h3>
      <Diagnostics lines={settings.hookDiagnostics} />
      <div className="settings-actions">
        <button type="button" className="text-button" onClick={() => command({ command: "reloadConfig", file: "hooks" })}>
          {t("Перечитать")}
        </button>
      </div>
    </>
  );
}

/** The settings window's page: agterm's Settings window, one for the app, its look taken from the window in front. */
export function SettingsApp() {
  const { state } = useStore();
  return state ? <Settings state={state} /> : <div className="app loading" />;
}

/**
 * Settings as agterm's Settings window lays them out: the tabs along the top and the tab's rows below, opening on General
 * each time. Ctrl+W closes the window, as agterm's closes with Cmd+W.
 */
function Settings({ state }: { state: AppState }) {
  const { lists } = useStore();
  const [tab, setTab] = useState<Tab>("general");
  useEffect(() => {
    command({ command: "requestLists" });
    const onKey = (event: KeyboardEvent) => {
      if (handleAppShortcut(event)) {
        event.preventDefault();
        event.stopPropagation();
      }
    };
    window.addEventListener("keydown", onKey, true);
    return () => window.removeEventListener("keydown", onKey, true);
  }, []);

  const settings = state.settings;
  return (
    <div className="settings">
      <nav className="settings-tabs">
        {tabs.map((item) => (
          <button key={item.id} type="button" className={"settings-tab" + (item.id === tab ? " selected" : "")} onClick={() => setTab(item.id)}>
            {t(item.title)}
          </button>
        ))}
      </nav>
      <div className="settings-body">
        {tab === "general" && <General settings={settings} />}
        {tab === "appearance" && <Appearance settings={settings} themes={lists?.themes ?? []} fonts={lists?.fonts ?? []} />}
        {tab === "notifications" && <Notifications settings={settings} />}
        {tab === "interface" && <Interface settings={settings} />}
        {tab === "status" && <AgentStatus settings={settings} sounds={lists?.sounds ?? []} />}
        {tab === "keys" && <Keys settings={settings} inputSources={lists?.inputSources ?? []} />}
      </div>
    </div>
  );
}
