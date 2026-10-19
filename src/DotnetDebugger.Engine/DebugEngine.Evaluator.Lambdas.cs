using ClrDebug;
using DotnetDebugger.Engine.Symbols;
using DotnetDebugger.Engine.Values;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetDebugger.Engine;

public sealed partial class DebugEngine
{
    // Lambdas as arguments of LINQ methods. The debuggee cannot run a lambda the debugger made up (that takes
    // compiling it into the process), so the LINQ methods that take one are carried out by the debugger itself: the
    // sequence is enumerated in the debuggee, the lambda is interpreted for every element (locals, parameters and
    // members of the frame are in reach, so captures come for free), and a sequence result becomes an array in the
    // debuggee, on which the rest of the expression goes on as usual (Count(), ToList(), First().Name, ...).
    private sealed partial class Evaluator
    {
        private const int LambdaElementLimit = 10_000;

        private static readonly string[] s_lambdaMethods =
        [
            "Where", "Select", "Any", "All", "Count", "First", "FirstOrDefault", "Last", "LastOrDefault", "Single", "SingleOrDefault",
            "Sum", "Min", "Max", "Average", "OrderBy", "OrderByDescending", "TakeWhile", "SkipWhile",
        ];

        /// <summary>A lambda or anonymous method: nothing is computed until a LINQ method applies it.</summary>
        private sealed record LambdaOperand(string[] Parameters, ExpressionSyntax Body, string Text) : Operand;

        /// <summary>Parameters of the lambdas being applied, innermost last; names shadow those of the frame.</summary>
        private readonly List<Dictionary<string, Operand>> _lambdaScopes = [];

