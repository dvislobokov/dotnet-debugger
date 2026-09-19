using System.Net.Sockets;
using System.Runtime.InteropServices;
using ClrDebug;

namespace DotnetDebugger.Engine.Launch;

/// <summary>
/// The process a DAP client starts inside its terminal on behalf of the debugger ("runInTerminal"). It creates the
/// debuggee suspended with the terminal's stdio, tells the debugger the process id, resumes the debuggee when the
/// debugger is ready and finally mirrors its exit code.
///
/// Wire protocol (one TCP connection to the debugger, text lines):
///   helper -> debugger:  "&lt;token&gt; &lt;pid&gt;"
///   debugger -> helper:  "resume"
///   helper -> debugger:  "exit &lt;code&gt;"
/// </summary>
public static class TerminalHelper
{
    public static int Run(int port, string token, string program, IReadOnlyList<string> args)
    {
        // Ctrl+C in the terminal is meant for the debuggee; this process just keeps waiting for it.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;

        using var client = new TcpClient();
        client.Connect(System.Net.IPAddress.Loopback, port);
        using NetworkStream stream = client.GetStream();
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };

        string commandLine = ProcessLauncher.BuildCommandLine(program, args);
        SuspendedProcess process = OperatingSystem.IsWindows() ? StartWindows(commandLine) : StartUnix(commandLine);
        try
        {
            writer.WriteLine($"{token} {process.ProcessId}");
            if (reader.ReadLine() != "resume")
                throw new IOException("The debugger went away before the debuggee was started.");
        }
        catch (Exception)
        {
            process.Kill();
            throw;
        }

        process.Resume();
        int exitCode = process.WaitForExit();
        try
        {
            writer.WriteLine($"exit {exitCode}");
        }
        catch (IOException)
        {
            // the debugger is already gone
        }
        return exitCode;
    }

    private sealed class SuspendedProcess
    {
        public required int ProcessId { get; init; }
        public required Action Resume { get; init; }
        public required Func<int> WaitForExit { get; init; }
        public required Action Kill { get; init; }
    }

    // ---------------------------------------------------------------- Windows

    private static SuspendedProcess StartWindows(string commandLine)
    {
        const uint CREATE_SUSPENDED = 0x4;
        const int STARTF_USESTDHANDLES = 0x100;
        const uint INFINITE = 0xFFFFFFFF;

        var si = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            dwFlags = STARTF_USESTDHANDLES,
            hStdInput = GetStdHandle(-10),
            hStdOutput = GetStdHandle(-11),
            hStdError = GetStdHandle(-12),
        };
        char[] cmd = (commandLine + "\0").ToCharArray();
        if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, true, CREATE_SUSPENDED, IntPtr.Zero, null, ref si, out PROCESS_INFORMATION pi))
            throw new InvalidOperationException($"Failed to start '{commandLine}': {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");

        return new SuspendedProcess
        {
            ProcessId = pi.dwProcessId,
            Resume = () =>
            {
                ResumeThread(pi.hThread);
                CloseHandle(pi.hThread);
            },
            WaitForExit = () =>
            {
                WaitForSingleObject(pi.hProcess, INFINITE);
                GetExitCodeProcess(pi.hProcess, out uint code);
                CloseHandle(pi.hProcess);
                return unchecked((int)code);
            },
            Kill = () => TerminateProcess(pi.hProcess, 1),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? lpApplicationName, char[] lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
        string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32")]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32")]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint exitCode);

    [DllImport("kernel32")]
    private static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);

    [DllImport("kernel32")]
    private static extern bool CloseHandle(IntPtr handle);

    // ---------------------------------------------------------------- Unix

    // dbgshim's launcher forks with this process' stdio and environment, which is exactly what is needed here.
    private static SuspendedProcess StartUnix(string commandLine)
    {
        DbgShim dbgShim = DebugEngine.LoadDbgShim();
        CreateProcessForLaunchResult result = dbgShim.CreateProcessForLaunch(commandLine, true, IntPtr.Zero, null!);
        int pid = result.ProcessId;
        return new SuspendedProcess
        {
            ProcessId = pid,
            Resume = () =>
            {
                dbgShim.ResumeProcess(result.ResumeHandle);
                dbgShim.CloseResumeHandle(result.ResumeHandle);
            },
            WaitForExit = () =>
            {
                while (true)
                {
                    int waited = waitpid(pid, out int status, 0);
                    if (waited == pid)
                        return (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
                    if (waited < 0 && Marshal.GetLastPInvokeError() != 4 /* EINTR */)
                        break;
                }
                // not our child after all (or already reaped): fall back to polling
                while (kill(pid, 0) == 0)
                    Thread.Sleep(100);
                return 0;
            },
            Kill = () => kill(pid, 9),
        };
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
