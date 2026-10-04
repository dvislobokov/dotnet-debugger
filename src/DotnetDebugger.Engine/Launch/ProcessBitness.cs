using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DotnetDebugger.Engine.Launch;

/// <summary>
/// 32-bit processes on 64-bit Windows. ICorDebug lives in the debugger's process (mscordbi), so only a debugger of the
/// debuggee's bitness can debug it; there is no 32-bit build of the adapter, so such targets are turned away with a
/// plain message before anything is started or attached to.
/// </summary>
public static class ProcessBitness
{
    private const ushort ImageFileMachineI386 = 0x014C;

    private const string Hint = "Build it for x64, or AnyCPU without \"Prefer 32-bit\".";

    /// <summary>
    /// Whether <paramref name="program"/> runs as a 32-bit process when started on this machine; null if that cannot
    /// be told. An apphost or native program says so in its PE header; a managed exe when it is x86 only, mixed mode or
    /// AnyCPU preferring 32-bit; a dll run by the dotnet host only when it cannot run as anything but x86.
    /// </summary>
    public static bool? Is32BitProgram(string program)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        if (!Environment.Is64BitOperatingSystem)
            return true;
        try
        {
            using var stream = File.OpenRead(program);
            using var pe = new PEReader(stream);
            if (pe.PEHeaders.CoffHeader.Machine != Machine.I386)
                return false;
            bool isDll = program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            if (pe.PEHeaders.CorHeader is not { } cor)
                return !isDll;
            CorFlags flags = cor.Flags;
            if ((flags & CorFlags.ILOnly) == 0)
                return true;
            // "prefer 32-bit" is encoded as both flags; for a dll the host decides
            if ((flags & CorFlags.Prefers32Bit) != 0)
                return !isDll;
            return (flags & CorFlags.Requires32Bit) != 0;
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null; // the launch itself will tell
        }
    }

    /// <summary>Whether the running process <paramref name="processId"/> is 32-bit; null if that cannot be told.</summary>
    public static bool? Is32BitProcess(int processId)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        if (!Environment.Is64BitOperatingSystem)
            return true;
        using SafeProcessHandle handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid)
            return null;
        try
        {
            // tells x86 from the other guests of an arm64 machine; Windows 10 1709 and later
            if (IsWow64Process2(handle, out ushort processMachine, out _))
                return processMachine == ImageFileMachineI386;
        }
        catch (EntryPointNotFoundException)
        {
        }
        return IsWow64Process(handle, out bool wow64) ? wow64 : null;
    }

    /// <summary>Throws when <paramref name="program"/> runs with a bitness other than this process'.</summary>
    public static void VerifyProgram(string program)
    {
        if (Is32BitProgram(program) is { } is32Bit)
            Verify(is32Bit, $"'{Path.GetFileName(program)}'");
    }

    /// <summary>Throws when the running process <paramref name="processId"/> has a bitness other than this process'.</summary>
    public static void VerifyProcess(int processId)
    {
        if (Is32BitProcess(processId) is { } is32Bit)
            Verify(is32Bit, $"Process {processId}");
    }

    private static void Verify(bool is32Bit, string target)
    {
        if (is32Bit != Environment.Is64BitProcess)
            return;
        throw new DebuggerException(is32Bit
            ? $"{target} runs as a 32-bit process, which dotnet-debugger cannot debug. {Hint}"
            : $"{target} runs as a 64-bit process, which this 32-bit dotnet-debugger cannot debug: use a 64-bit one.");
    }

    private const int ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool wow64);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(SafeProcessHandle process, out ushort processMachine, out ushort nativeMachine);
}
