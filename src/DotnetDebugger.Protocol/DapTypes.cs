using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDebugger.Protocol;

// Subset of the Debug Adapter Protocol types used by the adapter.
// https://microsoft.github.io/debug-adapter-protocol/specification

public sealed class ErrorResponseBody
{
    public ErrorMessage? Error { get; set; }
}

public sealed class ErrorMessage
{
    public int Id { get; set; }
    public string Format { get; set; } = "";
}

public sealed class Capabilities
{
    public bool SupportsConfigurationDoneRequest { get; set; }
    public bool SupportsTerminateRequest { get; set; }
    public bool SupportsExceptionInfoRequest { get; set; }
    public bool SupportTerminateDebuggee { get; set; }
    public bool SupportsConditionalBreakpoints { get; set; }
    public bool SupportsHitConditionalBreakpoints { get; set; }
    public bool SupportsLogPoints { get; set; }
    public bool SupportsFunctionBreakpoints { get; set; }
    public bool SupportsSetVariable { get; set; }
    public bool SupportsEvaluateForHovers { get; set; }
    public bool SupportsModulesRequest { get; set; }
    public bool SupportsLoadedSourcesRequest { get; set; }
    public bool SupportsCancelRequest { get; set; }
    public bool SupportsValueFormattingOptions { get; set; }
    public bool SupportsSetExpression { get; set; }
    public bool SupportsGotoTargetsRequest { get; set; }
    public bool SupportsRestartRequest { get; set; }
    public bool SupportsExceptionFilterOptions { get; set; }
    public ExceptionBreakpointsFilter[]? ExceptionBreakpointFilters { get; set; }
}

public sealed class ExceptionBreakpointsFilter
{
    public string Filter { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Description { get; set; }
    public bool Default { get; set; }
    public bool SupportsCondition { get; set; }
    public string? ConditionDescription { get; set; }
}

public sealed class InitializeArguments
{
    public string? ClientID { get; set; }
    public string? AdapterID { get; set; }
    public bool LinesStartAt1 { get; set; } = true;
    public bool ColumnsStartAt1 { get; set; } = true;
    public bool SupportsRunInTerminalRequest { get; set; }
}

public sealed class LaunchArguments
{
    public string? Program { get; set; }
    public string[]? Args { get; set; }
    public string? Cwd { get; set; }
    public Dictionary<string, string?>? Env { get; set; }
    public bool StopAtEntry { get; set; }
    public bool? JustMyCode { get; set; }
    public bool NoDebug { get; set; }

    /// <summary>Project file or directory; the alternative to <see cref="Program"/>.</summary>
    public string? Project { get; set; }
    public bool Build { get; set; }
    public string? Configuration { get; set; }

    /// <summary>null: default profile of the project; empty: do not use launchSettings.json.</summary>
    public string? LaunchSettingsProfile { get; set; }
    public string? LaunchSettingsFilePath { get; set; }

    /// <summary>"internalConsole" (default), "integratedTerminal" or "externalTerminal".</summary>
    public string? Console { get; set; }

    /// <summary>Step over properties and operators when stepping in. Default true.</summary>
    public bool? EnableStepFiltering { get; set; }

    /// <summary>Path prefix recorded in the PDB -> directory on this machine.</summary>
    public Dictionary<string, string>? SourceFileMap { get; set; }

    public SymbolOptionsArguments? SymbolOptions { get; set; }

    /// <summary>
    /// Run without precompiled (ReadyToRun) code so that everything debugged is jitted unoptimized.
    /// Default: only if the program itself was published ReadyToRun.
    /// </summary>
    public bool? SuppressJitOptimizations { get; set; }
}

public sealed class SymbolOptionsArguments
{
    /// <summary>Directories and http(s) symbol servers.</summary>
    public string[]? SearchPaths { get; set; }
    public string? CachePath { get; set; }
    public bool SearchMicrosoftSymbolServer { get; set; }
    public bool SearchNuGetOrgSymbolServer { get; set; }
}

public sealed class ValueFormat
{
    public bool Hex { get; set; }
}

public sealed class CancelArguments
{
    public int? RequestId { get; set; }
    public string? ProgressId { get; set; }
}

public sealed class ModulesResponseBody
{
    public Module[] Modules { get; set; } = [];
    public int TotalModules { get; set; }
}

public sealed class LoadedSourcesResponseBody
{
    public Source[] Sources { get; set; } = [];
}

public sealed class VariablePresentationHint
{
    public string? Kind { get; set; }
    public string[]? Attributes { get; set; }

