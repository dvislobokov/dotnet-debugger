import { execFile } from 'child_process';
import * as vscode from 'vscode';

interface ProcessItem extends vscode.QuickPickItem {
    pid: number;
}

/** Command behind "${command:pickProcess}": returns the chosen process id as a string, or undefined when cancelled. */
export async function pickProcess(): Promise<string | undefined> {
    const items = listProcesses().then(processes => processes.map<ProcessItem>(p => ({
        pid: p.pid,
        label: p.name,
        description: String(p.pid),
        detail: p.commandLine,
    })));
    const choice = await vscode.window.showQuickPick(items, {
        placeHolder: 'Select the .NET process to attach to',
        matchOnDescription: true,
        matchOnDetail: true,
    });
    return choice ? String(choice.pid) : undefined;
}

export interface ProcessInfo {
    pid: number;
    name: string;
    commandLine: string;
}

export async function listProcesses(): Promise<ProcessInfo[]> {
    const processes = process.platform === 'win32' ? await listWindows() : await listUnix();
    // Whether a process hosts the .NET runtime cannot be told from outside cheaply; likely candidates go first.
    const rank = (p: ProcessInfo) => (/(^|[\\/])dotnet(\.exe)?$/i.test(p.name) || /\.dll(\s|"|$)/i.test(p.commandLine) ? 0 : 1);
    return processes
        .filter(p => p.pid !== process.pid && p.pid > 4)
        .sort((a, b) => rank(a) - rank(b) || a.name.localeCompare(b.name) || a.pid - b.pid);
}

function run(file: string, args: string[]): Promise<string> {
    return new Promise((resolve, reject) => {
        execFile(file, args, { maxBuffer: 32 * 1024 * 1024, windowsHide: true }, (error, stdout) => error ? reject(error) : resolve(stdout));
    });
}

async function listWindows(): Promise<ProcessInfo[]> {
    const script = 'Get-CimInstance Win32_Process | Select-Object ProcessId,Name,CommandLine | ConvertTo-Json -Compress';
    const json = await run('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script]);
    const parsed: unknown = JSON.parse(json);
    const rows = (Array.isArray(parsed) ? parsed : [parsed]) as { ProcessId: number; Name: string | null; CommandLine: string | null }[];
    return rows.map(r => ({ pid: r.ProcessId, name: r.Name ?? '', commandLine: r.CommandLine ?? '' }));
}

async function listUnix(): Promise<ProcessInfo[]> {
    const output = await run('ps', ['-axo', 'pid=,comm=,args=']);
    const result: ProcessInfo[] = [];
    for (const line of output.split('\n')) {
        const match = /^\s*(\d+)\s+(\S+)\s+(.*)$/.exec(line);
        if (match) {
            result.push({ pid: Number(match[1]), name: match[2], commandLine: match[3] });
        }
    }
    return result;
}
