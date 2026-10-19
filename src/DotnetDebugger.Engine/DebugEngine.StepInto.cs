using ClrDebug;
using DotnetDebugger.Engine.Symbols;
using DotnetDebugger.Engine.Values;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ModuleMetadata = DotnetDebugger.Engine.Symbols.ModuleMetadata;

namespace DotnetDebugger.Engine;

/// <summary>One call of the current statement a "step in" can be aimed at. Columns are 1-based; 0 when the source could not be read.</summary>
public sealed record StepInTargetInfo(int Id, string Label, int Line, int Column, int EndLine, int EndColumn);

public sealed partial class DebugEngine
{
    /// <summary>
    /// A call of the statement at the top of a thread's stack: the call instruction, or for a lambda the method the
    /// statement makes a delegate of.
    /// </summary>
    /// <param name="CallSite">IL offset of the call instruction.</param>
    /// <param name="LowerBound">Where the previous call of the statement ends: a return address maps to an IL offset at or shortly before its call.</param>
    private sealed record StepInTarget(int ThreadId, ModuleKey Module, int MethodToken, int CallSite, int LowerBound, CallTarget Call, LoadedModule? LambdaModule, int LambdaOffset)
    {
        public bool IsLambda => LambdaModule != null;
    }

    private readonly Dictionary<int, StepInTarget> _stepInTargets = [];

    /// <summary>The call a step in is on its way to; cleared on arrival.</summary>
    private StepInTarget? _activeStepInTarget;

    /// <summary>A step into a lambda: a breakpoint on its first line, while the statement is stepped over.</summary>
    private CodeLocation? _stepInTargetBreakpoint;

    // ---------------------------------------------------------------- the targets of a statement

    /// <summary>
    /// The calls of the statement a frame is stopped at, in the order they are made: methods, constructors, property
    /// getters, delegate invocations and the lambdas the statement passes on. Empty for a frame that is not at the top
    /// of its thread (steps start there) or has no source.
    /// </summary>
    public IReadOnlyList<StepInTargetInfo> GetStepInTargets(int frameId)
    {
        lock (_lock)
        {
            RequireStopped();
            FrameRef frame = ResolveFrame(frameId);
            if (frame != TopFrame(frame.ThreadId) || GetILFrame(frame) is not { } ilFrame)
                return [];
            CorDebugFunction function = ilFrame.Function;
            if (!_modules.TryGetValue(function.Module, out LoadedModule? module) || module.Metadata is not { HasSymbols: true } metadata)
                return [];
            int token = (int)function.Token.Value;
            int ip = ilFrame.IP.pnOffset;
            if (metadata.GetStepRange(token, ip, function.ILCode.Size) is not { } range
                || metadata.GetSourceLocation(token, ip, out _) is not { } statement)
            {
                return [];
            }

            var calls = new List<(CallInstruction Instruction, CallTarget Target)>();
            foreach (CallInstruction instruction in metadata.GetCallInstructions(token))
            {
                if (instruction.Offset < range.Start || instruction.Offset >= range.End)
                    continue;
                if (metadata.DescribeCallTarget(instruction.Target) is not { } target || target.IsPropertySetter)
                    continue;
                // a delegate of something that is not a lambda (a method group): the call is somewhere else
                if (instruction.Kind == CallKind.LoadFunction && (target.MethodToken == null || !metadata.IsDeclaredInCompilerGeneratedType(target.MethodToken.Value)
                    && !target.MethodName.StartsWith('<')))
                {
                    continue;
                }
                calls.Add((instruction, target));
            }
            if (calls.Count == 0)
                return [];

            List<SyntaxCandidate>? candidates = ReadStatementSyntax(module, statement);
            var result = new List<StepInTargetInfo>();
            int cursor = 0;
            int previousCallEnd = range.Start;
            foreach ((CallInstruction instruction, CallTarget target) in calls)
            {
                int lowerBound = previousCallEnd;
                if (instruction.Kind != CallKind.LoadFunction)
                    previousCallEnd = instruction.Offset + 5;

                SyntaxCandidate? syntax = null;
                if (candidates != null)
                {
                    int index = FindCandidate(candidates, cursor, instruction.Kind, target);
                    if (index < 0)
                        continue; // not in the source: a helper the compiler calls (string concatenation, awaiters)
                    syntax = candidates[index];
                    cursor = index + 1;
                }

                LoadedModule? lambdaModule = null;
                int lambdaOffset = 0;
                if (instruction.Kind == CallKind.LoadFunction)
                {
                    if (metadata.GetFirstLineOffset(target.MethodToken!.Value) is not { } first)
                        continue;
                    lambdaModule = module;
                    lambdaOffset = first;
                }

                string label = syntax?.LambdaText ?? Label(target, syntax, frame);
                int id = ++_nextHandle;
                _stepInTargets[id] = new StepInTarget(frame.ThreadId, module.Key, token, instruction.Offset, lowerBound, target, lambdaModule, lambdaOffset);
                result.Add(syntax == null
                    ? new StepInTargetInfo(id, label, statement.Line, 0, statement.Line, 0)
                    : new StepInTargetInfo(id, label, syntax.Line, syntax.Column, syntax.EndLine, syntax.EndColumn));
            }
            return result;
        }
    }

