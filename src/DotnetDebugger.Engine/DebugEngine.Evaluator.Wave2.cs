using System.Globalization;
using System.Text;
using ClrDebug;
using DotnetDebugger.Engine.Symbols;
using DotnetDebugger.Engine.Values;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetDebugger.Engine;

public sealed partial class DebugEngine
{
    // Assignments, object creation, typeof/default/nameof/sizeof, interpolated strings, generic type names.
    private sealed partial class Evaluator
    {
        private const string SideEffectsNotAllowed = "Expressions with side effects are not evaluated in this context.";

        private Operand? EvalExtended(ExpressionSyntax node)
        {
            switch (node)
            {
                case AssignmentExpressionSyntax assignment:
                    return Assignment(assignment);
                case PostfixUnaryExpressionSyntax postfix when postfix.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression:
                {
                    Operand before = Snapshot(Eval(postfix.Operand));
                    Assign(postfix.Operand, BinaryOperation(postfix.Kind() == SyntaxKind.PostIncrementExpression ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression,
                        before, new HostOperand(1), postfix.OperatorToken.Text));
                    return before;
                }
                case PrefixUnaryExpressionSyntax prefix when prefix.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression:
                    return Assign(prefix.Operand, BinaryOperation(prefix.Kind() == SyntaxKind.PreIncrementExpression ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression,
                        Eval(prefix.Operand), new HostOperand(1), prefix.OperatorToken.Text));
                case CheckedExpressionSyntax checkedExpression:
                {
                    bool saved = _checked;
                    _checked = checkedExpression.Kind() == SyntaxKind.CheckedExpression;
                    try
                    {
                        return Eval(checkedExpression.Expression);
                    }
                    finally
                    {
                        _checked = saved;
                    }
                }
                case TypeOfExpressionSyntax typeOf:
                    return TypeOf(ResolveType(typeOf.Type));
                case DefaultExpressionSyntax defaultOf:
                    return Default(defaultOf.Type);
                case SizeOfExpressionSyntax sizeOf:
                    return new HostOperand(SizeOf(sizeOf.Type));
                case InterpolatedStringExpressionSyntax interpolated:
                    return Interpolate(interpolated);
                case ObjectCreationExpressionSyntax creation:
                    return New(creation);
                case ArrayCreationExpressionSyntax array:
                    return NewArray(array);
                case ImplicitArrayCreationExpressionSyntax implicitArray:
                    return NewArray(null, implicitArray.Initializer.Expressions.Select(Eval).ToList());
                case GenericNameSyntax or QualifiedNameSyntax or AliasQualifiedNameSyntax:
                    return ResolveType((TypeSyntax)node);
                default:
                    return null;
            }
        }

        // ---------------------------------------------------------------- assignment

        private Operand Assignment(AssignmentExpressionSyntax node)
        {
            if (node.Kind() == SyntaxKind.SimpleAssignmentExpression)
                return Assign(node.Left, Eval(node.Right));

            SyntaxKind operation = node.Kind() switch
            {
                SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
                SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
                SyntaxKind.MultiplyAssignmentExpression => SyntaxKind.MultiplyExpression,
                SyntaxKind.DivideAssignmentExpression => SyntaxKind.DivideExpression,
                SyntaxKind.ModuloAssignmentExpression => SyntaxKind.ModuloExpression,
                SyntaxKind.AndAssignmentExpression => SyntaxKind.BitwiseAndExpression,
                SyntaxKind.OrAssignmentExpression => SyntaxKind.BitwiseOrExpression,
                SyntaxKind.ExclusiveOrAssignmentExpression => SyntaxKind.ExclusiveOrExpression,
                SyntaxKind.LeftShiftAssignmentExpression => SyntaxKind.LeftShiftExpression,
                SyntaxKind.RightShiftAssignmentExpression => SyntaxKind.RightShiftExpression,
                _ => throw new DebuggerException($"Operator '{node.OperatorToken.Text}' is not supported."),
            };
            return Assign(node.Left, BinaryOperation(operation, Eval(node.Left), Eval(node.Right), node.OperatorToken.Text));
        }

