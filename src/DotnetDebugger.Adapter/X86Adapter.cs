using DotnetDebugger.Engine;
using DotnetDebugger.Engine.Launch;

namespace DotnetDebugger.Adapter;

/// <summary>How to start the 32-bit adapter, and where that was found (for the log).</summary>
internal sealed record X86AdapterCommand(string FileName, string[] Arguments, string Origin)
{
    public override string ToString() => string.Join(' ', [FileName, .. Arguments]) + $" ({Origin})";
}

/// <summary>
/// Finds the 32-bit (win-x86) build of the adapter, which debugs the 32-bit processes the 64-bit one cannot:
/// <list type="number">
/// <item>the launch/attach option "x86Adapter" or DOTNET_DEBUGGER_X86_ADAPTER (an exe, or a dll run by an x86 dotnet);</item>
/// <item>the "x86" folder next to this adapter: a self-contained single-file publish (build/publish.ps1 puts it there);</item>
/// <item>this very adapter run framework-dependent by the dotnet host of an installed x86 .NET (the .NET tool).</item>
/// </list>
/// </summary>
internal static class X86Adapter
{
    public const string OptionName = "x86Adapter";
    public const string EnvironmentVariable = "DOTNET_DEBUGGER_X86_ADAPTER";
    public const string FolderName = "x86";

    private const string ExecutableName = "dotnet-debugger.exe";
    private const string AssemblyFileName = "dotnet-debugger.dll";
    private const int MinimumRuntimeMajor = 8;

    /// <exception cref="DebuggerException">None was found; the message tells what was looked at.</exception>
    public static X86AdapterCommand Locate(string? option)
    {
        if (!string.IsNullOrEmpty(option))
            return FromPath(option, $"launch option \"{OptionName}\"");
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } variable)
            return FromPath(variable, EnvironmentVariable);

        string bundled = Path.Combine(AppContext.BaseDirectory, FolderName, ExecutableName);
        if (File.Exists(bundled))
            return new X86AdapterCommand(bundled, [], $"next to the adapter, in \"{FolderName}\"");

        // single-file builds have no dll to hand to another host
        string assembly = Path.Combine(AppContext.BaseDirectory, AssemblyFileName);
        string? host = ProcessBitness.FindX86DotnetHost(MinimumRuntimeMajor);
        if (File.Exists(assembly) && host != null)
            return new X86AdapterCommand(host, [assembly], "framework-dependent, x86 .NET runtime");

        throw new DebuggerException($"The 32-bit adapter was not found (looked for '{bundled}'" +
            (File.Exists(assembly) ? $" and for an x86 .NET {MinimumRuntimeMajor}+ runtime" : "") + ").");
    }

    private static X86AdapterCommand FromPath(string path, string origin)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new DebuggerException($"The 32-bit adapter '{path}' ({origin}) does not exist.");
        if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return new X86AdapterCommand(path, [], origin);
        string host = ProcessBitness.FindX86DotnetHost(MinimumRuntimeMajor)
            ?? throw new DebuggerException($"The 32-bit adapter '{path}' ({origin}) is a dll, which takes an x86 .NET {MinimumRuntimeMajor}+ runtime: none is installed.");
        return new X86AdapterCommand(host, [path], origin);
    }
}
