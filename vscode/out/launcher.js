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
exports.Launcher = exports.DEBUG_TYPE = void 0;
exports.findRunnableProjects = findRunnableProjects;
exports.findProjectAbove = findProjectAbove;
exports.readLaunchProfiles = readLaunchProfiles;
const fs = __importStar(require("fs"));
const path = __importStar(require("path"));
const vscode = __importStar(require("vscode"));
const processPicker_1 = require("./processPicker");
exports.DEBUG_TYPE = 'dotnet-debugger';
const PROJECT_GLOB = '**/*.{csproj,fsproj,vbproj}';
const PROJECT_EXTENSIONS = ['.csproj', '.fsproj', '.vbproj'];
const STARTUP_PROJECT_KEY = 'startupProject';
/**
 * One-click ways to start debugging without writing a launch.json: commands (palette, explorer context menu of
 * project files, editor run button) and a status bar item for the startup project.
 */
class Launcher {
    context;
    statusBar;
    constructor(context) {
        this.context = context;
        this.statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 10);
        this.statusBar.command = 'dotnet-debugger.debugStartupProject';
        context.subscriptions.push(this.statusBar, vscode.commands.registerCommand('dotnet-debugger.debugProject', (uri) => this.debugProject(uri, {})), vscode.commands.registerCommand('dotnet-debugger.debugProjectInTerminal', (uri) => this.debugProject(uri, { console: 'integratedTerminal' })), vscode.commands.registerCommand('dotnet-debugger.debugWithProfile', (uri, profile) => this.debugWithProfile(uri, profile)), vscode.commands.registerCommand('dotnet-debugger.debugActiveFile', () => this.debugActiveFile()), vscode.commands.registerCommand('dotnet-debugger.debugStartupProject', () => this.debugProject(this.startupProjectUri(), {})), vscode.commands.registerCommand('dotnet-debugger.selectStartupProject', (uri) => this.selectStartupProject(uri)), vscode.commands.registerCommand('dotnet-debugger.attachToProcess', (processId) => this.attach(processId)));
        void this.refreshStatusBar();
    }
    // ---------------------------------------------------------------- commands
    async debugProject(uri, extra) {
        const project = await this.resolveProject(uri);
        if (!project) {
            return false;
        }
        await this.setStartupProject(project);
        return vscode.debug.startDebugging(vscode.workspace.getWorkspaceFolder(vscode.Uri.file(project)), {
            name: `.NET: ${path.basename(project, path.extname(project))}`,
            type: exports.DEBUG_TYPE,
            request: 'launch',
            project,
            build: true,
            ...extra,
        });
    }
    async debugWithProfile(uri, profile) {
        const project = await this.resolveProject(uri);
        if (!project) {
            return false;
        }
        if (profile === undefined) {
            const profiles = readLaunchProfiles(project);
            if (profiles.length === 0) {
                void vscode.window.showWarningMessage(`${path.basename(project)} has no "Project" profiles in Properties/launchSettings.json.`);
                return false;
            }
            profile = await vscode.window.showQuickPick(profiles, { placeHolder: 'Select the launch profile' });
            if (profile === undefined) {
                return false;
            }
        }
        return this.debugProject(vscode.Uri.file(project), { launchSettingsProfile: profile });
    }
    /** Debugs the project that the file in the active editor belongs to (the nearest project file up the tree). */
    async debugActiveFile() {
        const file = vscode.window.activeTextEditor?.document.uri;
        const project = file?.scheme === 'file' ? findProjectAbove(file.fsPath) : undefined;
        if (!project) {
            void vscode.window.showWarningMessage('The active file does not belong to a .NET project.');
            return false;
        }
        return this.debugProject(vscode.Uri.file(project), {});
    }
    async attach(processId) {
        processId ??= await (0, processPicker_1.pickProcess)();
        if (processId === undefined) {
            return false;
        }
        return vscode.debug.startDebugging(undefined, { name: `.NET: Attach to ${processId}`, type: exports.DEBUG_TYPE, request: 'attach', processId });
    }
    async selectStartupProject(uri) {
        const project = uri ? uri.fsPath : await pickProject(await findRunnableProjects(undefined));
        if (project) {
            await this.setStartupProject(project);
        }
    }
    // ---------------------------------------------------------------- startup project
    startupProjectUri() {
        const stored = this.context.workspaceState.get(STARTUP_PROJECT_KEY);
        return stored && fs.existsSync(stored) ? vscode.Uri.file(stored) : undefined;
    }
    async setStartupProject(project) {
        await this.context.workspaceState.update(STARTUP_PROJECT_KEY, project);
        await this.refreshStatusBar();
    }
    async refreshStatusBar() {
        let project = this.startupProjectUri()?.fsPath;
        if (!project) {
            const projects = await findRunnableProjects(undefined);
            if (projects.length === 0) {
                this.statusBar.hide();
                return;
            }
            project = projects.length === 1 ? projects[0] : undefined;
        }
        this.statusBar.text = `$(debug-alt) ${project ? path.basename(project, path.extname(project)) : 'Debug .NET project...'}`;
        this.statusBar.tooltip = project ? `Build and debug ${project}` : 'Select a .NET project to build and debug';
        this.statusBar.show();
    }
    /** The project for a command: the clicked project file, the project owning the clicked file, or a choice. */
    async resolveProject(uri) {
        if (uri?.scheme === 'file') {
            if (PROJECT_EXTENSIONS.includes(path.extname(uri.fsPath).toLowerCase())) {
                return uri.fsPath;
            }
            const owner = findProjectAbove(uri.fsPath);
            if (owner) {
                return owner;
            }
        }
        const projects = await findRunnableProjects(undefined);
        if (projects.length === 0) {
            void vscode.window.showErrorMessage('No runnable .NET project found in the workspace.');
            return undefined;
        }
        return projects.length === 1 ? projects[0] : pickProject(projects, this.startupProjectUri()?.fsPath);
    }
}
exports.Launcher = Launcher;
async function pickProject(projects, preferred) {
    const items = projects
        .map(p => ({ label: path.basename(p, path.extname(p)), description: vscode.workspace.asRelativePath(p), project: p }))
        .sort((a, b) => Number(b.project === preferred) - Number(a.project === preferred));
    return (await vscode.window.showQuickPick(items, { placeHolder: 'Select the project to debug' }))?.project;
}
async function findRunnableProjects(folder) {
    const pattern = folder ? new vscode.RelativePattern(folder, PROJECT_GLOB) : PROJECT_GLOB;
    const files = await vscode.workspace.findFiles(pattern, '**/{bin,obj,node_modules}/**', 200);
    return files.map(f => f.fsPath).filter(isRunnableProject).sort();
}
// Cheap textual check that avoids an MSBuild evaluation per project: libraries and test projects are not launchable.
function isRunnableProject(projectFile) {
    try {
        const text = fs.readFileSync(projectFile, 'utf8');
        return /<OutputType>\s*(Exe|WinExe)\s*<\/OutputType>/i.test(text) || /Sdk="Microsoft\.NET\.Sdk\.(Web|Worker|BlazorWebAssembly)"/i.test(text);
    }
    catch {
        return false;
    }
}
function findProjectAbove(file) {
    const roots = (vscode.workspace.workspaceFolders ?? []).map(f => f.uri.fsPath.toLowerCase());
    for (let dir = path.dirname(file);; dir = path.dirname(dir)) {
        let entries = [];
        try {
            entries = fs.readdirSync(dir);
        }
        catch {
            // unreadable directory: keep walking up
        }
        const project = entries.find(e => PROJECT_EXTENSIONS.includes(path.extname(e).toLowerCase()));
        if (project) {
            return path.join(dir, project);
        }
        if (roots.includes(dir.toLowerCase()) || path.dirname(dir) === dir) {
            return undefined;
        }
    }
}
/** Names of the profiles `dotnet run` would accept (commandName "Project"); launchSettings.json allows comments. */
function readLaunchProfiles(projectFile) {
    try {
        const file = path.join(path.dirname(projectFile), 'Properties', 'launchSettings.json');
        const text = fs.readFileSync(file, 'utf8').replace(/^﻿/, '');
        const json = JSON.parse(stripJsonComments(text));
        return Object.entries(json.profiles ?? {})
            .filter(([, profile]) => profile.commandName?.toLowerCase() === 'project')
            .map(([name]) => name);
    }
    catch {
        return [];
    }
}
// Removes // and /* */ comments and trailing commas, leaving string contents alone.
function stripJsonComments(text) {
    let result = '';
    let inString = false;
    for (let i = 0; i < text.length; i++) {
        const c = text[i];
        if (inString) {
            result += c;
            if (c === '\\') {
                result += text[++i] ?? '';
            }
            else if (c === '"') {
                inString = false;
            }
        }
        else if (c === '"') {
            inString = true;
            result += c;
        }
        else if (c === '/' && text[i + 1] === '/') {
            while (i < text.length && text[i] !== '\n') {
                i++;
            }
            result += '\n';
        }
        else if (c === '/' && text[i + 1] === '*') {
            i = text.indexOf('*/', i + 2);
            i = i < 0 ? text.length : i + 1;
        }
        else {
            result += c;
        }
    }
    return result.replace(/,(\s*[}\]])/g, '$1');
}
//# sourceMappingURL=launcher.js.map