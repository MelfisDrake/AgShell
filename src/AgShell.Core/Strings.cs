using System.Globalization;

namespace AgShell.Core
{
    /// <summary>
    /// The app's own texts in the interface language. The Russian text is the key; English comes from the table, and a text
    /// missing from it stays Russian (a test keeps the table complete).
    /// </summary>
    public static class Strings
    {
        /// <summary>ru or en, from the settings and the language Windows shows itself in.</summary>
        public static string Language { get; private set; } = "ru";

        public static void Apply(AppSettings settings)
        {
            Language = settings.InterfaceLanguage(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        }

        /// <summary>The Russian texts the table translates, for the test that keeps it complete.</summary>
        public static IReadOnlyCollection<string> Translated => EnglishTexts.Keys;

        /// <summary>The text in the interface language; {0}-style holes take <paramref name="args"/>.</summary>
        public static string T(string russian, params object?[] args)
        {
            string text = (Language == "en" || inEnglish) && EnglishTexts.TryGetValue(russian, out string? english) ? english : russian;
            return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
        }

        [ThreadStatic]
        private static bool inEnglish;

        /// <summary>The texts built here in English whatever the interface language, for agshellctl's replies that repeat a dialog.</summary>
        public static string InEnglish(Func<string> build)
        {
            inEnglish = true;
            try
            {
                return build();
            }
            finally
            {
                inEnglish = false;
            }
        }

        private static readonly Dictionary<string, string> EnglishTexts = new Dictionary<string, string>
        {
            ["Не удалось сохранить настройки. Повторите изменение после устранения ошибки записи."] = "Could not save settings. Retry the change after resolving the write error.",
            ["1 живая сессия будет сброшена. "] = "1 live session will be reset. ",
            ["Будет сброшено живых сессий: {0}. "] = "{0} live sessions will be reset. ",
            ["Она появилась до последнего обновления живых сессий и будет создана заново на текущем. "] = "It predates the last Live sessions update and will be recreated on the current one. ",
            ["Все они появились до последнего обновления живых сессий и будут созданы заново на текущем. "] = "They all predate the last Live sessions update and will be recreated on the current one. ",
            ["Одна из них появилась до последнего обновления живых сессий и будет создана заново на текущем. "] = "1 of them predates the last Live sessions update and will be recreated on the current one. ",
            ["{0} из них появились до последнего обновления живых сессий и будут созданы заново на текущем. "] = "{0} of them predate the last Live sessions update and will be recreated on the current one. ",
            ["AgShell сразу закроется и откроется снова с вашими сессиями и раскладкой. Команды, которые шли в этих сессиях, по возможности запустятся снова; остальная работа в них остановится, а разговоры агентов, возможно, придётся продолжить вручную."] = "AgShell quits and reopens itself right away with your sessions and layout. Commands that were running in those sessions are started again where possible; other work running in them stops, and agent conversations may need to be resumed by hand.",
            ["Сбросить живые сессии?"] = "Reset Live Sessions?",
            ["Сбросить"] = "Reset",
            ["Сбросить живые сессии"] = "Reset Live Sessions",
            ["Живые сессии"] = "Live Sessions",
            ["Живые сессии не сброшены: не удалось прочитать список сессий."] = "Live sessions were not reset: the session list could not be read.",
            ["Сброс затронул {0} из {1} живых сессий. Для остальных запустите «Сбросить живые сессии…» ещё раз."] = "The reset covered {0} of {1} live sessions. Run Reset Live Sessions… again for the rest.",
            ["В части сессий ({0}) прежние процессы, возможно, ещё работают; их команды не запущены заново."] = "Some previous processes in {0} sessions may still be running; those commands were not restarted.",
            ["Для сброса живых сессий режим «живыми сессиями» нужен и в этом запуске, и в следующем."] = "Reset Live Sessions needs Live sessions mode for this launch and the next.",
            ["Не удалось прочитать список живых сессий. Ничего не сброшено."] = "The live session list could not be read. Nothing was reset.",
            ["Не удалось проверить сохранённые сессии. Ничего не сброшено."] = "The saved sessions could not be verified. Nothing was reset.",
            ["Не нашлось живых сессий для сброса."] = "No live sessions could be selected for reset.",
            ["1 проблема в remotes.conf"] = "1 issue in remotes.conf",
            ["Новая сессия"] = "New Session",
            ["Новое окно"] = "New Window",
            ["Дашборд"] = "Dashboard",
            ["Недавние сессии"] = "Recent Sessions",
            ["Сессии, ждущие внимания"] = "Sessions Needing Attention",
            ["1 проблема в конфиге ghostty — см. Просмотр событий, затем «Перечитать ghostty.conf»"] = "1 issue in ghostty config — see Event Viewer, then Reload ghostty.conf",
            ["1 проблема — см. Настройки ▸ Клавиши и хуки"] = "1 issue — see Settings ▸ Keys and hooks",
            ["Workspace «{0}» закрыт"] = "Workspace “{0}” closed",
            ["agshellctl добавлен в PATH пользователя: {0}."] = "agshellctl was added to the user's PATH: {0}.",
            ["agshellctl уже в PATH пользователя: {0}."] = "agshellctl is already on the user's PATH: {0}.",
            ["Агент берёт его сам, когда вы просите его управлять AgShell. Управляет он через agshellctl, так что поставьте и его, если ещё не поставили: палитра действий ▸ «Добавить agshellctl в PATH»."] = "The agent uses it automatically when you ask it to control AgShell. It drives the app via the agshellctl CLI, so install that too if you haven't: Command palette ▸ Add agshellctl to PATH.",
            ["здесь уже есть другой скилл «agshell» — он не тронут"] = "a different 'agshell' skill is already there — left untouched",
            ["Из PATH убраны другие папки с agshellctl.exe:"] = "Other folders with agshellctl.exe were removed from PATH:",
            ["Инструмент командной строки установлен"] = "Command Line Tool Installed",
            ["Каталог скиллов создан впервые — перезапустите работающие сессии агентов, чтобы они подхватили новый скилл."] = "A skills directory was created for the first time — restart any running agent session so it picks up the new skill.",
            ["Не удалось установить"] = "Install Failed",
            ["Журнал событий зарегистрирован"] = "Event Log Registered",
            ["Не удалось зарегистрировать журнал событий"] = "Event Log Registration Failed",
            ["События AgShell пишутся в журнал Windows: «Просмотр событий» ▸ «Журналы приложений и служб» ▸ AgShell ▸ Operational."] = "AgShell's events go to the Windows event log: Event Viewer ▸ Applications and Services Logs ▸ AgShell ▸ Operational.",
            ["Регистрация завершилась с кодом {0}."] = "Registration exited with code {0}.",
            ["Рядом с agshell.exe нет {0}."] = "{0} is missing beside agshell.exe.",
            ["Системный PATH не изменён (код {0})."] = "The system PATH was not changed (exit code {0}).",
            ["Откройте новый терминал и запустите «agshellctl --help»."] = "Open a new terminal and run “agshellctl --help”.",
            ["Пропущено:"] = "Skipped:",
            ["Скилл для агентов установлен"] = "Agent Skill Installed",
            ["Скилл не установлен"] = "Skill Not Installed",
            ["Скилл не установлен."] = "The skill was not installed.",
            ["Установлен для:"] = "Installed for:",
            ["remotes.conf, строка {0}: "] = "remotes.conf, line {0}: ",
            ["remotes.conf: не файл, а папка"] = "remotes.conf: a folder, not a file",
            ["{0} предлагает слишком много сессий"] = "{0} offers too many sessions",
            ["{0} уже есть выше, машина пропущена"] = "{0} is already listed above; machine skipped",
            ["{0}: сессия {1}, панель {2} там закончилась. Нажмите клавишу, чтобы закрыть панель"] = "{0}: session {1}, pane {2} ended there. Press a key to close the pane",
            ["{0}: сессия {1}, панель {2}: ssh завершился с кодом {3}. Нажмите клавишу, чтобы закрыть панель"] = "{0}: session {1}, pane {2}: ssh exited with code {3}. Press a key to close the pane",
            ["Закрыто сессий: {0}"] = "Sessions closed: {0}",
            ["Каталог для новой сессии"] = "Directory for the new session",
            ["Слишком много папок"] = "Too Many Folders",
            ["За раз можно открыть не больше {0} папок."] = "You can open up to {0} folders at once.",
            ["Каталог для новых сессий"] = "Directory for new sessions",
            ["Клавиши"] = "Keymap",
            ["Команда не сработала"] = "Command failed",
            ["Командная строка"] = "Command Prompt",
            ["Конфиг"] = "Config",
            ["Настройки"] = "Settings",
            ["Не удалось запустить WebView2: {0}"] = "Could not start WebView2: {0}",
            ["Не удалось запустить agshellctl"] = "Could not start agshellctl",
            ["Вывод agshellctl не дочитан: поток держит другой процесс."] = "The output of agshellctl was not read to its end: another process holds the stream.",
            ["Выберите версию OpenCode"] = "Choose OpenCode Version",
            ["Не удалось определить версию OpenCode в шелле входа (opencode --version). Выберите версию, которой пользуетесь, или пропустите OpenCode и установите остальные хуки."] = "Could not determine the OpenCode version in your login shell (opencode --version). Choose the version you use, or skip OpenCode and install the other hooks.",
            ["Пропустить OpenCode"] = "Skip OpenCode",
            ["Не удалось запустить {0}: {1}"] = "Could not start {0}: {1}",
            ["Не удалось запустить шелл: {0}"] = "Could not start the shell: {0}",
            ["Сохранённый id панели {0} несовместим с режимом live. Панель сохранена без замены id."] = "Saved pane id {0} is incompatible with live mode. The pane was preserved without replacing its id.",
            ["Живая панель недоступна. Сессия сохранена; для запуска программы нужен явный перезапуск панели."] = "The live pane is unavailable. The session was preserved; starting a program requires an explicit pane restart.",
            ["Открыть"] = "Open",
            ["Открыть {0} в браузере?"] = "Open {0} in your browser?",
            ["Открыть в браузере?"] = "Open in your browser?",
            ["Закроется 1 сессия. Окно можно снова открыть из палитры: «Открыть окно»."] = "This closes 1 session. The window can be reopened from the palette: “Open Window”.",
            ["Закроется сессий: {0}. Окно можно снова открыть из палитры: «Открыть окно»."] = "This closes {0} sessions. The window can be reopened from the palette: “Open Window”.",
            ["Закрыть"] = "Close",
            ["Закроется 1 окно и 1 сессия."] = "This closes 1 window and 1 session.",
            ["Закроется 1 окно и сессий: {1}."] = "This closes 1 window and {1} sessions.",
            ["Закроется окон: {0} и 1 сессия."] = "This closes {0} windows and 1 session.",
            ["Закроется окон: {0} и сессий: {1}."] = "This closes {0} windows and {1} sessions.",
            ["Закроется 1 окно и 1 сессия, все работающие шеллы завершатся."] = "This closes 1 window and 1 session, ending all running shells.",
            ["Закроется 1 окно и сессий: {1}, все работающие шеллы завершатся."] = "This closes 1 window and {1} sessions, ending all running shells.",
            ["Закроется окон: {0} и 1 сессия, все работающие шеллы завершатся."] = "This closes {0} windows and 1 session, ending all running shells.",
            ["Закроется окон: {0} и сессий: {1}, все работающие шеллы завершатся."] = "This closes {0} windows and {1} sessions, ending all running shells.",
            ["Выйти из AgShell?"] = "Quit AgShell?",
            ["Выйти"] = "Quit",
            ["Закрыть «{0}»?"] = "Close “{0}”?",
            ["Завершится 1 работающая сессия. Окно можно снова открыть из палитры: «Открыть окно»."] = "This ends 1 running session. The window can be reopened from the palette: “Open Window”.",
            ["Завершится работающих сессий: {0}. Окно можно снова открыть из палитры: «Открыть окно»."] = "This ends {0} running sessions. The window can be reopened from the palette: “Open Window”.",
            ["Отмена"] = "Cancel",
            ["Удалить"] = "Delete",
            ["Удалить «{0}»?"] = "Delete “{0}”?",
            ["О программе AgShell"] = "About AgShell",
            ["Версия {0}"] = "Version {0}",
            ["Версия {0} ({1})"] = "Version {0} ({1})",
            ["Его сессия закроется после короткого окна для отмены."] = "This closes its session after a short undo window.",
            ["Закроется сессий: {0} — после короткого окна для отмены."] = "This closes {0} sessions after a short undo window.",
            ["Его сессия закроется сразу. Его можно снова открыть из меню «Файл ▸ Недавно закрытые»."] = "This closes its session immediately. You can reopen it from File > Open Recent.",
            ["Сразу закроется сессий: {0}. Его можно снова открыть из меню «Файл ▸ Недавно закрытые»."] = "This closes {0} sessions immediately. You can reopen it from File > Open Recent.",
            ["Папка keymap.conf и hooks.conf"] = "Folder of keymap.conf and hooks.conf",
            ["Подключить с {0}"] = "Attach from {0}",
            ["Подключиться к сессии на другой машине"] = "Attach to a session on another machine",
            ["Проблем в конфиге ghostty: {0} — см. Просмотр событий, затем «Перечитать ghostty.conf»"] = "{0} issues in ghostty config — see Event Viewer, then Reload ghostty.conf",
            ["Проблем в remotes.conf: {0}"] = "{0} issues in remotes.conf",
            ["Проблем: {0} — см. Настройки ▸ Клавиши и хуки"] = "{0} issues — see Settings ▸ Keys and hooks",
            ["С какой машины подключить?"] = "Attach from which machine?",
            ["Связь с {0} потеряна · переподключение… · любая клавиша — сейчас"] = "Lost the connection to {0} · reconnecting… · any key retries now",
            ["Сессия «{0}» закрыта"] = "Session “{0}” closed",
            ["Удалённые машины"] = "Remotes",
            ["Хук не сработал"] = "Hook failed",
            ["Хуки"] = "Hooks",
            ["Хуки статусов агентов установлены"] = "Agent Status Hooks Installed",
            ["Хуки статусов агентов установлены — с предупреждением"] = "Agent Status Hooks Installed — with a warning",
            ["в подписи для {0} управляющие символы, машина пропущена"] = "the label for {0} has control characters; machine skipped",
            ["на {0} нечего подключать"] = "nothing to attach on {0}",
            ["не адрес ssh, машина пропущена"] = "not an ssh address; machine skipped",
            ["не удалось запустить ssh до {0}"] = "could not start ssh to {0}",
            ["{0} не назвал демон этой панели"] = "{0} did not name this pane's daemon",
            ["нет workspace, куда подключить сессию"] = "no workspace to attach the session to",
            ["нет ответа"] = "no answer",
            ["подключаю {0}…"] = "attaching {0}…",
            ["получаю сессии с {0}…"] = "listing sessions on {0}…",
            ["процесс завершился с кодом {0}, нажмите любую клавишу"] = "process exited with code {0}, press any key",
            ["сессий: {0}"] = "{0} sessions",
            ["1 сессия"] = "1 session",
        };
    }
}
