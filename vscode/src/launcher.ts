import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { pickProcess } from './processPicker';

export const DEBUG_TYPE = 'dotnet-debugger';
const PROJECT_GLOB = '**/*.{csproj,fsproj,vbproj}';
const PROJECT_EXTENSIONS = ['.csproj', '.fsproj', '.vbproj'];
const STARTUP_PROJECT_KEY = 'startupProject';

/**
 * One-click ways to start debugging without writing a launch.json: commands (palette, explorer context menu of
 * project files, editor run button) and a status bar item for the startup project.
 */
export class Launcher {
    private readonly statusBar: vscode.StatusBarItem;

    constructor(private readonly context: vscode.ExtensionContext) {
        this.statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 10);
        this.statusBar.command = 'dotnet-debugger.debugStartupProject';
        context.subscriptions.push(
            this.statusBar,
            vscode.commands.registerCommand('dotnet-debugger.debugProject', (uri?: vscode.Uri) => this.debugProject(uri, {})),
            vscode.commands.registerCommand('dotnet-debugger.debugProjectInTerminal', (uri?: vscode.Uri) => this.debugProject(uri, { console: 'integratedTerminal' })),
            vscode.commands.registerCommand('dotnet-debugger.debugWithProfile', (uri?: vscode.Uri, profile?: string) => this.debugWithProfile(uri, profile)),
            vscode.commands.registerCommand('dotnet-debugger.debugActiveFile', () => this.debugActiveFile()),
            vscode.commands.registerCommand('dotnet-debugger.debugStartupProject', () => this.debugProject(this.startupProjectUri(), {})),
            vscode.commands.registerCommand('dotnet-debugger.selectStartupProject', (uri?: vscode.Uri) => this.selectStartupProject(uri)),
            vscode.commands.registerCommand('dotnet-debugger.attachToProcess', (processId?: number | string) => this.attach(processId)),
        );
        void this.refreshStatusBar();
    }

    // ---------------------------------------------------------------- commands

    private async debugProject(uri: vscode.Uri | undefined, extra: Record<string, unknown>): Promise<boolean> {
        const project = await this.resolveProject(uri);
        if (!project) {
            return false;
        }
        await this.setStartupProject(project);
        return vscode.debug.startDebugging(vscode.workspace.getWorkspaceFolder(vscode.Uri.file(project)), {
            name: `.NET: ${path.basename(project, path.extname(project))}`,
            type: DEBUG_TYPE,
            request: 'launch',
            project,
            build: true,
            ...extra,
        });
    }

    private async debugWithProfile(uri: vscode.Uri | undefined, profile: string | undefined): Promise<boolean> {
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
    private async debugActiveFile(): Promise<boolean> {
        const file = vscode.window.activeTextEditor?.document.uri;
        const project = file?.scheme === 'file' ? findProjectAbove(file.fsPath) : undefined;
        if (!project) {
            void vscode.window.showWarningMessage('The active file does not belong to a .NET project.');
            return false;
        }
        return this.debugProject(vscode.Uri.file(project), {});
    }

    private async attach(processId: number | string | undefined): Promise<boolean> {
        processId ??= await pickProcess();
        if (processId === undefined) {
            return false;
        }
        return vscode.debug.startDebugging(undefined, { name: `.NET: Attach to ${processId}`, type: DEBUG_TYPE, request: 'attach', processId });
    }

    private async selectStartupProject(uri: vscode.Uri | undefined): Promise<void> {
        const project = uri ? uri.fsPath : await pickProject(await findRunnableProjects(undefined));
        if (project) {
            await this.setStartupProject(project);
        }
    }

    // ---------------------------------------------------------------- startup project

    private startupProjectUri(): vscode.Uri | undefined {
        const stored = this.context.workspaceState.get<string>(STARTUP_PROJECT_KEY);
        return stored && fs.existsSync(stored) ? vscode.Uri.file(stored) : undefined;
    }

    private async setStartupProject(project: string): Promise<void> {
        await this.context.workspaceState.update(STARTUP_PROJECT_KEY, project);
        await this.refreshStatusBar();
    }

    private async refreshStatusBar(): Promise<void> {
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
    private async resolveProject(uri: vscode.Uri | undefined): Promise<string | undefined> {
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

async function pickProject(projects: string[], preferred?: string): Promise<string | undefined> {
    const items = projects
        .map(p => ({ label: path.basename(p, path.extname(p)), description: vscode.workspace.asRelativePath(p), project: p }))
        .sort((a, b) => Number(b.project === preferred) - Number(a.project === preferred));
    return (await vscode.window.showQuickPick(items, { placeHolder: 'Select the project to debug' }))?.project;
}

export async function findRunnableProjects(folder: vscode.WorkspaceFolder | undefined): Promise<string[]> {
    const pattern = folder ? new vscode.RelativePattern(folder, PROJECT_GLOB) : PROJECT_GLOB;
    const files = await vscode.workspace.findFiles(pattern, '**/{bin,obj,node_modules}/**', 200);
    return files.map(f => f.fsPath).filter(isRunnableProject).sort();
}

// Cheap textual check that avoids an MSBuild evaluation per project: libraries and test projects are not launchable.
function isRunnableProject(projectFile: string): boolean {
    try {
        const text = fs.readFileSync(projectFile, 'utf8');
        return /<OutputType>\s*(Exe|WinExe)\s*<\/OutputType>/i.test(text) || /Sdk="Microsoft\.NET\.Sdk\.(Web|Worker|BlazorWebAssembly)"/i.test(text);
    } catch {
        return false;
    }
}

export function findProjectAbove(file: string): string | undefined {
    const roots = (vscode.workspace.workspaceFolders ?? []).map(f => f.uri.fsPath.toLowerCase());
    for (let dir = path.dirname(file); ; dir = path.dirname(dir)) {
        let entries: string[] = [];
        try {
            entries = fs.readdirSync(dir);
        } catch {
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
export function readLaunchProfiles(projectFile: string): string[] {
    try {
        const file = path.join(path.dirname(projectFile), 'Properties', 'launchSettings.json');
        const text = fs.readFileSync(file, 'utf8').replace(/^﻿/, '');
        const json = JSON.parse(stripJsonComments(text)) as { profiles?: Record<string, { commandName?: string }> };
        return Object.entries(json.profiles ?? {})
            .filter(([, profile]) => profile.commandName?.toLowerCase() === 'project')
            .map(([name]) => name);
    } catch {
        return [];
    }
}

// Removes // and /* */ comments and trailing commas, leaving string contents alone.
function stripJsonComments(text: string): string {
    let result = '';
    let inString = false;
    for (let i = 0; i < text.length; i++) {
        const c = text[i];
        if (inString) {
            result += c;
            if (c === '\\') {
                result += text[++i] ?? '';
            } else if (c === '"') {
                inString = false;
            }
        } else if (c === '"') {
            inString = true;
            result += c;
        } else if (c === '/' && text[i + 1] === '/') {
            while (i < text.length && text[i] !== '\n') {
                i++;
            }
            result += '\n';
        } else if (c === '/' && text[i + 1] === '*') {
            i = text.indexOf('*/', i + 2);
            i = i < 0 ? text.length : i + 1;
        } else {
            result += c;
        }
    }
    return result.replace(/,(\s*[}\]])/g, '$1');
}
