import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { spawn } from 'child_process';
import { findProjectAbove, readLaunchProfiles } from '../../launcher';
import { listProcesses } from '../../processPicker';

const repoRoot = path.resolve(__dirname, '..', '..', '..', '..');
const testApp = path.join(repoRoot, 'tests', 'TestApp');
const configuration = process.env.DOTNET_DEBUGGER_TEST_CONFIGURATION ?? 'Debug';
const testAppDll = path.join(testApp, 'bin', configuration, 'net9.0', 'TestApp.dll');

function adapterPath(): string {
    if (process.env.DOTNET_DEBUGGER_ADAPTER) {
        return process.env.DOTNET_DEBUGGER_ADAPTER;
    }
    return path.join(repoRoot, 'src', 'DotnetDebugger.Adapter', 'bin', configuration, 'net9.0', 'dotnet-debugger.dll');
}

/** 1-based line of the statement tagged with "// bp:<marker>" in Program.cs. */
function lineOf(marker: string): number {
    const lines = fs.readFileSync(path.join(testApp, 'Program.cs'), 'utf8').split(/\r?\n/);
    const index = lines.findIndex(l => l.trimEnd().endsWith('// bp:' + marker));
    assert.ok(index >= 0, `marker ${marker} not found`);
    return index + 1;
}

interface DapEvent { type: string; event: string; body: any }

/** Records what the adapter sends, per session, so tests can wait for protocol events. */
class Recorder implements vscode.DebugAdapterTrackerFactory {
    readonly events: DapEvent[] = [];
    private waiters: (() => void)[] = [];

    createDebugAdapterTracker(): vscode.DebugAdapterTracker {
        return {
            onDidSendMessage: (message: DapEvent) => {
                if (message.type === 'event') {
                    this.events.push(message);
                    this.waiters.splice(0).forEach(w => w());
                }
            },
        };
    }

    async waitFor(event: string, predicate: (body: any) => boolean = () => true, timeoutMs = 60_000): Promise<any> {
        const deadline = Date.now() + timeoutMs;
        for (;;) {
            const index = this.events.findIndex(e => e.event === event && predicate(e.body));
            if (index >= 0) {
                return this.events.splice(index, 1)[0].body;
            }
            if (Date.now() > deadline) {
                throw new Error(`Timed out waiting for '${event}'. Seen: ${this.events.map(e => e.event).join(', ')}`);
            }
            await new Promise<void>(resolve => {
                const timer = setTimeout(resolve, 500);
                this.waiters.push(() => { clearTimeout(timer); resolve(); });
            });
        }
    }

    output(category: string): string {
        return this.events.filter(e => e.event === 'output' && e.body.category === category).map(e => e.body.output).join('');
    }
}

