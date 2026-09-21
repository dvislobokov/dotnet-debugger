# dotnet-debugger

A debugger for .NET (CoreCLR) that speaks the [Debug Adapter Protocol](https://microsoft.github.io/debug-adapter-protocol/).
MIT licensed, written in C# on top of ICorDebug.

```
dotnet tool install -g dotnet-debugger-dap
dotnet-debugger --version
```

Runs on .NET 8 or newer (it rolls forward to whatever runtime is installed). The release archives and the VS Code
extension are self-contained and need no .NET at all.

The command talks DAP over stdin/stdout (or TCP with `--server[=PORT]`), which is what editors expect from a debug adapter.

## Neovim (nvim-dap)

```lua
local dap = require('dap')
dap.adapters.coreclr = { type = 'executable', command = 'dotnet-debugger' }
dap.configurations.cs = {
  {
    type = 'coreclr', request = 'launch', name = 'Launch project',
    project = '${workspaceFolder}', build = true,
  },
}
```

## Any other DAP client

Launch arguments (all optional except one of `program` / `project`):

| Field | Meaning |
| --- | --- |
| `program` | Path of the `.dll` or executable to debug |
| `project` | Project file or directory; the output assembly is found through MSBuild, `Properties/launchSettings.json` is applied |
| `build` | Run `dotnet build` first |
| `args`, `cwd`, `env` | Arguments, working directory, environment (`null` removes a variable) |
| `console` | `internalConsole` (default), `integratedTerminal`, `externalTerminal` |
| `stopAtEntry`, `justMyCode`, `enableStepFiltering`, `allowImplicitFuncEval` | As in other .NET debuggers |
| `symbolOptions`, `sourceFileMap` | Symbol servers and path mapping for code built elsewhere |

`attach` takes `processId`.

## VS Code

Use the *dotnet-debugger* extension, which bundles this adapter and adds one-click launch commands.

## Status

Verified on Windows x64 and Linux x64 with .NET 8, 9 and 10 (in containers the debugger needs
`--cap-add=SYS_PTRACE --security-opt seccomp=unconfined`). macOS, arm64 and Alpine are built but not verified yet.
Documentation, roadmap and issues: https://github.com/dvislobokov/dotnet-debugger