        // A value read now must not change when the variable it came from is assigned afterwards (x++).
        private Operand Snapshot(Operand operand) =>
            operand is RemoteOperand && TryToHost(operand, out object? host, out CorDebugClass? enumClass) ? new HostOperand(host, enumClass) : operand;

        /// <summary>Stores <paramref name="value"/> into a variable, field, array element, property or indexer.</summary>
        private Operand Assign(ExpressionSyntax left, Operand value)
        {
            if (!allowCalls)
                throw new DebuggerException(SideEffectsNotAllowed);
            while (left is ParenthesizedExpressionSyntax parenthesized)
                left = parenthesized.Expression;

            // setters first: evaluating a property as a value would call its getter for nothing
            switch (left)
            {
                case IdentifierNameSyntax id when Locals.All(l => l.Name != id.Identifier.ValueText):
                {
                    string name = id.Identifier.ValueText;
                    if (Locals.FirstOrDefault(l => l.Name == "this") is { Getter: { } thisGetter } && HasSetter(new RemoteOperand(thisGetter), name))
                    {
                        Call(new RemoteOperand(thisGetter), null, "set_" + name, [value], isPropertyAccess: false);
                        return Eval(left);
                    }
                    break;
                }
                case MemberAccessExpressionSyntax member when member.Name is IdentifierNameSyntax:
                {
                    Operand receiver = Eval(member.Expression);
                    string name = member.Name.Identifier.ValueText;
                    if (receiver is RemoteOperand remote && !HasField(remote, name) && HasSetter(remote, name))
                    {
                        Call(remote, null, "set_" + name, [value], isPropertyAccess: false);
                        return Eval(left);
                    }
                    if (receiver is TypeOperand type && type.Metadata.GetProperties(type.Token).Any(p => p.Name == name && p.IsStatic && p.SetterToken != 0))
                    {
                        Call(null, type, "set_" + name, [value], isPropertyAccess: false);
                        return Eval(left);
                    }
                    break;
                }
                case ElementAccessExpressionSyntax element:
                {
                    Operand receiver = Eval(element.Expression);
                    if (receiver is RemoteOperand remote && remote.Getter() is { } raw && ValueInspector.Unwrap(raw, out _) is { } unwrapped
                        && unwrapped.Type is not (CorElementType.SZArray or CorElementType.Array))
                    {
                        List<Operand> arguments = element.ArgumentList.Arguments.Select(a => Eval(a.Expression)).ToList();
                        arguments.Add(value);
                        Call(remote, null, "set_Item", arguments, isPropertyAccess: false);
                        return Eval(left);
                    }
                    break;
                }
            }

            if (Eval(left) is not RemoteOperand { IsLocation: true } location)
                throw new DebuggerException("The left-hand side of an assignment must be a variable, property or indexer.");
            AssignCore(location.Getter, value);
            return Eval(left);
        }

        private bool HasField(RemoteOperand target, string name)
        {
            CorDebugValue? unwrapped = target.Getter() is { } v ? ValueInspector.Unwrap(v, out _) : null;
            return unwrapped != null && _values.GetTypeLevels(unwrapped)
                .Any(l => l.Metadata.GetFields(l.Token).Any(f => !f.IsStatic && (f.Name == name || f.Name == $"<{name}>k__BackingField")));
        }

        private bool HasSetter(RemoteOperand target, string name)
        {
            CorDebugValue? unwrapped = target.Getter() is { } v ? ValueInspector.Unwrap(v, out _) : null;
            if (unwrapped == null)
                return false;
            PropertyDescription? property = MethodLevels(unwrapped)
                .SelectMany(l => l.Metadata.GetProperties(l.Token)).FirstOrDefault(p => p.Name == name && !p.IsStatic && !p.IsIndexer);
            if (property == null)
                return false;
            return property.SetterToken != 0 ? true : throw new DebuggerException($"Property '{name}' cannot be assigned to: it is read only.");
        }

