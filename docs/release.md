# Сборка и публикация релиза

Первый публичный бинарный выпуск — [0.5.1](https://github.com/MelfisDrake/AgShell/releases/tag/v0.5.1).
Конкретные изменения относительно прежней 0.5.0 описаны в README.md / README.ru.md:
MIT собственных вкладов, полная атрибуция source/distributions/отдельных hooks и skills,
provenance runtime, узкие Source/registry/reply проверки HTML-моста, публичный адрес
и приватный security channel. Локальный HTML остаётся доверенным и имеет командные
полномочия. Исправление — defense in depth; remote exploit не установлен.

Нужны Windows x64, .NET SDK 10, Node.js LTS и WebView2 Runtime. Для нативной VT нужен
Zig версии, закреплённой build-скриптом; для Native AOT — C++ build tools, для установщика — Inno Setup.
Новые компоненты окружения устанавливает владелец отдельно.

```powershell
scripts/build-agshell-vt.ps1
dotnet build agshell.slnx
dotnet test agshell.slnx
scripts/release.ps1 0.5.1
```

Последняя команда из чистого source собирает локальные ZIP/setup с VT, Native AOT,
тестами и Inno Setup, без публикации. Publish закрепляет AgShellReleaseRuntimeVersion=10.0.14
для тестового restore, приложения, CLI и live: локальный SDK и GitHub SDK могут выбирать разные patch
версии по умолчанию, а поставка должна соответствовать проверенным runtime notices.
Directory.Build.targets адресно задаёт runtime/host/AOT/linker packs .NET 10; Windows SDK
reference сохраняет свою версию. SDK и прямые PackageReference этим не обновляются. Без Inno локальный скрипт предупреждает
о пропущенном setup; такой неполный комплект не считается готовым выпуском.
Не использовать -NoAot для окончательной поставки.

Перед сборкой сохранить прежний dist вне репозитория вместе с полным manifest/hashes.
Проверить root LICENSE, THIRD-PARTY-NOTICES.md и все тексты licenses/ в конечном ZIP,
а также реальные compiler inputs установщика. Manifest 0.5.1 включает 35 license files,
в том числе Windows SDK projection/WinRT: оригинальные SDK terms, их текстовый вариант
и CsWinRT MIT; собственная MIT AgShell не заменяет условия этих компонентов. Сверить runtime/package versions с manifest
и официальными пакетами, source SHA, версии executable, AOT и hashes. Совпадение исходников
не доказывает идентичность local и GitHub self-contained сборок.

Проверка portable выполняется в отдельном state/config/WebView профиле: version/tree,
нативный заголовок/action popup, возврат ввода и локальный HTML-мост. Installer проверяется
компиляцией и статически; runtime upgrade/регистрация требуют отдельного этапа.
Сохранить работу шеллов и резервную копию состояния перед обновлением, закрыть старые окна.
Сборки не подписаны; SmartScreen может предупредить о неизвестном издателе.

Публичный репозиторий: https://github.com/MelfisDrake/AgShell.
Приватные сообщения об уязвимостях — по SECURITY.md. Штатный workflow release запускается
вручную с version=0.5.1 на проверенном main и передаёт `-Repository MelfisDrake/AgShell` явно.
Локальный `-Publish` также требует явный Repository. Перед запуском проверить repository ID,
visibility/remotes, отсутствие тега/release/чужого запуска, чистый pushed main и exact-head CI.
Во время workflow не двигать main: скрипт создаёт release с target main.
После успеха дополнить generated body конкретными EN/RU notes из README, требованиями,
ограничениями и immutable source URL; проверить nondraft/nonprerelease, tag commit,
анонимно скачать оба assets и сверить их GitHub digests/sizes, payload и published smoke.
При failure сначала изучить console/TRX/always artifact и фактическое состояние release;
повтор или замена опубликованных assets не должны происходить молча.

Проверенный scope — экспериментальный Windows 11 x64 / 96 DPI. Windows 10, другие/mixed
DPI, IME, accessibility, text scale/high contrast и полная графическая/alpha матрица
не проверены; у прозрачности без blur остаются ограничения.