    /// <summary>The value is fetched by the client on demand, through the variable's variablesReference.</summary>
    public bool? Lazy { get; set; }
}

public sealed class RunInTerminalArguments
{
    public string? Kind { get; set; }
    public string? Title { get; set; }
    public string? Cwd { get; set; }
    public string[] Args { get; set; } = [];
    public Dictionary<string, string?>? Env { get; set; }
}

public sealed class RunInTerminalResponseBody
{
    public int? ProcessId { get; set; }
    public int? ShellProcessId { get; set; }
}

public sealed class AttachArguments
{
    public JsonElement ProcessId { get; set; }
    public bool? JustMyCode { get; set; }
    public Dictionary<string, string>? SourceFileMap { get; set; }

    public int GetProcessId() => ProcessId.ValueKind switch
    {
        JsonValueKind.Number => ProcessId.GetInt32(),
        JsonValueKind.String => int.Parse(ProcessId.GetString()!),
        _ => throw new InvalidOperationException("'processId' is required for attach."),
    };
}

public sealed class DisconnectArguments
{
    public bool Restart { get; set; }
    public bool? TerminateDebuggee { get; set; }
}

public sealed class Source
{
    public string? Name { get; set; }
    public string? Path { get; set; }

    /// <summary>Non-zero: the content has to be requested from the adapter ("source" request).</summary>
    public int? SourceReference { get; set; }
}

public sealed class SourceArguments
{
    public Source? Source { get; set; }
    public int SourceReference { get; set; }
}

public sealed class SourceResponseBody
{
    public string Content { get; set; } = "";
    public string? MimeType { get; set; }
}

public sealed class GotoTargetsArguments
{
    public Source Source { get; set; } = new();
    public int Line { get; set; }
    public int? Column { get; set; }
}

public sealed class GotoTarget
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    public int Line { get; set; }
    public int? Column { get; set; }
    public int? EndLine { get; set; }
    public int? EndColumn { get; set; }
}

public sealed class GotoTargetsResponseBody
{
    public GotoTarget[] Targets { get; set; } = [];
}

public sealed class GotoArguments
{
    public int ThreadId { get; set; }
    public int TargetId { get; set; }
}

public sealed class SetExpressionArguments
{
    public string Expression { get; set; } = "";
    public string Value { get; set; } = "";
    public int? FrameId { get; set; }
}

public sealed class SetExpressionResponseBody
{
    public string Value { get; set; } = "";
    public string? Type { get; set; }
    public int VariablesReference { get; set; }
    public int? IndexedVariables { get; set; }
}

public sealed class ExceptionFilterOptions
{
    public string FilterId { get; set; } = "";
    public string? Condition { get; set; }
}

public sealed class RestartArguments
{
    public JsonElement? Arguments { get; set; }
}

public sealed class SourceBreakpoint
{
    public int Line { get; set; }
    public int? Column { get; set; }
    public string? Condition { get; set; }
    public string? HitCondition { get; set; }
    public string? LogMessage { get; set; }
}

public sealed class SetBreakpointsArguments
{
    public Source Source { get; set; } = new();
    public SourceBreakpoint[]? Breakpoints { get; set; }
}

public sealed class Breakpoint
{
    public int? Id { get; set; }
    public bool Verified { get; set; }
    public string? Message { get; set; }
    public Source? Source { get; set; }
    public int? Line { get; set; }
    public int? Column { get; set; }
    public int? EndLine { get; set; }
    public int? EndColumn { get; set; }
}

public sealed class FunctionBreakpoint
{
    public string Name { get; set; } = "";
    public string? Condition { get; set; }
    public string? HitCondition { get; set; }
}

public sealed class SetFunctionBreakpointsArguments
{
    public FunctionBreakpoint[] Breakpoints { get; set; } = [];
}

public sealed class SetBreakpointsResponseBody
{
    public Breakpoint[] Breakpoints { get; set; } = [];
}

public sealed class SetExceptionBreakpointsArguments
{
    public string[] Filters { get; set; } = [];
    public ExceptionFilterOptions[]? FilterOptions { get; set; }
}

public sealed class ThreadArguments
{
    public int ThreadId { get; set; }
}