    /// <summary>"Pricing.Total(lines, discount)", "new Pricing(rate)", "Order.Total.get", "multiply(x)" for a delegate.</summary>
    private string Label(CallTarget target, SyntaxCandidate? syntax, FrameRef frame)
    {
        string[] parameters = target.ParameterNames ?? ResolveParameterNames(target, frame) ?? target.ParameterTypes.Select(SimpleTypeName).ToArray();
        string arguments = "(" + string.Join(", ", parameters) + ")";
        if (target.IsConstructor)
            return "new " + target.TypeDisplayName + arguments;
        if (target.IsPropertyGetter)
            return target.TypeDisplayName + "." + (target.MethodName == "get_Item" ? "this[]" : target.MethodName[4..] + ".get");
        if (target.MethodName == "Invoke" && syntax is { Kind: CandidateKind.Invocation, Name: { } delegateName })
            return delegateName + arguments;
        return target.TypeDisplayName + "." + ModuleMetadata.PrettifyMethodName(target.MethodName) + arguments;
    }

    private static string SimpleTypeName(string clrName)
    {
        int bracket = clrName.IndexOf('<');
        string name = bracket < 0 ? clrName : clrName[..bracket];
        name = name[(name.LastIndexOf('.') + 1)..];
        int tick = name.IndexOf('`');
        return (tick < 0 ? name : name[..tick]) + (bracket < 0 ? "" : clrName[bracket..]);
    }

    // A method of another module: its parameter names live in that module's metadata.
    private string[]? ResolveParameterNames(CallTarget target, FrameRef frame)
    {
        try
        {
            if (FindType(target.TypeName, AppDomainOf(frame)) is not { } found || found.Module.Metadata is not { } metadata)
                return null;
            List<MethodDescription> methods = metadata.GetMethods(found.Token, target.MethodName)
                .Where(m => m.ParameterTypes.Length == target.ParameterTypes.Length).ToList();
            MethodDescription? method = methods.FirstOrDefault(m => m.ParameterTypes.SequenceEqual(target.ParameterTypes)) ?? methods.FirstOrDefault();
            return method == null ? null : metadata.GetMethodParameters(method.Token).ParameterNames;
        }
        catch (Exception e)
        {
            Log?.Invoke($"Parameter names of {target.TypeName}.{target.MethodName} are not available: {e.Message}");
            return null;
        }
    }

    // ---------------------------------------------------------------- the statement's source

    private enum CandidateKind
    {
        Invocation,
        Property,
        Indexer,
        Constructor,
        Lambda,
    }

    /// <summary>A place in the statement's source a call instruction may belong to.</summary>
    private sealed record SyntaxCandidate(CandidateKind Kind, string? Name, int Line, int Column, int EndLine, int EndColumn, int End, int Length, string? LambdaText);

