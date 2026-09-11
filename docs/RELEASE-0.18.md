# Приймання ECAD 0.18

Дата: 2026-09-11. Завдання — завершити стабілізацію для переходу до UI/UX 0.19. Application 0.18; document schema 13; library schema 1. Старі версії не є вимогою приймання.

## Перевірені критерії

| Область | Реалізація та доказ |
| --- | --- |
| Нумерація та конвертація | start/step, locked collisions, overflow, no-op/redo, Wire→Line без NetId; StabilizationChecks та inspector headless |
| Електричні зміни | Split/merge з metadata, конфліктами й перепризначенням клем/жил; NetReconciliationChecks, вкладка призначень з Undo |
| Бібліотечні посилання | Перевірка всіх сторінок і нерозміщених ProjectDevice; фізичні виконання узгоджуються з Undo/round-trip |
| Функції та видалення | Перевірка власника FunctionId, повторне використання не приховується від ERC, Paste створює функцію явно; delete очищує cross references/cable endpoints |
| Сторінки та persistence | Канонічні Pages, окремий ProjectSnapshot, schema 13 без root geometry; round-trip, негативні JSON/ID/null checks, атомарний save і ліміт 32 MiB на запис/читання |
| History/dirty | ContentRevision, no-op/navigation без dirty, приватні bytes для Undo/Redo навіть після зовнішнього пошкодження масиву; ReleaseReadinessChecks |
| Очищення | Явна команда видаляє лише кола без посилань; нерозміщені пристрої/функції не видаляються |
| Пакування | Окремі App/Libraries/Projects, два приклади бібліотек і схема; повторна збірка створює нову папку, sentinel-перевірка захищає попередні файли |
| Версія та automation | Directory.Build.props — єдине джерело; AppInfo/UI/log/package; Verify.ps1 та GitHub Windows/Ubuntu 24.04 matrix |

Локальний Release: 123 checks та PowerShell-перевірка пакування. Команда: `./scripts/Verify.ps1`. SDK локально 10.0.400-preview.0.26322.102, runtime 10.0.9; попередження NETSDK1057 повідомляє про preview SDK. CI використовує stable 10.0.x. Фактичний віддалений результат буде зафіксовано після завершення запуску.

## Діагностика швидкодії

Відтворення: `dotnet run --project tests/ECAD.Checks -c Release --no-build -- --profile`. Windows build 26200, .NET 10.0.9, 14 логічних CPU. Прогрів один виклик, далі наведена кількість повторів; вимірюються wall time та алокації поточного потоку. Значення одного діагностичного запуску, не гарантовані budgets.

| Сценарій | 1 000 ліній: час / алокації | 5 000 ліній: час / алокації |
| --- | --- | --- |
| Typed equality, 20 повторів | 7.22 ms / 7.02 MiB | 34.29 ms / 35.10 MiB |
| JSON tree comparison, 20 повторів | 1568.61 ms / 543.56 MiB | 13485.18 ms / 2717.53 MiB |
| ApplyDocument no-op, 20 повторів | 62.29 ms / 25.56 MiB | 368.91 ms / 112.07 MiB |
| Move + Undo, 10 повторів | 48.19 ms / 30.78 MiB | 610.65 ms / 150.64 MiB |

JSON comparison — контрольний алгоритм повної серіалізації read model, не точне відтворення старого commit. Ці результати підтверджують виграш від відмови від JSON-дерев у порівнянні, але не вимірюють UI latency, пам’ять усієї історії або великі електричні графи. Витрати snapshots/Normalize залишаються предметом профілювання пілота.

## Межі переходу

- 0.19 перебудовує shell/commands/panels/inspector та розділяє rendering/interaction. EditorSession уже є транзакційною межею, але повний UI facade/read-only view models ще попереду. Domain arrays поки mutable; прямі зміни не є підтримуваною командою редагування.
- Split кола з призначеннями потребує clear → split → reassign. Стабільні PinId, повні редактори клем/жил, електричні міжсторінкові переходи та рухомі номери належать 0.20.
- Навчальний файл перезбережено у schema 13; він зберігає 14 відомих повідомлень ERC. Це приклад моделі, не готова схема з ERC=0.
- Headless CI Ubuntu не замінює ручну desktop-перевірку HiDPI/keyboard/друку. Ручне UI-приймання належить 0.19; друк — 0.22. Шари залишаються наприкінці функціонального плану.

Наступний робочий етап — [0.19 у PLAN](../PLAN.md) та [UI/UX специфікація](UI-UX.md).