        // ---------------------------------------------------------------- types as values

        private CorDebugType ToCorDebugType(TypeOperand type)
        {
            ICorDebugType[] arguments = (type.TypeArguments ?? []).Select(a => ToCorDebugType(a).Raw).ToArray();
            CorElementType kind = type.Metadata.IsValueType(type.Token) ? CorElementType.ValueType : CorElementType.Class;
            return type.Class.GetParameterizedType(kind, arguments.Length, arguments);
        }

        private string AssemblyQualifiedName(TypeOperand type)
        {
            string name = type.Metadata.GetReflectionName(type.Token);
            if (type.TypeArguments is { Count: > 0 } arguments)
                name += "[" + string.Join(",", arguments.Select(a => "[" + AssemblyQualifiedName(a) + "]")) + "]";
            return name + ", " + type.Metadata.AssemblyName;
        }

        private Operand TypeOf(TypeOperand type)
        {
            TypeOperand systemType = ResolveQualifiedType("System.Type") ?? throw new DebuggerException("System.Type is not loaded.");
            return Call(null, systemType, "GetType", [new HostOperand(AssemblyQualifiedName(type))], isPropertyAccess: true);
        }

        private Operand Default(TypeSyntax typeSyntax)
        {
            if (typeSyntax is PredefinedTypeSyntax predefined)
            {
                return new HostOperand(predefined.Keyword.Kind() switch
                {
                    SyntaxKind.BoolKeyword => false,
                    SyntaxKind.ByteKeyword => (byte)0,
                    SyntaxKind.SByteKeyword => (sbyte)0,
                    SyntaxKind.ShortKeyword => (short)0,
                    SyntaxKind.UShortKeyword => (ushort)0,
                    SyntaxKind.IntKeyword => 0,
                    SyntaxKind.UIntKeyword => 0u,
                    SyntaxKind.LongKeyword => 0L,
                    SyntaxKind.ULongKeyword => 0ul,
                    SyntaxKind.FloatKeyword => 0f,
                    SyntaxKind.DoubleKeyword => 0d,
                    SyntaxKind.DecimalKeyword => 0m,
                    SyntaxKind.CharKeyword => '\0',
                    _ => null,
                });
            }
            TypeOperand type = ResolveType(typeSyntax);
            if (!type.Metadata.IsValueType(type.Token))
                return new HostOperand(null);
            if (type.Metadata.IsEnum(type.Token))
                return new HostOperand(0, type.Class);
            return CreateObject(type, []);
        }

        private static int SizeOf(TypeSyntax type) => (type as PredefinedTypeSyntax)?.Keyword.Kind() switch
        {
            SyntaxKind.BoolKeyword or SyntaxKind.ByteKeyword or SyntaxKind.SByteKeyword => 1,
            SyntaxKind.ShortKeyword or SyntaxKind.UShortKeyword or SyntaxKind.CharKeyword => 2,
            SyntaxKind.IntKeyword or SyntaxKind.UIntKeyword or SyntaxKind.FloatKeyword => 4,
            SyntaxKind.LongKeyword or SyntaxKind.ULongKeyword or SyntaxKind.DoubleKeyword => 8,
            SyntaxKind.DecimalKeyword => 16,
            _ => throw new DebuggerException("sizeof is only supported for the built-in value types."),
        };

        private static string NameOf(ExpressionSyntax argument) => argument switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => throw new DebuggerException("The argument of nameof must be a name."),
        };

        /// <summary>Resolves "List&lt;int&gt;", "System.Collections.Generic.Dictionary&lt;string, int&gt;" and friends.</summary>
        private TypeOperand? ResolveGenericType(TypeSyntax syntax)
        {
            (string prefix, GenericNameSyntax? generic) = syntax switch
            {
                GenericNameSyntax g => ("", g),
                QualifiedNameSyntax { Right: GenericNameSyntax g } q => (q.Left + ".", g),
                _ => ("", null),
            };
            if (generic == null)
                return null;

            string name = prefix.Replace(" ", "") + generic.Identifier.ValueText + "`" + generic.TypeArgumentList.Arguments.Count;
            TypeOperand definition = ResolveType(name) ?? throw new DebuggerException($"Type '{syntax}' not found.");
            return definition with { TypeArguments = generic.TypeArgumentList.Arguments.Select(ResolveType).ToList() };
        }

