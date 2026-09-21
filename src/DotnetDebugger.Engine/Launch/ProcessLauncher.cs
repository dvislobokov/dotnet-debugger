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

    /// <summary>Set when the process is not a child of this one and its launcher reports the exit code (Unix).</summary>
    public Task<int?>? ExitCode { get; init; }

    /// <summary>The debugger is done with the process, which may live on (detach): nobody is going to listen to its launcher.</summary>
    public Action? Abandon { get; init; }
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
        List<string> argv = BuildArguments(program, args);

        var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        try
        {
            IntPtr hIn = stdin.ClientSafePipeHandle.DangerousGetHandle();
            IntPtr hOut = stdout.ClientSafePipeHandle.DangerousGetHandle();
            IntPtr hErr = stderr.ClientSafePipeHandle.DangerousGetHandle();

            (int pid, Action resume, Task<int?>? exitCode, Action? abandon) = OperatingSystem.IsWindows()
                ? LaunchWindows(string.Join(' ', argv.Select(QuoteArgument)), cwd, env, hIn, hOut, hErr)
                : LaunchUnix(dbgShim, argv, cwd, env, (int)hIn, (int)hOut, (int)hErr);

            stdin.DisposeLocalCopyOfClientHandle();
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();
            return new LaunchedProcess { ProcessId = pid, Resume = resume, StdOut = stdout, StdErr = stderr, StdIn = stdin, ExitCode = exitCode, Abandon = abandon };
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
    internal static string BuildCommandLine(string program, IReadOnlyList<string> args) =>
        string.Join(' ', BuildArguments(program, args).Select(QuoteArgument));

    private static List<string> BuildArguments(string program, IReadOnlyList<string> args)
    {
        var argv = new List<string>();
        if (program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            argv.Add(FindDotnetHost());
        argv.Add(program);
        argv.AddRange(args);
        return argv;
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
    private static (int, Action, Task<int?>?, Action?) LaunchWindows(string commandLine, string? cwd, IReadOnlyDictionary<string, string?>? env,
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
            }, null, null);
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

    // The debuggee must not be a child of this process. The exit code of a child goes to whoever calls waitpid first,
    // and the PAL inside the debugging libraries does that for every process it watches: every now and then the code
    // was lost (waitpid failing with ECHILD). So a shell stands in between:
    //
    //   outer sh (our child, nobody cares)  --  inner sh: reports its pid, waits for "go", then exec's the debuggee
    //
    // exec keeps the pid, so the runtime startup hook can be registered for it while the shell is still waiting. When
    // the debuggee is gone the outer shell reports "exit N". Both directions are FIFOs: a shell has nothing better.
    //
    // A third FIFO is the watchdog: the debugger holds it open and never writes to it, except "detach" when it lets
    // the debuggee go. Reading from it returns when the debugger has said so or is no more (killed, out of memory,
    // crashed with its IDE), and a debuggee nobody debugs any longer is killed. PR_SET_PDEATHSIG would do the same on
    // Linux only, and only for a direct child.
    private const string LaunchScript =
        "ctl=\"$1\"; go=\"$2\"; alive=\"$3\"; shift 3\n" +
        // In the background and waited for: a shell reports the death of a foreground command ("Killed") on stderr, which
        // is the debuggee's stderr. A background command would get /dev/null as its stdin, hence "<&0".
        "/bin/sh -c 'echo \"pid $$\" > \"$0\"; read line < \"$1\"; shift; exec \"$@\"' \"$ctl\" \"$go\" \"$@\" <&0 &\n" +
        "child=$!\n" +
        // the control FIFO goes first: without the debugger nobody reads it, and the report below would block forever
        "( exec >/dev/null 2>&1; verdict=; read verdict < \"$alive\"; [ \"$verdict\" = detach ] || { rm -f \"$ctl\"; kill -9 $child; } ) &\n" +
        "watchdog=$!\n" +
        "wait $child 2>/dev/null\n" + // dash prints the remark from "wait" as well
        "code=$?\n" +
        "kill $watchdog 2>/dev/null\n" +
        // the FIFO disappears when the debugger has detached or is gone: nobody would ever read it
        "if [ -p \"$ctl\" ]; then echo \"exit $code\" > \"$ctl\"; fi\n";

    // The PAL's CreateProcess forks with the parent's fds 0/1/2, so the pipes are
    // put in place of our own stdio for the duration of the call.
    private static (int, Action, Task<int?>?, Action?) LaunchUnix(DbgShim dbgShim, List<string> argv, string? cwd,
        IReadOnlyDictionary<string, string?>? env, int fdIn, int fdOut, int fdErr)
    {
        string directory = Directory.CreateTempSubdirectory("dotnet-debugger-").FullName;
        string script = Path.Combine(directory, "launch.sh"), control = Path.Combine(directory, "ctl"), go = Path.Combine(directory, "go");
        string alive = Path.Combine(directory, "alive");
        FileStream? controlStream = null, goStream = null, aliveStream = null;
        byte[] envBlock = Encoding.UTF8.GetBytes(string.Concat(BuildEnvironment(env).Select(e => e + "\0")) + "\0");
        GCHandle envHandle = GCHandle.Alloc(envBlock, GCHandleType.Pinned);
        try
        {
            File.WriteAllText(script, LaunchScript);
            if (mkfifo(control, 0x180 /* 0600 */) != 0 || mkfifo(go, 0x180) != 0 || mkfifo(alive, 0x180) != 0)
                throw new DebuggerException($"Cannot create a FIFO in '{directory}' (errno {Marshal.GetLastPInvokeError()}).");
            // read+write: opening never blocks, and the reader sees no end of file between the two writers
            controlStream = new FileStream(control, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1);
            goStream = new FileStream(go, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1);
            aliveStream = new FileStream(alive, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1);

            string commandLine = string.Join(' ', new[] { "/bin/sh", script, control, go, alive }.Concat(argv).Select(QuoteArgument));
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
            dbgShim.ResumeProcess(result.ResumeHandle);
            dbgShim.CloseResumeHandle(result.ResumeHandle);

            var reader = new StreamReader(controlStream, Encoding.ASCII, false, 64, leaveOpen: true);
            Task<string?> first = Task.Run(reader.ReadLine);
            if (!first.Wait(TimeSpan.FromSeconds(15)) || first.Result?.Split(' ') is not ["pid", var pidText] || !int.TryParse(pidText, out int pid))
                throw new DebuggerException("The launcher shell did not report the process id" + (first.IsCompleted ? $": '{first.Result}'." : "."));

            FileStream controlOwned = controlStream, goOwned = goStream, aliveOwned = aliveStream;
            int shell = result.ProcessId;
            Task<int?> exitCode = Task.Run(() =>
            {
                try
                {
                    return reader.ReadLine()?.Split(' ') is ["exit", var codeText] && int.TryParse(codeText, out int code) ? code : (int?)null;
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                    return null;
                }
                finally
                {
                    waitpid(shell, out _, 0); // no zombie; whoever else reaps it is welcome
                    controlOwned.Dispose();
                    goOwned.Dispose();
                    aliveOwned.Dispose();
                    TryDelete(directory);
                }
            });
            controlStream = goStream = aliveStream = null;
            return (pid, () =>
            {
                goOwned.Write("go\n"u8);
                goOwned.Flush();
            }, exitCode, () =>
            {
                try
                {
                    aliveOwned.Write("detach\n"u8);
                    aliveOwned.Flush();
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                    // the debuggee is gone already
                }
                TryDelete(directory);
            });
        }
        catch
        {
            controlStream?.Dispose();
            goStream?.Dispose();
            aliveStream?.Dispose();
            TryDelete(directory);
            throw;
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

        static void TryDelete(string directory)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldfd, int newfd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
