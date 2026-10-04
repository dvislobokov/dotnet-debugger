using ClrDebug;
using DotnetDebugger.Engine.Launch;

namespace DotnetDebugger.Engine;

// Launching .NET Framework programs: the environment they get and the start in the client's terminal.
public sealed partial class DebugEngine
{
    /// <summary>
    /// NGen images (the precompiled framework assemblies) are optimized code: stepping into the framework and its locals
    /// suffer, and the runtime prefers them whatever the debugger asks for per module. Without justMyCode the framework is
    /// what one is about to step into, so it is jitted instead (slower start: all of mscorlib is jitted). With justMyCode
    /// nothing changes. A value the user provides (or inherited from the debugger's environment) is never overridden.
    /// </summary>
    private static IReadOnlyDictionary<string, string?>? FrameworkEnvironment(LaunchOptions options)
    {
        const string ZapDisable = "COMPlus_ZapDisable";
        if (options.JustMyCode || options.Environment?.ContainsKey(ZapDisable) == true || Environment.GetEnvironmentVariable(ZapDisable) != null)
            return options.Environment;
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in options.Environment ?? new Dictionary<string, string?>())
            environment[name] = value;
        environment[ZapDisable] = "1";
        return environment;
    }

    /// <summary>The debuggee created by the debugger with the terminal <paramref name="external"/> lends; see <see cref="FrameworkTerminalLaunch"/>.</summary>
    private (ExternalLaunch Launch, CorDebugProcess Process) StartInTerminal(CorDebug framework, LaunchOptions options,
        IReadOnlyDictionary<string, string?>? environment, ExternalLaunch external)
    {
        if (!OperatingSystem.IsWindows() || external.Host is not { } host)
            throw new DebuggerException("The terminal started the .NET Framework program by itself: it cannot be debugged from its start.");
        try
        {
            var (processId, resume, process) = FrameworkTerminalLaunch.Start(framework, options.Program, options.Args,
                options.WorkingDirectory ?? Path.GetDirectoryName(Path.GetFullPath(options.Program)), environment, external.ProcessId, host, Log);
            host.Started(processId);
            return (external with { ProcessId = processId, Resume = resume, Host = null }, process);
        }
        catch (Exception)
        {
            host.Started(0);
            throw;
        }
    }
}
