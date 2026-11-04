# 0009. Уведомления: toast Windows через CommunityToolkit

Статус: принято, 2026-10-17

## Решение

Уведомления сессий (OSC 9, OSC 777, `agshellctl notify`) показываются как toast Windows через `ToastNotificationManagerCompat` из пакета `CommunityToolkit.WinUI.Notifications`. Клик по toast поднимает окно и выбирает сессию. Если окно не в фокусе, кнопка на панели задач мигает (`FlashWindowEx`), это замена прыжку иконки в Dock у agterm. Целевая платформа приложения — `net10.0-windows10.0.19041.0`.

## Альтернативы

- **Windows App SDK (`AppNotificationManager`).** Современный API, но неупакованному приложению нужен bootstrapper и рантайм Windows App SDK на машине.
- **Значок в трее (`Shell_NotifyIcon`) и всплывающая подсказка.** Без зависимостей, но выглядит устаревше и требует держать значок в трее.
- **WinRT `ToastNotificationManager` напрямую.** Неупакованному приложению самому нужно регистрировать AppUserModelID и COM-активатор; CommunityToolkit делает это за нас.

## Почему

CommunityToolkit работает без MSIX (см. ADR 0008), сам регистрирует приложение в `HKCU` и даёт событие клика в работающем процессе. Если в Windows выключены уведомления, toast не появится, но счётчик в боковой панели и мигание на панели задач останутся.
