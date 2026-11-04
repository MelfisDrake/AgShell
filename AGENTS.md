# AGENTS.md

## С чего начать

- Что делаем сейчас: [docs/roadmap.md](docs/roadmap.md).
- Почему стек именно такой: [docs/decisions/](docs/decisions/). Не меняй принятое решение молча: если нужно отступить, предложи новый ADR, который заменяет старый.
- Образец поведения и модели: [umputun/agterm](https://github.com/umputun/agterm). Перед каждой функцией смотри, как она сделана там (код, `site/docs.html`, `CHANGELOG.md`), и делай так же: его решения считаем проверенными. Отступай только там, где мешает Windows, и записывай причину. Swift-код дословно не переносим, берём модель, протокол команд и поведение.

## Модель

Окно → workspace → сессия → сплит / scratch / overlay. Сессия — один шелл со своим именем, cwd, историей и статусом агента (`idle`, `active`, `blocked`, `completed`).

- Источник правды — `AppModel` в `AgShell.Core`. Страница получает состояние целиком (`state`) и шлёт команды (`command`), сама модель не меняет.
- cwd шеллы сообщают через OSC 9;9 после каждой команды; интеграцию для PowerShell, cmd и bash подкладывает `ShellProfiles`.
- API: `AgShell.Protocol` — типы, клиент и сервер канала; `ControlDispatcher` в `AgShell.Core` выполняет команды через `IControlHost` (его реализует окно). Новая команда добавляется в диспетчер, в `agshellctl` и в справочник — обе версии: [docs/agshellctl.md](docs/agshellctl.md) (английская) и [docs/agshellctl.ru.md](docs/agshellctl.ru.md).
- У сессии до трёх терминалов (`PaneRole`: main, split, scratch). Шеллы в `SessionHost` и терминалы на странице ключуются парой «сессия + панель» (`PaneKey`, `paneKey`); при swap и при передаче сессии сплиту ключи переезжают вместе с шеллами.
- Шелл получает `AGSHELL_SESSION_ID`, `AGSHELL_PANE`, `AGSHELL_WORKSPACE_ID`, `AGSHELL_PIPE` и каталог `agshellctl` в `PATH`; в WSL — через `WSLENV`.
- Статус агента и счётчик уведомлений живут в `Session` и не сохраняются; события (`status`, `notify`, `session.*`) пишутся в `AppModel.Events`, их читают `events` и `session wait`.
- Новые интерактивные Codex в AgShell запускай с `--no-daemon`: общий app-server передаёт hooks окружение другой вкладки. Общий сервер не останавливай; доверие hooks проверяется отдельно через `/hooks`.
- Скилл для агентов — `plugins/agshell/skills/agshell/SKILL.md`, встроен в `agshellctl` (ставится `agshellctl install skill`) и раздаётся как плагин Claude Code и Codex. Новая команда должна попасть и туда.

## Код

- C#, .NET 10. Блочный `namespace { }`, явный `class Program` с `Main`; без top-level statements и file-scoped namespace.
- Никакого `var`, всегда явные типы.
- Комментарии только там, где без них непонятно.
- Фронтенд внутри WebView2: TypeScript, сборка Vite.
- Любая новая функция интерфейса по возможности сразу получает команду в API и в `agshellctl`.

## Сборка и запуск

- Нужны .NET SDK 10 и Node.js LTS в `PATH`.
- `dotnet build agshell.slnx` собирает всё, включая фронтенд (`npm ci` при первой сборке, затем `npm run build` в `src/web`); `-p:SkipWebBuild=true` пропускает фронтенд.
- `dotnet test agshell.slnx` — тесты.
- Приложение: `src/AgShell.App/bin/Debug/net10.0-windows10.0.19041.0/agshell.exe`, рядом `agshellctl.exe`.
- `AGSHELL_STATE_DIR=<каталог>` запускает отдельный экземпляр со своим `state.json` и своим каналом; так удобно проверять, не трогая рабочее состояние. Экземпляр на один каталог состояния всегда один.
- Debug-сборка без `AGSHELL_STATE_DIR`, как в agterm, берёт соседний `%APPDATA%\agshell-debug` (переменная выставляется в окружение процесса, её наследуют шеллы и `agshellctl`); keymap.conf, hooks.conf, ghostty.conf и remotes.conf она читает из обычного `%APPDATA%\agshell`. Чтобы Debug работала с боевым состоянием, запусти её с `AGSHELL_STATE_DIR=%APPDATA%\agshell`.
- Разработка фронтенда с горячей перезагрузкой: `npm run dev` в `src/web`, затем запустить `agshell.exe` с переменной `AGSHELL_DEV_URL=http://localhost:5173/`.
- Пользовательская документация двуязычная: `README.md` и `docs/agshellctl.md` — английские, рядом `README.ru.md` и `docs/agshellctl.ru.md`. Любая правка — сразу в обеих версиях; `DocsTests` сверяет, что у пары совпадают заголовки, блоки кода и таблицы. Документы для разработчиков (`AGENTS.md`, `docs/roadmap.md`, `docs/release.md`, `docs/decisions/`) — только на русском.
- Интерфейс двуязычный (русский и английский). Русский текст — ключ: в `src/web` оборачивай его в `t("…")` (подстановки `{name}`, склонения — `plural`; в таблицах, собранных при загрузке модуля, — `tr("…")` и `t()` там, где текст показывается) и добавляй английский в `src/web/src/strings.ts`; в C# — `Strings.T("…")` с подстановками `{0}` и английским в `src/AgShell.Core/Strings.cs`. Сборку фронтенда валит `scripts/check-strings.mjs`, C# проверяет `StringsTests`. Вывод и ошибки `agshellctl` — на английском, как в agterm, их не переводим.
- Буквы и цифры в сочетаниях клавиш сравнивай по `event.code` (физическая клавиша): при русской раскладке `key` для Ctrl+C равен `с`. Именованные клавиши (Enter, Escape, стрелки) — по `event.key`: он от раскладки не зависит и есть у синтетических нажатий, у которых `code` пустой.

## Решения и план

- Новое архитектурное решение — новый файл `docs/decisions/NNNN-краткое-имя.md` по образцу существующих.
- Закончил этап или пункт — отметь его в `docs/roadmap.md` в том же коммите.
