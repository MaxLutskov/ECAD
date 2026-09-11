# Архітектура ECAD

Актуальність: 0.18, 2026-09-11. Розділи «Ціль» описують майбутній стан. Черговість і приймання — тільки [PLAN](../PLAN.md); факти й дефекти — [AUDIT](AUDIT.md).

## Поточна структура

Три проєкти .NET 10: ECAD.Core без UI-залежностей, ECAD.Desktop з Avalonia 12.1.2, ECAD.Checks з domain/integration/persistence/headless suites. Нові assemblies заради кожної функції не створювалися.

Core/Application містить правила NetNumbering, PathEditing, LibraryUsage, NetReconciliation, ProjectMaintenance, DocumentContent та HistorySnapshot. EditorSession залишається транзакційною межею. Core/Persistence містить ProjectSnapshot, DocumentStructure та FileJson. Desktop адаптує команди до inspector та вкладки призначень.

Pages є канонічними. Root Elements/WidthMm/HeightMm — властивості доступу до активної сторінки, без другої копії даних після нормалізації. ProjectSnapshot schema 13 зберігає лише Pages і спільну модель. Вкладені records поки спільні з domain: це окремий верхній контракт, не повністю незалежний DTO-граф.

DrawingCanvas поки поєднує renderer/input; MainWindow і PropertyPanel містять code-behind. Ці межі, command registry і read-only UI view models — робота 0.19. Контакти поки ElementId+PinIndex; PinId і повний graph — 0.20.

## Ціль: межі перед новими assemblies

У продовженні стабілізації `Core/Application/LibraryUsage` централізує перевірку використання та оновлення фізичних посилань усього проєкту. `ElectricalProjectModel.Validate` перевіряє FunctionId разом із DeviceId і бібліотечні посилання ProjectDevice. Нормалізація відхиляє неправильний явно заданий FunctionId; правила duplicate function та автоматичного створення для відсутнього ID ще не перенесені у команди.

Спочатку розділити відповідальності папками/namespace та тестами, зберігаючи працездатний solution. Не потрібен окремий csproj для кожної області. Після перенесення першого вертикального сценарію до application facade виділити `ECAD.Application`; `ECAD.Infrastructure` виділити, коли persistence/file-dialog/platform adapters матимуть готові порти. До того межі можуть жити у чинних assemblies.

Цільовий напрям: Desktop → Application → Core; Infrastructure → Application/Core; Desktop composition root підключає реалізації Infrastructure. Domain не знає View, Avalonia, файлового шляху чи діалогу. UI надсилає запит і показує результат, не викликає Normalize та не будує новий електричний документ самостійно.

| Область | Розміщення/відповідальність у цілі |
| --- | --- |
| Domain, Geometry, Documents, Project Model | Core modules: одиниці, геометрія, ідентичність, інваріанти, канонічні сторінки; DTO формату окремо |
| Electrical, Libraries, Validation/ERC | Core modules: функції/контакти/граф, каталог, чисті правила з typed issues; не UI lists |
| Application, Commands | Application module/assembly: use cases, транзакції, undo/redo, selection context, change summaries, import/update policy |
| Persistence | Infrastructure: ZIP/JSON DTO, version migrations, atomic save, recovery; ports визначає Application |
| Rendering | Desktop/Rendering: Avalonia renderer, viewport/culling, geometry adapters; незалежний від input handlers |
| Tools/Interaction | Desktop/Interaction: state machines, preview, snap feedback; commit через application commands |
| UI | Desktop/Views, ViewModels, Controls, Styles: shell, docks, inspector editor registry, command adapters |
| Panel Layout | Core model + Application commands + окреме Desktop workspace; physical instance посилається на ProjectDevice, а не дублює BOM |
| Export, Printing, Reports | Чисті read models у Application; форматери/друк у Infrastructure; без screen capture canvas як джерела PDF |
| Platform abstractions | Порти file dialogs, workspace paths, clipboard, launcher, clock/log; Windows/Linux adapters, перевірювані fake implementations |
| Tests | Unit/domain, serialization fixtures, application integration, UI/headless, regressions; розділення за залежностями, а не за кожним класом |

## Problem → Impact → Target → Migration