        private static LambdaOperand Lambda(AnonymousFunctionExpressionSyntax node)
        {
            string[] parameters = node switch
            {
                SimpleLambdaExpressionSyntax simple => [simple.Parameter.Identifier.ValueText],
                ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.Select(p => p.Identifier.ValueText).ToArray(),
                AnonymousMethodExpressionSyntax anonymous => (anonymous.ParameterList?.Parameters ?? default).Select(p => p.Identifier.ValueText).ToArray(),
                _ => throw new DebuggerException($"Expressions of kind '{node.Kind()}' are not supported."),
            };
            ExpressionSyntax body = node.Body switch
            {
                ExpressionSyntax expression => expression,
                BlockSyntax { Statements: [ReturnStatementSyntax { Expression: { } returned }] } => returned,
                _ => throw new DebuggerException("Only lambdas with an expression body (x => x > 2) or a single return statement are supported."),
            };
            string text = string.Join(" ", node.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return new LambdaOperand(parameters, body, text);
        }

        private Operand? LambdaParameter(string name)
        {
            for (int i = _lambdaScopes.Count - 1; i >= 0; i--)
                if (_lambdaScopes[i].TryGetValue(name, out Operand? value))
                    return value;
            return null;
        }

        private Operand Apply(LambdaOperand lambda, params Operand[] arguments)
        {
            if (arguments.Length < lambda.Parameters.Length)
                throw new DebuggerException($"The lambda '{lambda.Text}' takes {lambda.Parameters.Length} parameter(s); this method gives it {arguments.Length}.");
            var scope = new Dictionary<string, Operand>();
            for (int i = 0; i < lambda.Parameters.Length; i++)
                scope[lambda.Parameters[i]] = Snapshot(arguments[i]);
            _lambdaScopes.Add(scope);
            try
            {
                return Eval(lambda.Body);
            }
            finally
            {
                _lambdaScopes.RemoveAt(_lambdaScopes.Count - 1);
            }
        }

        // ---------------------------------------------------------------- the LINQ methods

        /// <summary>A call with a lambda among its arguments; only the LINQ methods on a sequence are known.</summary>
        private Operand InvokeWithLambda(InvocationExpressionSyntax node, List<Operand> arguments)
        {
            (Operand? target, string? name) = node.Expression switch
            {
                MemberAccessExpressionSyntax member => (Eval(member.Expression), SimpleName(member.Name)),
                MemberBindingExpressionSyntax binding => (_conditionalReceiver, SimpleName(binding.Name)),
                _ => (null, null),
            };
            if (target == null || name == null || !s_lambdaMethods.Contains(name))
            {
                throw new DebuggerException($"Lambdas are only supported as arguments of the LINQ methods {string.Join(", ", s_lambdaMethods)} "
                    + "on a collection (list.Where(x => x > 2).Count()).");
            }
            if (arguments.Count != 1 || arguments[0] is not LambdaOperand lambda)
                throw new DebuggerException($"'{name}' with a lambda takes exactly one argument, the lambda.");
            if (target is HostOperand { Value: string } text)
                target = new RemoteOperand(Materialize(text));
            if (target is not RemoteOperand sequence || !IsSequence(sequence))
                throw new DebuggerException($"'{name}' with a lambda needs a collection of the debuggee to work on.");

            (List<Operand> elements, CorDebugType elementType) = Enumerate(sequence);
            switch (name)
            {
                case "Where":
                    return ArrayOf(elementType, Filter(elements, lambda, name));
                case "TakeWhile":
                    return ArrayOf(elementType, elements.TakeWhile((e, i) => Test(lambda, e, i, name)).ToList());
                case "SkipWhile":
                    return ArrayOf(elementType, elements.SkipWhile((e, i) => Test(lambda, e, i, name)).ToList());
                case "Select":
                {
                    List<Operand> selected = elements.Select((e, i) => Snapshot(Apply(lambda, e, new HostOperand(i)))).ToList();
                    return ArrayOf(ElementTypeOf(selected), selected);
                }
                case "Any":
                    return new HostOperand(elements.Where((e, i) => Test(lambda, e, i, name)).Any());
                case "All":
                    return new HostOperand(elements.Select((e, i) => Test(lambda, e, i, name)).All(b => b));
                case "Count":
                    return new HostOperand(Filter(elements, lambda, name).Count);
                case "First" or "FirstOrDefault" or "Last" or "LastOrDefault" or "Single" or "SingleOrDefault":
                {
                    List<Operand> matching = Filter(elements, lambda, name);
                    if (name.StartsWith("Single", StringComparison.Ordinal) && matching.Count > 1)
                        throw new DebuggerException("InvalidOperationException: Sequence contains more than one matching element.");
                    if (matching.Count == 0)
                    {
                        return name.EndsWith("OrDefault", StringComparison.Ordinal)
                            ? DefaultOf(elementType)
                            : throw new DebuggerException("InvalidOperationException: Sequence contains no matching element.");
                    }
                    return name.StartsWith("Last", StringComparison.Ordinal) ? matching[^1] : matching[0];
                }
                case "Sum" or "Min" or "Max" or "Average":
                    return Aggregate(name, elements.Select(e => ToHost(Apply(lambda, e))).ToList());
                case "OrderBy" or "OrderByDescending":
                {
                    var keyed = elements.Select((e, i) => (Element: e, Key: ToHost(Apply(lambda, e)), Index: i)).ToList();
                    keyed.Sort((a, b) =>
                    {
                        int order = CompareKeys(a.Key, b.Key);
                        if (name == "OrderByDescending")
                            order = -order;
                        return order != 0 ? order : a.Index.CompareTo(b.Index); // a stable sort, as LINQ's
                    });
                    return ArrayOf(elementType, keyed.Select(k => k.Element).ToList());
                }
                default:
                    throw new DebuggerException($"'{name}' is not supported with a lambda.");
            }
        }

        private List<Operand> Filter(List<Operand> elements, LambdaOperand predicate, string method) =>
            elements.Where((e, i) => Test(predicate, e, i, method)).ToList();

        private bool Test(LambdaOperand predicate, Operand element, int index, string method)
        {
            object? outcome = ToHost(Apply(predicate, element, new HostOperand(index)));
            return outcome is bool b ? b : throw new DebuggerException($"The lambda of '{method}' must return bool, not {ValueInspector.FormatHost(outcome).Type ?? "null"}.");
        }

        private static int CompareKeys(object? a, object? b)
        {
            if (a == null || b == null)
                return a == null ? (b == null ? 0 : -1) : 1;
            if (a is string sa && b is string sb)
                return string.CompareOrdinal(sa, sb);
            try
            {
                dynamic x = a, y = b;
                return x < y ? -1 : x > y ? 1 : 0;
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                return a is IComparable comparable ? comparable.CompareTo(b) : throw new DebuggerException("The keys of OrderBy cannot be compared.");
            }
        }

        private static Operand Aggregate(string method, List<object?> values)
        {
            if (values.Count == 0 && method != "Sum")
                throw new DebuggerException("InvalidOperationException: Sequence contains no elements.");
            if (values.Any(v => v is not (null or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal)))
                throw new DebuggerException($"The lambda of '{method}' must return a number.");
            var numbers = values.Where(v => v != null).Select(v => (dynamic)v!).ToList();
            if (numbers.Count == 0)
                return new HostOperand(method == "Sum" ? 0 : null);
            dynamic result = numbers[0];
            switch (method)
            {
                case "Sum":
                    for (int i = 1; i < numbers.Count; i++)
                        result += numbers[i];
                    return new HostOperand((object)result);
                case "Min":
                    for (int i = 1; i < numbers.Count; i++)
                        if (numbers[i] < result)
                            result = numbers[i];
                    return new HostOperand((object)result);
                case "Max":
                    for (int i = 1; i < numbers.Count; i++)
                        if (numbers[i] > result)
                            result = numbers[i];
                    return new HostOperand((object)result);
                default:
                {
                    dynamic sum = numbers[0];
                    for (int i = 1; i < numbers.Count; i++)
                        sum += numbers[i];
                    object average = sum is decimal d ? d / numbers.Count : (double)sum / numbers.Count;
                    return new HostOperand(average);
                }
            }
        }

        // ---------------------------------------------------------------- sequences and arrays

        /// <summary>The elements of a sequence of the debuggee, read through an object[] the debuggee makes of it.</summary>
        private (List<Operand> Elements, CorDebugType ElementType) Enumerate(RemoteOperand sequence)
        {
            CorDebugValue? unwrapped = sequence.Getter() is { } v ? ValueInspector.Unwrap(v, out _) : null;
            if (unwrapped == null)
                throw new DebuggerException("NullReferenceException: the value is null.");
            CorDebugType elementType = SequenceElementType(sequence, unwrapped);

            Func<CorDebugValue?> array = ((IEvalHost)engine).EnumerateToArray(sequence.Getter, _context);
            CorDebugArrayValue Array() => ValueInspector.Unwrap(array()!, out _)!.As<CorDebugArrayValue>();
            int count = Array().Count;
            if (count > LambdaElementLimit)
                throw new DebuggerException($"The sequence has {count} elements; lambdas are evaluated by the debugger over at most {LambdaElementLimit}.");

            var elements = new List<Operand>(count);
            for (int i = 0; i < count; i++)
            {
                int index = i;
                elements.Add(new RemoteOperand(() => Array().GetElementAtPosition(index)));
            }
            return (elements, elementType);
        }

        /// <summary>Whether the value can be enumerated: handing anything else to Enumerable.Cast would crash the debuggee.</summary>
        private bool IsSequence(RemoteOperand operand)
        {
            CorDebugValue? unwrapped = operand.Getter() is { } v ? ValueInspector.Unwrap(v, out _) : null;
            if (unwrapped == null)
                return true; // reported as null by Enumerate
            if (unwrapped.Type is CorElementType.SZArray or CorElementType.Array or CorElementType.String)
                return true;
            if (unwrapped.Type is not (CorElementType.Class or CorElementType.ValueType or CorElementType.Object))
                return false;
            return ClosedSignatures(operand).Any(s => s.Name == "System.Collections.IEnumerable");
        }

        /// <summary>T of the IEnumerable&lt;T&gt; the value is; object when it is only an IEnumerable.</summary>
        private CorDebugType SequenceElementType(RemoteOperand sequence, CorDebugValue unwrapped)
        {
            if (unwrapped.Type is CorElementType.SZArray)
                return unwrapped.ExactType.FirstTypeParameter;
            if (unwrapped.Type == CorElementType.String)
                return BuildType(new TypeSignature("System.Char", []));
            TypeSignature? enumerable = ClosedSignatures(sequence)
                .FirstOrDefault(s => s.Name == "System.Collections.Generic.IEnumerable`1" && s.Arguments.Count == 1 && !s.Arguments[0].IsTypeVariable);
            return BuildType(enumerable?.Arguments[0] ?? new TypeSignature("System.Object", []));
        }

        /// <summary>The one type all elements are (the first's exact type, or what the host values are); object otherwise.</summary>
        private CorDebugType ElementTypeOf(List<Operand> elements)
        {
            TypeSignature? common = null;
            foreach (Operand element in elements)
            {
                TypeSignature? signature = element switch
                {
                    HostOperand { EnumClass: { } enumClass, Value: not null } => new TypeSignature(EnumTypeName(enumClass) ?? "System.Object", []),
                    HostOperand { Value: { } hostValue } => new TypeSignature(hostValue.GetType().FullName!, []),
                    HostOperand => null,
                    RemoteOperand remote when remote.Getter() is { } raw && ValueInspector.Unwrap(raw, out _) is { } value => ClosedSignature(value.ExactType),
                    RemoteOperand => null,
                    _ => throw new DebuggerException("Select must return values."),
                };
                if (signature == null)
                    continue;
                if (common == null)
                    common = signature;
                else if (common.ToString() != signature.ToString()) // records compare their argument lists by reference
                    return BuildType(new TypeSignature("System.Object", []));
            }
            return BuildType(common ?? new TypeSignature("System.Object", []));
        }

        private Operand DefaultOf(CorDebugType elementType)
        {
            if (elementType.Type is CorElementType.Class or CorElementType.String or CorElementType.SZArray or CorElementType.Array or CorElementType.Object)
                return new HostOperand(null);
            if (TypeOperandOf(elementType) is { } type)
                return type.Metadata.IsValueType(type.Token) ? CreateObject(type, []) : new HostOperand(null);
            return new HostOperand(ValueInspector.ReadPrimitive(CreatePrimitive(0)) is { } zero && elementType.Type == CorElementType.I4 ? zero : null);
        }
    }
}
