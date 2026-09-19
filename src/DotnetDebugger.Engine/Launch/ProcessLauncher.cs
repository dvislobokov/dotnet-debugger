using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using ClrDebug;

namespace DotnetDebugger.Engine.Launch;

internal sealed class LaunchedProcess
{
    public required int ProcessId { get; init; }
    public required Action Resume { get; init; }
    public required Stream StdOut { get; init; }
    public required Stream StdErr { get; init; }
    public required Stream StdIn { get; init; }
}

/// <summary>
/// Starts the debuggee suspended with its stdio redirected into pipes, so the runtime startup
/// hook can be registered before any managed code runs.
/// </summary>
internal static class ProcessLauncher
{
    public static LaunchedProcess Launch(DbgShim dbgShim, string program, IReadOnlyList<string> args, string? cwd,
        IReadOnlyDictionary<string, string?>? env)
    {
        string commandLine = BuildCommandLine(program, args);

        var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        try
        {
            IntPtr hIn = stdin.ClientSafePipeHandle.DangerousGetHandle();
            IntPtr hOut = stdout.ClientSafePipeHandle.DangerousGetHandle();
            IntPtr hErr = stderr.ClientSafePipeHandle.DangerousGetHandle();

            (int pid, Action resume) = OperatingSystem.IsWindows()
                ? LaunchWindows(commandLine, cwd, env, hIn, hOut, hErr)
                : LaunchUnix(dbgShim, commandLine, cwd, env, (int)hIn, (int)hOut, (int)hErr);

            stdin.DisposeLocalCopyOfClientHandle();
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();
            return new LaunchedProcess { ProcessId = pid, Resume = resume, StdOut = stdout, StdErr = stderr, StdIn = stdin };
        }
        catch
        {
            stdin.Dispose();
            stdout.Dispose();
            stderr.Dispose();
            throw;
        }
    }

    /// <summary>Command line for a program that is either a managed dll (run by the dotnet host) or an executable.</summary>
    internal static string BuildCommandLine(string program, IReadOnlyList<string> args)
    {
        var argv = new List<string>();
        if (program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            argv.Add(FindDotnetHost());
        argv.Add(program);
        argv.AddRange(args);
        return string.Join(' ', argv.Select(QuoteArgument));
    }

    private static string FindDotnetHost()
    {
        string exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        string? current = Environment.ProcessPath;
        if (current != null && Path.GetFileName(current).Equals(exe, StringComparison.OrdinalIgnoreCase))
            return current;

        var dirs = new List<string?> { Environment.GetEnvironmentVariable("DOTNET_ROOT") };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (string? dir in dirs)
        {
            if (string.IsNullOrEmpty(dir))
                continue;
            string candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate))
                return candidate;
        }
        return exe;
    }

    // MSVCRT command line quoting rules; the CoreCLR PAL parses command lines the same way.
    internal static string QuoteArgument(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
            return arg;

        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
                sb.Append('\\', backslashes * 2 + 1);
            else
                sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    private static List<string> BuildEnvironment(IReadOnlyDictionary<string, string?>? overrides)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var env = new SortedDictionary<string, string>(comparer);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";
        if (overrides != null)
        {
            foreach (var (key, value) in overrides)
            {
                if (value == null)
                    env.Remove(key);
                else
                    env[key] = value;
            }
        }
        return env.Select(kv => kv.Key + "=" + kv.Value).ToList();
    }

    // ---------------------------------------------------------------- Windows

    // dbgshim's CreateProcessForLaunch passes bInheritHandles=FALSE, which makes stdio
    // redirection impossible, so on Windows we create the suspended process ourselves.
    private static (int, Action) LaunchWindows(string commandLine, string? cwd, IReadOnlyDictionary<string, string?>? env,
        IntPtr hIn, IntPtr hOut, IntPtr hErr)
    {
        const uint CREATE_SUSPENDED = 0x4, CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000;
        const int STARTF_USESTDHANDLES = 0x100;

        var si = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            dwFlags = STARTF_USESTDHANDLES,
            hStdInput = hIn,
            hStdOutput = hOut,
            hStdError = hErr,
        };

        string envBlock = string.Concat(BuildEnvironment(env).Select(e => e + "\0")) + "\0";
        IntPtr envPtr = Marshal.StringToHGlobalUni(envBlock);
        try
        {
            char[] cmd = (commandLine + "\0").ToCharArray();
            if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, true,
                    CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                    envPtr, string.IsNullOrEmpty(cwd) ? null : cwd, ref si, out PROCESS_INFORMATION pi))
            {
                throw new InvalidOperationException(
                    $"Failed to start '{commandLine}': {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
            }

            CloseHandle(pi.hProcess);
            return (pi.dwProcessId, () =>
            {
                ResumeThread(pi.hThread);
                CloseHandle(pi.hThread);
            });
        }
        finally
        {
            Marshal.FreeHGlobal(envPtr);
        }
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
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32")]
    private static extern bool CloseHandle(IntPtr handle);

    // ---------------------------------------------------------------- Unix

    private static readonly object s_stdioSwapLock = new();

    // The PAL's CreateProcess forks with the parent's fds 0/1/2, so the pipes are
    // put in place of our own stdio for the duration of the call.
    private static (int, Action) LaunchUnix(DbgShim dbgShim, string commandLine, string? cwd,
        IReadOnlyDictionary<string, string?>? env, int fdIn, int fdOut, int fdErr)
    {
        byte[] envBlock = Encoding.UTF8.GetBytes(string.Concat(BuildEnvironment(env).Select(e => e + "\0")) + "\0");
        GCHandle envHandle = GCHandle.Alloc(envBlock, GCHandleType.Pinned);
        try
        {
            CreateProcessForLaunchResult result;
            lock (s_stdioSwapLock)
            {
                int saved0 = dup(0), saved1 = dup(1), saved2 = dup(2);
                try
                {
                    dup2(fdIn, 0);
                    dup2(fdOut, 1);
                    dup2(fdErr, 2);
                    result = dbgShim.CreateProcessForLaunch(commandLine, true, envHandle.AddrOfPinnedObject(),
                        string.IsNullOrEmpty(cwd) ? null! : cwd);
                }
                finally
                {
                    Restore(saved0, 0);
                    Restore(saved1, 1);
                    Restore(saved2, 2);
                }
            }

            return (result.ProcessId, () =>
            {
                dbgShim.ResumeProcess(result.ResumeHandle);
                dbgShim.CloseResumeHandle(result.ResumeHandle);
            });
        }
        finally
        {
            envHandle.Free();
        }

        static void Restore(int saved, int target)
        {
            if (saved < 0)
                return;
            dup2(saved, target);
            close(saved);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int dup(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldfd, int newfd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
