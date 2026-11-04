# План публичного исходного снимка

AgShell — экспериментальный Windows-терминал на WPF, WebView2, ConPTY и xterm.js.
Модель workspace/session/pane и команды развиваются по образцу закреплённого agterm.

Подготовлены исходники, тесты, сборочные скрипты, двуязычная пользовательская документация,
MIT собственных вкладов и лицензии upstream. Публичный исходный репозиторий:
https://github.com/MelfisDrake/AgShell; приватные сообщения об уязвимостях — через GitHub
согласно SECURITY.md. Подготовка исходного снимка завершена.

Этап 0.5.1 — первый публичный бинарный выпуск с MIT/полной атрибуцией и узким
HTML bridge guard: штатная сборка VT/AOT/installer, runtime provenance и проверка
конечных ZIP/setup и включённых Windows SDK projection/WinRT notices. Самостоятельные hooks сохраняют оба copyright в полном MIT;
skill/plugin включают собственный LICENSE и оригинальный LICENSE-agterm.
Закрытие HTML overlay останавливает дальнейшую инициализацию закрытого браузера;
адресные native regression cases проверяют реальные async-продолжения.
Fixture проверки stdin использует готовность потомка до выхода родителя;
exit/cancellation limits и продуктовый BackgroundCommand сохранены. Notes и процедура выпуска подготовлены в README и release.md.
Установка и проверка runtime upgrade/регистрации — следующий отдельный этап;
полная Windows/DPI/IME/accessibility/alpha матрица остаётся открытой.

Публичные пределы проверки: [sanitation-summary.md](sanitation-summary.md).
Архитектура: [decisions/](decisions/). Сборка: [release.md](release.md).
