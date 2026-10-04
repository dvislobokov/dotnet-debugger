using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClrDebug;

namespace DotnetDebugger.Engine.Launch;

/// <summary>
/// The .NET Framework 4.x runtime (clr.dll). Its debugging library, mscordbi, is part of Windows and is handed out by
/// the metahost (mscoree) instead of dbgshim, which knows nothing but CoreCLR.
/// </summary>
internal static class FrameworkRuntime
{
    /// <summary>Every .NET Framework from 4.0 to 4.8.x is this one runtime.</summary>
    public const string Version = "v4.0.30319";

    private static readonly Guid CLSID_CLRDebuggingLegacy = new("DF8395B5-A4BA-450b-A77C-A9A47762C520");

    /// <summary>
    /// A managed executable that is not .NET (Core): an apphost or a single-file bundle is native code, and an
    /// application of .NET (Core) built as a managed exe has a runtimeconfig.json and references System.Runtime.
    /// </summary>
    public static bool IsFrameworkProgram(string program)
    {
        if (!OperatingSystem.IsWindows() || !program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return false;
        if (File.Exists(Path.ChangeExtension(program, ".runtimeconfig.json")))
            return false;
        try
        {
            using var stream = File.OpenRead(program);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return false;
            MetadataReader reader = pe.GetMetadataReader();
            bool mscorlib = false;
            foreach (AssemblyReferenceHandle handle in reader.AssemblyReferences)
            {
                string name = reader.GetString(reader.GetAssemblyReference(handle).Name);
                if (name is "System.Runtime" or "System.Private.CoreLib" or "netstandard")
                    return false;
                mscorlib |= name == "mscorlib";
            }
            return mscorlib;
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The debugging library has to be of the bitness of the debuggee. An AnyCPU program runs as 32-bit when it prefers
    /// to (the default of old project templates). The 64-bit adapter hands 32-bit programs to its 32-bit build before
    /// they get here; this is the last line for when that build is not in the game.
    /// </summary>
    public static void VerifyBitness(string program) => ProcessBitness.VerifyMatches(program);

    /// <summary>Whether <paramref name="processId"/> has the .NET Framework 4 runtime loaded.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsLoadedIn(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            CLRMetaHost metaHost = Extensions.CLRCreateInstance().CLRMetaHost;
            foreach (object item in metaHost.EnumerateLoadedRuntimes(process.Handle))
            {
                if (new CLRRuntimeInfo((ICLRRuntimeInfo)item).VersionString.StartsWith("v4.", StringComparison.Ordinal))
                    return true;
            }
        }
        catch (Exception)
        {
            // gone, of another bitness, not ours to open: none of these can be debugged by us anyway
        }
        return false;
    }

    /// <summary>A fresh, uninitialized ICorDebug of the installed .NET Framework 4.</summary>
    [SupportedOSPlatform("windows")]
    public static CorDebug CreateCorDebug()
    {
        try
        {
            CLRMetaHost metaHost = Extensions.CLRCreateInstance().CLRMetaHost;
            var runtime = new CLRRuntimeInfo((ICLRRuntimeInfo)metaHost.GetRuntime(Version, typeof(ICLRRuntimeInfo).GUID));
            return new CorDebug((ICorDebug)runtime.GetInterface(CLSID_CLRDebuggingLegacy, typeof(ICorDebug).GUID));
        }
        catch (Exception e)
        {
            throw new DebuggerException($"The debugging library of .NET Framework 4 is not available: {ErrorText.Describe(e)}");
        }
    }
}
