# .NET Debugger for VS Code

VS Code front end of dotnet-debugger, a .NET debugger speaking the Debug Adapter Protocol.

## Starting a debug session

No `launch.json` is needed for the common cases:

| How | What happens |
| --- | --- |
| **F5** without a `launch.json` | builds and debugs the workspace's runnable project (asks if there are several) |
| Status bar **`▷ <project>`** or **Ctrl+Alt+F5** | builds and debugs the startup project (the last project you debugged) |
| ▶ menu in the editor title of a C#/F#/VB file | *Debug Project of Active File*, *Debug Project in Terminal* |
| Right click on a `.csproj` / `.fsproj` / `.vbproj` | *Debug Project*, *Debug Project in Terminal*, *Debug Project with Launch Profile...*, *Set as Startup Project* |
| Command palette, `.NET Debugger: Attach to Process...` | pick a process and attach |

*In Terminal* runs the application in the integrated terminal, which is required for `Console.ReadLine`/`Console.ReadKey`.
`Properties/launchSettings.json` is honoured: arguments, environment, working directory and `applicationUrl` of the
first profile with `"commandName": "Project"` (or the profile you pick) are applied.

## launch.json

*Run and Debug → create a launch.json file → .NET (dotnet-debugger)* generates one configuration per runnable project.

```jsonc
{
  "name": ".NET: Launch",
  "type": "dotnet-debugger",
  "request": "launch",
  "project": "${workspaceFolder}/src/App/App.csproj", // or "program": ".../App.dll"
  "build": true,                       // dotnet build before launching
  "configuration": "Debug",
  "args": [],                          // overrides the launch profile's commandLineArgs
  "env": { "NAME": "value" },          // on top of the launch profile
  "cwd": "${workspaceFolder}",
  "launchSettingsProfile": "https",    // "" ignores launchSettings.json
  "console": "internalConsole",        // integratedTerminal | externalTerminal
  "stopAtEntry": false,
  "justMyCode": true,
  "enableStepFiltering": true,         // step in skips properties and operators
  "allowImplicitFuncEval": true,       // false: never run ToString()/getters just to show a value
  "sourceFileMap": { "/_/": "${workspaceFolder}" }   // paths in the symbols -> local directories
}
```

```jsonc
{ "name": ".NET: Attach", "type": "dotnet-debugger", "request": "attach", "processId": "${command:pickProcess}" }
```

## While debugging

- Right click a thread in the Call Stack: *Freeze Thread* / *Thaw Thread*
- *Restart* (Ctrl+Shift+F5) starts a fresh process in the same session; breakpoints stay
- Exception filters accept type conditions: `System.IO.*`, `!System.OperationCanceledException`
- *Jump to Cursor* (set next statement) works within the current method
- Watch/Debug Console: assignments, `new`, `typeof`, format specifiers (`x,h`, `s,nq`, `obj,raw`), `$exception`, `$ReturnValue`
- `.NET Debugger: Open Adapter Logs` turns tracing on and opens the log folder (attach the log to bug reports)

## Settings

- `dotnet-debugger.adapterPath` — use a specific `dotnet-debugger` executable (or `.dll`) instead of the bundled one
- `dotnet-debugger.trace` — write a protocol trace into the extension's log folder

## Building

```
../build/publish.ps1          # publishes the adapter into ../artifacts/publish/<rid>
npm install
npm run package [-- <rid>]    # compiles, bundles the adapter, creates dotnet-debugger-<platform>-<version>.vsix
npm test                      # integration tests inside a downloaded VS Code (needs ../tests/TestApp built)
```

Install the result with `code --install-extension dotnet-debugger-win32-x64-0.1.0.vsix`.