suite('dotnet-debugger in VS Code', () => {
    let recorder: Recorder;
    let registration: vscode.Disposable;

    suiteSetup(async () => {
        await vscode.workspace.getConfiguration('dotnet-debugger').update('adapterPath', adapterPath(), vscode.ConfigurationTarget.Global);
    });

    setup(() => {
        recorder = new Recorder();
        registration = vscode.debug.registerDebugAdapterTrackerFactory('dotnet-debugger', recorder);
        vscode.debug.removeBreakpoints(vscode.debug.breakpoints);
    });

    teardown(async () => {
        await vscode.debug.stopDebugging();
        registration.dispose();
    });

    function addBreakpoint(marker: string): void {
        const location = new vscode.Location(vscode.Uri.file(path.join(testApp, 'Program.cs')), new vscode.Position(lineOf(marker) - 1, 0));
        vscode.debug.addBreakpoints([new vscode.SourceBreakpoint(location)]);
    }

    test('launches a dll, stops at a breakpoint, evaluates and continues', async () => {
        addBreakpoint('add');
        const started = await vscode.debug.startDebugging(vscode.workspace.workspaceFolders![0], {
            name: 'test', type: 'dotnet-debugger', request: 'launch', program: testAppDll, args: ['basic'],
        });
        assert.ok(started, 'startDebugging returned false');

        const stopped = await recorder.waitFor('stopped');
        assert.strictEqual(stopped.reason, 'breakpoint');
        const session = vscode.debug.activeDebugSession!;
        const stack = await session.customRequest('stackTrace', { threadId: stopped.threadId });
        assert.strictEqual(stack.stackFrames[0].name, 'TestApp.Program.Add()');
        assert.strictEqual(stack.stackFrames[0].line, lineOf('add'));

        const sum = await session.customRequest('evaluate', { expression: 'a + b', frameId: stack.stackFrames[0].id, context: 'watch' });
        assert.strictEqual(sum.result, '52');

        await session.customRequest('continue', { threadId: stopped.threadId });
        const exited = await recorder.waitFor('exited');
        assert.strictEqual(exited.exitCode, 3);
        assert.ok(recorder.output('stdout').includes('mode: basic'));
    });

    test('launches a project (MSBuild output lookup + launchSettings.json)', async () => {
        const started = await vscode.debug.startDebugging(vscode.workspace.workspaceFolders![0], {
            name: 'test', type: 'dotnet-debugger', request: 'launch', project: '${workspaceFolder}', configuration,
            launchSettingsProfile: 'TestProfile',
        });
        assert.ok(started);
        await recorder.waitFor('exited');
        assert.ok(recorder.output('stdout').includes('arg: fromProfile; env: profile-env;'), recorder.output('stdout'));
    });

    test('runs in the integrated terminal and reads what is typed there', async () => {
        addBreakpoint('stdinEcho');
        const terminalsBefore = new Set(vscode.window.terminals);
        const started = await vscode.debug.startDebugging(vscode.workspace.workspaceFolders![0], {
            name: 'test', type: 'dotnet-debugger', request: 'launch', program: testAppDll, args: ['stdin'], console: 'integratedTerminal',
        });
        assert.ok(started);

        await recorder.waitFor('process');
        const terminal = vscode.window.terminals.find(t => !terminalsBefore.has(t));
        assert.ok(terminal, 'no terminal was opened for the debuggee');
        // give the debuggee a moment to reach Console.ReadLine, then type
        await new Promise(resolve => setTimeout(resolve, 3000));
        terminal.sendText('typed in vscode');

        const stopped = await recorder.waitFor('stopped');
        const session = vscode.debug.activeDebugSession!;
        const stack = await session.customRequest('stackTrace', { threadId: stopped.threadId });
        const line = await session.customRequest('evaluate', { expression: 'line', frameId: stack.stackFrames[0].id, context: 'watch' });
        assert.strictEqual(line.result, '"typed in vscode"');

        await session.customRequest('continue', { threadId: stopped.threadId });
        await recorder.waitFor('exited');
        assert.ok(!recorder.output('stdout').includes('echo:'), 'terminal output must not come through the debug console');
    });

    test('F5 without launch.json resolves the workspace project', async () => {
        // an empty configuration is what VS Code passes when there is no launch.json
        const started = await vscode.debug.startDebugging(vscode.workspace.workspaceFolders![0], {} as vscode.DebugConfiguration);
        assert.ok(started);
        const exited = await recorder.waitFor('exited', () => true, 180_000);
        assert.strictEqual(exited.exitCode, 3);
        assert.ok(recorder.output('console').includes('Building TestApp.csproj'), recorder.output('console'));
    });

    test('command: debug a project file (explorer context menu)', async () => {
        const project = vscode.Uri.file(path.join(testApp, 'TestApp.csproj'));
        assert.ok(await vscode.commands.executeCommand('dotnet-debugger.debugProject', project));
        await recorder.waitFor('exited', () => true, 180_000);
        // no args given: the default launch profile of the project applies
        assert.ok(recorder.output('stdout').includes('arg: from default profile; env: default-env;'), recorder.output('stdout'));
    });

    test('command: debug with a named launch profile', async () => {
        const project = vscode.Uri.file(path.join(testApp, 'TestApp.csproj'));
        assert.deepStrictEqual(readLaunchProfiles(project.fsPath), ['Default', 'TestProfile']);
        assert.ok(await vscode.commands.executeCommand('dotnet-debugger.debugWithProfile', project, 'TestProfile'));
        await recorder.waitFor('exited', () => true, 180_000);
        assert.ok(recorder.output('stdout').includes('arg: fromProfile; env: profile-env;'), recorder.output('stdout'));
    });

    test('command: debug the project of the active file, then the startup project', async () => {
        const document = await vscode.workspace.openTextDocument(path.join(testApp, 'Models.cs'));
        await vscode.window.showTextDocument(document);
        assert.strictEqual(findProjectAbove(document.uri.fsPath), path.join(testApp, 'TestApp.csproj'));

        assert.ok(await vscode.commands.executeCommand('dotnet-debugger.debugActiveFile'));
        await recorder.waitFor('exited', () => true, 180_000);

        // the project just debugged became the startup project (status bar / Ctrl+Alt+F5)
        assert.ok(await vscode.commands.executeCommand('dotnet-debugger.debugStartupProject'));
        await recorder.waitFor('exited', () => true, 180_000);
    });

    test('command: attach to a process by id', async () => {
        const debuggee = spawn('dotnet', [testAppDll, 'wait'], { stdio: 'ignore' });
        try {
            await new Promise(resolve => setTimeout(resolve, 2000));
            addBreakpoint('loop');
            assert.ok(await vscode.commands.executeCommand('dotnet-debugger.attachToProcess', debuggee.pid));
            const started = await recorder.waitFor('process');
            assert.strictEqual(started.startMethod, 'attach');
            const stopped = await recorder.waitFor('stopped');
            assert.strictEqual(stopped.reason, 'breakpoint');
        } finally {
            await vscode.debug.stopDebugging();
            debuggee.kill();
        }
    });

    test('restart keeps the session and the breakpoints', async () => {
        addBreakpoint('add');
        assert.ok(await vscode.debug.startDebugging(vscode.workspace.workspaceFolders![0], {
            name: 'test', type: 'dotnet-debugger', request: 'launch', program: testAppDll, args: ['basic'],
        }));
        const first = await recorder.waitFor('process');
        await recorder.waitFor('stopped');

        await vscode.commands.executeCommand('workbench.action.debug.restart');
        const second = await recorder.waitFor('process', body => body.systemProcessId !== first.systemProcessId);
        assert.notStrictEqual(second.systemProcessId, first.systemProcessId);
        const stopped = await recorder.waitFor('stopped');
        assert.strictEqual(stopped.reason, 'breakpoint');
    });

    test('command: freeze a thread from the call stack', async () => {
        addBreakpoint('add');
        assert.ok(await vscode.debug.startDebugging(vscode.workspace.workspaceFolders![0], {
            name: 'test', type: 'dotnet-debugger', request: 'launch', program: testAppDll, args: ['basic'],
        }));
        const stopped = await recorder.waitFor('stopped');
        const session = vscode.debug.activeDebugSession!;

        await vscode.commands.executeCommand('dotnet-debugger.freezeThread', { sessionId: session.id, threadId: stopped.threadId });
        let threads = await session.customRequest('threads');
        assert.ok(threads.threads.some((t: any) => t.id === stopped.threadId && t.name.endsWith('(frozen)')));
        await vscode.commands.executeCommand('dotnet-debugger.thawThread', { sessionId: session.id, threadId: stopped.threadId });
        threads = await session.customRequest('threads');
        assert.ok(!threads.threads.some((t: any) => t.name.endsWith('(frozen)')));
    });

    test('command: collect diagnostics', async () => {
        await vscode.commands.executeCommand('dotnet-debugger.collectDiagnostics');
        const text = vscode.window.activeTextEditor!.document.getText();
        assert.match(text, /adapter version: \d+\.\d+/);
        assert.ok(text.includes('dotnet --info'));
        await vscode.commands.executeCommand('workbench.action.closeActiveEditor');
    });

    test('process picker lists processes', async () => {
        const processes = await listProcesses();
        assert.ok(processes.length > 5);
        assert.ok(processes.every(p => p.pid > 0 && typeof p.name === 'string'));
    });
});
