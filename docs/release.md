# Сборка и подготовка релиза

Нужны Windows x64, .NET SDK 10, Node.js LTS и WebView2 Runtime. Для нативной VT нужен
Zig версии, закреплённой build-скриптом; для Native AOT — C++ build tools, для установщика — Inno Setup.
Новые компоненты окружения устанавливает владелец отдельно.

```powershell
dotnet build agshell.slnx
dotnet test agshell.slnx
scripts/release.ps1 0.5.1
```

Последняя команда — только будущая локальная сборка с VT, AOT, тестами и Inno Setup;
этот этап её не запускает. Новая версия 0.5.1 предлагается как первый публичный выпуск
после проверки кандидата, выбора адреса и приватного security-канала. Выпуск требует
отдельного решения. Не заменять существующие частные assets 0.5.0.

Проверить LICENSE, THIRD-PARTY-NOTICES.md и licenses/ в конечном ZIP и установщике.
Зафиксировать реальный source SHA, версии runtime/packages, provenance и hashes;
совпадение исходников не доказывает идентичность self-contained runtime предыдущей поставке.

Перед публикацией заменить draft RepositoryUrl в AppVersion.cs, AppPublisherURL в installer/agshell.iss и README marketplace
назначением выбранного нового репозитория; включить и независимо проверить private
vulnerability reporting согласно SECURITY. Обновить финальный SECURITY только после проверки.
Публикационный режим скрипта требует явно переданный Repository; по умолчанию он выключен.
Текущий кандидат не имеет remote и не опубликован. Проверить итоговый снимок повторно
после любых изменений и только затем отдельно разрешать публикацию.
