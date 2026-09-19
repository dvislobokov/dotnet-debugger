"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.activate = activate;
exports.deactivate = deactivate;
const child_process_1 = require("child_process");
const fs = __importStar(require("fs"));
const path = __importStar(require("path"));
const vscode = __importStar(require("vscode"));
const launcher_1 = require("./launcher");
const processPicker_1 = require("./processPicker");
function activate(context) {
    context.subscriptions.push(vscode.commands.registerCommand('dotnet-debugger.pickProcess', processPicker_1.pickProcess), vscode.debug.registerDebugAdapterDescriptorFactory(launcher_1.DEBUG_TYPE, new AdapterFactory(context)), vscode.debug.registerDebugConfigurationProvider(launcher_1.DEBUG_TYPE, new ConfigurationProvider()));
    new launcher_1.Launcher(context);
    // call stack context menu of a thread: { sessionId, threadId }
    const threadRequest = (request) => async (item) => {
        const session = vscode.debug.activeDebugSession;
        if (!session || item?.threadId === undefined) {
            return;
        }
        await session.customRequest(request, { threadId: Number(item.threadId) });
        void vscode.window.setStatusBarMessage(request.endsWith('freezeThread') ? 'Thread frozen' : 'Thread thawed', 3000);
    };
    context.subscriptions.push(vscode.commands.registerCommand('dotnet-debugger.freezeThread', threadRequest('dotnet/freezeThread')), vscode.commands.registerCommand('dotnet-debugger.thawThread', threadRequest('dotnet/thawThread')), vscode.commands.registerCommand('dotnet-debugger.collectDiagnostics', () => collectDiagnostics(context)), vscode.commands.registerCommand('dotnet-debugger.openLogs', async () => {
        // a trace is what a bug report needs: turn it on and show where the files end up
        await vscode.workspace.getConfiguration(launcher_1.DEBUG_TYPE).update('trace', true, vscode.ConfigurationTarget.Global);
        fs.mkdirSync(context.logUri.fsPath, { recursive: true });
        await vscode.env.openExternal(context.logUri);
        void vscode.window.showInformationMessage('Tracing is on: every debug session now writes adapter-<time>.log into the opened folder.');
    }));
}
/** Everything a bug report needs, as a text document the user can review and paste. */
async function collectDiagnostics(context) {
    const lines = [];
    const settings = vscode.workspace.getConfiguration(launcher_1.DEBUG_TYPE);
    lines.push(`extension: ${context.extension.packageJSON.version}`);
    lines.push(`vscode: ${vscode.version} (${process.platform}-${process.arch})`);
    try {
        const adapter = findAdapter(context, settings.get('adapterPath') ?? '');
        lines.push(`adapter: ${adapter}`);
        const isDll = adapter.toLowerCase().endsWith('.dll');
        lines.push('adapter version: ' + (await capture(isDll ? 'dotnet' : adapter, isDll ? [adapter, '--version'] : ['--version'])).trim());
    }
    catch (error) {
        lines.push(`adapter: ${error instanceof Error ? error.message : String(error)}`);
    }
    const session = vscode.debug.activeDebugSession;
    if (session?.type === launcher_1.DEBUG_TYPE) {
        try {
            lines.push('session: ' + JSON.stringify(await session.customRequest('dotnet/info')));
        }
        catch {
            lines.push('session: active, but dotnet/info failed');
        }
    }
    lines.push(`trace: ${settings.get('trace') ? 'on, logs in ' + context.logUri.fsPath : 'off (".NET Debugger: Open Adapter Logs" turns it on)'}`);
    lines.push('', '--- dotnet --info ---', await capture('dotnet', ['--info']));
    const document = await vscode.workspace.openTextDocument({ content: lines.join('\n'), language: 'plaintext' });
    await vscode.window.showTextDocument(document);
}
function capture(file, args) {
    return new Promise(resolve => {
        (0, child_process_1.execFile)(file, args, { windowsHide: true, timeout: 20000 }, (error, stdout, stderr) => resolve(error ? `failed: ${error.message}` : stdout || stderr));
    });
}
function deactivate() {
    // nothing to clean up: the adapter process belongs to the debug session
}
class AdapterFactory {
    context;
    constructor(context) {
        this.context = context;
    }
    createDebugAdapterDescriptor() {
        const settings = vscode.workspace.getConfiguration(launcher_1.DEBUG_TYPE);
        const adapter = findAdapter(this.context, settings.get('adapterPath') ?? '');
        const args = [];
        if (settings.get('trace')) {
            fs.mkdirSync(this.context.logUri.fsPath, { recursive: true });
            args.push('--log=' + path.join(this.context.logUri.fsPath, `adapter-${Date.now()}.log`));
        }
        // a framework-dependent dll needs the dotnet host, an apphost runs by itself
        return adapter.toLowerCase().endsWith('.dll')
            ? new vscode.DebugAdapterExecutable('dotnet', [adapter, ...args])
            : new vscode.DebugAdapterExecutable(adapter, args);
    }
}
function findAdapter(context, configured) {
    if (configured) {
        if (!fs.existsSync(configured)) {
            throw new Error(`dotnet-debugger.adapterPath points to '${configured}', which does not exist.`);
        }
        return configured;
    }
    const executable = process.platform === 'win32' ? 'dotnet-debugger.exe' : 'dotnet-debugger';
    const candidates = [
        path.join(context.extensionPath, 'adapter', executable),
        path.join(context.extensionPath, 'adapter', 'dotnet-debugger.dll'),
    ];
    const found = candidates.find(fs.existsSync) ?? findOnPath(executable);
    if (!found) {
        throw new Error('The dotnet-debugger adapter is not bundled with this build of the extension. Install it with '
            + '"dotnet tool install -g dotnet-debugger-dap" or set "dotnet-debugger.adapterPath".');
    }
    return found;
}
/** A globally installed .NET tool (dotnet tool install -g dotnet-debugger-dap) serves as the adapter, too. */
function findOnPath(executable) {
    const home = process.env.USERPROFILE ?? process.env.HOME ?? '';
    const directories = [...(process.env.PATH ?? '').split(path.delimiter), path.join(home, '.dotnet', 'tools')];
    return directories.filter(d => d).map(d => path.join(d, executable)).find(fs.existsSync);
}
class ConfigurationProvider {
    /** Generates launch.json content: one launch configuration per project that produces an executable. */
    async provideDebugConfigurations(folder) {
        const projects = await (0, launcher_1.findRunnableProjects)(folder);
        const configurations = projects.map(project => ({
            name: `.NET: Launch ${path.basename(project, path.extname(project))}`,
            type: launcher_1.DEBUG_TYPE,
            request: 'launch',
            project: folder ? '${workspaceFolder}/' + path.relative(folder.uri.fsPath, project).replace(/\\/g, '/') : project,
            build: true,
        }));
        configurations.push({ name: '.NET: Attach', type: launcher_1.DEBUG_TYPE, request: 'attach', processId: '${command:pickProcess}' });
        return configurations;
    }
    /** F5 without a launch.json: debug the (only, or chosen) project of the workspace. */
    async resolveDebugConfiguration(folder, config) {
        if (config.type || config.request || config.name) {
            return config;
        }
        const projects = await (0, launcher_1.findRunnableProjects)(folder);
        if (projects.length === 0) {
            void vscode.window.showErrorMessage('No runnable .NET project found in the workspace. Add a launch.json with "program" or "project".');
            return undefined;
        }
        const project = projects.length === 1
            ? projects[0]
            : await vscode.window.showQuickPick(projects, { placeHolder: 'Select the project to debug' });
        if (!project) {
            return undefined;
        }
        return { name: `.NET: Launch ${path.basename(project)}`, type: launcher_1.DEBUG_TYPE, request: 'launch', project, build: true };
    }
    resolveDebugConfigurationWithSubstitutedVariables(_folder, config) {
        if (config.request === 'launch' && !config.program && !config.project) {
            void vscode.window.showErrorMessage('A launch configuration needs either "project" or "program".');
            return undefined;
        }
        if (config.request === 'attach' && !config.processId) {
            return undefined; // the process picker was cancelled
        }
        return config;
    }
}
//# sourceMappingURL=extension.js.map