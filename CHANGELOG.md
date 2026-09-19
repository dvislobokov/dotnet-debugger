# Changelog

All notable changes to this project are documented here. Versions follow [SemVer](https://semver.org); the adapter,
the .NET tool and the VS Code extension share one version number.

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
- VS Code extension with one-click launch commands; .NET tool package (`dotnet tool install -g dotnet-debugger-dap`).
