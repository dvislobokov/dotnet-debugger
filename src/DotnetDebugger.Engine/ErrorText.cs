using System.Text.RegularExpressions;

namespace DotnetDebugger.Engine;

/// <summary>
/// What failures of the debugging interface are called in front of the user. ICorDebug errors surface as COM
/// exceptions whose text is "Error HRESULT X has been returned from a call to a COM component.": true, and of no use
/// to somebody debugging a program. Everything that leaves the debugger as text passes through here.
/// </summary>
public static partial class ErrorText
{
    private static readonly Dictionary<string, string> s_known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CORDBG_E_PROCESS_TERMINATED"] = "the debuggee is not running",
        ["CORDBG_E_PROCESS_DETACHED"] = "the debuggee is not running",
        ["CORDBG_E_PROCESS_NOT_SYNCHRONIZED"] = "the debuggee is running",
        ["CORDBG_E_DEBUGGER_ALREADY_ATTACHED"] = "the process is already being debugged",
        ["0x800700B7"] = "the process is already being debugged", // ERROR_ALREADY_EXISTS: the debugger transport of the process is taken
        ["ERROR_ALREADY_EXISTS"] = "the process is already being debugged",
        ["CORDBG_E_FUNC_EVAL_BAD_START_POINT"] = "the thread is not at a point where the debugger can run code",
        ["CORDBG_E_ILLEGAL_AT_GC_UNSAFE_POINT"] = "the thread is not at a point where the debugger can run code",
        ["CORDBG_E_ILLEGAL_IN_PROLOG"] = "the thread is not at a point where the debugger can run code",
        ["CORDBG_E_ILLEGAL_IN_NATIVE_CODE"] = "the thread is running native code",
        ["CORDBG_E_ILLEGAL_IN_OPTIMIZED_CODE"] = "the code is optimized",
        ["CORDBG_E_ILLEGAL_IN_STACK_OVERFLOW"] = "the thread has run out of stack",
        ["CORDBG_E_FUNC_EVAL_NOT_COMPLETE"] = "an evaluation is still running",
        ["CORDBG_E_IL_VAR_NOT_AVAILABLE"] = "the variable is not available here (it may have been optimized away)",
        ["CORDBG_E_VARIABLE_IS_ACTUALLY_LITERAL"] = "the value is a constant without storage",
        ["CORDBG_E_CLASS_NOT_LOADED"] = "the type has not been loaded yet",
        ["CORDBG_E_STATIC_VAR_NOT_AVAILABLE"] = "the static field has not been initialized yet",
        ["CORDBG_E_OBJECT_NEUTERED"] = "the value is no longer available",
        ["CORDBG_E_BAD_THREAD_STATE"] = "the thread is in a state that does not allow this",
        ["CORDBG_E_THREAD_NOT_SCHEDULED"] = "the thread is not scheduled",
        ["CORDBG_E_CANT_CALL_ON_THIS_THREAD"] = "the thread cannot be used for this",
        ["CORDBG_E_UNRECOVERABLE_ERROR"] = "the debugging interface of the runtime has failed",
        ["ERROR_INVALID_PARAMETER"] = "an argument is not valid",
        ["E_INVALIDARG"] = "an argument is not valid",
        ["E_ACCESSDENIED"] = "access is denied",
        ["ERROR_ACCESS_DENIED"] = "access is denied",
        ["E_FAIL"] = "the runtime could not do that",
        ["E_NOTIMPL"] = "the runtime does not support that",
        ["S_FALSE"] = "there is nothing to return",
    };

    [GeneratedRegex(@"Error HRESULT (\S+) has been returned from a call to a COM component\.?")]
    private static partial Regex ComErrorPattern();

    /// <summary>The message of <paramref name="exception"/> in plain words.</summary>
    public static string Describe(Exception exception) => Clean(exception.Message);

    /// <summary>Replaces the COM interop boilerplate in <paramref name="message"/>; the rest of the text stays.</summary>
    public static string Clean(string message)
    {
        if (!message.Contains("HRESULT", StringComparison.Ordinal))
            return message;
        return ComErrorPattern().Replace(message, m =>
        {
            string text = Explain(m.Groups[1].Value);
            if (m.Index == 0)
                text = char.ToUpperInvariant(text[0]) + text[1..];
            return m.Index + m.Length == message.Length ? text + "." : text;
        });
    }

    /// <summary>Plain words for the name (or number) of an HRESULT.</summary>
    public static string Explain(string hresult)
    {
        // an HRESULT without a name arrives as a number
        if (int.TryParse(hresult, out int number))
            hresult = "0x" + number.ToString("X8");
        return s_known.TryGetValue(hresult, out string? text) ? text : $"the runtime reported the error {hresult}";
    }
}