public sealed class ContinueResponseBody
{
    public bool AllThreadsContinued { get; set; } = true;
}

public sealed class Thread
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class ThreadsResponseBody
{
    public Thread[] Threads { get; set; } = [];
}

public sealed class StackTraceArguments
{
    public int ThreadId { get; set; }
    public int? StartFrame { get; set; }
    public int? Levels { get; set; }
}

public sealed class StackFrame
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public Source? Source { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public int? EndLine { get; set; }
    public int? EndColumn { get; set; }
    public string? PresentationHint { get; set; }
}

public sealed class StackTraceResponseBody
{
    public StackFrame[] StackFrames { get; set; } = [];
    public int TotalFrames { get; set; }
}

public sealed class ScopesArguments
{
    public int FrameId { get; set; }
}

public sealed class Scope
{
    public string Name { get; set; } = "";
    public string? PresentationHint { get; set; }
    public int VariablesReference { get; set; }
    public bool Expensive { get; set; }
}

public sealed class ScopesResponseBody
{
    public Scope[] Scopes { get; set; } = [];
}

public sealed class VariablesArguments
{
    public int VariablesReference { get; set; }
    public string? Filter { get; set; }
    public int? Start { get; set; }
    public int? Count { get; set; }
    public ValueFormat? Format { get; set; }
}

public sealed class Variable
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Type { get; set; }
    public string? EvaluateName { get; set; }
    public VariablePresentationHint? PresentationHint { get; set; }
    public int VariablesReference { get; set; }
    public int? NamedVariables { get; set; }
    public int? IndexedVariables { get; set; }
}

public sealed class VariablesResponseBody
{
    public Variable[] Variables { get; set; } = [];
}

public sealed class ExceptionInfoResponseBody
{
    public string ExceptionId { get; set; } = "";
    public string? Description { get; set; }
    public string BreakMode { get; set; } = "always";
    public ExceptionDetails? Details { get; set; }
}

public sealed class ExceptionDetails
{
    public string? Message { get; set; }
    public string? TypeName { get; set; }
    public string? FullTypeName { get; set; }
    public string? StackTrace { get; set; }
    public ExceptionDetails[]? InnerException { get; set; }
}

public sealed class EvaluateArguments
{
    public string Expression { get; set; } = "";
    public int? FrameId { get; set; }
    public string? Context { get; set; }
    public ValueFormat? Format { get; set; }
}

public sealed class EvaluateResponseBody
{
    public string Result { get; set; } = "";
    public string? Type { get; set; }
    public int VariablesReference { get; set; }
    public int? NamedVariables { get; set; }
    public int? IndexedVariables { get; set; }
}

public sealed class SetVariableArguments
{
    public int VariablesReference { get; set; }
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class SetVariableResponseBody
{
    public string Value { get; set; } = "";
    public string? Type { get; set; }
    public int VariablesReference { get; set; }
    public int? NamedVariables { get; set; }
    public int? IndexedVariables { get; set; }
}

public sealed class ProcessEventBody
{
    public string Name { get; set; } = "";
    public int? SystemProcessId { get; set; }
    public bool IsLocalProcess { get; set; } = true;
    public string? StartMethod { get; set; }
}

public sealed class StoppedEventBody
{
    public string Reason { get; set; } = "";
    public string? Description { get; set; }
    public int? ThreadId { get; set; }
    public string? Text { get; set; }
    public bool AllThreadsStopped { get; set; } = true;
    public int[]? HitBreakpointIds { get; set; }
}

public sealed class ContinuedEventBody
{
    public int ThreadId { get; set; }
    public bool AllThreadsContinued { get; set; } = true;
}

public sealed class ExitedEventBody
{
    public int ExitCode { get; set; }
}

public sealed class ThreadEventBody
{
    public string Reason { get; set; } = "";
    public int ThreadId { get; set; }
}

public sealed class OutputEventBody
{
    public string Category { get; set; } = "console";
    public string Output { get; set; } = "";
}

public sealed class BreakpointEventBody
{
    public string Reason { get; set; } = "changed";
    public Breakpoint Breakpoint { get; set; } = new();
}

public sealed class Module
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Path { get; set; }
    public string? SymbolStatus { get; set; }
}

public sealed class ModuleEventBody
{
    public string Reason { get; set; } = "new";
    public Module Module { get; set; } = new();
}
