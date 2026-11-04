# 0016. Продуктовая композиция заголовка: WindowChrome и read-only identity

Статус: принято для первого и второго продуктовых срезов 5.8, 2026-11-03. Уточняет ADR0001 и заменяет ограничение ADR0015 «только прототип» в границе этого среза.

## Основание

После живого показа финального нативного прототипа A со штатной верхней рамкой пользователь
03.11.2026 ответил «Ну ок пусть так». До этого поручил исправить прототип, проверить Snap и затем
переносить в приложение. PM объявил первый срез интеграции; его отдельное поручение воспроизведено
в [публичные границы проверки](../sanitation-summary.md). Это новое решение после
[ADR0015](0015-windowchrome-titlebar-prototype.md) и [публичные границы проверки](../sanitation-summary.md),
где принятия ещё не было. Их исторические результаты не переименовываются в продуктовые проверки.

Принят именно внешний вид Hidden: штатная верхняя рамка и клиентская drag-полоса 3 DIP;
при 96 DPI normal суммарно 7 px, maximized видимые 3 DIP. Это выбор вида,
а не доказательство невозможности общей высоты 3 DIP.

## Решение и граница

В MainWindow WPF WindowChrome сохраняет штатные Windows caption buttons normal/compact.
Общая строка WPF над существующим native menu и HWND WebView2 содержит read-only identity и полный кластер
recent/attention | scratch/split/overlay | dashboard/quick/custom, плюс sidebar toggle. Первый срез оставлял действия
в DOM; второй переносит их и три настоящих popover (recent, attention, custom) в WPF по поручению после «Делай».
Это разрешённое уточнение ADR0001 только для chrome, не переход всего интерфейса в WPF.

Identity: workspace выбранной сессии → displayName main/split → пользовательское имя окна;
контекст заменяет detail в normal и идёт после host в compact. Scratch не меняет identity main/split.
Dashboard показывает своё имя; zoom — identity сессии. Существующие toolbarMode и Interface toggles
сохраняют ключи и defaults. MinWidth остаётся 640. Native captions и resize принадлежат ОС.

Переносятся проверенные insets maximized, DWM caption reserve и его закраска ниже native buttons,
Hidden NonClientFrameEdges.Top и сохранение обычных restore bounds при fullscreen из maximized.
Обычное окно оставляет 6 DIP по бокам и снизу вне HWND WebView2: эти зоны получает WPF/native resize;
WebView и pageLayer имеют одинаковые отступы. В maximized/fullscreen эти клиентские отступы снимаются.
WindowChrome сохраняется при fullscreen; окно занимает полный текущий монитор, а не только основной.
Существующий reveal product menu и close pipeline, включая ADR0014, сохраняются.
Драйверы опытов и макетные действия в продукт не входят.

A не заменяется на B, собственные caption buttons, layered window или CompositionControl.
С125/С128 и alpha без blur этим решением не закрыты. Это не объявление всей 5.8 готовой.

## Проекция и клавиатура второго среза

AppModel остаётся источником данных. Как у существующего native menu, страница строит только проекцию состояния:
видимость, gates, glyph, tooltip с текущим keymap, строки MRU/attention/custom. WPF не хранит независимые сессии,
историю или настройки. Выбор передаёт id на страницу, где он повторно проверяется по свежему состоянию и проходит
через прежние command/toggleSplit/runCustom (включая selection/context и учёт usage).
Сочетания в popup передаются с native scan code в тот же JS shortcut matcher, включая key-up/repeat:
второго keymap parser в WPF нет. Обработанное сочетание закрывает свой popup по generation token;
обычные навигационные клавиши и Escape остаются у списка, ввод за ним не синтезируется. Host дополнительно отсекает
ask/pick и смену cover до доставки следующей проекции. Меню, API, keymap и defaults не меняются.

Открытый popup удерживает auto-follow на host независимо от DOM suppression. Сообщение toolbarPopover удерживает
страницу от автоматического захвата терминального ввода. Popup получает native focus на свой HWND, затем WPF
keyboard focus на строку; Tab/стрелки перемещают выбор, Enter выбирает, Escape закрывает. Собственная активация
popup HWND отличается от ухода к другому окну. Возврат разрешён только пока foreground принадлежит владельцу или
именно его popup; восстанавливается прежний native HWND, включая page overlay. Outside click, уход к другому окну,
пустой/запрещённый список, смена режима/cover закрывают popup. ADR0014 и close cancel сохраняются.

Финальная регрессия: проекция передаёт также текущий DOM gate (`held`), включая clipboard prompt и welcome;
межоконный attention сохраняет его у цели и повторно проверяет источник/цель до handoff. Снятие popup-cover
не разрешает странице неактивного окна забирать foreground: native page восстанавливает focus только при
совпадении foreground с корневым HWND владельца, в том числе после асинхронной инициализации страницы.

Общая DOM action row удаляется целиком; `--chrome-top` внутри WebView равен нулю. Все main/split/scratch/overlay,
HUD, pageLayer, dashboard и quick остаются в прежних подсистемах, получают viewport ниже WPF toolbar/menu.
При narrow 640 сжимается read-only identity и отпускается привязка к sidebar, actions сохраняют ширину, captions —
свой reserve. В zoom/dashboard доступны только соответствующие exit controls; Hidden не раскрывает controls.

Это выбранная пользователем WPF-композиция, а не доказательство невозможности DOM toolbar/popovers.
Сложность focus/airspace сама по себе не считается корнем отступления от agterm.

## Сверка с agterm и корни отличий

Локальный agterm e0b4844, без fetch: `WindowContentView+Titlebar.swift`, `WindowControlArea.swift`,
`WindowAppearance.swift`, `agtermCore/TitlebarComposition.swift`, `Session.subtitleDetail`,
`WindowContentView.swift:98–105`, `site/docs.html:1480–1518`, `CHANGELOG.md` (toolbar/Hidden).

- WPF toolbar и popup HWND вместо SwiftUI toolbar/NSPopover → выбранная композиция A →
  [решение пользователя 03.11.2026: второй продуктовый срез после «Делай», полный action cluster и WPF popovers].
- Кнопки справа и Windows maximize/system menu вместо AppKit traffic lights/zoom → штатный контракт Windows →
  [факт: прототип и новые native действия/HTMAXBUTTON в частном нативном стенде; публичные пределы приведены в sanitation-summary.md].
- Hidden normal 4 px native top + 3 DIP вместо 3 px agterm → выбранный вид A →
  [решение пользователя 03.11.2026: «Ну ок пусть так» после живого показа].
- Отпускание выравнивания identity у sidebar при нехватке места → исходный дизайн A с Windows caption reserve →
  [решение пользователя 03.11.2026: перенос принятого A по утверждённому дизайну в этом поручении].

## Проверки и ограничения

Покрытие и неудачные попытки разделены в [публичные границы проверки](../sanitation-summary.md).
Ручной Snap прежнего прототипа не доказывает hover/Win+Z продукта. Остальная DPI/темная/светлая,
high contrast, accessibility и аппаратная матрица фиксируется в [публичных границах проверки](../sanitation-summary.md).
