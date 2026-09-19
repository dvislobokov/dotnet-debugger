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
exports.pickProcess = pickProcess;
exports.listProcesses = listProcesses;
const child_process_1 = require("child_process");
const vscode = __importStar(require("vscode"));
/** Command behind "${command:pickProcess}": returns the chosen process id as a string, or undefined when cancelled. */
async function pickProcess() {
    const items = listProcesses().then(processes => processes.map(p => ({
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
async function listProcesses() {
    const processes = process.platform === 'win32' ? await listWindows() : await listUnix();
    // Whether a process hosts the .NET runtime cannot be told from outside cheaply; likely candidates go first.
    const rank = (p) => (/(^|[\\/])dotnet(\.exe)?$/i.test(p.name) || /\.dll(\s|"|$)/i.test(p.commandLine) ? 0 : 1);
    return processes
        .filter(p => p.pid !== process.pid && p.pid > 4)
        .sort((a, b) => rank(a) - rank(b) || a.name.localeCompare(b.name) || a.pid - b.pid);
}
function run(file, args) {
    return new Promise((resolve, reject) => {
        (0, child_process_1.execFile)(file, args, { maxBuffer: 32 * 1024 * 1024, windowsHide: true }, (error, stdout) => error ? reject(error) : resolve(stdout));
    });
}
async function listWindows() {
    const script = 'Get-CimInstance Win32_Process | Select-Object ProcessId,Name,CommandLine | ConvertTo-Json -Compress';
    const json = await run('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script]);
    const parsed = JSON.parse(json);
    const rows = (Array.isArray(parsed) ? parsed : [parsed]);
    return rows.map(r => ({ pid: r.ProcessId, name: r.Name ?? '', commandLine: r.CommandLine ?? '' }));
}
async function listUnix() {
    const output = await run('ps', ['-axo', 'pid=,comm=,args=']);
    const result = [];
    for (const line of output.split('\n')) {
        const match = /^\s*(\d+)\s+(\S+)\s+(.*)$/.exec(line);
        if (match) {
            result.push({ pid: Number(match[1]), name: match[2], commandLine: match[3] });
        }
    }
    return result;
}
//# sourceMappingURL=processPicker.js.map