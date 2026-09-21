namespace DotnetDebugger.Engine;

/// <summary>The "filter" of a DAP variables request.</summary>
public enum VariableFilter
{
    All,
    Named,
    Indexed,
}

public sealed class LaunchOptions
{
    public required string Program { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
    public bool StopAtEntry { get; init; }
    public bool JustMyCode { get; init; } = true;

    /// <summary>Where to look for symbols that are not next to their module.</summary>
    public Symbols.SymbolOptions? SymbolOptions { get; init; }

    /// <summary>Path prefix in the PDB -> directory on this machine.</summary>
    public IReadOnlyDictionary<string, string>? SourceFileMap { get; init; }

    /// <summary>Step in does not enter property accessors and operators.</summary>
    public bool StepFiltering { get; init; } = true;

    /// <summary>
    /// Starts the debuggee somewhere else (e.g. in the client's terminal) instead of as a child of the debugger.
    /// The process must be created suspended. Called without any engine lock held; may block.
    /// </summary>
    public Func<ExternalLaunch>? ExternalLauncher { get; init; }
}

/// <param name="ExitCode">Completes with the exit code if whoever started the process gets to know it.</param>
public sealed record ExternalLaunch(int ProcessId, Action Resume, Task<int?>? ExitCode = null);

public sealed record StopInfo(string Reason, int ThreadId, string? Description = null, string? Text = null, int[]? BreakpointIds = null);

public sealed record BreakpointInfo(int Id, string? Path, bool Verified, int Line, int? Column, int? EndLine, int? EndColumn, string? Message);

public sealed record ThreadInfo(int Id, string Name);

/// <param name="SourceReference">Non-zero when the file is not on disk but its text can be fetched with GetSource.</param>
/// <param name="PresentationHint">"label" for separators such as "[Async Call Stack]", "subtle" for frames without source.</param>
public sealed record FrameInfo(int Id, string Name, string? SourcePath, int Line, int Column, int EndLine, int EndColumn, int SourceReference = 0,
    string? PresentationHint = null);

public sealed record ModuleLoadInfo(int Id, string Name, string Path, bool HasSymbols);

/// <param name="BreakMode">Why the debugger stopped: "always" (thrown), "unhandled" or "userUnhandled".</param>
public sealed record ExceptionData(string TypeName, string? Message, string BreakMode, string? StackTrace = null, ExceptionData? Inner = null);

public sealed record SourceFileInfo(string Path);

public sealed class DebuggerException(string message) : Exception(message);