        // ---------------------------------------------------------------- interpolated strings

        private Operand Interpolate(InterpolatedStringExpressionSyntax node)
        {
            var sb = new StringBuilder();
            foreach (InterpolatedStringContentSyntax content in node.Contents)
            {
                if (content is InterpolatedStringTextSyntax text)
                {
                    sb.Append(text.TextToken.ValueText);
                    continue;
                }

                var hole = (InterpolationSyntax)content;
                Operand value = Eval(hole.Expression);
                object? formattable = TryToHost(value, out object? host, out CorDebugClass? enumClass) && enumClass == null
                    ? host
                    : ToVariable("", value, new DisplayOptions(NoQuotes: true)).Value; // what the variables view would show
                string alignment = hole.AlignmentClause == null ? "" : "," + ToHost(Eval(hole.AlignmentClause.Value));
                string format = hole.FormatClause == null ? "" : ":" + hole.FormatClause.FormatStringToken.ValueText;
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0" + alignment + format + "}", formattable);
            }
            return new HostOperand(sb.ToString());
        }

        // ---------------------------------------------------------------- new

        private Operand New(ObjectCreationExpressionSyntax node)
        {
            if (!allowCalls)
                throw new DebuggerException(SideEffectsNotAllowed);
            TypeOperand type = ResolveType(node.Type);
            List<Operand> arguments = (node.ArgumentList?.Arguments ?? default).Select(a => Eval(a.Expression)).ToList();

            Operand created;
            if (type.Metadata.GetTypeName(type.Token) == "System.String")
            {
                // string constructors are runtime intrinsics a func-eval cannot call; the debugger's own runtime can
                created = new HostOperand(Activator.CreateInstance(typeof(string), arguments.Select(ToHost).ToArray()));
            }
            else
            {
                created = CreateObject(type, arguments);
            }

            foreach (ExpressionSyntax initializer in node.Initializer?.Expressions ?? default)
            {
                if (initializer is not AssignmentExpressionSyntax { Left: IdentifierNameSyntax member } assignment || created is not RemoteOperand instance)
                    throw new DebuggerException("Only member initializers are supported.");
                string name = member.Identifier.ValueText;
                Operand value = Eval(assignment.Right);
                if (!HasField(instance, name) && HasSetter(instance, name))
                    Call(instance, null, "set_" + name, [value], isPropertyAccess: false);
                else if (InstanceMember(instance, name) is RemoteOperand { IsLocation: true } field)
                    AssignCore(field.Getter, value);
                else
                    throw new DebuggerException($"'{name}' cannot be assigned to.");
            }
            return created;
        }

        private RemoteOperand CreateObject(TypeOperand type, List<Operand> arguments)
        {
            CorDebugType[] typeArguments = (type.TypeArguments ?? []).Select(ToCorDebugType).ToArray();
            ICorDebugType[] rawTypeArguments = typeArguments.Select(t => t.Raw).ToArray();
            CorDebugValue? result;

            if (arguments.Count == 0 && type.Metadata.IsValueType(type.Token))
            {
                CorDebugClass cls = type.Class;
                result = engine.RunEval(frame.ThreadId, eval => eval.NewParameterizedObjectNoConstructor(cls.Raw, rawTypeArguments.Length, rawTypeArguments));
            }
            else
            {
                var argumentTypes = arguments.Select(ClrTypeOf).ToList();
                MethodDescription? constructor = type.Metadata.GetMethods(type.Token, ".ctor")
                    .Where(m => !m.IsStatic && m.ParameterTypes.Length == arguments.Count)
                    .Select(m => (Method: m, Score: Score(m, arguments, argumentTypes)))
                    .Where(c => c.Score >= 0)
                    .OrderByDescending(c => c.Score)
                    .Select(c => c.Method)
                    .FirstOrDefault()
                    ?? throw new DebuggerException($"'{type.Metadata.GetTypeName(type.Token)}' has no constructor taking {arguments.Count} argument(s) of these types.");

                ICorDebugValue[] values = MaterializeArguments(constructor, arguments).Select(v => v.Raw).ToArray();
                CorDebugFunction function = type.Module.Module.GetFunctionFromToken(new mdMethodDef(constructor.Token));
                result = engine.RunEval(frame.ThreadId, eval => eval.NewParameterizedObject(function.Raw, rawTypeArguments.Length, rawTypeArguments, values.Length, values));
            }
            return new RemoteOperand(engine.Stabilize(() => result));
        }

        /// <summary>Debuggee values for call arguments. Strings first: creating them runs code, which invalidates plain values.</summary>
        private List<CorDebugValue> MaterializeArguments(MethodDescription method, List<Operand> arguments)
        {
            var getters = new Func<CorDebugValue?>?[arguments.Count];
            for (int i = 0; i < arguments.Count; i++)
            {
                getters[i] = arguments[i] switch
                {
                    RemoteOperand remote => remote.Getter,
                    HostOperand { Value: string } host => Materialize(host),
                    HostOperand => null,
                    _ => throw new DebuggerException("Types and namespaces cannot be passed as arguments."),
                };
            }

            var values = new List<CorDebugValue>();
            for (int i = 0; i < arguments.Count; i++)
            {
                CorDebugValue? value = getters[i] != null
                    ? getters[i]!()
                    : CreatePrimitive(ConvertForParameter(((HostOperand)arguments[i]).Value, method.ParameterTypes[i]));
                values.Add(value ?? throw new DebuggerException($"Argument {i + 1} is not available."));
            }
            return values;
        }

        private Operand NewArray(ArrayCreationExpressionSyntax node)
        {
            TypeSyntax elementType = node.Type.ElementType;
            List<Operand>? elements = node.Initializer?.Expressions.Select(Eval).ToList();
            ExpressionSyntax? size = node.Type.RankSpecifiers.FirstOrDefault()?.Sizes.FirstOrDefault();
            int length = size is null or OmittedArraySizeExpressionSyntax
                ? elements?.Count ?? throw new DebuggerException("An array needs a size or an initializer.")
                : Convert.ToInt32(ToHost(Eval(size)), CultureInfo.InvariantCulture);
            return NewArray(ResolveType(elementType), elements, length);
        }

        private Operand NewArray(TypeOperand? elementType, List<Operand> elements)
        {
            if (elements.Count == 0 && elementType == null)
                throw new DebuggerException("No best type found for the implicitly-typed array.");
            if (elementType == null)
            {
                string clrName = ClrTypeOf(elements[0]) ?? throw new DebuggerException("No best type found for the implicitly-typed array.");
                elementType = ResolveQualifiedType(clrName) ?? throw new DebuggerException($"Type '{clrName}' not found.");
            }
            return NewArray(elementType, elements, elements.Count);
        }

        private Operand NewArray(TypeOperand elementType, List<Operand>? elements, int length)
        {
            if (!allowCalls)
                throw new DebuggerException(SideEffectsNotAllowed);
            ICorDebugType rawElementType = ToCorDebugType(elementType).Raw;
            CorDebugValue? created = engine.RunEval(frame.ThreadId, eval => eval.NewParameterizedArray(rawElementType, 1, [length], [0]));
            var array = new RemoteOperand(engine.Stabilize(() => created));

            for (int i = 0; i < (elements?.Count ?? 0); i++)
            {
                int index = i;
                AssignCore(() => ValueInspector.Unwrap(array.Getter()!, out _)!.As<CorDebugArrayValue>().GetElementAtPosition(index), elements![i]);
            }
            return array;
        }
    }
}
