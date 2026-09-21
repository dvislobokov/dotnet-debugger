using System.Diagnostics;
using System.Runtime.InteropServices;
using ClrDebug;

namespace DotnetDebugger.Engine.Launch;

/// <summary>
/// What can be told about a process before attaching to it. Attaching blindly goes wrong in ways nobody can read:
/// a process without the runtime never calls back, a second debugger takes the process down with it (Windows).
/// </summary>
internal static class AttachChecks
{
    public static void Verify(int processId, DbgShim dbgShim, Action<string>? log)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited)
                throw new DebuggerException($"Process {processId} has exited.");
        }
        catch (ArgumentException)
        {
            throw new DebuggerException($"Process {processId} does not exist.");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // cannot tell (access rights): the attach itself will
            log?.Invoke($"Cannot look at process {processId}: {e.Message}");
        }

        if (DebuggerOf(processId, log) is { } debugger)
            throw new DebuggerException($"Process {processId} is already being debugged{debugger}: a process can have one debugger only.");

        bool hasRuntime;
        try
        {
            EnumerateCLRsResult runtimes = dbgShim.EnumerateCLRs(processId);
            hasRuntime = runtimes.Items.Length > 0;
            dbgShim.CloseCLREnumeration(runtimes.HandleArrayOut, runtimes.StringArrayOut, runtimes.Items.Length);
        }
        catch (Exception e)
        {
            // E_FAIL is how dbgshim says "none" on some platforms
            log?.Invoke($"EnumerateCLRs({processId}) failed: {e.Message}");
            hasRuntime = false;
        }
        if (!hasRuntime)
            throw new DebuggerException($"Process {processId} does not host the .NET runtime (or cannot be accessed): there is nothing to attach to.");
    }

    /// <returns>null if no debugger is attached; otherwise text to add to the message (may be empty).</returns>
    private static string? DebuggerOf(int processId, Action<string>? log)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // ICorDebug is a native debugger of the process on Windows, so this sees other .NET debuggers too
                using Process process = Process.GetProcessById(processId);
                return CheckRemoteDebuggerPresent(process.Handle, out bool present) && present ? "" : null;
            }
            if (OperatingSystem.IsLinux())
            {
                foreach (string line in File.ReadLines($"/proc/{processId}/status"))
                {
                    if (line.StartsWith("TracerPid:", StringComparison.Ordinal))
                        return int.TryParse(line.AsSpan(10).Trim(), out int tracer) && tracer != 0 ? $" (traced by process {tracer})" : null;
                }
            }
        }
        catch (Exception e)
        {
            log?.Invoke($"Cannot tell whether process {processId} is being debugged: {e.Message}");
        }
        return null;
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool present);
}