| Проблема | Наслідок | Ціль | Поступова міграція |
| --- | --- | --- | --- |
| Canvas одночасно renderer/input/tools | Зміна одного інструмента зачіпає інші | Renderer + tool state machines + scene query | Спочатку перенести координатні перетворення та один Path tool, порівняти існуючі pointer checks |
| PropertyPanel/MainWindow містять domain edits | Правила відрізняються за місцем виклику | Typed application commands | Почати Wire→Line та Renumber; UI стає адаптером, потім решта inspector |
| Whole-document Normalize при багатьох діях | Мовчазна зміна електричної моделі | Explicit migrations + validated transactions | Зафіксувати split/merge/copy/open fixtures, витягти graph reconciliation; старі версії не є критерієм приймання |
| Root/Pages та metadata дублюються | Невизначене джерело правди | Pages канонічні, ProjectDevice володіє функціями | DTO старої schema читається мігратором; read adapter для старого UI, потім прибрати дублікати |
| Mutable arrays у snapshots, no-op/dirty за reference | Історія може бути нестабільною | Protected snapshots + content revision | Defensive copies/read-only API; відокремити selection/page focus; delta undo лише після профілювання |
| Tagged DrawingElement з багатьма optional fields | Новий Kind вимагає switch у багатьох місцях | Typed operations/geometry adapters | Не міняти формат одразу: registry операцій для існуючих records, далі typed model за потреби |
| Статичні paths/log/library | Важче тестувати та змінювати platform behavior | Injected ports, read-only catalog | Передати залежності через конструктори від composition root; DI framework необов'язковий |
| Persistence серіалізує domain напряму | Refactor стає зміною формату | Versioned DTO + migration pipeline | ProjectSnapshot v13 уже впроваджено; надалі відділяти вкладені DTO за потреби; міграція не записує файл без save |
| Checks один файл, fail-fast/reflection | Складніше локалізувати regression | Незалежні suites + stable test seams | Зберегти 94 сценарії, рознести файли, потім стандартний runner та CI; reflection прибирати після появи facade |
| Пакування змішує distribution і data | Ризик втрати Projects/Libraries | Staging + manifest + user-data boundary | Sentinel regression, атомарний publish каталогу, жодного очищення користувацьких даних |

## Версії, збереження та історія

Application 0.18 береться з Directory.Build.props: AppInfo читає assembly version, журнал — повну build version, пакувальник перевіряє відповідність. DocumentFormat.Current = 13; library schema = 1. Старі версії не є вимогою; наявні readers і fixtures залишено без розширення гарантій.

FileJson перевіряє обов’язкові поля, повторні JSON-ключі й null-записи перед десеріалізацією. Schema 13 відхиляє root-копію геометрії. DocumentStructure перевіряє колекції й ID до нормалізації; помилки не замінюють відкритий документ. ProjectFile записує атомарно; ліміт project.json і бібліотеки — 32 MiB.

ContentRevision відділяє зміну даних від навігації. DocumentContent порівнює вкладені records/arrays структурно без JSON-дерев. HistorySnapshot зберігає приватні UTF-8 bytes поточного і попередніх станів: зовнішня зміна масиву не пошкоджує збережену історію. Публічні arrays поки mutable; пряме редагування read model не є API зміни документа. Повні snapshots мають витрати пам’яті, delta undo залишається рішенням після профілювання.

NetReconciliation узгоджує split/merge перед commit: несумісні параметри або два locked номери відхиляються; сумісне merge переносить посилання. Split копіює параметри у нове незакріплене коло; попередній ID лишається у гілки з найменшим ID провідника на сторінці. За наявності призначень потрібно явно clear → split → reassign. RemoveUnusedNets видаляє лише кола без провідників/клем/жил; нерозміщені пристрої й функції зберігаються. DeleteElements очищує міжсторінкові посилання та кінцеві елементи жил.

CI викликає scripts/Verify.ps1 на Windows/Ubuntu 24.04 зі stable .NET 10. Використано [checkout](https://github.com/actions/checkout) і [setup-dotnet](https://github.com/actions/setup-dotnet), read-only permissions. Докази та профілювання — [приймання 0.18](RELEASE-0.18.md).

## Розширення після фундаменту

Нові primitives підключають geometry operations та renderer; property editors реєструються за типом властивості, а не великим switch; ERC rules повертають stable code/entity/page/severity; reports споживають read models; PLC I/O, кабелі й клеми використовують спільні Device/Function/Pin IDs. Макроси/шаблони мають remapping IDs при вставці. Ці внутрішні контракти дають шлях до розширень, але публічний plugin SDK, sandbox та web/mobile transport зараз не створюються.

## Перевірка переходу

Додано історичні fixtures schema 6–11 у `tests/fixtures`; вони записані відповідними writer з Git, генератор і походження збережені поруч. `MigrationFixtureChecks` перевіряє geometry/labels/dimensions/catalog/pages/references, читання поточним loader, save/open та Undo, не змінюючи вихідний файл. Схеми 1–5 не мають доступного історичного writer; synthetic checks v1/v5 зберігаються з явно обмеженою доказовістю.

Кожен перенесений сценарій зберігає open/save, geometry, labels, selection, undo/redo. Domain tests не завантажують Avalonia; persistence fixtures перевіряють schema окремо; integration tests покривають транзакції та відмову без зміни стану; headless tests — клавіатуру, pointer, focus, inspector. Реальні Windows/Ubuntu UI та профілювання доповнюють, а не замінюються headless прогоном.
