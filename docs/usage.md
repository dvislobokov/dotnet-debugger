# Руководство пользователя

## Установка

### VS Code

Расширение **.NET Debugger (dotnet-debugger)** содержит адаптер для вашей платформы — больше ничего ставить не нужно.
Адаптер внутри самодостаточный (self-contained): ему не нужен установленный .NET, достаточно рантайма самого
отлаживаемого приложения.

До публикации в Marketplace: скачайте `.vsix` для своей платформы со страницы релизов и выполните
`code --install-extension dotnet-debugger-<платформа>-<версия>.vsix`.

### .NET-инструмент (для Neovim, Emacs, Helix и любых других DAP-клиентов)

```
dotnet tool install -g dotnet-debugger-dap
dotnet-debugger --version
```

Пакет называется `dotnet-debugger-dap`, команда — `dotnet-debugger`. Пакет один на все платформы. Работает на .NET 8
и новее: собран под .NET 8 и сам переходит на тот рантайм, который установлен (8, 9, 10...).
Расширение VS Code без встроенного адаптера тоже найдёт установленный так инструмент.

### Архив

В релизах есть самодостаточные сборки адаптера по платформам (`dotnet-debugger-<версия>-<rid>.zip|tar.gz`): один
исполняемый файл и `dbgshim`, установленный .NET не нужен. Распаковать и указать путь в настройке клиента.

### Закрытая (корпоративная) среда

- **На машине только .NET 8, новее ставить нельзя.** Подходит любой вариант: `.vsix` и архивы вообще не зависят от
  установленного .NET, а инструмент (`dotnet tool`) работает на .NET 8.
- **Нет интернета.** `.vsix` ставится из файла (`code --install-extension файл.vsix`), адаптер внутри. NuGet-пакет
  инструмента можно положить во внутренний NuGet-репозиторий (Artifactory, Nexus) или поставить из папки:
  `dotnet tool install -g dotnet-debugger-dap --add-source <папка с .nupkg>`.
- **Сеть.** Адаптер сам никуда не обращается и ничего не отправляет. Серверы символов выключены, пока их явно не
  включат в `symbolOptions`.
- **Запрещён запуск неподписанных программ (AppLocker, WDAC).** Исполняемый файл адаптера не подписан. Вариант с
  инструментом запускается подписанным `dotnet` (`dotnet <путь>/dotnet-debugger.dll`), что обычно разрешено.
- **Контейнеры.** Отладчику нужен `ptrace`: `--cap-add=SYS_PTRACE --security-opt seccomp=unconfined`.

## Запуск отладки в VS Code

`launch.json` не обязателен:

| Способ | Что происходит |
| --- | --- |
| **F5** без `launch.json` | сборка и отладка запускаемого проекта рабочей области |
| Статус-бар **`▷ <проект>`**, **Ctrl+Alt+F5** | сборка и отладка стартового проекта (последнего отлаживавшегося) |
| Меню ▶ в заголовке редактора | *Debug Project of Active File*, *Debug Project in Terminal* |
| Правый клик по `.csproj` | *Debug Project*, *in Terminal*, *with Launch Profile...*, *Set as Startup Project* |
| Палитра: `.NET Debugger: Attach to Process...` | выбор процесса и подключение |

Приложению нужен ввод с клавиатуры (`Console.ReadLine`) — запускайте «in Terminal» или укажите
`"console": "integratedTerminal"`.

## launch.json

```jsonc
{
  "name": ".NET: Launch",
  "type": "dotnet-debugger",
  "request": "launch",
  "project": "${workspaceFolder}/src/App/App.csproj",   // или "program": ".../App.dll"
  "build": true
}
```

| Поле | Описание |
| --- | --- |
| `program` | Путь к `.dll` (запускается через `dotnet`) или к исполняемому файлу приложения |
| `project` | Файл проекта или каталог. Выходная сборка определяется через MSBuild, применяется `Properties/launchSettings.json` |
| `build`, `configuration` | Выполнить `dotnet build` перед запуском; конфигурация сборки (по умолчанию `Debug`) |
| `args`, `cwd`, `env` | Аргументы, рабочий каталог, переменные окружения (`null` удаляет переменную). Имеют приоритет над профилем запуска |
| `launchSettingsProfile` | Имя профиля. Не задано — первый с `"commandName": "Project"`; `""` — не использовать `launchSettings.json` |
| `console` | `internalConsole` (по умолчанию), `integratedTerminal`, `externalTerminal` |
| `stopAtEntry` | Остановиться на первой строке `Main` |
| `justMyCode` | По умолчанию `true`: код без символов пропускается при шагах и сворачивается в `[External Code]` |
| `enableStepFiltering` | По умолчанию `true`: Step Into не заходит в свойства и операторы |
| `symbolOptions` | `{ "searchPaths": ["C:/symbols", "https://symbols.example.com"], "cachePath": "...", "searchMicrosoftSymbolServer": true, "searchNuGetOrgSymbolServer": true }` |
| `sourceFileMap` | `{ "/_/": "${workspaceFolder}" }` — пути из символов → локальные каталоги (сборки из CI, контейнеров) |
| `suppressJitOptimizations` | Запуск без предкомпилированного (ReadyToRun) кода; включается сам для R2R-программ |

