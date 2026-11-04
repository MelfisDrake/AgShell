# Сборка и подготовка релиза

Нужны Windows x64, .NET SDK 10, Node.js LTS и WebView2 Runtime. Для нативной VT нужен
Zig версии, закреплённой build-скриптом; для Native AOT — C++ build tools, для установщика — Inno Setup.
Новые компоненты окружения устанавливает владелец отдельно.

```powershell
scripts/build-agshell-vt.ps1
dotnet build agshell.slnx
dotnet test agshell.slnx
scripts/release.ps1 0.5.1
```

Последняя команда — только будущая локальная сборка с VT, AOT, тестами и Inno Setup;
этот этап её не запускает. Новая версия 0.5.1 предлагается как первый публичный выпуск
после отдельной проверки конечной поставки. Выпуск требует
отдельного решения. Не заменять существующие частные assets 0.5.0.

Проверить LICENSE, THIRD-PARTY-NOTICES.md и licenses/ в конечном ZIP и установщике.
Зафиксировать реальный source SHA, версии runtime/packages, provenance и hashes;
совпадение исходников не доказывает идентичность self-contained runtime предыдущей поставке.

Публичный репозиторий: https://github.com/MelfisDrake/AgShell.
AppVersion, installer и marketplace используют этот адрес. GitHub private vulnerability
reporting описан в SECURITY.md. Бинарных публичных выпусков пока нет.
Будущая публикация требует отдельного решения; workflow release запускается вручную
и передаёт `-Repository MelfisDrake/AgShell` явно. Локальный скрипт с `-Publish`
также требует явный `-Repository MelfisDrake/AgShell`; без `-Publish` загрузки нет.
Перед выпуском проверить чистый main, source SHA, CI, notices и фактические assets.
