# UI/UX Architecture & Redesign — специфікація

Це цільовий дизайн, не опис готового UI і не другий roadmap. Реалізація shell — milestone 0.19 у [PLAN](../PLAN.md); електричні редактори, panel layout і layers додаються у визначених там етапах через ті самі контракти. Поточний стан: toolbar rows, фіксований inspector, модальні редактори бібліотеки/електропроєкту, code-behind.

## Interaction references

Переглянуто офіційні матеріали 2026-09-10. Це порівняння документованих патернів, не hands-on тест усіх продуктів. Висновки для ECAD — наші проєктні рішення; конкретні layouts не копіюємо.

| Джерело | Патерн → застосування в ECAD |
| --- | --- |
| [AutoCAD: Properties palette](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-DidYouKnow/files/GUID-94C065AB-FF9E-4752-B778-23D2FBB87E18.htm) | Спільні властивості selection → один inspector із mixed values для multi-selection |
| [EPLAN: docking](https://www.eplan.help/en-us/Infoportal/Content/Plattform/2026/Content/htm/userinterface_h_bedienelementeandocken.htm) | Навігатори як dockable panels, збереження workspace → розкладка панелей окремо від документа |
| [SEE Electrical: tutorial](https://www.ige-xao.com/images/en/uk/pdf/products/see_electrical/Tutorial_SEE-Electrical.pdf) | Workspace information у Properties pane → inspector залежить від контексту проєкт/аркуш/об'єкт. Джерело історичне, не твердження про найновіший UI |
| [SOLIDWORKS Electrical: interface](https://help.solidworks.com/2025/English/SWElec/r_swelec_interface.htm) | Dockable вкладки та component explorer → відділення структури документа від логічних пристроїв |
| [KiCad: schematic editor 9.0](https://docs.kicad.org/9.0/en/eeschema/eeschema.html) | Hierarchy navigator, properties, selection filter, hotkey actions → єдина модель команд та явні фільтри вибору |
| [QElectroTech: collections](https://download.qelectrotech.org/qet/manuals/html/users/interface/panels/collections_panel.html) | QET/user/project collections і пошук → видимі джерело та область бібліотеки, швидкий пошук і вставка |

## Workspace

Центр — canvas з вкладками аркушів. Ліворуч — вкладки «Проєкт/Аркуші», «Бібліотеки», «Пристрої/Кола». Праворуч — контекстний inspector. Унизу — ERC/повідомлення, які можна згорнути. Верх — menu bar та компактний toolbar частих команд; інструменти малювання у toolbox. Status bar показує координати в мм, масштаб, крок сітки, активну прив'язку, режим електрична/графічна лінія та підказку наступної дії.

Панелі мають змінюваний розмір, docking/tab groups, hide/show та reset layout. Розкладка зберігається у user settings і коректно відновлюється після відключення монітора. Спочатку підтримуємо dock у головному вікні; floating windows — лише після перевірки фокусу/HiDPI на обох ОС. Panel layout використовує той самий shell з іншим canvas context. Для layers передбачити PanelId/command contract, а самі Layer model/UI реалізувати наприкінці функціонального плану.

## Команди й панелі

Один каталог команд: ID, локалізована назва, icon, shortcut, category, CanExecute, checked/active state, handler. Menu, toolbar, context menu та searchable command palette посилаються на той самий ID. UI handler викликає application use case і обробляє typed result; правила domain не живуть у кнопці. Panel registry: ID, title, factory, default placement, availability, persisted layout version. Не потрібна plugin-система для реєстрації власних панелей.

Меню: Файл, Редагування, Вигляд, Креслення, Електрика, Бібліотеки, Довідка; рідкі команди — у меню/палітрі, а не новий ряд кнопок. Context menu показує доступні дії для hit object або selection. Disabled дія має пояснення. Пошук команди знаходить українські назви й сталі позначення NO/NC.

## Selection і введення

- Зберегти L/C/R/D, наявні P/A, Escape, Undo/Redo; shortcut не перехоплюється всередині текстового поля. Усі команди доступні клавіатурою, переходи focus передбачувані.
- Hover, selected, active tool, disabled відрізняються не тільки кольором. Рамка зліва направо — contained selection, справа наліво — crossing; підказка та відповідне оформлення рамки. Multi-selection inspector показує спільні поля, «різні значення», кількість об'єктів; редагування однією транзакцією.
- Snap показує тип, ціль і маркер; guides відрізняються від геометрії документа. Контакт/вузол має пріоритет у screen tolerance; перетин сам по собі не з'єднує кола.
- Числове введення біля курсора: одна система Length/Angle, Width/Height, Radius/Diameter, Arc parameters; порожнє поле вільне; Tab наступне, Enter commit, Esc cancel→select. Довільне малювання мишею залишається доступним. Кома/крапка, одиниці й точність однакові в inspector і canvas input.
- Preview не змінює документ. Недопустиме число/конфлікт обмежень лишає попередній стан і пояснюється біля поля. Ніякого непомітного округлення точного розміру до сітки.

## Reusable Avalonia controls і design system

Початкові tokens — кандидати для перевірки прототипом: spacing 4/8/12/16/24 DIP; звичайний control height 32 DIP, compact 28 DIP; body 14 DIP, secondary 12 DIP. Не плутати DIP інтерфейсу з мм креслення. Typography підтримує кирилицю, табличні цифри для координат, системний fallback; не задавати розмір креслярського тексту шрифтом UI.

Спільні controls: CommandButton/MenuItem, ToolGroup, PropertyRow, NumericUnitEditor, MixedValueEditor, EntityPicker, SearchTree, DockHost, ValidationSummary, InlineNotification, EmptyState, ProgressOverlay. Спільні styles/resources: surface/text/border/selection/snap/error/focus кольори, spacing, typography, strokes, icons. Light/dark readiness означає semantic resources, а не інверсію полотна; папір/preview print має окрему палітру.

Icons — узгоджені SVG/path assets із перевіреним походженням, 16/20/24 DIP, tooltip із назвою та shortcut; замінити неоднакові Unicode glyphs. Критичні/неочевидні дії мають текст. Accessibility: automation names, labels, tab order, видимий focus, читабельний contrast, scale 100/150/200%; колір не є єдиним носієм помилки.

## Зворотний зв'язок

Inspector не перебудовує весь UI та не втрачає focus при кожному Changed. Зміни selection, документа й viewport — різні повідомлення. Empty project/library/ERC panel пояснює наступну дію. Помилки полів inline; очікувана відмова команди — повідомлення з дією; неочікувана помилка — короткий текст, correlation ID і «Журнал». Навігатор/ERC знаходить об'єкт на потрібному аркуші та виділяє його.

Confirmations потрібні для втрати незбережених змін і незворотних дій, а не кожного Undo-able edit. Довгі import/export/check operations мають progress/cancel, не блокують dispatcher; успішна завершена операція не створює зайвого модального вікна. Dialog shell стандартизує заголовок, validation, Enter/Esc і мінімальний розмір. Локалізація через ресурси; технічні NO/NC і позначення одиниць залишаються сталими.

## Перевірка дизайну

Приймання у PLAN спирається на сценарії: намалювати мишею і числом; вибрати групу; змінити спільну властивість; знайти символ і вставити; перейти ERC→аркуш→об'єкт; save/open; undo/redo; сховати/повернути панелі. Потрібні headless focus/command tests та ручні прогони Windows/Ubuntu при 100/150/200%, вузькому вікні й українських довгих назвах. До впровадження docking library зробити малий spike і перевірити сумісність, ліцензію та клавіатурну навігацію; вибір пакета ще не зроблено.
