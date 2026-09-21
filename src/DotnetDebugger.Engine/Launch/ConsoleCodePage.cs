using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DotnetDebugger.Engine.Launch;

/// <summary>
/// Windows only. A program whose output is redirected encodes it with the code page of its console, which is the OEM
/// one (437, 866, ...) for the hidden console a debuggee gets: text outside of it is lost before it leaves the
/// process, while the debugger reads the pipe as UTF-8. So the debuggee's console is switched to UTF-8 before managed
/// code runs (System.Console asks for the code page when it is first used).
///
/// A process can only be attached to one console, and this one has its own (the terminal it was started from, or a
/// hidden one): a short-lived helper process does the switching instead, this executable run with
/// <see cref="HelperOption"/>.
/// </summary>
public static class ConsoleCodePage
{
    public const string HelperOption = "--console-utf8";

    private const uint Utf8 = 65001;
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Switches the console of <paramref name="processId"/> to UTF-8. The process must have got as far as having one.</summary>
    internal static void SwitchToUtf8(int processId, Action<string>? log)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            string processPath = Environment.ProcessPath ?? throw new InvalidOperationException("the path of the debugger is unknown");
            var startInfo = new ProcessStartInfo(processPath) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, Assembly.GetEntryAssembly()!.GetName().Name + ".dll"));
            startInfo.ArgumentList.Add(HelperOption);
            startInfo.ArgumentList.Add(processId.ToString());

            using Process helper = Process.Start(startInfo)!;
            if (!helper.WaitForExit(HelperTimeout))
            {
                helper.Kill();
                log?.Invoke("The console code page helper did not finish: program output that is not ASCII may be garbled.");
            }
            else if (helper.ExitCode != 0)
            {
                log?.Invoke($"The console code page helper failed (error {helper.ExitCode}): program output that is not ASCII may be garbled.");
            }
        }
        catch (Exception e)
        {
            log?.Invoke("Cannot switch the debuggee's console to UTF-8: " + e.Message);
        }
    }

    /// <summary>The helper process: returns 0, or the Windows error code.</summary>
    public static int RunHelper(int processId)
    {
        if (!OperatingSystem.IsWindows())
            return 0;
        FreeConsole();
        if (!AttachConsole(processId))
            return Math.Max(1, Marshal.GetLastPInvokeError());
        bool done = SetConsoleOutputCP(Utf8) & SetConsoleCP(Utf8);
        int error = done ? 0 : Math.Max(1, Marshal.GetLastPInvokeError());
        FreeConsole();
        return error;
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint codePage);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetConsoleCP(uint codePage);
}
