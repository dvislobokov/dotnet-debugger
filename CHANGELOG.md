# Changelog

All notable changes to this project are documented here. Versions follow [SemVer](https://semver.org); the adapter,
the .NET tool and the VS Code extension share one version number.

## Unreleased

Fixes for the findings of a black-box DAP probe of 0.1.0 (regression tests: `FindingsTests`).

- An unhandled exception always stops the debugger, whatever the exception filters; on Windows the exit code reported
  afterwards is `0xE0434352` instead of 0.
- Windows: program output that is not ASCII arrives intact (the debuggee's console is switched to UTF-8).
- `stepIn` enters `async` methods; `next` past the end of an `async` method stops in the awaiting caller; a step never
  stops in code without source (`justMyCode: false`); `stopAtEntry` works with an `async` top-level `Main`.
- Strings longer than 4096 characters are displayed (shortened; whole in the `clipboard` context).
- `attach` fails with a plain message for a process that does not exist, has no .NET runtime or is already being
  debugged, and a refused second debugger no longer kills the process. Error messages no longer quote `HRESULT`s.
- `variables` without a range returns at most 10 000 children; arrays and lists of primitives are read in one go
  (a million elements: a second instead of minutes and gigabytes); paging deep into a `Dictionary` is O(page);
  `cancel` and `disconnect` interrupt a long `variables` request.
- Implicit evaluations (`ToString()`, property getters) time out after 1 s and are not repeated during the same stop;
  new launch/attach option `allowImplicitFuncEval`. An evaluation that cannot be aborted is reported once (`output`,
  category `important`) and later evaluations on that thread answer in plain words.
- Malformed DAP input (broken JSON, a non-numeric `Content-Length`) no longer ends the session.
- Enums of assemblies without symbols are shown by name; `DebuggerDisplay` with `\{` (anonymous types);
  `$exception` can be evaluated in an `[External Code]` frame; invalid hit conditions and lines <= 0 are rejected with
  a message; a condition that is not `bool` is reported; a missing `cwd` fails the launch on every platform.
- Linux/macOS: the debuggee no longer outlives a killed adapter.
- Expressions: optional parameters are filled in, enum arguments (`perm.HasFlag(Perm.Write)`), delegates are invoked
  (`twice(4)`), tuple element names of locals (`tuple.Name`), `checked` / `unchecked`, pointer dereference (`*p`),
  type parameters of the current frame (`typeof(T)`, `default(T)`), `null` for `Nullable<T>` and whole structs in
  assignments and `setVariable`, the element count format specifier (`numbers,5`).

## 0.1.0 — unreleased

First public version.

- Debug Adapter Protocol server for .NET (CoreCLR) built on ICorDebug: launch, attach, breakpoints (line, column,
  conditional, hit count, logpoints, function), stepping (incl. async, step filters), call stack (incl. async call
  stack), variables (DebuggerDisplay/TypeProxy/Browsable, collections, spans), expression evaluation (func-eval,
  assignments, object creation, extension methods), exceptions with type conditions, set next statement, restart.
- Launching by project with `launchSettings.json`, running in the client's terminal, symbol servers, embedded sources,
  `sourceFileMap`.
- Verified on Windows x64 and Linux x64 with .NET 8, 9 and 10, including self-contained, single-file and ReadyToRun
  applications. macOS builds are produced but have not been verified yet.
- The adapter targets .NET 8 and rolls forward, so it starts on a machine that has nothing but .NET 8; release archives
  and the VS Code extension are self-contained single-file builds that need no installed .NET.
- VS Code extension with one-click launch commands; .NET tool package (`dotnet tool install -g dotnet-debugger-dap`).
