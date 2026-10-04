using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DotnetDebugger.Engine.Launch;

/// <summary>
/// 32-bit processes on 64-bit Windows. ICorDebug lives in the debugger's process (mscordbi), so only a debugger of the
/// debuggee's bitness can debug it: the 64-bit adapter hands such sessions to its 32-bit build.
/// </summary>
public static class ProcessBitness
{
    private const ushort ImageFileMachineI386 = 0x014C;

    /// <summary>What to tell a user whose program the adapter at hand cannot debug because it is 32-bit.</summary>
    public const string X86AdapterHint =
        "Debugging it takes the 32-bit (win-x86) build of dotnet-debugger: the release archives and the VS Code extension " +
        "for Windows bring it in the \"x86\" folder next to dotnet-debugger.exe, DOTNET_DEBUGGER_X86_ADAPTER (or the launch " +
        "option \"x86Adapter\") can point to one, and an adapter run by dotnet (the .NET tool) uses an installed x86 .NET " +
        "runtime (8 or newer). Alternatively build the program for x64, or AnyCPU without \"Prefer 32-bit\".";

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

    /// <summary>
    /// Throws when <paramref name="program"/> runs with a bitness other than this process', which the debugging library
    /// cannot bridge.
    /// </summary>
    public static void VerifyMatches(string program)
    {
        if (Is32BitProgram(program) is not { } is32Bit || is32Bit != Environment.Is64BitProcess)
            return;
        string name = Path.GetFileName(program);
        throw new DebuggerException(is32Bit
            ? $"'{name}' runs as a 32-bit process, which this (64-bit) adapter cannot debug. {X86AdapterHint}"
            : $"'{name}' runs as a 64-bit process, which this 32-bit adapter cannot debug: use the 64-bit dotnet-debugger.");
    }

    /// <summary>
    /// The dotnet host of an x86 .NET installation (64-bit Windows only), optionally only if it has a runtime of at least
    /// <paramref name="minimumRuntimeMajor"/>.
    /// </summary>
    public static string? FindX86DotnetHost(int minimumRuntimeMajor = 0)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
            return null;
        var roots = new List<string?>
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)"),
            InstallLocationFromRegistry(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"),
        };
        foreach (string? root in roots)
        {
            if (string.IsNullOrEmpty(root))
                continue;
            string host = Path.Combine(root, "dotnet.exe");
            if (File.Exists(host) && (minimumRuntimeMajor == 0 || HasRuntime(root, minimumRuntimeMajor)))
                return host;
        }
        return null;
    }

    private static bool HasRuntime(string root, int minimumMajor)
    {
        try
        {
            string shared = Path.Combine(root, "shared", "Microsoft.NETCore.App");
            return Directory.Exists(shared) && Directory.EnumerateDirectories(shared)
                .Any(d => Version.TryParse(Path.GetFileName(d).Split('-')[0], out Version? v) && v.Major >= minimumMajor);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? InstallLocationFromRegistry()
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using RegistryKey? key = baseKey.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x86");
            return key?.GetValue("InstallLocation") as string;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
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
