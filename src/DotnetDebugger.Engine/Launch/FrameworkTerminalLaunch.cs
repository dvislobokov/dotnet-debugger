using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClrDebug;

namespace DotnetDebugger.Engine.Launch;

/// <summary>
/// A .NET Framework program run in the client's terminal ("runInTerminal").
///
/// The debugging library of .NET Framework has no runtime startup hook (dbgshim's RegisterForRuntimeStartup): it can
/// only join a process before managed code runs by creating that process itself, ICorDebug::CreateProcess. Joining a
/// process the terminal started (DebugActiveProcess once clr.dll is loaded) would miss whatever ran before, Main
/// included. So the roles are swapped: the helper in the terminal (<see cref="TerminalHelper"/>) starts nothing and
/// lends the debugger its terminal, the debugger creates the debuggee with it:
///
///  - stdio: the helper's standard handles, duplicated into the debugger and inherited by the debuggee;
///  - console: a process inherits the console of its creator, so for the duration of the call the debugger leaves its
///    own console (a hidden one usually) and attaches to the helper's. Ctrl+C, Console.ReadKey, the window size, the
///    code page: all of it is the terminal's, just as when the terminal starts the program itself.
///
/// The guarantees are those of a launch into the debug console: the process starts suspended, breakpoints set before
/// configurationDone and stopAtEntry hold from the first line of Main. The differences: the environment is the
/// debugger's (plus "env" of the launch configuration), not the terminal's, and the debuggee is a child of the debugger,
/// which takes it along when it dies (as the debug console launch does). The helper mirrors the exit code.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class FrameworkTerminalLaunch
{
    // The console is process wide: two of these at once would hand out each other's terminal.
    private static readonly object s_consoleLock = new();

    public static (int ProcessId, Action Resume, CorDebugProcess Process) Start(CorDebug corDebug, string program, IReadOnlyList<string> args,
        string? cwd, IReadOnlyDictionary<string, string?>? env, int hostProcessId, TerminalHost host, Action<string>? log)
    {
        const uint PROCESS_DUP_HANDLE = 0x40;
        IntPtr hostProcess = OpenProcess(PROCESS_DUP_HANDLE, false, hostProcessId);
        if (hostProcess == IntPtr.Zero)
            throw new DebuggerException($"Cannot reach the terminal (process {hostProcessId}): {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");

        var handles = new List<IntPtr>();
        try
        {
            IntPtr Duplicate(IntPtr handle)
            {
                const uint DUPLICATE_SAME_ACCESS = 0x2;
                // a handle the helper does not have (a GUI parent, closed stdio) is simply not passed on
                if (handle == IntPtr.Zero || handle == new IntPtr(-1)
                    || !DuplicateHandle(hostProcess, handle, GetCurrentProcess(), out IntPtr local, 0, true, DUPLICATE_SAME_ACCESS))
                {
                    return IntPtr.Zero;
                }
                handles.Add(local);
                return local;
            }

            var si = new STARTUPINFOW
            {
                cb = Marshal.SizeOf<STARTUPINFOW>(),
                dwFlags = STARTF.STARTF_USESTDHANDLES,
                hStdInput = Duplicate(host.StdIn),
                hStdOutput = Duplicate(host.StdOut),
                hStdError = Duplicate(host.StdErr),
            };
            string commandLine = ProcessLauncher.BuildCommandLine(program, args);
            string envBlock = string.Concat(ProcessLauncher.BuildEnvironment(env).Select(e => e + "\0")) + "\0";
            IntPtr envPtr = Marshal.StringToHGlobalUni(envBlock);
            try
            {
                lock (s_consoleLock)
                {
                    using ConsoleSwap? swap = host.HasConsole ? ConsoleSwap.AttachTo(hostProcessId, log) : null;
                    // without the terminal's console the debuggee gets a hidden one of its own, as in the debug console
                    CreateProcessFlags flags = CreateProcessFlags.CREATE_SUSPENDED | CreateProcessFlags.CREATE_UNICODE_ENVIRONMENT
                        | (swap != null ? (CreateProcessFlags)0 : CreateProcessFlags.CREATE_NO_WINDOW);
                    var security = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
                    var pi = default(ClrDebug.PROCESS_INFORMATION);
                    CorDebugProcess process = corDebug.CreateProcess(null!, commandLine, security, security, true, flags,
                        envPtr, string.IsNullOrEmpty(cwd) ? null! : cwd, si, ref pi, CorDebugCreateProcessFlags.DEBUG_NO_SPECIAL_OPTIONS);

                    CloseHandle(pi.hProcess);
                    IntPtr thread = pi.hThread;
                    return (pi.dwProcessId, () =>
                    {
                        ResumeThread(thread);
                        CloseHandle(thread);
                    }, process);
                }
            }
            catch (DebugException e)
            {
                throw new DebuggerException($"Failed to start '{commandLine}': {ErrorText.Describe(e)}");
            }
            finally
            {
                Marshal.FreeHGlobal(envPtr);
            }
        }
        finally
        {
            // the debuggee has its own copies by now
            foreach (IntPtr handle in handles)
                CloseHandle(handle);
            CloseHandle(hostProcess);
        }
    }

    /// <summary>
    /// This process attached to another process' console, and back on dispose. The standard handles are left alone:
    /// the debugger's are its protocol channel. Its own console, if it had one, is joined again through a process still
    /// attached to it (a console nobody else uses is gone with FreeConsole; a debugger does not miss it).
    /// </summary>
    private sealed class ConsoleSwap : IDisposable
    {
        private readonly IntPtr[] _std = new IntPtr[3];
        private readonly uint[] _formerCompanions;

        private ConsoleSwap(uint[] formerCompanions)
        {
            _formerCompanions = formerCompanions;
            for (int i = 0; i < 3; i++)
                _std[i] = GetStdHandle(-10 - i);
        }

        public static ConsoleSwap? AttachTo(int processId, Action<string>? log)
        {
            var companions = new uint[64];
            uint count = GetConsoleProcessList(companions, (uint)companions.Length);
            uint self = (uint)Environment.ProcessId;
            var swap = new ConsoleSwap(companions.Take((int)Math.Min(count, (uint)companions.Length)).Where(p => p != self).ToArray());
            FreeConsole();
            if (AttachConsole(processId))
                return swap;
            log?.Invoke($"Cannot attach to the console of the terminal (error {Marshal.GetLastPInvokeError()}): the program gets a console of its own.");
            swap.Dispose();
            return null;
        }

        public void Dispose()
        {
            FreeConsole();
            foreach (uint companion in _formerCompanions)
            {
                if (AttachConsole((int)companion))
                    break;
            }
            // Attaching may install the console's handles as the standard ones.
            for (int i = 0; i < 3; i++)
                SetStdHandle(-10 - i, _std[i]);
        }
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess, out IntPtr targetHandle,
        uint access, bool inheritHandle, uint options);

    [DllImport("kernel32")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32")]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32")]
    private static extern uint GetConsoleProcessList(uint[] processes, uint count);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32")]
    private static extern bool FreeConsole();

    [DllImport("kernel32")]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32")]
    private static extern bool SetStdHandle(int which, IntPtr handle);
}