Подключение к процессу:

```jsonc
{ "name": ".NET: Attach", "type": "dotnet-debugger", "request": "attach", "processId": "${command:pickProcess}" }
```

## Neovim (nvim-dap)

```lua
local dap = require('dap')
dap.adapters.coreclr = { type = 'executable', command = 'dotnet-debugger' }
dap.configurations.cs = {
  { type = 'coreclr', request = 'launch', name = 'Launch project', project = '${workspaceFolder}', build = true },
  { type = 'coreclr', request = 'attach', name = 'Attach', processId = require('dap.utils').pick_process },
}
```

## Что умеет окно Watch и Debug Console

- арифметика, сравнения, `?:`, `??`, `?.`, приведения, `is`/`as`, `typeof`, `default`, `nameof`, `$"интерполяция"`
- поля, свойства, индексаторы, статические члены, вызовы методов (в т.ч. generic и методов расширения: `items.Count()`,
  `list.First()`), создание объектов и массивов, присваивания
- спецификаторы формата: `x,h` — hex, `s,nq` — без кавычек, `obj,raw` — без визуализаторов
- псевдопеременные: `$exception`, `$ReturnValue`
- **не поддерживаются** лямбды (`Where(x => ...)`), паттерны, `await`

При наведении мыши методы не вызываются и ничего не присваивается.

## Брейкпоинты

Условия (`i == 7 && total > 0`), счётчик срабатываний (`5`, `>=5`, `%5`), logpoints (`i={i} сумма={total + 1}`),
брейкпоинты на функцию (`Method`, `Type.Method`, `Namespace.Type.Method`), inline-брейкпоинт внутри однострочной лямбды
(Shift+F9). Фильтры исключений принимают условия по типам: `System.IO.*`, `!System.OperationCanceledException`.

## Если что-то не работает

| Симптом | Что проверить |
| --- | --- |
| Брейкпоинт серый («No loaded module with symbols...») | Рядом с `.dll` есть `.pdb` (portable)? Собрано в `Debug`? Для сборок из CI/контейнера нужен `sourceFileMap` |
| Брейкпоинт с предупреждением «source file differs» | Исходник изменён после сборки — пересоберите |
| Останавливается не там / «перескакивает» строки | Оптимизированная сборка (`Release`). Для R2R-программ адаптер сам отключает предкомпилированный код |
| Приложение ждёт ввода и «висит» | `internalConsole` не умеет ввод: нужен `"console": "integratedTerminal"` |
| Свойства объекта показываются «лениво» (значок глаза) | Вычисление заняло больше секунды; щёлкните по значению |
| Вычисление выражения «timed out» / «cancelled» | Вызванный код ждёт блокировку или другой поток: на время вычисления остальные потоки заморожены |
| Single-file приложение не отлаживается | Нужен установленный рантайм той же версии или runtime-пакет в кэше NuGet (оттуда берутся библиотеки отладки) |

Для отчёта об ошибке: **.NET Debugger: Collect Diagnostics** и **.NET Debugger: Open Adapter Logs** (включает трассу
протокола). Вне VS Code — `dotnet-debugger --log=<файл>`. В трассе есть пути, имена и значения переменных вашей
программы — просмотрите её перед публикацией.

## Поддерживаемые платформы

Проверено: Windows x64 и Linux x64, отлаживаемые приложения на .NET 8, 9 и 10 (обычные, self-contained, single-file,
ReadyToRun). В контейнере отладчику нужен `ptrace`: `--cap-add=SYS_PTRACE --security-opt seccomp=unconfined`.
Сборки для macOS, arm64 и Alpine выпускаются, но пока не проверены — сообщения о результатах приветствуются.
