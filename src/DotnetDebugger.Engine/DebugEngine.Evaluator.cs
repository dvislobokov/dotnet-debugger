using System.Runtime.InteropServices;
using ClrDebug;
using DotnetDebugger.Engine.Symbols;
using DotnetDebugger.Engine.Values;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModuleMetadata = DotnetDebugger.Engine.Symbols.ModuleMetadata;

namespace DotnetDebugger.Engine;

public sealed partial class DebugEngine
{
    private static readonly string[] s_implicitNamespaces =
        ["System", "System.Collections.Generic", "System.Linq", "System.IO", "System.Threading", "System.Threading.Tasks", "System.Text"];

    private readonly Dictionary<string, (LoadedModule Module, int Token)?> _typeCache = [];

    /// <summary>Finds a loaded type by full name, preferring user code over libraries.</summary>
    private (LoadedModule Module, int Token)? FindType(string fullName)
    {
        if (_typeCache.TryGetValue(fullName, out var cached))
            return cached;
        (LoadedModule, int)? result = null;
        foreach (LoadedModule module in _modules.Values.OrderByDescending(m => m.Metadata?.HasSymbols == true).ThenBy(m => m.Id))
        {
            if (module.Metadata?.FindType(fullName) is { } token)
            {
                result = (module, token);
                break;
            }
        }
        return _typeCache[fullName] = result;
    }

    /// <summary>
    /// Interprets C# expressions against a stack frame. Syntax comes from Roslyn; semantics are a pragmatic subset:
    /// host-side arithmetic on values read from the debuggee, and func-evals for properties, indexers and methods.
    /// </summary>
    /// <param name="self">
    /// When set, names are resolved against this object instead of the frame's locals ([DebuggerDisplay] expressions).
    /// </param>
    private sealed partial class Evaluator(DebugEngine engine, FrameRef frame, bool allowCalls, Func<CorDebugValue?>? self = null)
    {
        private abstract record Operand;

        /// <param name="EnumClass">Set when <paramref name="Value"/> is the underlying value of an enum.</param>
        private sealed record HostOperand(object? Value, CorDebugClass? EnumClass = null) : Operand;

        /// <param name="IsLocation">The getter returns a storage location (variable, field, element) that can be assigned to.</param>
        private sealed record RemoteOperand(Func<CorDebugValue?> Getter, bool IsLocation = false) : Operand;

        private sealed record TypeOperand(LoadedModule Module, int Token, IReadOnlyList<TypeOperand>? TypeArguments = null) : Operand
        {
            public ModuleMetadata Metadata => Module.Metadata!;
            public CorDebugClass Class => Module.Module.GetClassFromToken(new mdTypeDef(Token));
        }

        private sealed record NamespaceOperand(string Name) : Operand;

        private sealed record MethodLevel(CorDebugModule Module, ModuleMetadata Metadata, int Token, CorDebugType? Type);

        private const string ExceptionVariable = "__debugger_exception";
        private const string ReturnValueVariable = "__debugger_returnvalue";

        private readonly ValueInspector _values = engine._values;
        private readonly InspectionContext _context = new(frame.ThreadId, frame);
        private List<(string Name, Func<CorDebugValue?> Getter)>? _locals;
        private Operand? _conditionalReceiver;
        private List<CorDebugType>? _explicitTypeArguments;

        private List<(string Name, Func<CorDebugValue?> Getter)> Locals =>
            _locals ??= self != null ? [("this", self)] : engine.GetLocalRefs(frame);

        // ---------------------------------------------------------------- entry points

        public VariableInfo EvaluateToVariable(string expression, DisplayOptions? baseOptions = null)
        {
            (string code, DisplayOptions options) = SplitFormatSpecifiers(expression, baseOptions ?? DisplayOptions.Default);
            return ToVariable(expression, Run(code), options);
        }

        /// <summary>Evaluates to a host value (enums as their underlying value); throws for non-primitive objects.</summary>
        public object? EvaluateToHost(string expression) => ToHost(Run(expression));

        /// <summary>Display text of the value; <paramref name="quoteStrings"/> is what ",nq" would turn off anyway.</summary>
        public string EvaluateToText(string expression, bool quoteStrings)
        {
            (string code, DisplayOptions options) = SplitFormatSpecifiers(expression, new DisplayOptions(NoQuotes: !quoteStrings));
            return ToVariable(expression, Run(code), options).Value;
        }

        // "expression,h" / ",nq" / ",raw" / ",d": the debugger's format specifiers, which are not C#
        private static (string Code, DisplayOptions Options) SplitFormatSpecifiers(string expression, DisplayOptions options)
        {
            while (true)
            {
                int comma = LastTopLevelComma(expression);
                if (comma < 0)
                    return (expression, options);
                string specifier = expression[(comma + 1)..].Trim();
                if (specifier.Length == 0 || !specifier.All(char.IsAsciiLetter))
                    return (expression, options);

                options = specifier switch
                {
                    "h" or "x" or "X" => options with { Hex = true },
                    "d" => options with { Hex = false },
                    "nq" => options with { NoQuotes = true },
                    "raw" => options with { Raw = true },
                    _ => throw new DebuggerException($"Unknown format specifier '{specifier}'. Supported: h, d, nq, raw."),
                };
                expression = expression[..comma];
            }
        }

