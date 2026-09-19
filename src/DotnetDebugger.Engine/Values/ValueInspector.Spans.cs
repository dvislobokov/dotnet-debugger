using System.Globalization;
using System.Text;
using ClrDebug;
using DotnetDebugger.Engine.Symbols;

namespace DotnetDebugger.Engine.Values;

/// <summary>One element of a span: either a value already read into the debugger, or a way to get at it.</summary>
internal sealed record SpanElement(object? HostValue, Func<CorDebugValue?>? Getter)
{
    public bool IsHost => Getter == null;
}

// Span<T> / ReadOnlySpan<T>. ICorDebug has no notion of "element i behind this ref T", so the elements are obtained
// by other means: primitives and references straight from the debuggee's memory, structs through span.ToArray().
internal sealed partial class ValueInspector
{
    private const int MaxSpanElements = 100_000;

    public static bool IsSpan(string typeName) =>
        typeName.StartsWith("System.Span<", StringComparison.Ordinal) || typeName.StartsWith("System.ReadOnlySpan<", StringComparison.Ordinal);

    /// <summary>Number of elements of the span returned by <paramref name="getter"/>.</summary>
    public int GetSpanLength(Func<CorDebugValue?> getter) =>
        getter() is { } span && GetFieldByName(span, "_length") is { } length
            ? Convert.ToInt32(ReadPrimitive(length), CultureInfo.InvariantCulture)
            : 0;

    /// <summary>The text a span of chars covers, or null for other spans.</summary>
    public string? GetSpanText(Func<CorDebugValue?> getter)
    {
        if (ElementTypeOf(getter)?.Type != CorElementType.Char)
            return null;
        int length = Math.Min(GetSpanLength(getter), MaxStringLength);
        return length == 0 ? "" : Encoding.Unicode.GetString(_host.ReadMemory(ReferenceOf(getter), length * 2));
    }

    /// <param name="start">First element wanted.</param>
    /// <param name="count">Number of elements wanted; 0 for all (up to a sanity limit).</param>
    public List<SpanElement> GetSpanElements(Func<CorDebugValue?> getter, InspectionContext? context, int start = 0, int count = 0)
    {
        int length = Math.Min(GetSpanLength(getter), MaxSpanElements);
        start = Math.Clamp(start, 0, length);
        int end = count > 0 ? Math.Min(length, start + count) : length;
        var result = new List<SpanElement>();
        if (end <= start || ElementTypeOf(getter) is not { } elementType)
            return result;

        int? size = PrimitiveSize(elementType.Type);
        if (size != null)
        {
            // one read for the whole page
            byte[] memory = _host.ReadMemory(ReferenceOf(getter) + (ulong)(start * size.Value), (end - start) * size.Value);
            for (int i = 0; i < end - start; i++)
                result.Add(new SpanElement(DecodePrimitive(elementType.Type, memory.AsSpan(i * size.Value, size.Value)), null));
            return result;
        }

        if (elementType.Type is CorElementType.Class or CorElementType.String or CorElementType.Object or CorElementType.SZArray or CorElementType.Array)
        {
            // an array of object references: each slot holds the address of an object (or 0)
            for (int i = start; i < end; i++)
            {
                int index = i;
                result.Add(new SpanElement(null, () =>
                {
                    // re-read every time: the GC moves objects (and fixes up the span's reference) whenever the debuggee runs
                    ulong address = BitConverter.ToUInt64(_host.ReadMemory(ReferenceOf(getter) + (ulong)(index * IntPtr.Size), IntPtr.Size).Concat(new byte[8]).ToArray(), 0);
                    return address == 0 ? null : _host.GetObjectAt(address);
                }));
            }
            return result;
        }

        // structs: their layout is the runtime's business, so let the debuggee copy them into an array
        if (context == null)
            return result;
        Func<CorDebugValue?> array = SpanToArray(getter, context);
        for (int i = start; i < end; i++)
        {
            int index = i;
            result.Add(new SpanElement(null, () => ElementAt(array, index)));
        }
        return result;
    }

    private Func<CorDebugValue?> SpanToArray(Func<CorDebugValue?> getter, InspectionContext context)
    {
        CorDebugValue span = (getter() is { } v ? Unwrap(v, out _) : null) ?? throw new EvalFailedException("The span is not available.");
        TypeLevel level = GetTypeLevels(span).First();
        MethodDescription toArray = level.Metadata.GetMethods(level.Token, "ToArray").First(m => !m.IsStatic && m.ParameterTypes.Length == 0);
        CorDebugFunction function = level.Type.Class.Module.GetFunctionFromToken(new mdMethodDef(toArray.Token));
        CorDebugValue? result = _host.CallFunction(context.ThreadId, function, TypeArgumentsOf(level.Type), [span]);
        return _host.Stabilize(() => result);
    }