    /// <summary>
    /// The calls the statement spells out, in the order the compiler makes them. For nested calls that is inner
    /// before outer, which is the order of the places where the calls end: A(B()) ends B first.
    /// </summary>
    private List<SyntaxCandidate>? ReadStatementSyntax(LoadedModule module, SourceLocation statement)
    {
        string? text = null;
        try
        {
            string local = ToLocalPath(statement.Path);
            if (File.Exists(local))
                text = File.ReadAllText(local);
            text ??= module.Metadata?.GetEmbeddedSource(statement.Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"Cannot read {statement.Path}: {e.Message}");
        }
        if (text == null)
            return null;

        SourceText source = SourceText.From(text);
        if (statement.EndLine > source.Lines.Count || statement.Line < 1)
            return null;
        TextSpan span;
        try
        {
            int start = source.Lines.GetPosition(new LinePosition(statement.Line - 1, statement.Column - 1));
            int end = source.Lines.GetPosition(new LinePosition(statement.EndLine - 1, statement.EndColumn - 1));
            span = TextSpan.FromBounds(Math.Min(start, end), Math.Max(start, end));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        SyntaxNode root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var candidates = new List<SyntaxCandidate>();
        foreach (SyntaxNode node in root.DescendantNodes(span, descendIntoTrivia: false))
        {
            if (!node.Span.IntersectsWith(span))
                continue;
            switch (node)
            {
                case InvocationExpressionSyntax invocation:
                {
                    SyntaxNode name = invocation.Expression switch
                    {
                        MemberAccessExpressionSyntax member => member.Name,
                        MemberBindingExpressionSyntax binding => binding.Name,
                        _ => invocation.Expression,
                    };
                    string? identifier = (name as SimpleNameSyntax)?.Identifier.ValueText;
                    candidates.Add(Candidate(CandidateKind.Invocation, identifier, name.Span, invocation.Span, source));
                    break;
                }
                case ObjectCreationExpressionSyntax creation:
                    candidates.Add(Candidate(CandidateKind.Constructor, RightmostName(creation.Type), creation.Type.Span, creation.Span, source));
                    break;
                case ImplicitObjectCreationExpressionSyntax implicitCreation:
                    candidates.Add(Candidate(CandidateKind.Constructor, null, implicitCreation.NewKeyword.Span, implicitCreation.Span, source));
                    break;
                case MemberAccessExpressionSyntax member when member.Parent is not InvocationExpressionSyntax { Expression: var callee } || callee != member:
                    candidates.Add(Candidate(CandidateKind.Property, member.Name.Identifier.ValueText, member.Name.Span, member.Span, source));
                    break;
                case ElementAccessExpressionSyntax element:
                    candidates.Add(Candidate(CandidateKind.Indexer, "Item", element.ArgumentList.Span, element.Span, source));
                    break;
                case AnonymousFunctionExpressionSyntax lambda:
                {
                    string lambdaText = string.Join(" ", lambda.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    if (lambdaText.Length > 60)
                        lambdaText = lambdaText[..57] + "...";
                    candidates.Add(Candidate(CandidateKind.Lambda, null, lambda.Span, lambda.Span, source) with { LambdaText = lambdaText });
                    break;
                }
            }
        }
        candidates.Sort((a, b) => a.End != b.End ? a.End.CompareTo(b.End) : a.Length.CompareTo(b.Length));
        return candidates;
    }

    private static SyntaxCandidate Candidate(CandidateKind kind, string? name, TextSpan highlight, TextSpan whole, SourceText source)
    {
        LinePosition start = source.Lines.GetLinePosition(highlight.Start);
        LinePosition end = source.Lines.GetLinePosition(highlight.End);
        return new SyntaxCandidate(kind, name, start.Line + 1, start.Character + 1, end.Line + 1, end.Character + 1, whole.End, whole.Length, null);
    }

    private static string? RightmostName(TypeSyntax type) => type switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        PredefinedTypeSyntax predefined => predefined.Keyword.ValueText,
        _ => null,
    };

    /// <summary>The first candidate from <paramref name="cursor"/> on that the call instruction can be; -1 for none.</summary>
    private static int FindCandidate(List<SyntaxCandidate> candidates, int cursor, CallKind kind, CallTarget target)
    {
        for (int i = cursor; i < candidates.Count; i++)
        {
            SyntaxCandidate candidate = candidates[i];
            bool matches = kind switch
            {
                CallKind.LoadFunction => candidate.Kind == CandidateKind.Lambda,
                CallKind.NewObject => candidate.Kind == CandidateKind.Constructor && (candidate.Name == null || candidate.Name == SimpleName(target.TypeDisplayName)
                    || candidate.Name == KeywordFor(target.TypeName)),
                _ when target.IsPropertyGetter => target.MethodName == "get_Item"
                    ? candidate.Kind == CandidateKind.Indexer
                    : candidate.Kind == CandidateKind.Property && candidate.Name == target.MethodName[4..],
                _ => candidate.Kind == CandidateKind.Invocation
                    && (candidate.Name == ModuleMetadata.PrettifyMethodName(target.MethodName) || target.MethodName == "Invoke"),
            };
            if (matches)
                return i;
        }
        return -1;
    }

    private static string SimpleName(string displayName) => displayName[(displayName.LastIndexOf('.') + 1)..];

    private static string? KeywordFor(string clrTypeName) => clrTypeName switch
    {
        "System.String" => "string", "System.Object" => "object", "System.Int32" => "int", "System.Int64" => "long",
        "System.Double" => "double", "System.Decimal" => "decimal", "System.Boolean" => "bool", "System.Char" => "char",
        _ => null,
    };

    // ---------------------------------------------------------------- stepping to a target

    /// <summary>Arms a step towards one call of the current statement; the step itself is started by the caller.</summary>
    /// <returns>The kind of step to start: in for a call, over for a lambda (a breakpoint on its first line catches it).</returns>
    private StepKind ArmStepInTarget(CorDebugThread thread, int targetId)
    {
        if (!_stepInTargets.TryGetValue(targetId, out StepInTarget? target))
            throw new DebuggerException("Unknown step in target.");
        if (target.ThreadId != thread.Id)
            throw new DebuggerException("The step in target belongs to another thread.");
        if (thread.ActiveFrame is not CorDebugILFrame frame || frame.Function.Token.Value != (uint)target.MethodToken || KeyOf(frame.Function.Module) != target.Module)
            throw new DebuggerException("The step in target belongs to another statement.");

        if (target.IsLambda)
        {
            _stepInTargetBreakpoint = AcquireNativeBreakpoint(target.LambdaModule!, target.Call.MethodToken!.Value, target.LambdaOffset);
            return StepKind.Over;
        }
        _activeStepInTarget = target;
        return StepKind.In;
    }

    private void ClearStepInTarget()
    {
        _activeStepInTarget = null;
        if (_stepInTargetBreakpoint is { } location)
        {
            _stepInTargetBreakpoint = null;
            ReleaseNativeBreakpoint(location);
        }
    }

    /// <summary>
    /// A step in just entered <paramref name="entered"/>: is that the call the user picked? Decided by where the
    /// statement's method is (its nearest frame below: glue such as an async method's kick-off stub or a delegate's
    /// Invoke may sit in between) and, for anything but a delegate's Invoke, by the method's name as the user knows
    /// it (a virtual call enters an override of the same name, an async method is entered through its MoveNext).
    /// The return address maps to an IL offset only approximately: the call instruction, or the one before it. So
    /// "at the call" means after the previous call of the statement and not past this one.
    /// </summary>
    private bool IsStepInTargetCall(CorDebugILFrame entered, StepInTarget target)
    {
        try
        {
            CorDebugILFrame? caller = null;
            for (CorDebugFrame? up = entered.Caller; up != null && caller == null; up = TryGet(() => up.Caller))
            {
                if (up is CorDebugILFrame ilFrame && ilFrame.Function.Token.Value == (uint)target.MethodToken && KeyOf(ilFrame.Function.Module) == target.Module)
                    caller = ilFrame;
            }
            if (caller == null)
                return false;
            int ip = caller.IP.pnOffset;
            if (ip < target.LowerBound || ip > target.CallSite + 5)
                return false;
            if (target.Call.MethodName == "Invoke")
                return true;
            string? name = GetMethodName(entered)?.TrimEnd('(', ')');
            if (name == null)
                return true;
            name = name[(name.LastIndexOf('.') + 1)..];
            int generic = name.IndexOf('<');
            if (generic > 0)
                name = name[..generic];
            return name == ModuleMetadata.PrettifyMethodName(target.Call.MethodName)
                || (target.Call.IsConstructor && name == ".ctor")
                || (target.Call.IsPropertyGetter && name == "get");
        }
        catch (Exception e)
        {
            Log?.Invoke("Cannot tell whether the step reached its target: " + e.Message);
            return true;
        }
    }

    /// <summary>The frame is the statement's method, stopped before the call the step is aimed at.</summary>
    private bool IsBeforeStepInTarget(CorDebugILFrame frame, StepInTarget target)
    {
        try
        {
            return frame.Function.Token.Value == (uint)target.MethodToken && KeyOf(frame.Function.Module) == target.Module
                && frame.IP.pnOffset <= target.CallSite + 5;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <returns>true if the breakpoint is the one awaiting a lambda the user wants to step into.</returns>
    private bool OnStepInTargetBreakpoint(CodeLocation location)
    {
        if (_stepInTargetBreakpoint != location)
            return false;
        ClearStepInTarget();
        DeactivateStepper();
        ClearAsyncStep();
        ClearReturnSites();
        return true;
    }
}