        private static int LastTopLevelComma(string text)
        {
            int depth = 0, last = -1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c is '"' or '\'')
                {
                    for (i++; i < text.Length && text[i] != c; i++)
                        if (text[i] == '\\')
                            i++;
                }
                else if (c is '(' or '[' or '{')
                    depth++;
                else if (c is ')' or ']' or '}')
                    depth--;
                else if (c == ',' && depth <= 0)
                    last = i;
            }
            return last;
        }

        public void Assign(Func<CorDebugValue?> location, string expression)
        {
            try
            {
                AssignCore(location, Run(expression));
            }
            catch (EvalFailedException e)
            {
                throw new DebuggerException(e.Message);
            }
        }

        private Operand Run(string expression)
        {
            // "$exception" is a debugger pseudo variable, not C#
            ExpressionSyntax syntax = SyntaxFactory.ParseExpression(
                expression.Replace("$exception", ExceptionVariable).Replace("$ReturnValue", ReturnValueVariable));
            Diagnostic? error = syntax.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
            if (error != null)
                throw new DebuggerException("Invalid expression: " + error.GetMessage());
            try
            {
                return Eval(syntax);
            }
            catch (EvalFailedException e)
            {
                throw new DebuggerException(e.Message);
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException e)
            {
                throw new DebuggerException(e.Message);
            }
            catch (Exception e) when (e is ArithmeticException or InvalidCastException or FormatException or COMException)
            {
                throw new DebuggerException(e.GetType().Name + ": " + e.Message);
            }
        }

        private VariableInfo ToVariable(string name, Operand operand, DisplayOptions? options = null)
        {
            switch (operand)
            {
                case HostOperand { EnumClass: { } enumClass } host when options?.Hex != true:
                    return _values.DescribeHost(name, host.Value, _values.FormatEnum(enumClass, ToBits(host.Value!)));
                case HostOperand host:
                    return _values.DescribeHost(name, host.Value, null, options);
                case RemoteOperand remote:
                    return _values.Describe(name, remote.Getter, name, _context, options);
                case TypeOperand type:
                    throw new DebuggerException($"'{type.Metadata.GetTypeName(type.Token)}' is a type, which is not valid in the given context.");
                default:
                    throw new DebuggerException($"The name '{((NamespaceOperand)operand).Name}' does not exist in the current context.");
            }
        }

        // ---------------------------------------------------------------- syntax walk

        private Operand Eval(ExpressionSyntax node)
        {
            switch (node)
            {
                case ParenthesizedExpressionSyntax p:
                    return Eval(p.Expression);
                case LiteralExpressionSyntax literal:
                    return new HostOperand(literal.Token.Value);
                case ThisExpressionSyntax:
                    return Identifier("this");
                case IdentifierNameSyntax id:
                    return Identifier(id.Identifier.ValueText);
                case PredefinedTypeSyntax predefined:
                    return ResolveType(predefined.ToString()) ?? throw new DebuggerException($"Type '{predefined}' not found.");
                case MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression, Name: GenericNameSyntax genericMember } genericAccess:
                {
                    string prefix = Eval(genericAccess.Expression) switch
                    {
                        NamespaceOperand ns => ns.Name + ".",
                        TypeOperand outer => outer.Metadata.GetTypeName(outer.Token) + ".",
                        _ => throw new DebuggerException("Generic members are not supported in expressions."),
                    };
                    string name = prefix + genericMember.Identifier.ValueText + "`" + genericMember.TypeArgumentList.Arguments.Count;
                    TypeOperand definition = ResolveQualifiedType(name) ?? throw new DebuggerException($"Type '{genericAccess}' not found.");
                    return definition with { TypeArguments = genericMember.TypeArgumentList.Arguments.Select(ResolveType).ToList() };
                }
                case MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression } member:
                    return Member(Eval(member.Expression), SimpleName(member.Name));
                case MemberBindingExpressionSyntax binding:
                    return Member(_conditionalReceiver ?? throw new DebuggerException("Invalid expression."), SimpleName(binding.Name));
                case ElementBindingExpressionSyntax binding:
                    return ElementAccess(_conditionalReceiver ?? throw new DebuggerException("Invalid expression."), binding.ArgumentList);
                case ConditionalAccessExpressionSyntax conditional:
                    return ConditionalAccess(conditional);
                case InvocationExpressionSyntax invocation:
                    return Invoke(invocation);
                case ElementAccessExpressionSyntax element:
                    return ElementAccess(Eval(element.Expression), element.ArgumentList);
                case PrefixUnaryExpressionSyntax unary when unary.Kind() is not (SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression):
                    return Unary(unary);
                case CastExpressionSyntax cast:
                    return Cast(Eval(cast.Expression), cast.Type);
                case ConditionalExpressionSyntax ternary:
                    return ToHost(Eval(ternary.Condition)) is true ? Eval(ternary.WhenTrue) : Eval(ternary.WhenFalse);
                case BinaryExpressionSyntax binary:
                    return Binary(binary);
                default:
                    return EvalExtended(node) ?? throw new DebuggerException($"Expressions of kind '{node.Kind()}' are not supported.");
            }
        }

        private static string SimpleName(SimpleNameSyntax name) => name is IdentifierNameSyntax
            ? name.Identifier.ValueText
            : throw new DebuggerException("Generic names are not supported in expressions.");

        private Operand Identifier(string name)
        {
            foreach ((string localName, Func<CorDebugValue?> getter) in Locals)
                if (localName == name)
                    return new RemoteOperand(getter, IsLocation: true);

            if (name == "this")
                throw new DebuggerException("Keyword 'this' is not available in the current context.");
            if (name == ExceptionVariable)
            {
                Func<CorDebugValue?> exception = () => engine.RequireProcess().GetThread(frame.ThreadId).CurrentException;
                if (exception() == null)
                    throw new DebuggerException("No exception is being processed on this thread.");
                return new RemoteOperand(engine.Stabilize(exception));
            }
            if (name == ReturnValueVariable)
            {
                if (engine._returnValue is not { } returned || returned.ThreadId != frame.ThreadId)
                    throw new DebuggerException("$ReturnValue is only available right after a method has returned.");
                return returned.Getter != null ? new RemoteOperand(returned.Getter) : new HostOperand(returned.HostValue, returned.EnumClass);
            }

            // members of "this", then static members of the type the code belongs to
            if (Locals.FirstOrDefault(l => l.Name == "this") is { Getter: { } thisGetter }
                && TryMember(new RemoteOperand(thisGetter), name) is { } instanceMember)
            {
                return instanceMember;
            }
            if (EnclosingType() is { } enclosing && TryMember(enclosing, name) is { } staticMember)
                return staticMember;

            return (Operand?)ResolveType(name) ?? new NamespaceOperand(name);
        }

        private TypeOperand? EnclosingType()
        {
            if (self != null)
            {
                CorDebugValue? target = self() is { } v ? ValueInspector.Unwrap(v, out _) : null;
                ValueInspector.TypeLevel? level = target == null ? null : _values.GetTypeLevels(target).FirstOrDefault();
                return level != null && engine._modules.TryGetValue(level.Type.Class.Module.BaseAddress.Value, out LoadedModule? owner)
                    ? new TypeOperand(owner, level.Token)
                    : null;
            }

            CorDebugFunction function = engine.RequireILFrame(frame).Function;
            if (!engine._modules.TryGetValue(function.Module.BaseAddress.Value, out LoadedModule? module) || module.Metadata == null)
                return null;
            int declaring = module.Metadata.GetDeclaringType((int)function.Token.Value);
            return new TypeOperand(module, module.Metadata.GetEnclosingUserType(declaring));
        }

        private TypeOperand? ResolveType(string name)
        {
            string? keyword = name switch
            {
                "bool" => "System.Boolean", "byte" => "System.Byte", "sbyte" => "System.SByte", "char" => "System.Char",
                "short" => "System.Int16", "ushort" => "System.UInt16", "int" => "System.Int32", "uint" => "System.UInt32",
                "long" => "System.Int64", "ulong" => "System.UInt64", "float" => "System.Single", "double" => "System.Double",
                "decimal" => "System.Decimal", "string" => "System.String", "object" => "System.Object",
                "nint" => "System.IntPtr", "nuint" => "System.UIntPtr",
                _ => null,
            };

            var candidates = new List<string> { keyword ?? name };
            if (keyword == null && EnclosingType() is { } enclosing)
            {
                string typeName = enclosing.Metadata.GetTypeName(enclosing.Token);
                candidates.Add(typeName + "." + name); // nested type
                string ns = enclosing.Metadata.GetTypeNamespace(enclosing.Token);
                while (ns.Length > 0)
                {
                    candidates.Add(ns + "." + name);
                    int dot = ns.LastIndexOf('.');
                    ns = dot < 0 ? "" : ns[..dot];
                }
                candidates.AddRange(s_implicitNamespaces.Select(n => n + "." + name));
            }

            foreach (string candidate in candidates)
                if (engine.FindType(candidate) is { } found)
                    return new TypeOperand(found.Module, found.Token);
            return null;
        }

        private TypeOperand ResolveType(TypeSyntax syntax) =>
            ResolveGenericType(syntax)
            ?? ResolveType(syntax.ToString().Replace(" ", ""))
            ?? throw new DebuggerException($"Type '{syntax}' not found.");

        // ---------------------------------------------------------------- member access

        private Operand Member(Operand target, string name) =>
            TryMember(target, name) ?? throw new DebuggerException(target switch
            {
                NamespaceOperand ns => $"The name '{ns.Name}.{name}' does not exist in the current context.",
                TypeOperand type => $"'{type.Metadata.GetTypeName(type.Token)}' does not contain a definition for '{name}'.",
                _ => $"'{name}' is not a member of the value.",
            });

        private Operand? TryMember(Operand target, string name)
        {
            switch (target)
            {
                case NamespaceOperand ns:
                    if (ns.Name.Count(c => c == '.') > 8)
                        return null;
                    return (Operand?)ResolveQualifiedType(ns.Name + "." + name) ?? new NamespaceOperand(ns.Name + "." + name);

                case TypeOperand type:
                    return StaticMember(type, name);

                case HostOperand { Value: null }:
                    throw new DebuggerException("NullReferenceException: the value is null.");

                case HostOperand host:
                {
                    // properties of strings and primitives are pure, so they can be read in the debugger itself
                    System.Reflection.PropertyInfo? property = host.Value!.GetType().GetProperty(name, Type.EmptyTypes);
                    return property == null ? null : new HostOperand(property.GetValue(host.Value));
                }

                default:
                    return InstanceMember((RemoteOperand)target, name);
            }
        }

        private TypeOperand? ResolveQualifiedType(string fullName) =>
            engine.FindType(fullName) is { } found ? new TypeOperand(found.Module, found.Token) : null;

        private Operand? StaticMember(TypeOperand type, string name)
        {
            ModuleMetadata metadata = type.Metadata;
            foreach (FieldDescription field in metadata.GetFields(type.Token))
            {
                if (field.Name != name || (!field.IsStatic && !field.IsLiteral))
                    continue;
                if (field.IsLiteral)
                {
                    object? constant = metadata.GetConstant(field.Token, out _);
                    return new HostOperand(constant, metadata.IsEnum(type.Token) ? type.Class : null);
                }
                int token = field.Token;
                CorDebugClass cls = type.Class;
                return new RemoteOperand(() => StaticFieldValue(cls, token), IsLocation: true);
            }

            if (metadata.GetProperties(type.Token).Any(p => p.Name == name && p.IsStatic))
                return Call(null, type, "get_" + name, [], isPropertyAccess: true);

            // nested type
            return ResolveQualifiedType(metadata.GetTypeName(type.Token) + "." + name);
        }

        private CorDebugValue StaticFieldValue(CorDebugClass cls, int fieldToken)
        {
            ICorDebugFrame? rawFrame = engine.GetILFrame(frame)?.Raw;
            var attempts = new Func<CorDebugValue>[]
            {
                () => cls.GetStaticFieldValue(fieldToken, rawFrame!),
                () => cls.GetStaticFieldValue(fieldToken, null!),
                () => cls.GetParameterizedType(CorElementType.Class, 0, []).GetStaticFieldValue(new mdFieldDef(fieldToken), rawFrame!),
                () => cls.GetParameterizedType(CorElementType.ValueType, 0, []).GetStaticFieldValue(new mdFieldDef(fieldToken), rawFrame!),
            };
            Exception? first = null;
            foreach (Func<CorDebugValue> attempt in attempts)
            {
                try
                {
                    return attempt();
                }
                catch (Exception e)
                {
                    engine.Log?.Invoke("GetStaticFieldValue attempt failed: " + e.Message);
                    first ??= e;
                }
            }
            throw new DebuggerException("The static field is not available: " + first!.Message);
        }

        private Operand? InstanceMember(RemoteOperand target, string name)
        {
            CorDebugValue value = target.Getter() ?? throw new DebuggerException("The value is not available.");
            CorDebugValue? unwrapped = ValueInspector.Unwrap(value, out bool isNull);
            if (isNull || unwrapped == null)
                throw new DebuggerException("NullReferenceException: the value is null.");

            switch (unwrapped.Type)
            {
                case CorElementType.SZArray or CorElementType.Array when name is "Length":
                    return new HostOperand(unwrapped.As<CorDebugArrayValue>().Count);
                case CorElementType.SZArray or CorElementType.Array when name is "Rank":
                    return new HostOperand(unwrapped.As<CorDebugArrayValue>().Rank);
                case CorElementType.String when name is "Length":
                    return new HostOperand(unwrapped.As<CorDebugStringValue>().Length);
            }

            if (unwrapped.Type == CorElementType.ValueType && ValueInspector.IsSpan(_values.GetTypeName(unwrapped)) && name is "Length" or "IsEmpty")
            {
                int length = _values.GetSpanLength(target.Getter);
                return new HostOperand(name == "Length" ? length : length == 0);
            }

            // Nullable<T> has no metadata-level properties worth a func-eval
            if (unwrapped.Type == CorElementType.ValueType && _values.GetTypeName(unwrapped).StartsWith("System.Nullable<", StringComparison.Ordinal))
            {
                bool hasValue = ValueInspector.ReadPrimitive(_values.GetFieldByName(unwrapped, "hasValue")!) is true;
                if (name == "HasValue")
                    return new HostOperand(hasValue);
                if (name == "Value")
                {
                    if (!hasValue)
                        throw new DebuggerException("InvalidOperationException: Nullable object must have a value.");
                    return new RemoteOperand(() => _values.GetFieldByName(target.Getter()!, "value"));
                }
            }

            // fields (auto-property backing fields count: reading them is cheaper and safer than calling the getter)
            List<ValueInspector.TypeLevel> levels = _values.GetTypeLevels(unwrapped);
            foreach (ValueInspector.TypeLevel level in levels)
            {
                foreach (FieldDescription field in level.Metadata.GetFields(level.Token))
                {
                    if (field.IsStatic || field.IsLiteral || (field.Name != name && field.Name != $"<{name}>k__BackingField"))
                        continue;
                    CorDebugClass cls = level.Type.Class;
                    int token = field.Token;
                    return new RemoteOperand(() =>
                    {
                        CorDebugValue? owner = target.Getter() is { } v ? ValueInspector.Unwrap(v, out _) : null;
                        return owner?.As<CorDebugObjectValue>().GetFieldValue(cls.Raw, new mdFieldDef(token));
                    }, IsLocation: true);
                }
            }

            foreach (MethodLevel level in MethodLevels(unwrapped))
            {
                PropertyDescription? property = level.Metadata.GetProperties(level.Token).FirstOrDefault(p => p.Name == name && !p.IsIndexer);
                if (property == null)
                    continue;
                return property.IsStatic
                    ? Call(null, new TypeOperand(ModuleOf(level), level.Token), "get_" + name, [], isPropertyAccess: true)
                    : Call(target, null, "get_" + name, [], isPropertyAccess: true);
            }

            // static fields and constants are reachable through an instance in the debugger, as a convenience
            foreach (MethodLevel level in MethodLevels(unwrapped))
                if (StaticMember(new TypeOperand(ModuleOf(level), level.Token), name) is { } member and not TypeOperand)
                    return member;
            return null;
        }

        private LoadedModule ModuleOf(MethodLevel level) => engine._modules[level.Module.BaseAddress.Value];

        private Operand ConditionalAccess(ConditionalAccessExpressionSyntax node)
        {
            Operand receiver = Eval(node.Expression);
            if (IsNull(receiver))
                return new HostOperand(null);

            Operand? saved = _conditionalReceiver;
            _conditionalReceiver = receiver;
            try
            {
                return Eval(node.WhenNotNull);
            }
            finally
            {
                _conditionalReceiver = saved;
            }
        }

        private bool IsNull(Operand operand)
        {
            switch (operand)
            {
                case HostOperand host:
                    return host.Value == null;
                case RemoteOperand remote:
                {
                    CorDebugValue? value = remote.Getter();
                    if (value == null)
                        return true;
                    ValueInspector.Unwrap(value, out bool isNull);
                    if (isNull)
                        return true;
                    // Nullable<T> without a value
                    return _values.TryReadHostValue(value, out object? host) && host == null;
                }
                default:
                    return false;
            }
        }

        // ---------------------------------------------------------------- element access

        private Operand ElementAccess(Operand target, BracketedArgumentListSyntax argumentList)
        {
            List<Operand> indices = argumentList.Arguments.Select(a => Eval(a.Expression)).ToList();
            if (target is HostOperand { Value: string text })
                return new HostOperand(text[Convert.ToInt32(ToHost(indices.Single()))]);
            if (target is not RemoteOperand remote)
                throw new DebuggerException("Cannot apply indexing to this expression.");

            CorDebugValue? unwrapped = remote.Getter() is { } v ? ValueInspector.Unwrap(v, out _) : null;
            if (unwrapped == null)
                throw new DebuggerException("NullReferenceException: the value is null.");

            if (unwrapped.Type == CorElementType.ValueType && ValueInspector.IsSpan(_values.GetTypeName(unwrapped)))
            {
                // the indexer returns "ref T", which a func-eval cannot hand back
                int index = Convert.ToInt32(ToHost(indices.Single()));
                if (index < 0 || index >= _values.GetSpanLength(remote.Getter))
                    throw new DebuggerException("IndexOutOfRangeException: Index was outside the bounds of the span.");
                SpanElement element = _values.GetSpanElements(remote.Getter, _context, index, 1).Single();
                return element.IsHost ? new HostOperand(element.HostValue) : new RemoteOperand(engine.Stabilize(element.Getter!));
            }

            if (unwrapped.Type is CorElementType.SZArray or CorElementType.Array)
            {
                var array = unwrapped.As<CorDebugArrayValue>();
                int rank = array.Rank;
                int[] dims = array.GetDimensions(rank);
                int[] position = indices.Select(i => Convert.ToInt32(ToHost(i))).ToArray();
                if (position.Length != rank)
                    throw new DebuggerException($"Wrong number of indices inside []; expected {rank}.");
                for (int d = 0; d < rank; d++)
                    if (position[d] < 0 || position[d] >= dims[d])
                        throw new DebuggerException("IndexOutOfRangeException: Index was outside the bounds of the array.");
                return new RemoteOperand(() =>
                {
                    CorDebugValue? owner = remote.Getter() is { } a ? ValueInspector.Unwrap(a, out _) : null;
                    return owner?.As<CorDebugArrayValue>().GetElement(rank, position);
                }, IsLocation: true);
            }

            if (unwrapped.Type == CorElementType.String)
            {
                string s = _values.ReadString(unwrapped)!;
                int index = Convert.ToInt32(ToHost(indices.Single()));
                if (index < 0 || index >= s.Length)
                    throw new DebuggerException("IndexOutOfRangeException: Index was outside the bounds of the string.");
                return new HostOperand(s[index]);
            }

            return Call(remote, null, "get_Item", indices, isPropertyAccess: true);
        }

        // ---------------------------------------------------------------- invocation

        private Operand Invoke(InvocationExpressionSyntax node)
        {
            if (node.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" } && node.ArgumentList.Arguments.Count == 1)
                return new HostOperand(NameOf(node.ArgumentList.Arguments[0].Expression));
            if (!allowCalls)
                throw new DebuggerException("Method calls are not evaluated in this context.");
            List<Operand> arguments = node.ArgumentList.Arguments.Select(a => Eval(a.Expression)).ToList();

            // Method<T>(...): the type arguments are given instead of inferred
            if (node.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax genericMethod } genericCall)
            {
                _explicitTypeArguments = genericMethod.TypeArgumentList.Arguments.Select(t => ToCorDebugType(ResolveType(t))).ToList();
                try
                {
                    return CallOn(Eval(genericCall.Expression), genericMethod.Identifier.ValueText, arguments);
                }
                finally
                {
                    _explicitTypeArguments = null;
                }
            }

            switch (node.Expression)
            {
                case IdentifierNameSyntax id:
                {
                    string name = id.Identifier.ValueText;
                    if (Locals.FirstOrDefault(l => l.Name == "this") is { Getter: { } thisGetter }
                        && TryCall(new RemoteOperand(thisGetter), null, name, arguments, false) is { } viaThis)
                    {
                        return viaThis;
                    }
                    if (EnclosingType() is { } enclosing && TryCall(null, enclosing, name, arguments, false) is { } viaType)
                        return viaType;
                    throw new DebuggerException($"The name '{name}' does not exist in the current context.");
                }
                case MemberAccessExpressionSyntax member:
                    return CallOn(Eval(member.Expression), SimpleName(member.Name), arguments);
                case MemberBindingExpressionSyntax binding:
                    return CallOn(_conditionalReceiver ?? throw new DebuggerException("Invalid expression."), SimpleName(binding.Name), arguments);
                default:
                    throw new DebuggerException("Only methods can be invoked.");
            }
        }

        private Operand CallOn(Operand target, string name, List<Operand> arguments) => target switch
        {
            TypeOperand type => Call(null, type, name, arguments, false),
            NamespaceOperand ns => throw new DebuggerException($"The name '{ns.Name}' does not exist in the current context."),
            HostOperand { Value: null } => throw new DebuggerException("NullReferenceException: the value is null."),
            HostOperand { Value: string } host => CallInstanceOrExtension(new RemoteOperand(Materialize(host)), name, arguments),
            HostOperand host => CallOnHostOrExtension(host, host.Value, name, arguments),
            RemoteOperand remote when IsPrimitive(remote) => CallOnHostOrExtension(remote, ToHost(remote), name, arguments),
            _ => CallInstanceOrExtension((RemoteOperand)target, name, arguments),
        };

        private Operand CallInstanceOrExtension(RemoteOperand target, string name, List<Operand> arguments) =>
            TryCall(target, null, name, arguments, isPropertyAccess: false)
            ?? TryCallExtension(target, name, arguments)
            ?? TryCall(target, null, name, arguments, isPropertyAccess: false, explicitImplementations: true)
            ?? throw new DebuggerException($"No method or extension method '{name}' takes {arguments.Count} argument(s) of these types.");

        private Operand CallOnHostOrExtension(Operand target, object? value, string name, List<Operand> arguments)
        {
            try
            {
                return CallOnHost(value, name, arguments);
            }
            catch (DebuggerException e) when (e.Message.StartsWith("No overload", StringComparison.Ordinal) || e.Message.StartsWith("The operation is only", StringComparison.Ordinal))
            {
                return TryCallExtension(target, name, arguments)
                    ?? throw new DebuggerException($"No method or extension method '{name}' takes {arguments.Count} argument(s) of these types.");
            }
        }

        private static bool IsPrimitive(RemoteOperand operand) =>
            operand.Getter() is { } value && ValueInspector.Unwrap(value, out _) is { } unwrapped && ValueInspector.ReadPrimitive(unwrapped) != null;

        // ICorDebug cannot call instance methods on primitives that are not boxed; their methods are pure, so the
        // debugger's own runtime gives the same answer.
        private Operand CallOnHost(object? value, string name, List<Operand> arguments)
        {
            if (value == null)
                throw new DebuggerException("NullReferenceException: the value is null.");
            object?[] hostArguments = arguments.Select(ToHost).ToArray();
            try
            {
                return new HostOperand(value.GetType().InvokeMember(name,
                    System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null, value, hostArguments, System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (MissingMethodException)
            {
                throw new DebuggerException($"No overload of '{name}' takes {arguments.Count} argument(s) of these types.");
            }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException != null)
            {
                throw new DebuggerException(e.InnerException.GetType().Name + ": " + e.InnerException.Message);
            }
        }

        private Operand Call(RemoteOperand? target, TypeOperand? type, string name, List<Operand> arguments, bool isPropertyAccess)
        {
            if (!allowCalls && !isPropertyAccess)
                throw new DebuggerException("Method calls are not evaluated in this context.");
            return TryCall(target, type, name, arguments, isPropertyAccess)
                ?? (target == null ? null : TryCall(target, type, name, arguments, isPropertyAccess, explicitImplementations: true))
                ?? throw new DebuggerException($"No overload of '{name}' takes {arguments.Count} argument(s) of these types.");
        }

        /// <summary>Base-type chain to search members in, including for primitives, strings and arrays.</summary>
        private List<MethodLevel> MethodLevels(CorDebugValue unwrapped)
        {
            List<MethodLevel> result = _values.GetTypeLevels(unwrapped)
                .Select(l => new MethodLevel(l.Type.Class.Module, l.Metadata, l.Token, l.Type)).ToList();
            if (result.Count > 0)
                return result;

            string clrName = unwrapped.Type is CorElementType.SZArray or CorElementType.Array ? "System.Array" : _values.ClrTypeName(unwrapped.ExactType);
            string[] chain = unwrapped.Type is CorElementType.String or CorElementType.SZArray or CorElementType.Array
                ? [clrName, "System.Object"]
                : [clrName, "System.ValueType", "System.Object"];
            foreach (string typeName in chain)
                if (engine.FindType(typeName) is { } found)
                    result.Add(new MethodLevel(found.Module.Module, found.Module.Metadata!, found.Token, null));
            return result;
        }

        /// <param name="explicitImplementations">
        /// Look at explicit interface implementations ("IList.Contains") instead of the ordinary methods. C# only sees
        /// those through the interface, so they come last: after instance and extension methods.
        /// </param>
        private Operand? TryCall(RemoteOperand? target, TypeOperand? type, string name, List<Operand> arguments, bool isPropertyAccess,
            bool explicitImplementations = false)
        {
            List<MethodLevel> levels;
            if (target != null)
            {
                CorDebugValue? self = target.Getter() is { } v ? ValueInspector.Unwrap(v, out _) : null;
                if (self == null)
                    throw new DebuggerException("NullReferenceException: the value is null.");
                levels = MethodLevels(self);
            }
            else
            {
                levels = [new MethodLevel(type!.Module.Module, type.Metadata, type.Token, null)];
            }

            // overload resolution by argument count and a simple type score; the most derived type wins
            var argumentTypes = arguments.Select(ClrTypeOf).ToList();
            (MethodLevel Level, MethodDescription Method, Dictionary<int, TypeSignature> Bindings)? best = null;
            foreach (MethodLevel level in levels)
            {
                int bestScore = 0;
                // "Interface.Method" implementations are private and only reachable by their mangled name
                List<MethodDescription> candidates = explicitImplementations
                    ? level.Metadata.GetExplicitImplementations(level.Token, name)
                    : level.Metadata.GetMethods(level.Token, name);
                foreach (MethodDescription method in candidates)
                {
                    if (method.IsStatic != (target == null) || method.ParameterTypes.Length != arguments.Count)
                        continue;
                    int score = 1 + ScoreByUnification(method, arguments, argumentTypes, out Dictionary<int, TypeSignature> bindings);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = (level, method, bindings);
                    }
                }
                if (best != null)
                    break;
            }
            if (best == null)
                return null;

            (MethodLevel chosenLevel, MethodDescription chosen, Dictionary<int, TypeSignature> inferred) = best.Value;

            // Anything that runs code (string creation) comes first: it invalidates plain ICorDebug values.
            var argumentGetters = new Func<CorDebugValue?>[arguments.Count];
            for (int i = 0; i < arguments.Count; i++)
            {
                argumentGetters[i] = arguments[i] switch
                {
                    HostOperand { Value: string } host => Materialize(host),
                    // A primitive for an "object" parameter has to be boxed by the caller. (For a generic "T value" it must
                    // not be: the func-eval takes the raw value there and misreads a box.)
                    HostOperand { Value: not null } host when chosen.ParameterTypes[i] == "System.Object" => MaterializeBoxed(host.Value),
                    RemoteOperand remote when chosen.ParameterTypes[i] == "System.Object" && IsPrimitive(remote) => MaterializeBoxed(ToHost(remote)!),
                    RemoteOperand remote => remote.Getter,
                    HostOperand host => null!, // created right before the call
                    _ => throw new DebuggerException("Types and namespaces cannot be passed as arguments."),
                };
            }

            var values = new List<CorDebugValue>();
            CorDebugValue? thisValue = null;
            if (target != null)
            {
                CorDebugValue raw = target.Getter()!;
                CorDebugValue unwrappedSelf = ValueInspector.Unwrap(raw, out _)!;
                bool isValueType = unwrappedSelf.Type is not (CorElementType.Class or CorElementType.Object or CorElementType.String
                    or CorElementType.SZArray or CorElementType.Array);
                thisValue = isValueType ? unwrappedSelf : raw;
                values.Add(thisValue);
            }
            for (int i = 0; i < arguments.Count; i++)
            {
                CorDebugValue? value = argumentGetters[i] != null
                    ? argumentGetters[i]()
                    : CreatePrimitive(ConvertForParameter(((HostOperand)arguments[i]).Value, chosen.ParameterTypes[i]));
                values.Add(value ?? throw new DebuggerException($"Argument {i + 1} is not available."));
            }

            // class type arguments come from the exact type of "this", method type arguments are inferred from the arguments
            var typeArguments = new List<CorDebugType>();
            if (target != null)
            {
                MethodLevel current = MethodLevels(ValueInspector.Unwrap(target.Getter()!, out _)!)
                    .First(l => l.Token == chosenLevel.Token && ReferenceEquals(l.Metadata, chosenLevel.Metadata));
                if (current.Type != null)
                    typeArguments.AddRange(ValueInspector.TypeArgumentsOf(current.Type));
            }
            else if (type!.TypeArguments is { Count: > 0 } classArguments)
            {
                typeArguments.AddRange(classArguments.Select(ToCorDebugType));
            }

            if (_explicitTypeArguments is { } given && chosen.GenericParameterCount > 0)
            {
                if (given.Count != chosen.GenericParameterCount)
                    throw new DebuggerException($"Method '{name}' takes {chosen.GenericParameterCount} type argument(s).");
                typeArguments.AddRange(given);
            }
            for (int g = 0; g < (_explicitTypeArguments == null ? chosen.GenericParameterCount : 0); g++)
            {
                // inferred while matching the arguments against the parameter types (IEnumerable<T> <- List<int>)
                if (!inferred.TryGetValue(g, out TypeSignature? typeArgument))
                    throw new DebuggerException($"The type arguments for method '{name}' cannot be inferred.");
                typeArguments.Add(BuildType(typeArgument));
            }

            CorDebugFunction function = chosenLevel.Module.GetFunctionFromToken(new mdMethodDef(chosen.Token));
            CorDebugValue? result = ((IEvalHost)engine).CallFunction(frame.ThreadId, function, typeArguments.ToArray(), values.ToArray());
            return FromResult(result);
        }

        private Operand FromResult(CorDebugValue? result)
        {
            if (result == null)
                return new HostOperand(null);
            if (_values.TryReadHostValue(result, out object? host))
            {
                return host is ValueInspector.EnumBits bits
                    ? new HostOperand(bits.Underlying, bits.Class)
                    : new HostOperand(host);
            }
            return new RemoteOperand(engine.Stabilize(() => result));
        }

        private string? ClrTypeOf(Operand operand) => operand switch
        {
            HostOperand host => host.Value?.GetType().FullName,
            RemoteOperand remote => remote.Getter() is { } v ? _values.GetClrTypeName(v) : null,
            _ => null,
        };

        private static readonly HashSet<string> s_numericTypes =
        [
            "System.SByte", "System.Byte", "System.Int16", "System.UInt16", "System.Int32", "System.UInt32",
            "System.Int64", "System.UInt64", "System.Single", "System.Double", "System.Char",
        ];

        // 0 disqualifies the overload
        private int Score(MethodDescription method, List<Operand> arguments, List<string?> argumentTypes)
        {
            int total = 0;
            for (int i = 0; i < arguments.Count; i++)
            {
                string parameter = method.ParameterTypes[i];
                string? argument = argumentTypes[i];
                bool parameterIsValueType = s_numericTypes.Contains(parameter) || parameter is "System.Boolean" or "System.Decimal";
                int score;
                if (parameter.StartsWith("ref ", StringComparison.Ordinal) || parameter.EndsWith('*'))
                    score = 0;
                else if (argument == null)
                    score = parameterIsValueType ? 0 : 3;
                else if (argument == parameter)
                    score = 6;
                else if (parameter.StartsWith('!'))
                    score = 4;
                else if (s_numericTypes.Contains(parameter) && s_numericTypes.Contains(argument) && arguments[i] is HostOperand)
                    score = IsWidening(argument, parameter) ? 3 : 0;
                else if (parameter == "System.Object")
                    score = 1;
                else if (parameterIsValueType || s_numericTypes.Contains(argument) || argument is "System.Boolean" or "System.String")
                    score = 0;
                else
                    score = 2; // some other reference type: base class or interface, trust the user
                if (score == 0)
                    return -1;
                total += score;
            }
            return total;
        }

        private static bool IsWidening(string from, string to)
        {
            string[] order = ["System.SByte", "System.Byte", "System.Int16", "System.UInt16", "System.Char", "System.Int32",
                "System.UInt32", "System.Int64", "System.UInt64", "System.Single", "System.Double"];
            return Array.IndexOf(order, from) <= Array.IndexOf(order, to);
        }

        private static object? ConvertForParameter(object? value, string parameterType)
        {
            if (value == null || !s_numericTypes.Contains(parameterType) || value is bool)
                return value;
            Type? type = Type.GetType(parameterType);
            return type == null || type == value.GetType() ? value : Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- creating values in the debuggee

        private Func<CorDebugValue?> Materialize(HostOperand host)
        {
            if (host.Value is string text)
            {
                CorDebugValue? created = engine.RunEval(frame.ThreadId, eval => eval.NewString(text));
                return engine.Stabilize(() => created);
            }
            object? value = host.Value;
            return () => CreatePrimitive(value);
        }

        private Func<CorDebugValue?> MaterializeBoxed(object value)
        {
            TypeOperand type = ResolveQualifiedType(value.GetType().FullName!) ?? throw new DebuggerException($"Type '{value.GetType()}' not found.");
            CorDebugClass cls = type.Class;
            CorDebugValue? box = engine.RunEval(frame.ThreadId, eval => eval.NewParameterizedObjectNoConstructor(cls.Raw, 0, []));
            Func<CorDebugValue?> boxed = engine.Stabilize(() => box);

            // the box holds a zeroed System.Int32-style struct whose only field is the raw value
            CorDebugValue slot = _values.GetFieldByName(boxed()!, "m_value") ?? throw new DebuggerException("Unexpected layout of a boxed primitive.");
            CorDebugValue template = CreatePrimitive(value);
            var bytes = new byte[template.Size];
            var pinned = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                template.As<CorDebugGenericValue>().GetValue(pinned.AddrOfPinnedObject());
            }
            finally
            {
                pinned.Free();
            }
            ValueInspector.WriteBytes(slot, bytes);
            return boxed;
        }

        private CorDebugValue CreatePrimitive(object? value)
        {
            CorDebugEval eval = engine.RequireProcess().GetThread(frame.ThreadId).CreateEval();
            if (value == null)
                return eval.CreateValue(CorElementType.Class, null!);

            (CorElementType type, byte[] bytes) = value switch
            {
                bool v => (CorElementType.Boolean, new[] { (byte)(v ? 1 : 0) }),
                char v => (CorElementType.Char, BitConverter.GetBytes(v)),
                sbyte v => (CorElementType.I1, new[] { (byte)v }),
                byte v => (CorElementType.U1, new[] { v }),
                short v => (CorElementType.I2, BitConverter.GetBytes(v)),
                ushort v => (CorElementType.U2, BitConverter.GetBytes(v)),
                int v => (CorElementType.I4, BitConverter.GetBytes(v)),
                uint v => (CorElementType.U4, BitConverter.GetBytes(v)),
                long v => (CorElementType.I8, BitConverter.GetBytes(v)),
                ulong v => (CorElementType.U8, BitConverter.GetBytes(v)),
                float v => (CorElementType.R4, BitConverter.GetBytes(v)),
                double v => (CorElementType.R8, BitConverter.GetBytes(v)),
                _ => throw new DebuggerException($"Values of type '{value.GetType().Name}' cannot be passed to the debuggee."),
            };
            CorDebugValue created = eval.CreateValue(type, null!);
            ValueInspector.WriteBytes(created, bytes);
            return created;
        }

        // ---------------------------------------------------------------- operators

        private bool TryToHost(Operand operand, out object? value, out CorDebugClass? enumClass)
        {
            value = null;
            enumClass = null;
            switch (operand)
            {
                case HostOperand host:
                    value = host.Value;
                    enumClass = host.EnumClass;
                    return true;
                case RemoteOperand remote:
                {
                    CorDebugValue? raw = remote.Getter();
                    if (raw == null)
                        throw new DebuggerException("The value is not available.");
                    if (!_values.TryReadHostValue(raw, out value))
                        return false;
                    if (value is ValueInspector.EnumBits bits)
                    {
                        value = bits.Underlying;
                        enumClass = bits.Class;
                    }
                    return true;
                }
                default:
                    ToVariable("", operand); // throws the "is a type/namespace" error
                    return false;
            }
        }

        private object? ToHost(Operand operand) => TryToHost(operand, out object? value, out _)
            ? value
            : throw new DebuggerException("The operation is only supported for primitive values and strings.");

        private static ulong ToBits(object value) => value is ulong u ? u : unchecked((ulong)Convert.ToInt64(value));

        private Operand Unary(PrefixUnaryExpressionSyntax node)
        {
            dynamic? operand = ToHost(Eval(node.Operand));
            return new HostOperand(node.Kind() switch
            {
                SyntaxKind.UnaryMinusExpression => -operand,
                SyntaxKind.UnaryPlusExpression => +operand,
                SyntaxKind.LogicalNotExpression => !operand,
                SyntaxKind.BitwiseNotExpression => ~operand,
                _ => throw new DebuggerException($"Operator '{node.OperatorToken.Text}' is not supported."),
            });
        }

        private Operand Binary(BinaryExpressionSyntax node)
        {
            switch (node.Kind())
            {
                case SyntaxKind.LogicalAndExpression:
                    return new HostOperand(ToHost(Eval(node.Left)) is true && ToHost(Eval(node.Right)) is true);
                case SyntaxKind.LogicalOrExpression:
                    return new HostOperand(ToHost(Eval(node.Left)) is true || ToHost(Eval(node.Right)) is true);
                case SyntaxKind.CoalesceExpression:
                {
                    Operand left = Eval(node.Left);
                    return IsNull(left) ? Eval(node.Right) : left;
                }
                case SyntaxKind.IsExpression:
                    return new HostOperand(IsInstanceOf(Eval(node.Left), ResolveType((TypeSyntax)node.Right)));
                case SyntaxKind.AsExpression:
                {
                    Operand left = Eval(node.Left);
                    return IsInstanceOf(left, ResolveType((TypeSyntax)node.Right)) ? left : new HostOperand(null);
                }
            }

            return BinaryOperation(node.Kind(), Eval(node.Left), Eval(node.Right), node.OperatorToken.Text);
        }

        private Operand BinaryOperation(SyntaxKind kind, Operand leftOperand, Operand rightOperand, string operatorText)
        {
            bool leftIsHost = TryToHost(leftOperand, out object? l, out _);
            bool rightIsHost = TryToHost(rightOperand, out object? r, out _);
            if (!leftIsHost || !rightIsHost)
            {
                // objects only support reference comparison
                if (kind is not (SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression))
                    throw new DebuggerException($"Operator '{operatorText}' cannot be applied to these operands.");
                bool same = AddressOf(leftOperand, leftIsHost, l) == AddressOf(rightOperand, rightIsHost, r);
                return new HostOperand(kind == SyntaxKind.EqualsExpression ? same : !same);
            }

            dynamic? a = l, b = r;
            return new HostOperand(kind switch
            {
                SyntaxKind.AddExpression => a + b,
                SyntaxKind.SubtractExpression => a - b,
                SyntaxKind.MultiplyExpression => a * b,
                SyntaxKind.DivideExpression => a / b,
                SyntaxKind.ModuloExpression => a % b,
                SyntaxKind.EqualsExpression => a == b,
                SyntaxKind.NotEqualsExpression => a != b,
                SyntaxKind.LessThanExpression => a < b,
                SyntaxKind.LessThanOrEqualExpression => a <= b,
                SyntaxKind.GreaterThanExpression => a > b,
                SyntaxKind.GreaterThanOrEqualExpression => a >= b,
                SyntaxKind.BitwiseAndExpression => a & b,
                SyntaxKind.BitwiseOrExpression => a | b,
                SyntaxKind.ExclusiveOrExpression => a ^ b,
                SyntaxKind.LeftShiftExpression => a << b,
                SyntaxKind.RightShiftExpression => a >> b,
                _ => throw new DebuggerException($"Operator '{operatorText}' is not supported."),
            });
        }

        private static ulong AddressOf(Operand operand, bool isHost, object? hostValue)
        {
            if (isHost)
                return hostValue == null ? 0UL : throw new DebuggerException("Objects can only be compared with other objects or null.");
            CorDebugValue value = ((RemoteOperand)operand).Getter()!;
            if (value is CorDebugReferenceValue reference)
                return reference.IsNull ? 0 : reference.Value.Value;
            return value.Address.Value;
        }

        private bool IsInstanceOf(Operand operand, TypeOperand type)
        {
            if (operand is not RemoteOperand remote)
                throw new DebuggerException("Type tests are only supported for objects of the debuggee.");
            CorDebugValue? unwrapped = remote.Getter() is { } v ? ValueInspector.Unwrap(v, out _) : null;
            if (unwrapped == null)
                return false;
            List<MethodLevel> levels = MethodLevels(unwrapped);
            if (levels.Any(l => l.Token == type.Token && ReferenceEquals(l.Metadata, type.Metadata)))
                return true;
            string wanted = type.Metadata.GetTypeName(type.Token);
            return levels.Any(l => l.Metadata.GetInterfaceNames(l.Token).Contains(wanted));
        }

        private Operand Cast(Operand operand, TypeSyntax typeSyntax)
        {
            if (typeSyntax is PredefinedTypeSyntax predefined && predefined.Keyword.Kind() is not (SyntaxKind.ObjectKeyword or SyntaxKind.StringKeyword))
            {
                dynamic? value = ToHost(operand) ?? throw new DebuggerException("NullReferenceException: the value is null.");
                return new HostOperand(predefined.Keyword.Kind() switch
                {
                    SyntaxKind.BoolKeyword => (bool)value,
                    SyntaxKind.ByteKeyword => (byte)value,
                    SyntaxKind.SByteKeyword => (sbyte)value,
                    SyntaxKind.ShortKeyword => (short)value,
                    SyntaxKind.UShortKeyword => (ushort)value,
                    SyntaxKind.IntKeyword => (int)value,
                    SyntaxKind.UIntKeyword => (uint)value,
                    SyntaxKind.LongKeyword => (long)value,
                    SyntaxKind.ULongKeyword => (ulong)value,
                    SyntaxKind.FloatKeyword => (float)value,
                    SyntaxKind.DoubleKeyword => (double)value,
                    SyntaxKind.DecimalKeyword => (decimal)value,
                    SyntaxKind.CharKeyword => (char)value,
                    _ => throw new DebuggerException($"Casts to '{predefined}' are not supported."),
                });
            }

            TypeOperand type = ResolveType(typeSyntax);
            if (type.Metadata.IsEnum(type.Token))
                return new HostOperand(ToHost(operand), type.Class);
            if (IsNull(operand))
                return operand;
            if (operand is RemoteOperand && IsInstanceOf(operand, type))
                return operand;
            throw new DebuggerException($"InvalidCastException: the value is not a '{type.Metadata.GetTypeName(type.Token)}'.");
        }

        // ---------------------------------------------------------------- assignment

        private void AssignCore(Func<CorDebugValue?> location, Operand source)
        {
            CorDebugValue target = location() ?? throw new DebuggerException("The variable is not available.");
            bool targetIsReference = target is CorDebugReferenceValue && target.Type is not (CorElementType.Ptr or CorElementType.FnPtr or CorElementType.ByRef);

            if (targetIsReference)
            {
                string? targetType = target.Type == CorElementType.String ? "System.String" : null;
                Func<CorDebugValue?>? sourceGetter = source switch
                {
                    HostOperand { Value: null } => null,
                    HostOperand { Value: string } host => Materialize(host), // runs code: "target" is stale from here on
                    RemoteOperand remote => remote.Getter,
                    _ => throw new DebuggerException("The value cannot be converted to the type of the variable."),
                };
                if (targetType == "System.String" && source is not HostOperand && ClrTypeOf(source) is not (null or "System.String"))
                    throw new DebuggerException("The value cannot be converted to 'string'.");

                ulong address = 0;
                if (sourceGetter != null)
                {
                    if (sourceGetter() is not CorDebugReferenceValue sourceReference)
                        throw new DebuggerException("The value cannot be converted to the type of the variable.");
                    address = sourceReference.IsNull ? 0 : sourceReference.Value.Value;
                }
                ((CorDebugReferenceValue)location()!).Value = new CORDB_ADDRESS(address);
                return;
            }

            object? value = TryToHost(source, out object? hostValue, out _)
                ? hostValue
                : throw new DebuggerException("The value cannot be converted to the type of the variable.");
            if (value == null)
                throw new DebuggerException("Cannot assign null to a value type.");

            CorDebugValue slot = location()!;
            if (slot.Type == CorElementType.ValueType && _values.GetFieldByName(slot, "value__") is { } enumSlot)
                slot = enumSlot;

            byte[] bytes = slot.Type switch
            {
                CorElementType.Boolean => value is bool b ? [(byte)(b ? 1 : 0)] : throw Mismatch(),
                CorElementType.Char => value is char c ? BitConverter.GetBytes(c) : throw Mismatch(),
                CorElementType.I1 => [(byte)Number<sbyte>()],
                CorElementType.U1 => [Number<byte>()],
                CorElementType.I2 => BitConverter.GetBytes(Number<short>()),
                CorElementType.U2 => BitConverter.GetBytes(Number<ushort>()),
                CorElementType.I4 => BitConverter.GetBytes(Number<int>()),
                CorElementType.U4 => BitConverter.GetBytes(Number<uint>()),
                CorElementType.I8 => BitConverter.GetBytes(Number<long>()),
                CorElementType.U8 => BitConverter.GetBytes(Number<ulong>()),
                CorElementType.R4 => BitConverter.GetBytes(Number<float>()),
                CorElementType.R8 => BitConverter.GetBytes(Number<double>()),
                _ => throw new DebuggerException("Variables of this type cannot be modified."),
            };
            ValueInspector.WriteBytes(slot, bytes);

            T Number<T>() where T : struct
            {
                if (value is bool or string or char && typeof(T) != typeof(char))
                    throw Mismatch();
                try
                {
                    return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (Exception e) when (e is OverflowException or InvalidCastException or FormatException)
                {
                    throw new DebuggerException("The value does not fit the type of the variable.");
                }
            }

            DebuggerException Mismatch() => new("The value cannot be converted to the type of the variable.");
        }
    }
}
