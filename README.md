# dotnet-debugger

Отладчик для .NET (CoreCLR) с поддержкой [Debug Adapter Protocol](https://microsoft.github.io/debug-adapter-protocol/).
Написан на C# поверх ICorDebug (через [ClrDebug](https://github.com/lordmilko/ClrDebug)) и `dbgshim`.

[![CI](https://github.com/dvislobokov/dotnet-debugger/actions/workflows/ci.yml/badge.svg)](https://github.com/dvislobokov/dotnet-debugger/actions/workflows/ci.yml)
Лицензия: [MIT](LICENSE) · [Руководство пользователя](docs/usage.md) · [План развития](ROADMAP.md) · [Изменения](CHANGELOG.md)

## Установка

```
dotnet tool install -g dotnet-debugger-dap      # команда: dotnet-debugger
```

Для VS Code — расширение из `vscode/` (platform-specific `.vsix` в релизах). Подробности, `launch.json`, Neovim и
разбор типичных проблем — в [docs/usage.md](docs/usage.md).

## Структура

| Проект | Назначение |
| --- | --- |
| `src/DotnetDebugger.Protocol` | Типы DAP и транспорт (`Content-Length`-фрейминг) |
| `src/DotnetDebugger.Engine` | Движок: запуск/attach, брейкпоинты, степпинг, стек, переменные, исключения. Ничего не знает о DAP |
| `src/DotnetDebugger.Adapter` | Исполняемый файл: связывает DAP-запросы с движком |
| `vscode` | Расширение VS Code (TypeScript): команды запуска, выбор процесса, генерация `launch.json`; свои интеграционные тесты в настоящем VS Code |
| `build` | Скрипты публикации адаптера (`publish.ps1`, `publish.sh`) |
| `tests/TestApp` | Отлаживаемое приложение для интеграционных тестов |
| `tests/DotnetDebugger.Tests` | Интеграционные тесты (реальный адаптер + реальный debuggee через DAP) и юнит-тесты протокола |

Метаданные и portable PDB (включая embedded) читаются с диска через `System.Reflection.Metadata`.

## Сборка и тесты

```
dotnet build
dotnet test
```

Трассу протокола во время тестов можно включить переменной `DOTNET_DEBUGGER_TEST_LOG=<файл>`.

## Публикация и релизы

```
./build/publish.ps1 [-Rid win-x64,win-arm64] [-SelfContained]     # -> artifacts/publish/<rid>/dotnet-debugger(.exe)
dotnet pack src/DotnetDebugger.Adapter -c Release                 # -> artifacts/packages/dotnet-debugger-dap.<версия>.nupkg
./build/set-version.ps1 -Version 0.2.0                            # одна версия для адаптера, тулы и расширения
```

Релиз делает тег: `git tag v0.2.0 && git push --tags` запускает `.github/workflows/release.yml`, который собирает
архивы адаптера для семи RID, platform-specific `.vsix`, NuGet-пакет тулы и создаёт GitHub Release. Публикация в
Marketplace и Open VSX включается наличием секретов `VSCE_PAT`, `OVSX_PAT`. В NuGet.org пакет уходит через
[Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) — без долгоживущих ключей: на
nuget.org заводится политика (владелец `dvislobokov`, репозиторий `dotnet-debugger`, workflow `release.yml`), а в
переменных репозитория — `NUGET_USER` с именем пользователя nuget.org. Запасной путь — секрет `NUGET_API_KEY`.

Всё собирается под .NET 8 (`DebuggerTargetFramework` в `Directory.Build.props`) с `RollForward=Major`: адаптер работает
на любом установленном рантайме начиная с 8. Архивы релиза и `.vsix` — self-contained, им .NET не нужен вовсе.

CI (`.github/workflows/ci.yml`) гоняет полный набор тестов, матрицу рантаймов, установленную тулу и тесты расширения
на Windows, Linux и macOS. macOS пока помечен `experimental` (его падение не ломает сборку): там код ещё не проверялся.
Linux можно прогнать локально в Docker: `./build/test-in-docker.ps1 [-Matrix] [-Stress] [-Filter ...]`.

Тесты можно направить на опубликованный адаптер: `DOTNET_DEBUGGER_ADAPTER=<путь к dotnet-debugger.exe или .dll> dotnet test`.

## VS Code

```
./build/publish.ps1
cd vscode && npm install && npm run package
code --install-extension dotnet-debugger-0.1.0.vsix
```

После этого: **F5** без `launch.json`, кнопка в статус-баре / **Ctrl+Alt+F5** (стартовый проект), меню ▶ в заголовке
редактора, правый клик по `.csproj` (*Debug Project*, *in Terminal*, *with Launch Profile...*), команда
*.NET Debugger: Attach to Process...*. Подробности и схема `launch.json` — в [vscode/README.md](vscode/README.md).

## Запуск

```
dotnet-debugger [--server[=PORT]] [--log=FILE]
```

По умолчанию DAP идёт через stdin/stdout; `--server` — один сеанс по TCP (порт 4711 по умолчанию).
Без публикации: `dotnet src/DotnetDebugger.Adapter/bin/Debug/net9.0/dotnet-debugger.dll`.

### Параметры `launch`

| Поле | Описание |
| --- | --- |
| `program` | Путь к `.dll` (запускается через `dotnet`) или к apphost-`.exe` |
| `project` | Альтернатива `program`: файл проекта или каталог. Выходная сборка определяется через MSBuild (`TargetPath`) |
| `build`, `configuration` | Выполнить `dotnet build` перед запуском (вывод сборки идёт в `output`); конфигурация сборки |
| `args`, `cwd`, `env` | Аргументы, рабочий каталог, переменные окружения (`null` удаляет переменную) |
| `launchSettingsProfile`, `launchSettingsFilePath` | Профиль `Properties/launchSettings.json`. Не задан — первый профиль с `commandName: Project`; `""` — не использовать. Из профиля берутся `commandLineArgs`, `environmentVariables`, `workingDirectory`, `applicationUrl` (→ `ASPNETCORE_URLS`); значения из `launch` имеют приоритет |
| `console` | `internalConsole` (по умолчанию), `integratedTerminal`, `externalTerminal`. В терминале у приложения настоящий stdin (`Console.ReadLine`); реализовано через `runInTerminal` и вспомогательный режим `dotnet-debugger --run-in-terminal` |
| `symbolOptions` | `{ searchPaths: [каталоги и http(s)-серверы], cachePath, searchMicrosoftSymbolServer, searchNuGetOrgSymbolServer }` — где искать PDB, которых нет рядом с модулем. Серверы опрашиваются при `justMyCode: false` или по запросу `dotnet/loadSymbols { moduleId }` |
| `sourceFileMap` | Отображение путей из PDB в локальные каталоги |
| `suppressJitOptimizations` | Запуск без предкомпилированного (ReadyToRun) кода. По умолчанию включается сам, если программа опубликована с ReadyToRun |
| `stopAtEntry` | Остановиться на первой строке `Main` (в т.ч. `async Main`) |
| `justMyCode` | По умолчанию `true`: код без PDB пропускается при степпинге и сворачивается в `[External Code]` |
| `allowImplicitFuncEval` | По умолчанию `true`: значения описываются запуском кода программы (`ToString()`, `[DebuggerDisplay]`, геттеры свойств, type proxy). `false` — только тип и поля; свойства вычисляются по запросу (lazy), выражения в `evaluate` работают как обычно |

`attach` принимает `processId` (а также `justMyCode`, `sourceFileMap`, `allowImplicitFuncEval`). До подключения проверяется, что процесс существует, что в нём
загружен .NET и что его ещё никто не отлаживает: второй отладчик получает отказ, а процесс остаётся жив.

### Neovim (nvim-dap)

```lua
dap.adapters.coreclr = {
  type = 'executable',
  command = 'dotnet',
  args = { '<repo>/src/DotnetDebugger.Adapter/bin/Debug/net9.0/dotnet-debugger.dll' },
}
```

## Что реализовано

Всё перечисленное покрыто интеграционными тестами (`tests/DotnetDebugger.Tests`, сценарии — в `tests/TestApp`).

**Сеанс**
- `launch` (stdout/stderr отлаживаемого процесса → события `output`), `attach`, `disconnect` (terminate/detach), `terminate`
- запуск по проекту (`project` + `build`), `launchSettings.json`, запуск в терминале клиента (`runInTerminal`) с настоящим stdin
- `restart` (новый процесс в той же сессии, брейкпоинты сохраняются), `dotnet/info`
- исходники: `sourceFileMap` (пути из PDB ↔ локальные), встроенные в PDB исходники через запрос `source`, предупреждение, если файл отличается от скомпилированного
- запросы `modules`, `loadedSources`, `cancel`; `pause`/`terminate`/`disconnect` не ждут очереди запросов
- события `process`, `thread`, `module`, `exited`, `terminated`; `Debugger.Break()`, `Debug.WriteLine` → `output`

**Брейкпоинты**
- по строкам, с отложенной привязкой при загрузке модуля и установкой «на лету»
- по колонке — так ставится брейкпоинт внутрь однострочной лямбды (`x => x * factor`)
- фильтры исключений `all`, `user-unhandled`, `unhandled`, с условиями по типам (`System.IO.*`, `!System.OperationCanceledException`)
- условные (`condition`), по числу срабатываний (`hitCondition`: `5`, `>=5`, `%5`, ...), logpoints (`logMessage` с `{выражениями}`)
- на функцию (`setFunctionBreakpoints`): `Method`, `Type.Method`, `Namespace.Type.Method`
- внутри лямбд, LINQ-предикатов, локальных функций, итераторов, async-методов, generic-методов

**Выполнение**
- `continue`, `pause`, `next`, `stepIn`, `stepOut` по диапазонам sequence points, с Just My Code
- step over через `await` остаётся в async-методе (даже если продолжение на другом потоке)
- step in не проваливается в P/Invoke и в скомпилированные expression trees / динамические методы
- `[DebuggerHidden]`, `[DebuggerStepThrough]`, `[DebuggerNonUserCode]`; step in пропускает свойства и операторы (`enableStepFiltering`)
- step over через `await` следит за своим вызовом async-метода (а не за любым), step out из async-метода приводит в ожидающий его async-метод
- set next statement (`gotoTargets` / `goto`) в пределах текущего метода
- заморозка / разморозка потоков
- после step over — что вернули вызовы пройденной строки; после возврата из метода — его результат: `<Метод>() returned` в Locals и `$ReturnValue`

**Стек и переменные**
- логический async-стек: после физических кадров — «[Async Call Stack]» с методами, ожидающими текущий (с их переменными)
- имена кадров как в исходнике: `Program.Run.AnonymousMethod__0()`, `Program.Run.LocalFunction()`, `Box<string>.Describe<int>()`,
  async/iterator-методы вместо `<Foo>d__1.MoveNext`; не-пользовательский код сворачивается в `[External Code]`
- примитивы, строки, enum (включая flags), `Nullable<T>`, `decimal`, boxed-значения, массивы (в т.ч. многомерные, с пагинацией)
- объекты: поля базовых классов, **свойства (вычисляются через func-eval)**, узел `Static members`
- отображение значения: `[DebuggerDisplay]`, переопределённый `ToString()`, `[DebuggerBrowsable]`; без выполнения кода — `DateTime`, `TimeSpan`, `Guid`,
  `DateTimeOffset`, `KeyValuePair`, кортежи, исключения
- коллекции (`Count = N`, элементы, `Raw View`): `List`, `Dictionary`, `HashSet`, `Queue`, `Stack`, `SortedList`, `ImmutableArray`, `ReadOnlyCollection`, `ArraySegment`
- на неявные вычисления (ToString, свойства) — бюджет 1 с на запрос; что не успело, приходит ленивыми переменными (вычисляются по клику)
- псевдопеременные `$exception` и `$ReturnValue`
- захваченные переменные замыканий и локальные переменные async/iterator-методов под исходными именами
- `[DebuggerTypeProxy]` (в т.ч. прокси из BCL), `Memory<T>`, `ConcurrentDictionary`, `Span<T>` / `ReadOnlySpan<T>` (с элементами и `span[i]` в выражениях), «Results View» для любых `IEnumerable`
- `setVariable` / `setExpression`: локальные переменные, поля, элементы массивов, свойства (через сеттер), enum-ы; значение — любое выражение
- `evaluateName` у переменных (работает «Add to Watch»)

**Выражения** (`evaluate`: watch, hover, repl; условия брейкпоинтов; logpoints; `setVariable`)
- литералы, арифметика, сравнения, логика, `?:`, `??`, `?.`, приведения, `is` / `as` (включая интерфейсы)
- присваивания (`=`, `+=`, `++`, ...), `new T(...) { ... }`, `new T[n]`, `new[] { ... }`, `typeof`, `default`, `nameof`, `sizeof`, `$"интерполяция {x,5:F1}"`
- методы расширения, включая LINQ без лямбд: `numbers.Sum()`, `list.Distinct().Count()`, `map.First().Value`, свои расширения
- generic-имена типов (`List<int>`, `Comparer<int>.Default`), явные аргументы типа у методов (`Array.Empty<string>()`), явные реализации интерфейсов
- локальные переменные, `this`, поля, свойства, статические члены, константы, enum-ы, `$exception`
- индексаторы массивов (в т.ч. многомерных), строк, `List<T>`, `Dictionary<K,V>` и любых типов с `get_Item`
- вызовы методов с разрешением перегрузок, в т.ч. статических (`Math.Max(3, x)`), виртуальных и generic (вывод типов по аргументам)
- спецификаторы формата: `x,h` (hex), `x,d`, `s,nq` (без кавычек), `obj,raw` (без визуализаторов); `format.hex` из DAP
- в контексте `hover` вызовы методов запрещены (без побочных эффектов)
- func-eval с таймаутом 5 с и прерыванием; остальные потоки на время вычисления заморожены; запрос `cancel` прерывает вычисление
  (код, заснувший внутри рантайма, прервать невозможно — такое вычисление бросается, отладчик остаётся рабочим)

**Исключения**
- фильтры `all` и `unhandled`; `exceptionInfo` с типом, сообщением, stack trace и цепочкой inner exceptions

## Ограничения и дальнейшие этапы

План развития по вехам — в [ROADMAP.md](ROADMAP.md); что и как уже сделано (волны 1–3) — в [docs/roadmap-done.md](docs/roadmap-done.md).

- Только управляемый код: нативные кадры и шаг внутрь P/Invoke (mixed-mode) не поддерживаются
- Выражения: нет лямбд (а значит `Where(x => ...)`), `is`-паттернов, `with`, `await`, динамических членов (`ExpandoObject`) —
  до компиляции выражений Roslyn (веха 4.1). Имена элементов кортежей известны только для локальных переменных (они есть
  лишь в PDB); `typeof(T)` в коде, общем для ссылочных типов, берётся из значения параметра типа `T`
- Вычисления, которые нельзя прервать. Неявные вычисления (`ToString()`, геттеры при раскрытии объекта) ждут 1 с, явные — 5 с,
  после чего прерываются; после первого неявного таймаута до конца остановки значения показываются без запуска кода. Код,
  заблокированный внутри рантайма (`lock`, `Wait`, `Sleep`), прервать нельзя: отладчик сообщает об этом один раз (`output`
  с категорией `important`), на этом потоке больше ничего не вычисляется, а после `continue` поток не вернётся в свой код,
  пока вычисляемый код не завершится сам
- .NET 8 и старше на Linux/macOS не умеют останавливать поток в цикле без вызовов и выделений памяти (`while (true) { }`):
  `pause` или таймаут вычисления в такой момент оставляют сессии только `terminate`. .NET 9+ это исправили
- Код выхода после необработанного исключения на Windows: отладочный конвейер рантайма сам завершает процесс с кодом 0,
  поэтому отладчик сообщает `0xE0434352` — то, чем тот же процесс завершился бы без отладчика
- Запрос `variables` без `start`/`count` возвращает не больше 10 000 элементов и узел-примечание `[...]`: большие
  коллекции клиент должен запрашивать страницами (`indexedVariables` ему сообщается)
- thread-static поля; «кто держит блокировку»
- SourceLink; stdin в режиме `internalConsole`
- Hot reload (Edit and Continue), data breakpoints
- Публикация расширения в Marketplace / Open VSX (нужны лицензия, иконка, репозиторий), platform-specific `.vsix`

## Статус платформ

| Платформа | Состояние |
| --- | --- |
| Windows x64 | проверено: .NET 8, 9, 10; обычные, self-contained, single-file и ReadyToRun приложения; опубликованный адаптер, .NET-тула, расширение в VS Code |
| Linux x64 | проверено в Docker (`./build/test-in-docker.ps1 -Matrix -Stress`): весь набор тестов, стресс-тесты и матрица рантаймов. Не проверены: расширение VS Code на Linux, musl (Alpine), arm64 |
| macOS | проверено локально (`bash build/test-on-macos.sh`, 2026-09-21): весь набор тестов зелёный. Нужен включённый режим разработчика (`DevToolsSecurity -enable`). Не проверены: матрица рантаймов и стресс-тесты, расширение VS Code; в CI задача выключена — полный прогон истощает hosted runner |
