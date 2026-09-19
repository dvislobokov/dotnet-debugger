using ClrDebug;
using DotnetDebugger.Engine.Symbols;
using DotnetDebugger.Engine.Values;

namespace DotnetDebugger.Engine;

public sealed partial class DebugEngine
{
    // Overload resolution by type unification, and extension methods on top of it.
    private sealed partial class Evaluator
    {
        private static readonly string[] s_arrayInterfaces =
        [
            "System.Collections.Generic.IEnumerable`1", "System.Collections.Generic.ICollection`1", "System.Collections.Generic.IList`1",
            "System.Collections.Generic.IReadOnlyCollection`1", "System.Collections.Generic.IReadOnlyList`1",
        ];

        // ---------------------------------------------------------------- what a value "is"

        private TypeSignature ClosedSignature(CorDebugType type)
        {
            switch (type.Type)
            {
                case CorElementType.Class or CorElementType.ValueType:
                {
                    CorDebugClass cls = type.Class;
                    string name = engine.GetMetadata(cls.Module)?.GetTypeName((int)cls.Token.Value) ?? "?";
                    return new TypeSignature(name, ValueInspector.TypeArgumentsOf(type).Select(ClosedSignature).ToList());
                }
                case CorElementType.SZArray or CorElementType.Array:
                    return new TypeSignature("", [ClosedSignature(type.FirstTypeParameter)], type.Type == CorElementType.SZArray ? 1 : type.Rank);
                default:
                    return new TypeSignature(_values.ClrTypeName(type), []);
            }
        }

        /// <summary>
        /// Every type an argument can be passed as: its own type, base types and implemented interfaces, all closed
        /// over the actual type arguments (List&lt;int&gt; is an IEnumerable`1&lt;System.Int32&gt;).
        /// </summary>
        private List<TypeSignature> ClosedSignatures(Operand operand)
        {
            var result = new List<TypeSignature>();
            void AddNamed(string clrName)
            {
                result.Add(new TypeSignature(clrName, []));
                if (engine.FindType(clrName) is { } found)
                    result.AddRange(found.Module.Metadata!.GetInterfaceSignatures(found.Token).Select(TypeSignature.Parse));
            }

            switch (operand)
            {
                case HostOperand { Value: { } hostValue }:
                    AddNamed(hostValue.GetType().FullName!);
                    break;

                case RemoteOperand remote when remote.Getter() is { } raw && ValueInspector.Unwrap(raw, out _) is { } value:
                {
                    List<ValueInspector.TypeLevel> levels = _values.GetTypeLevels(value);
                    foreach (ValueInspector.TypeLevel level in levels)
                    {
                        List<TypeSignature> arguments = ValueInspector.TypeArgumentsOf(level.Type).Select(ClosedSignature).ToList();
                        result.Add(new TypeSignature(level.Metadata.GetTypeName(level.Token), arguments));
                        foreach (string iface in level.Metadata.GetInterfaceSignatures(level.Token))
                            result.Add(TypeSignature.Parse(iface).Substitute(arguments));
                    }
                    if (levels.Count > 0)
                        break;

                    if (value.Type is CorElementType.SZArray or CorElementType.Array)
                    {
                        TypeSignature array = ClosedSignature(value.ExactType);
                        result.Add(array);
                        if (array.ArrayRank == 1)
                            result.AddRange(s_arrayInterfaces.Select(i => new TypeSignature(i, [array.Arguments[0]])));
                        AddNamed("System.Array");
                    }
                    else
                    {
                        AddNamed(_values.ClrTypeName(value.ExactType));
                    }
                    break;
                }
            }
            result.Add(new TypeSignature("System.Object", []));
            return result;
        }

        private CorDebugType BuildType(TypeSignature signature)
        {
            if (signature.ArrayRank > 0)
            {
                CorDebugAppDomain appDomain = engine.RequireProcess().AppDomains.First();
                return appDomain.GetArrayOrPointerType(signature.ArrayRank == 1 ? CorElementType.SZArray : CorElementType.Array,
                    signature.ArrayRank, BuildType(signature.Arguments[0]).Raw);
            }

            var found = engine.FindType(signature.Name) ?? throw new DebuggerException($"Type '{signature.Name}' is not loaded in the debuggee.");
            var type = new TypeOperand(found.Module, found.Token);
            ICorDebugType[] arguments = signature.Arguments.Select(a => BuildType(a).Raw).ToArray();
            CorElementType kind = type.Metadata.IsValueType(type.Token) ? CorElementType.ValueType : CorElementType.Class;
            return type.Class.GetParameterizedType(kind, arguments.Length, arguments);
        }

        // ---------------------------------------------------------------- overload scoring

        /// <summary>
        /// How well the arguments fit a method; -1 if they do not. <paramref name="bindings"/> receives what the
        /// method's type parameters have to be for the call to work.
        /// </summary>
        private int ScoreByUnification(MethodDescription method, List<Operand> arguments, List<string?> argumentTypes,
            out Dictionary<int, TypeSignature> bindings)
        {
            bindings = [];
            int total = method.GenericParameterCount == 0 ? 1 : 0;
            for (int i = 0; i < arguments.Count; i++)
            {
                string parameter = method.ParameterTypes[i];
                string? argument = argumentTypes[i];
                bool parameterIsValueType = s_numericTypes.Contains(parameter) || parameter is "System.Boolean" or "System.Decimal";

                if (parameter.StartsWith("ref ", StringComparison.Ordinal) || parameter.EndsWith('*') || parameter == "fnptr")
                    return -1;
                if (argument == null)
                {
                    if (parameterIsValueType)
                        return -1;
                    total += 3;
                }
                else if (argument == parameter)
                {
                    total += 6;
                }
                else if (s_numericTypes.Contains(parameter) && s_numericTypes.Contains(argument) && arguments[i] is HostOperand)
                {
                    if (!IsWidening(argument, parameter))
                        return -1;
                    total += 3;
                }
                else if (parameter == "System.Object")
                {
                    total += 1;
                }
                else
                {
                    TypeSignature parameterSignature;
                    try
                    {
                        parameterSignature = TypeSignature.Parse(parameter);
                    }
                    catch (FormatException)
                    {
                        return -1;
                    }

                    bool matched = false;
                    foreach (TypeSignature closed in ClosedSignatures(arguments[i]))
                    {
                        var attempt = new Dictionary<int, TypeSignature>(bindings);
                        if (!parameterSignature.Unify(closed, attempt))
                            continue;
                        bindings = attempt;
                        matched = true;
                        break;
                    }
                    if (!matched)
                        return -1;
                    total += parameterSignature.ContainsMethodVariable ? 4 : 5;
                }
            }
            return total;
        }

        // ---------------------------------------------------------------- extension methods

        /// <summary>target.Name(args) as Name(target, args) of a static class with [Extension] methods of that name.</summary>
        private Operand? TryCallExtension(Operand target, string name, List<Operand> arguments)
        {
            var extended = new List<Operand>(arguments.Count + 1) { target };
            extended.AddRange(arguments);

            // the user's own extensions win over the framework's
            foreach (LoadedModule module in engine._modules.Values.OrderByDescending(m => m.Metadata?.HasSymbols == true).ThenBy(m => m.Id))
            {
                if (module.Metadata == null)
                    continue;
                foreach (int typeToken in module.Metadata.GetExtensionTypes(name))
                {
                    if (TryCall(null, new TypeOperand(module, typeToken), name, extended, isPropertyAccess: false) is { } result)
                        return result;
                }
            }
            return null;
        }
    }
}