    private CorDebugType? ElementTypeOf(Func<CorDebugValue?> getter) =>
        getter() is { } v && Unwrap(v, out _) is { } span ? TryGet(() => span.ExactType.FirstTypeParameter) : null;

    // Span<T>._reference is a "ref T": the value of that reference is the address of element 0
    private ulong ReferenceOf(Func<CorDebugValue?> getter)
    {
        CorDebugValue reference = (getter() is { } span ? GetFieldByName(span, "_reference") : null)
            ?? throw new InvalidOperationException("Unexpected layout of Span<T>.");
        return reference is CorDebugReferenceValue byRef ? byRef.Value.Value : reference.Address.Value;
    }

    private static int? PrimitiveSize(CorElementType type) => type switch
    {
        CorElementType.Boolean or CorElementType.I1 or CorElementType.U1 => 1,
        CorElementType.Char or CorElementType.I2 or CorElementType.U2 => 2,
        CorElementType.I4 or CorElementType.U4 or CorElementType.R4 => 4,
        CorElementType.I8 or CorElementType.U8 or CorElementType.R8 => 8,
        CorElementType.I or CorElementType.U => IntPtr.Size,
        _ => null,
    };

    private static object DecodePrimitive(CorElementType type, ReadOnlySpan<byte> b) => type switch
    {
        CorElementType.Boolean => b[0] != 0,
        CorElementType.Char => (char)BitConverter.ToUInt16(b),
        CorElementType.I1 => (sbyte)b[0],
        CorElementType.U1 => b[0],
        CorElementType.I2 => BitConverter.ToInt16(b),
        CorElementType.U2 => BitConverter.ToUInt16(b),
        CorElementType.I4 => BitConverter.ToInt32(b),
        CorElementType.U4 => BitConverter.ToUInt32(b),
        CorElementType.I8 => BitConverter.ToInt64(b),
        CorElementType.U8 => BitConverter.ToUInt64(b),
        CorElementType.R4 => BitConverter.ToSingle(b),
        CorElementType.R8 => BitConverter.ToDouble(b),
        CorElementType.I => b.Length == 4 ? (nint)BitConverter.ToInt32(b) : (nint)BitConverter.ToInt64(b),
        _ => b.Length == 4 ? (nuint)BitConverter.ToUInt32(b) : (nuint)BitConverter.ToUInt64(b),
    };

    private VariableInfo DescribeSpan(string name, Func<CorDebugValue?> getter, Func<CorDebugValue?> stable, string typeName,
        string? evaluateName, InspectionContext? context, DisplayOptions options)
    {
        int length = GetSpanLength(stable);
        string display = GetSpanText(stable) is { } text
            ? FormatHost(text, options).Text
            : "{" + typeName + "[" + length + "]}";
        DisplayOptions childOptions = options.ForChildren();
        return new VariableInfo
        {
            Name = name, Value = display, Type = typeName, EvaluateName = evaluateName, Location = getter, Context = context,
            IndexedChildren = length,
            ChildrenProvider = length == 0 ? null : (start, count) =>
            {
                var children = new List<VariableInfo>();
                List<SpanElement> elements = GetSpanElements(stable, context, start, count);
                for (int i = 0; i < elements.Count; i++)
                {
                    string index = "[" + (start + i) + "]";
                    string? elementName = evaluateName == null ? null : evaluateName + index;
                    SpanElement element = elements[i];
                    if (element.IsHost)
                    {
                        VariableInfo host = DescribeHost(index, element.HostValue, null, childOptions);
                        children.Add(new VariableInfo { Name = index, Value = host.Value, Type = host.Type, EvaluateName = elementName });
                    }
                    else
                    {
                        children.Add(Describe(index, element.Getter!, elementName, context, childOptions));
                    }
                }
                if (start == 0)
                {
                    children.Add(new VariableInfo
                    {
                        Name = "Raw View", Value = "", Context = context,
                        ChildrenProvider = (s, n) => GetObjectChildren(stable, evaluateName, context, s, n, rawView: true, childOptions),
                    });
                }
                return children;
            },
        };
    }
}
