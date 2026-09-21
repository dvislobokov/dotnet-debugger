using System.Globalization;
using System.Runtime.InteropServices;
using ClrDebug;
using DotnetDebugger.Engine.Symbols;

namespace DotnetDebugger.Engine.Values;

/// <summary>
/// Identifies a stack frame in a way that survives the process being resumed for a func-eval. Frames are counted from
/// the bottom of the stack: an evaluation that could not be aborted leaves its frames on top of the thread's stack,
/// which must not change what the frames below are called.
/// </summary>
internal sealed record FrameRef(int ThreadId, int Depth);

/// <summary>Where a value was found: needed to evaluate properties (thread) and statics/expressions (frame).</summary>
internal sealed record InspectionContext(int ThreadId, FrameRef? Frame);

/// <summary>One row of a variables view.</summary>
public sealed class VariableInfo
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public string? Type { get; init; }
    public string? EvaluateName { get; init; }
    public int IndexedChildren { get; init; }
    public bool HasChildren => ChildrenProvider != null;

    /// <summary>
    /// The value has not been computed (yet) because that is expensive. Its "children" are exactly one variable:
    /// the computed value (this is how DAP clients resolve lazy variables).
    /// </summary>
    public bool IsLazy { get; init; }

    /// <summary>Re-reads the storage location of the variable (raw, not dereferenced); null if it is not assignable.</summary>
    internal Func<CorDebugValue?>? Location { get; init; }
    internal Func<int, int, List<VariableInfo>>? ChildrenProvider { get; init; }
    internal InspectionContext? Context { get; init; }
}

/// <summary>How values are rendered; set through format specifiers ("x,h") or the client's value format.</summary>
/// <param name="FullStrings">Do not shorten long strings (the client wants the value for the clipboard).</param>
/// <param name="ElementLimit">Show only the first elements of an array or collection ("numbers,5").</param>
internal sealed record DisplayOptions(bool Hex = false, bool NoQuotes = false, bool Raw = false, bool FullStrings = false, int? ElementLimit = null)
{
    public static readonly DisplayOptions Default = new();

    /// <summary>Number formatting carries over to members and elements, the rest is about the value itself.</summary>
    public DisplayOptions ForChildren() => Hex ? new DisplayOptions(Hex: true) : Default;
}

internal sealed class EvalFailedException(string message) : Exception(message);

/// <summary>Services of the engine the inspector needs for anything that requires running code in the debuggee.</summary>
internal interface IEvalHost
{
    /// <summary>Runs a function in the debuggee. Throws <see cref="EvalFailedException"/> if it throws or cannot run.</summary>
    /// <param name="isImplicit">Nobody asked for this call: it serves the display of a value, and gets less patience.</param>
    CorDebugValue? CallFunction(int threadId, CorDebugFunction function, CorDebugType[] typeArguments, CorDebugValue[] arguments, bool isImplicit = false);

    /// <summary>Returns a getter that keeps working after the process was resumed (strong handle for heap objects).</summary>
    Func<CorDebugValue?> Stabilize(Func<CorDebugValue?> getter);

    CorDebugFrame? GetFrame(FrameRef frame);

    /// <summary>True once the current request has used up its time for running code in the debuggee.</summary>
    bool BudgetExceeded { get; }

    /// <summary>Expands a [DebuggerDisplay] format string against the object returned by <paramref name="self"/>.</summary>
    string FormatDebuggerDisplay(string template, Func<CorDebugValue?> self, InspectionContext context);

    /// <summary>Instantiates a [DebuggerTypeProxy] type around <paramref name="target"/>; null if that is not possible.</summary>
    Func<CorDebugValue?>? CreateTypeProxy(string proxyTypeName, Func<CorDebugValue?> target, InspectionContext context);

    /// <summary>Raw bytes of the debuggee's memory.</summary>
    byte[] ReadMemory(ulong address, int size);

    /// <summary>The object living at <paramref name="address"/> of the managed heap.</summary>
    CorDebugValue? GetObjectAt(ulong address);

    /// <summary>Runs the sequence to completion and returns its elements as an object[].</summary>
    Func<CorDebugValue?> EnumerateToArray(Func<CorDebugValue?> sequence, InspectionContext context);

    /// <summary>Throws <see cref="OperationCanceledException"/> once the client has given up on the current request.</summary>
    void ThrowIfCancelled();
}

/// <summary>Turns ICorDebugValue objects into display strings and child lists.</summary>
internal sealed partial class ValueInspector
{
    private const int MaxStringLength = 4096;
    private const int MaxProperties = 200;

    /// <summary>
    /// Children returned for a request that names no range. A client that was told "indexedVariables: 1000000" is
    /// expected to page; one that does not would otherwise wait minutes for an answer no UI can show.
    /// </summary>
    internal const int MaxUnpagedChildren = 10_000;

    private readonly Func<CorDebugModule, ModuleMetadata?> _getMetadata;
    private readonly IEvalHost _host;

    public ValueInspector(Func<CorDebugModule, ModuleMetadata?> getMetadata, IEvalHost host)
    {
        _getMetadata = getMetadata;
        _host = host;
    }

    // ---------------------------------------------------------------- describe

    public VariableInfo Describe(string name, Func<CorDebugValue?> getter, string? evaluateName, InspectionContext? context,
        DisplayOptions? options = null)
    {
        _host.ThrowIfCancelled();
        try
        {
            VariableInfo info = DescribeCore(name, getter, evaluateName, context, options ?? DisplayOptions.Default);
            return options?.ElementLimit is { } limit && info.IndexedChildren > limit && info.ChildrenProvider is { } all
                ? new VariableInfo
                {
                    Name = info.Name, Value = info.Value, Type = info.Type, EvaluateName = info.EvaluateName, Location = info.Location,
                    Context = info.Context, IndexedChildren = limit,
                    ChildrenProvider = (start, count) =>
                    {
                        int first = Math.Max(0, start), take = count > 0 ? Math.Min(count, limit - first) : limit - first;
                        return take <= 0 ? [] : all(first, take);
                    },
                }
                : info;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new VariableInfo { Name = name, Value = $"<error: {ErrorText.Describe(e)}>", EvaluateName = evaluateName };
        }
    }

    public VariableInfo DescribeHost(string name, object? value, string? enumName = null, DisplayOptions? options = null)
    {
        (string text, string? type) = FormatHost(value, options ?? DisplayOptions.Default);
        return new VariableInfo { Name = name, Value = enumName ?? text, Type = type };
    }

    private VariableInfo DescribeCore(string name, Func<CorDebugValue?> getter, string? evaluateName, InspectionContext? context,
        DisplayOptions options)
    {
        CorDebugValue? value = getter();
        if (value == null)
            return new VariableInfo { Name = name, Value = "<unavailable>", EvaluateName = evaluateName };

        string? declaredType = SafeTypeName(value);
        CorDebugValue? target = Unwrap(value, out bool isNull);
        if (isNull || target == null)
            return Leaf("null", declaredType);

        string typeName = SafeTypeName(target) ?? declaredType ?? "?";
        switch (target.Type)
        {
            case CorElementType.String:
                return Leaf(FormatHost(ReadStringValue(target, StringLimit(options)), options).Text, "string");

            case CorElementType.SZArray:
            case CorElementType.Array:
            {
                var array = target.As<CorDebugArrayValue>();
                int count = array.Count;
                int rank = array.Rank;
                string dims = rank == 1 ? count.ToString() : string.Join(", ", array.GetDimensions(rank));
                int bracket = typeName.IndexOf('[');
                string display = bracket < 0 ? typeName : typeName[..(bracket + 1)] + dims + typeName[(typeName.IndexOf(']', bracket))..];
                Func<CorDebugValue?> stable = count > 0 ? _host.Stabilize(getter) : getter;
                DisplayOptions childOptions = options.ForChildren();
                return new VariableInfo
                {
                    Name = name, Value = "{" + display + "}", Type = typeName, EvaluateName = evaluateName,
                    Location = getter, Context = context, IndexedChildren = count,
                    ChildrenProvider = count > 0 ? (start, n) => GetArrayChildren(stable, evaluateName, context, start, n, childOptions) : null,
                };
            }

            case CorElementType.Class when typeName == "System.String":
                // a string reached through its address (ICorDebugProcess.GetObject) is typed as a plain object
                return Leaf(FormatHost(ReadStringValue(target, StringLimit(options)), options).Text, "string");

            case CorElementType.Class:
            case CorElementType.ValueType:
            case CorElementType.Object:
                return DescribeObject(name, getter, target, typeName, evaluateName, context, options);

            case CorElementType.Ptr:
            case CorElementType.ByRef:
            case CorElementType.FnPtr:
                return Leaf(target is CorDebugReferenceValue r ? "0x" + r.Value.Value.ToString("X") : "?", typeName);

            default:
                return Leaf(FormatHost(ReadPrimitive(target), options).Text, typeName);
        }

        VariableInfo Leaf(string text, string? type) => new()
        {
            Name = name, Value = text, Type = type, EvaluateName = evaluateName, Location = getter, Context = context,
        };
    }

    private VariableInfo DescribeObject(string name, Func<CorDebugValue?> getter, CorDebugValue target, string typeName,
        string? evaluateName, InspectionContext? context, DisplayOptions options)
    {
        var obj = target.As<CorDebugObjectValue>();
        CorDebugType type = target.ExactType;
        VariableInfo Leaf(string text, string? shownType = null) => new()
        {
            Name = name, Value = text, Type = shownType ?? typeName, EvaluateName = evaluateName, Location = getter, Context = context,
        };

        if (IsEnum(type))
        {
            CorDebugValue raw = GetField(obj, type, "value__")!;
            return Leaf(options.Hex ? FormatHost(ReadPrimitive(raw), options).Text : FormatEnum(type.Class, ReadIntegerBits(raw)));
        }

        // A boxed primitive unboxes to a System.Int32-style struct wrapping the raw value in m_value.
        if (type.Type == CorElementType.ValueType && typeName.StartsWith("System.", StringComparison.Ordinal)
            && GetField(obj, type, "m_value") is { } primitive && primitive.Type != CorElementType.ValueType)
        {
            return Leaf(FormatHost(ReadPrimitive(primitive), options).Text, SafeTypeName(primitive));
        }

        if (typeName.StartsWith("System.Nullable<", StringComparison.Ordinal))
        {
            CorDebugValue? hasValue = GetField(obj, type, "hasValue");
            if (hasValue != null && ReadIntegerBits(hasValue) == 0)
                return Leaf("null");
            VariableInfo inner = DescribeCore(name, () => NullableValue(getter), evaluateName, context, options);
            return new VariableInfo
            {
                Name = name, Value = inner.Value, Type = typeName, EvaluateName = evaluateName, Location = getter, Context = context,
                IndexedChildren = inner.IndexedChildren, ChildrenProvider = inner.ChildrenProvider,
            };
        }

        if (typeName == "decimal" && TryReadDecimal(obj, type) is { } d)
            return Leaf(d.ToString(CultureInfo.InvariantCulture));

        // From here on the value is expandable. The handle is taken first: computing the display text may run code
        // in the debuggee, after which "target" and friends are stale.
        Func<CorDebugValue?> stable = _host.Stabilize(getter);
        DisplayOptions childOptions = options.ForChildren();
        string display = "{" + typeName + "}";
        int indexed = 0;
        bool rawView = options.Raw;
        if (!options.Raw && IsSpan(typeName))
            return DescribeSpan(name, getter, stable, typeName, evaluateName, context, options);
        if (!options.Raw)
        {
            if (TryGet(() => TryGetCollection(stable, typeName, context, childOptions)) is { } collection)
            {
                display = "Count = " + collection.Count;
                indexed = collection.Count;
            }
            else
            {
                display = TryGet(() => FormatWellKnown(stable, typeName, context, childOptions))
                    ?? TryGet(() => FormatWithDebuggerDisplay(stable, context))
                    ?? TryGet(() => FormatException(stable))
                    ?? TryGet(() => FormatWithToString(stable, context))
                    ?? display;
            }
        }
        return new VariableInfo
        {
            Name = name, Value = display, Type = typeName, EvaluateName = evaluateName, Location = getter, Context = context,
            IndexedChildren = indexed,
            ChildrenProvider = (start, n) => GetObjectChildren(stable, evaluateName, context, start, n, rawView, childOptions),
        };
    }

    private CorDebugValue? NullableValue(Func<CorDebugValue?> getter)
    {
        CorDebugValue? target = getter() is { } v ? Unwrap(v, out _) : null;
        return target == null ? null : GetField(target.As<CorDebugObjectValue>(), target.ExactType, "value");
    }

    // ---------------------------------------------------------------- display text of objects

    private bool CanEvaluate(InspectionContext? context) => context?.Frame != null && !_host.BudgetExceeded;

    private string? FormatWithDebuggerDisplay(Func<CorDebugValue?> getter, InspectionContext? context)
    {
        if (!CanEvaluate(context))
            return null;
        CorDebugValue? target = getter() is { } v ? Unwrap(v, out _) : null;
        if (target == null)
            return null;

        foreach (TypeLevel level in GetTypeLevels(target))
        {
            if (level.Metadata.GetDebuggerDisplay(level.Token) is not { } template)
                continue;
            string text = _host.FormatDebuggerDisplay(template, getter, context!);
            // library authors use expressions this debugger may not understand; their ToString() is the better fallback
            return !level.Metadata.HasSymbols && text.Contains("<error", StringComparison.Ordinal) ? null : text;
        }
        return null;
    }

    private string? FormatException(Func<CorDebugValue?> getter)
    {
        CorDebugValue? target = getter() is { } v ? Unwrap(v, out _) : null;
        if (target == null || !GetTypeLevels(target).Any(l => l.Metadata.GetTypeName(l.Token) == "System.Exception"))
            return null;
        string? message = ReadString(GetFieldByName(target, "_message"));
        return "{" + TypeName(target.ExactType) + (message == null ? "" : ": " + message) + "}";
    }

    private string? FormatWithToString(Func<CorDebugValue?> getter, InspectionContext? context)
    {
        if (!CanEvaluate(context))
            return null;
        CorDebugValue? self = getter();
        CorDebugValue? target = self == null ? null : Unwrap(self, out _);
        if (self == null || target == null)
            return null;

        // only a ToString() somebody bothered to write says more than the type name
        TypeLevel? declaring = GetTypeLevels(target).FirstOrDefault(l => l.Metadata.DeclaresMethod(l.Token, "ToString", 0));
        if (declaring == null || declaring.Metadata.GetTypeName(declaring.Token) is "System.Object" or "System.ValueType" or "System.Enum")
            return null;

        MethodDescription method = declaring.Metadata.GetMethods(declaring.Token, "ToString").First(m => !m.IsStatic && m.ParameterTypes.Length == 0);
        CorDebugFunction function = declaring.Type.Class.Module.GetFunctionFromToken(new mdMethodDef(method.Token));
        CorDebugValue thisArgument = target.Type == CorElementType.ValueType ? target : self;
        try
        {
            string? text = ReadString(_host.CallFunction(context!.ThreadId, function, TypeArgumentsOf(declaring.Type), [thisArgument], isImplicit: true));
            return text == null ? null : "{" + text.ReplaceLineEndings(" ") + "}";
        }
        catch (EvalFailedException)
        {
            return null;
        }
    }

    // Types everybody knows, shown without running any code.
    private string? FormatWellKnown(Func<CorDebugValue?> getter, string typeName, InspectionContext? context, DisplayOptions options)
    {
        CorDebugValue? Field(string field) => getter() is { } v ? GetFieldByName(v, field) : null;
        string Part(string field) => DescribeCore(field, () => Field(field), null, context, options).Value;

        switch (typeName)
        {
            case "System.DateTime":
                return "{" + FormatDateTime(ReadIntegerBits(Field("_dateData")!)) + "}";
            case "System.TimeSpan":
                return "{" + new TimeSpan((long)ReadIntegerBits(Field("_ticks")!)).ToString("c", CultureInfo.InvariantCulture) + "}";
            case "System.DateTimeOffset":
            {
                ulong utc = ReadIntegerBits(GetFieldByName(Field("_dateTime")!, "_dateData")!);
                var offset = TimeSpan.FromMinutes(Convert.ToInt64(ReadPrimitive(Field("_offsetMinutes")!), CultureInfo.InvariantCulture));
                var local = new DateTime((long)(utc & 0x3FFFFFFFFFFFFFFF) + offset.Ticks, DateTimeKind.Unspecified);
                string sign = offset < TimeSpan.Zero ? "-" : "+";
                return "{" + FormatDateTime((ulong)local.Ticks) + " " + sign + offset.ToString(@"hh\:mm", CultureInfo.InvariantCulture) + "}";
            }
            case "System.Guid":
            {
                int a = (int)ReadIntegerBits(Field("_a")!);
                short b = (short)ReadIntegerBits(Field("_b")!), c = (short)ReadIntegerBits(Field("_c")!);
                byte[] rest = "defghijk".Select(f => (byte)ReadIntegerBits(Field("_" + f)!)).ToArray();
                return "{" + new Guid(a, b, c, rest) + "}";
            }
        }

        // a (ReadOnly)Memory<char> over a string reads best as the text it covers
        if (typeName is "System.ReadOnlyMemory<char>" or "System.Memory<char>" && ReadString(Field("_object")) is { } text)
        {
            int index = Convert.ToInt32(ReadPrimitive(Field("_index")!), CultureInfo.InvariantCulture) & int.MaxValue;
            int length = Convert.ToInt32(ReadPrimitive(Field("_length")!), CultureInfo.InvariantCulture);
            return index + length <= text.Length ? FormatHost(text.Substring(index, length), options).Text : null;
        }

        if (typeName.StartsWith("System.Collections.Generic.KeyValuePair<", StringComparison.Ordinal))
            return "[" + Part("key") + ", " + Part("value") + "]";

        bool valueTuple = typeName.StartsWith("System.ValueTuple<", StringComparison.Ordinal);
        if (valueTuple || typeName.StartsWith("System.Tuple<", StringComparison.Ordinal))
        {
            var parts = new List<string>();
            for (int i = 1; i <= 8; i++)
            {
                string field = (valueTuple ? "Item" : "m_Item") + i;
                if (i == 8)
                    field = valueTuple ? "Rest" : "m_Rest";
                if (Field(field) == null)
                    break;
                parts.Add(Part(field));
            }
            return "(" + string.Join(", ", parts) + ")";
        }
        return null;
    }

    private static string FormatDateTime(ulong dateData)
    {
        var value = new DateTime((long)(dateData & 0x3FFFFFFFFFFFFFFF));
        string text = value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (value.Ticks % TimeSpan.TicksPerSecond != 0)
            text += value.ToString(".FFFFFFF", CultureInfo.InvariantCulture);
        return dateData >> 62 == 1 ? text + " UTC" : text;
    }

    // ---------------------------------------------------------------- children

    private List<VariableInfo> GetArrayChildren(Func<CorDebugValue?> getter, string? evaluateName, InspectionContext? context,
        int start, int count, DisplayOptions options)
    {
        var result = new List<VariableInfo>();
        CorDebugValue? target = getter() is { } v ? Unwrap(v, out _) : null;
        if (target == null)
            return result;

        var array = target.As<CorDebugArrayValue>();
        int total = array.Count;
        int rank = array.Rank;
        int[]? dims = rank > 1 ? array.GetDimensions(rank) : null;
        (int first, int end) = PageOf(start, count, total);
        string Index(int position) => "[" + FormatIndex(position, dims) + "]";

        if (TryReadPrimitiveElements(getter, array, first, end, 0, Index, evaluateName, context, options) is { } primitives)
        {
            result = primitives;
        }
        else
        {
            for (int i = first; i < end; i++)
            {
                int position = i;
                string index = Index(position);
                result.Add(Describe(index, () => ElementAt(getter, position), evaluateName == null ? null : evaluateName + index, context, options));
            }
        }
        AddNoteAboutTheRest(result, count, end, total);
        return result;
    }

    /// <summary>The range [first, end) a request is answered with: what it asks for, or the first elements if it names no range.</summary>
    private static (int First, int End) PageOf(int start, int count, int total)
    {
        int first = Math.Clamp(start, 0, total);
        return (first, (int)Math.Min(total, first + (long)(count > 0 ? count : MaxUnpagedChildren)));
    }

    private static void AddNoteAboutTheRest(List<VariableInfo> result, int count, int end, int total)
    {
        if (count <= 0 && end < total)
            result.Add(new VariableInfo { Name = "[...]", Value = $"{total - end} more elements are not shown: they have to be requested in ranges (start, count)." });
    }

    /// <summary>
    /// Elements of an array of primitives, read from the debuggee's memory in one go. One ICorDebugValue per element
    /// costs a round trip each, and memory in the debugging interface that is not given back before the debuggee
    /// continues: for a million elements that is minutes and gigabytes.
    /// </summary>
    /// <param name="offset">Position in the array of the element called <paramref name="nameOf"/>(0).</param>
    /// <returns>null if the elements are not primitives (or the layout could not be made sense of).</returns>
    private List<VariableInfo>? TryReadPrimitiveElements(Func<CorDebugValue?> arrayGetter, CorDebugArrayValue array, int first, int end, int offset,
        Func<int, string> nameOf, string? evaluateName, InspectionContext? context, DisplayOptions options)
    {
        if (end <= first)
            return null;
        try
        {
            CorElementType elementType = array.ElementType;
            int size = elementType switch
            {
                CorElementType.Boolean or CorElementType.I1 or CorElementType.U1 => 1,
                CorElementType.Char or CorElementType.I2 or CorElementType.U2 => 2,
                CorElementType.I4 or CorElementType.U4 or CorElementType.R4 => 4,
                CorElementType.I8 or CorElementType.U8 or CorElementType.R8 => 8,
                _ => 0,
            };
            if (size == 0 || offset + end > array.Count)
                return null;

            CorDebugValue firstElement = array.GetElementAtPosition(offset + first);
            if (firstElement.Type != elementType || firstElement.Size != size)
                return null;
            ulong address = firstElement.Address.Value;
            if (address == 0)
                return null;

            var result = new List<VariableInfo>(end - first);
            const int ChunkElements = 256 * 1024;
            for (int chunkStart = first; chunkStart < end; chunkStart += ChunkElements)
            {
                _host.ThrowIfCancelled();
                int chunkCount = Math.Min(ChunkElements, end - chunkStart);
                byte[] bytes = _host.ReadMemory(address + (ulong)(chunkStart - first) * (ulong)size, chunkCount * size);
                for (int i = 0; i < chunkCount; i++)
                {
                    ReadOnlySpan<byte> b = bytes.AsSpan(i * size, size);
                    object value = elementType switch
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
                        _ => BitConverter.ToDouble(b),
                    };
                    int index = chunkStart + i;
                    int position = offset + index;
                    string name = nameOf(index);
                    (string text, string? type) = FormatHost(value, options);
                    result.Add(new VariableInfo
                    {
                        Name = name, Value = text, Type = type, EvaluateName = evaluateName == null ? null : evaluateName + name,
                        Location = () => ElementAt(arrayGetter, position), Context = context,
                    });
                }
            }
            return result;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private static CorDebugValue? ElementAt(Func<CorDebugValue?> arrayGetter, int position)
    {
        CorDebugValue? target = arrayGetter() is { } v ? Unwrap(v, out _) : null;
        return target?.As<CorDebugArrayValue>().GetElementAtPosition(position);
    }

    private static string FormatIndex(int position, int[]? dims)
    {
        if (dims == null)
            return position.ToString();
        var indices = new int[dims.Length];
        for (int d = dims.Length - 1; d >= 0; d--)
        {
            indices[d] = position % dims[d];
            position /= dims[d];
        }
        return string.Join(", ", indices);
    }

    private List<VariableInfo> GetObjectChildren(Func<CorDebugValue?> getter, string? evaluateName, InspectionContext? context,
        int start, int count, bool rawView, DisplayOptions options, bool publicOnly = false)
    {
        var result = new List<VariableInfo>();
        CorDebugValue? target = getter() is { } v ? Unwrap(v, out _) : null;
        if (target == null)
            return result;

        if (!rawView && TryGet(() => TryGetCollection(getter, SafeTypeName(target) ?? "", context, options)) is { } collection)
        {
            try
            {
                (int first, int end) = PageOf(start, count, collection.Count);
                List<VariableInfo>? primitives = null;
                if (collection.Backing is { } backing && backing.Array() is { } backingValue && Unwrap(backingValue, out _) is { } backingArray)
                {
                    primitives = TryReadPrimitiveElements(backing.Array, backingArray.As<CorDebugArrayValue>(), first, end, backing.Offset,
                        i => "[" + i + "]", evaluateName, context, options);
                }
                if (primitives != null)
                {
                    result.AddRange(primitives);
                }
                else if (end > first)
                {
                    foreach (CollectionItem item in collection.Items().Skip(first).Take(end - first))
                        result.Add(Describe(item.Name, item.Getter, evaluateName == null || !item.Addressable ? null : evaluateName + item.Name, context, options));
                }
                AddNoteAboutTheRest(result, count, end, collection.Count);
                result.Add(new VariableInfo
                {
                    Name = "Raw View", Value = "", Context = context,
                    ChildrenProvider = (s, n) => GetObjectChildren(getter, evaluateName, context, s, n, rawView: true, options),
                });
                return result;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                result.Clear(); // unexpected layout: fall back to the plain view
            }
        }

        // [DebuggerTypeProxy]: show the public face of the proxy instead of the object's own members
        if (!rawView && context?.Frame != null && !_host.BudgetExceeded)
        {
            string? proxyType = GetTypeLevels(target).Select(l => l.Metadata.GetDebuggerTypeProxy(l.Token)).FirstOrDefault(p => p != null);
            Func<CorDebugValue?>? proxy = proxyType == null ? null : TryGet(() => _host.CreateTypeProxy(proxyType, getter, context));
            if (proxy != null)
            {
                result.AddRange(GetObjectChildren(proxy, evaluateName: null, context, start, count, rawView: false, options, publicOnly: true));
                result.Add(new VariableInfo
                {
                    Name = "Raw View", Value = "", Context = context,
                    ChildrenProvider = (s, n) => GetObjectChildren(getter, evaluateName, context, s, n, rawView: true, options),
                });
                return result;
            }
            target = getter() is { } again ? Unwrap(again, out _) : null; // creating the proxy ran code
            if (target == null)
                return result;
        }

        // what members say about themselves ([DebuggerBrowsable], compiler generated); the raw view shows everything
        List<TypeLevel> levels = GetTypeLevels(target);
        var browsable = new Dictionary<string, BrowsableState>();
        if (!rawView)
        {
            foreach (TypeLevel level in levels)
                foreach (var (member, state) in level.Metadata.GetBrowsableStates(level.Token))
                    browsable.TryAdd(member, state);
        }
        if (publicOnly)
        {
            foreach (TypeLevel level in levels)
                foreach (string member in level.Metadata.GetNonPublicMembers(level.Token))
                    browsable[CleanFieldName(member)] = BrowsableState.Never;
        }

        // sequences that are not one of the known collections: their elements only exist once enumerated
        bool isSequence = !rawView && !publicOnly && context?.Frame != null
            && levels.Any(l => l.Metadata.GetInterfaceNames(l.Token).Contains("System.Collections.IEnumerable"));

        void Add(VariableInfo member, string memberName)
        {
            if (browsable.GetValueOrDefault(memberName) == BrowsableState.RootHidden && member.ChildrenProvider != null)
                result.AddRange(member.ChildrenProvider(0, 0).Where(c => c.Name is not ("Raw View" or "Static members")));
            else
                result.Add(member);
        }

        // fields
        var shown = new HashSet<string>();
        bool hasStatics = false;
        foreach (TypeLevel level in levels)
        {
            foreach (FieldDescription field in level.Metadata.GetFields(level.Token))
            {
                if (field.IsStatic || field.IsLiteral)
                {
                    hasStatics = true;
                    continue;
                }
                string fieldName = CleanFieldName(field.Name);
                if (!shown.Add(fieldName))
                    continue;
                if (browsable.TryGetValue(fieldName, out BrowsableState state) && state == BrowsableState.Never)
                    continue;
                CorDebugClass cls = level.Type.Class;
                int token = field.Token;
                Add(Describe(fieldName, () => FieldOf(getter, cls, token), Member(evaluateName, fieldName), context, options), fieldName);
            }
        }

        // properties, by running their getters
        if (context != null)
        {
            var properties = new List<(int Level, PropertyDescription Property)>();
            for (int i = 0; i < levels.Count; i++)
            {
                foreach (PropertyDescription property in levels[i].Metadata.GetProperties(levels[i].Token))
                {
                    if (!property.IsStatic && !property.IsIndexer && !property.Name.Contains('.') && shown.Add(property.Name)
                        && browsable.GetValueOrDefault(property.Name, BrowsableState.Collapsed) != BrowsableState.Never)
                    {
                        properties.Add((i, property));
                    }
                }
            }

            bool evaluationBroken = false;
            foreach ((int levelIndex, PropertyDescription property) in properties.Take(MaxProperties))
            {
                string propertyName = property.Name;
                string? propertyEvaluateName = Member(evaluateName, propertyName);
                if (evaluationBroken)
                {
                    result.Add(new VariableInfo { Name = propertyName, Value = "<not evaluated>" });
                }
                else if (_host.BudgetExceeded)
                {
                    // Out of time for this request: the client asks for these one by one, when the user wants them.
                    result.Add(new VariableInfo
                    {
                        Name = propertyName, Value = "", EvaluateName = propertyEvaluateName, Context = context, IsLazy = true,
                        ChildrenProvider = (_, _) => [EvaluateProperty(getter, levelIndex, property, propertyEvaluateName, context, options, out _)],
                    });
                }
                else
                {
                    Add(EvaluateProperty(getter, levelIndex, property, propertyEvaluateName, context, options, out evaluationBroken), propertyName);
                }
            }
        }

        if (isSequence)
        {
            result.Add(new VariableInfo
            {
                Name = "Results View", Value = "", Context = context, IsLazy = true,
                ChildrenProvider = (_, _) => [ResultsView(getter, context!, options)],
            });
        }

        if (hasStatics && !rawView && !publicOnly)
        {
            result.Add(new VariableInfo
            {
                Name = "Static members", Value = "", Context = context,
                ChildrenProvider = (_, _) => GetStaticChildren(getter, context, options),
            });
        }
        return result;
    }

    private VariableInfo ResultsView(Func<CorDebugValue?> sequence, InspectionContext context, DisplayOptions options)
    {
        try
        {
            VariableInfo elements = Describe("Results View", _host.EnumerateToArray(sequence, context), null, context, options);
            return new VariableInfo
            {
                Name = "Results View", Value = "Expanding the Results View enumerated the sequence", Context = context,
                IndexedChildren = elements.IndexedChildren, ChildrenProvider = elements.ChildrenProvider,
            };
        }
        catch (Exception e) when (e is EvalFailedException or DebuggerException)
        {
            return new VariableInfo { Name = "Results View", Value = "<" + e.Message + ">" };
        }
    }

    private VariableInfo EvaluateProperty(Func<CorDebugValue?> getter, int levelIndex, PropertyDescription property, string? evaluateName,
        InspectionContext context, DisplayOptions options, out bool evaluationBroken)
    {
        evaluationBroken = false;
        try
        {
            // everything obtained before a func-eval is stale afterwards, so start over from the getter
            CorDebugValue? self = getter();
            CorDebugValue? selfTarget = self == null ? null : Unwrap(self, out _);
            if (self == null || selfTarget == null)
                return new VariableInfo { Name = property.Name, Value = "<unavailable>" };

            TypeLevel level = GetTypeLevels(selfTarget)[levelIndex];
            CorDebugFunction function = level.Type.Class.Module.GetFunctionFromToken(new mdMethodDef(property.GetterToken));
            CorDebugValue thisArgument = selfTarget.Type == CorElementType.ValueType ? selfTarget : self;
            CorDebugValue? value = _host.CallFunction(context.ThreadId, function, TypeArgumentsOf(level.Type), [thisArgument], isImplicit: true);
            return DescribeResult(property.Name, value, evaluateName, context, options);
        }
        catch (EvalFailedException e)
        {
            // after a timeout the host stops evaluating implicitly, which turns the remaining properties into lazy ones
            evaluationBroken = e.Message.Contains("not possible", StringComparison.Ordinal);
            return new VariableInfo { Name = property.Name, Value = "<" + e.Message + ">" };
        }
        catch (Exception e)
        {
            return new VariableInfo { Name = property.Name, Value = $"<error: {ErrorText.Describe(e)}>" };
        }
    }

    /// <summary>Describes a value produced by a func-eval, keeping it alive for later expansion.</summary>
    public VariableInfo DescribeResult(string name, CorDebugValue? value, string? evaluateName, InspectionContext? context,
        DisplayOptions? options = null)
    {
        if (value == null)
            return new VariableInfo { Name = name, Value = "null", EvaluateName = evaluateName };
        Func<CorDebugValue?> stable = _host.Stabilize(() => value);
        VariableInfo info = Describe(name, stable, evaluateName, context, options);
        // results of calls are not storage locations
        return new VariableInfo
        {
            Name = info.Name, Value = info.Value, Type = info.Type, EvaluateName = info.EvaluateName,
            IndexedChildren = info.IndexedChildren, ChildrenProvider = info.ChildrenProvider, Context = context,
        };
    }

    private List<VariableInfo> GetStaticChildren(Func<CorDebugValue?> getter, InspectionContext? context, DisplayOptions options)
    {
        var result = new List<VariableInfo>();
        CorDebugValue? target = getter() is { } v ? Unwrap(v, out _) : null;
        if (target == null)
            return result;

        foreach (TypeLevel level in GetTypeLevels(target))
        {
            string typeName = level.Metadata.GetTypeName(level.Token);
            foreach (FieldDescription field in level.Metadata.GetFields(level.Token))
            {
                if (!field.IsStatic && !field.IsLiteral)
                    continue;
                string evaluateName = typeName + "." + field.Name;
                if (field.IsLiteral)
                {
                    object? constant = level.Metadata.GetConstant(field.Token, out _);
                    VariableInfo info = DescribeHost(field.Name, constant, null, options);
                    result.Add(new VariableInfo { Name = info.Name, Value = info.Value, Type = info.Type, EvaluateName = evaluateName });
                    continue;
                }
                CorDebugType type = level.Type;
                int token = field.Token;
                result.Add(Describe(CleanFieldName(field.Name), () => StaticField(type, token, context?.Frame), evaluateName, context, options));
            }
        }
        return result;
    }

    public CorDebugValue? StaticField(CorDebugType type, int fieldToken, FrameRef? frame)
    {
        CorDebugFrame? corFrame = frame == null ? null : _host.GetFrame(frame);
        return type.GetStaticFieldValue(new mdFieldDef(fieldToken), corFrame?.Raw!);
    }

    private static CorDebugValue? FieldOf(Func<CorDebugValue?> ownerGetter, CorDebugClass cls, int fieldToken)
    {
        CorDebugValue? target = ownerGetter() is { } v ? Unwrap(v, out _) : null;
        return target?.As<CorDebugObjectValue>().GetFieldValue(cls.Raw, new mdFieldDef(fieldToken));
    }

    private static string? Member(string? parent, string name) => parent == null ? null : parent + "." + name;

    // ---------------------------------------------------------------- collections

    /// <param name="Addressable">Whether "collection" + Name is a valid expression for the item (true for indexers).</param>
    private sealed record CollectionItem(string Name, Func<CorDebugValue?> Getter, bool Addressable = true);

    /// <param name="Items">All items, in order. Skipping must be cheap: requests page deep into large collections.</param>
    /// <param name="Backing">The array the items "[i]" live in (item i at Offset + i), where it is that simple.</param>
    private sealed record CollectionView(int Count, Func<IEnumerable<CollectionItem>> Items, (Func<CorDebugValue?> Array, int Offset)? Backing = null);

    private const string Generic = "System.Collections.Generic.";

    /// <summary>
    /// Understands the memory layout of the common BCL collections, so their elements can be shown without running
    /// code in the debuggee. Returns null for everything else (and throws if a layout is not what it used to be).
    /// </summary>
    private CollectionView? TryGetCollection(Func<CorDebugValue?> getter, string typeName, InspectionContext? context, DisplayOptions options)
    {
        CorDebugValue? Field(string field) => getter() is { } v ? GetFieldByName(v, field) : null;
        int Int(string field) => Convert.ToInt32(ReadPrimitive(Field(field) ?? throw new InvalidOperationException(field)), CultureInfo.InvariantCulture);
        int Length(string arrayField) => Field(arrayField) is { } a && Unwrap(a, out _) is { } array ? array.As<CorDebugArrayValue>().Count : 0;
        CollectionItem Indexed(int index, Func<CorDebugValue?> element, bool addressable = true) => new("[" + index + "]", element, addressable);
        string Key(Func<CorDebugValue?> key) => DescribeCore("key", key, null, context, options).Value;

        if (typeName.StartsWith(Generic + "List<", StringComparison.Ordinal))
        {
            int size = Int("_size");
            return new CollectionView(size, () => Enumerable.Range(0, size).Select(i => Indexed(i, () => ElementAt(() => Field("_items"), i))),
                (() => Field("_items"), 0));
        }
        if (typeName.StartsWith(Generic + "Stack<", StringComparison.Ordinal))
        {
            int size = Int("_size");
            // in the order the elements would be popped
            return new CollectionView(size, () => Enumerable.Range(0, size).Select(i => Indexed(i, () => ElementAt(() => Field("_array"), size - 1 - i), false)));
        }
        if (typeName.StartsWith(Generic + "Queue<", StringComparison.Ordinal))
        {
            int size = Int("_size"), head = Int("_head"), capacity = Math.Max(1, Length("_array"));
            return new CollectionView(size, () => Enumerable.Range(0, size).Select(i => Indexed(i, () => ElementAt(() => Field("_array"), (head + i) % capacity), false)));
        }
        if (typeName.StartsWith("System.ArraySegment<", StringComparison.Ordinal))
        {
            int offset = Int("_offset"), size = Int("_count");
            return new CollectionView(size, () => Enumerable.Range(0, size).Select(i => Indexed(i, () => ElementAt(() => Field("_array"), offset + i))),
                (() => Field("_array"), offset));
        }
        if (typeName.StartsWith("System.Collections.Immutable.ImmutableArray<", StringComparison.Ordinal))
        {
            int size = Length("array");
            return new CollectionView(size, () => Enumerable.Range(0, size).Select(i => Indexed(i, () => ElementAt(() => Field("array"), i))),
                (() => Field("array"), 0));
        }
        if (typeName.StartsWith("System.Collections.ObjectModel.ReadOnlyCollection<", StringComparison.Ordinal))
        {
            // a wrapper: whatever the inner list is
            CorDebugValue? inner = Field("list") is { } l ? Unwrap(l, out _) : null;
            if (inner == null)
                return null;
            if (inner.Type is CorElementType.SZArray)
            {
                int size = inner.As<CorDebugArrayValue>().Count;
                return new CollectionView(size, () => Enumerable.Range(0, size).Select(i => Indexed(i, () => ElementAt(() => Field("list"), i))));
            }
            return TryGetCollection(() => Field("list"), TypeName(inner.ExactType), context, options);
        }
        if (typeName.StartsWith("System.Memory<", StringComparison.Ordinal) || typeName.StartsWith("System.ReadOnlyMemory<", StringComparison.Ordinal))
        {
            if (Field("_object") is not { } backing || Unwrap(backing, out _) is not { Type: CorElementType.SZArray })
                return null; // strings, memory managers, empty
            int offset = Int("_index") & int.MaxValue, size = Int("_length");
            return new CollectionView(size, () => Enumerable.Range(0, size).Select(i => Indexed(i, () => ElementAt(() => Field("_object"), offset + i), false)));
        }
        if (typeName.StartsWith("System.Collections.Concurrent.ConcurrentDictionary<", StringComparison.Ordinal))
        {
            // _tables._buckets[i]._node -> _key, _value, _next
            IEnumerable<Func<CorDebugValue?>> Nodes()
            {
                int buckets = Field("_tables") is { } t && GetFieldByName(t, "_buckets") is { } b && Unwrap(b, out _) is { } array ? array.As<CorDebugArrayValue>().Count : 0;
                for (int i = 0; i < buckets; i++)
                {
                    int bucket = i;
                    Func<CorDebugValue?> node = () => GetFieldByName(ElementAt(() => GetFieldByName(Field("_tables")!, "_buckets"), bucket)!, "_node");
                    for (int guard = 0; guard < 100_000; guard++)
                    {
                        if (node() is not { } current || Unwrap(current, out bool isNull) == null || isNull)
                            break;
                        Func<CorDebugValue?> captured = node;
                        yield return captured;
                        node = () => GetFieldByName(captured()!, "_next");
                    }
                }
            }
            int count = Nodes().Count();
            return new CollectionView(count, () => Nodes().Select(n =>
                new CollectionItem("[" + Key(() => GetFieldByName(n()!, "_key")) + "]", () => GetFieldByName(n()!, "_value"))));
        }
        if (typeName.StartsWith(Generic + "SortedList<", StringComparison.Ordinal))
        {
            int size = Int("_size");
            return new CollectionView(size, () => Enumerable.Range(0, size).Select(i =>
                new CollectionItem("[" + Key(() => ElementAt(() => Field("keys"), i)) + "]", () => ElementAt(() => Field("values"), i))));
        }

        bool dictionary = typeName.StartsWith(Generic + "Dictionary<", StringComparison.Ordinal);
        if (dictionary || typeName.StartsWith(Generic + "HashSet<", StringComparison.Ordinal))
        {
            // _entries[0.._count) where next >= -1 are live; freed entries chain through more negative values
            int used = Int("_count"), live = used - Int("_freeCount");
            string next = dictionary ? "next" : "Next";
            CollectionItem Item(int position, int index)
            {
                Func<CorDebugValue?> entry = () => ElementAt(() => Field("_entries"), position);
                return dictionary
                    ? new CollectionItem("[" + Key(() => GetFieldByName(entry()!, "key")) + "]", () => GetFieldByName(entry()!, "value"))
                    : Indexed(index, () => GetFieldByName(entry()!, "Value"), false);
            }
            IEnumerable<CollectionItem> Items()
            {
                int index = 0;
                for (int i = 0; i < used; i++)
                {
                    _host.ThrowIfCancelled();
                    if (ElementAt(() => Field("_entries"), i) is not { } e || Convert.ToInt32(ReadPrimitive(GetFieldByName(e, next)!), CultureInfo.InvariantCulture) < -1)
                        continue;
                    yield return Item(i, index++);
                }
            }
            // nothing was ever removed: entry i is item i, and a page deep inside costs no more than the first one
            return used == live
                ? new CollectionView(live, () => Enumerable.Range(0, live).Select(i => Item(i, i)))
                : new CollectionView(live, Items);
        }
        return null;
    }

    // ---------------------------------------------------------------- type walking

    internal sealed record TypeLevel(CorDebugType Type, ModuleMetadata Metadata, int Token);

    /// <summary>The exact type of an (unwrapped) object followed by its base types, skipping those without metadata.</summary>
    public List<TypeLevel> GetTypeLevels(CorDebugValue target)
    {
        var result = new List<TypeLevel>();
        if (target.Type is not (CorElementType.Class or CorElementType.ValueType or CorElementType.Object))
            return result;
        for (CorDebugType? type = target.ExactType; type != null; type = TryGet(() => type.Base))
        {
            if (type.Type is not (CorElementType.Class or CorElementType.ValueType))
                break;
            CorDebugClass cls = type.Class;
            if (_getMetadata(cls.Module) is { } metadata)
                result.Add(new TypeLevel(type, metadata, (int)cls.Token.Value));
        }
        return result;
    }

    public static CorDebugType[] TypeArgumentsOf(CorDebugType type) => TryGet(() => type.TypeParameters) ?? [];

    /// <summary>Instance fields of an object, most-derived type first. Accepts references and boxes.</summary>
    public IEnumerable<(string Name, CorDebugValue? Value)> EnumerateFields(CorDebugValue value)
    {
        CorDebugValue? target = Unwrap(value, out bool isNull);
        if (isNull || target == null)
            yield break;

        foreach (TypeLevel level in GetTypeLevels(target))
        {
            var obj = target.As<CorDebugObjectValue>();
            foreach (FieldDescription field in level.Metadata.GetFields(level.Token))
            {
                if (field.IsStatic || field.IsLiteral)
                    continue;
                CorDebugClass cls = level.Type.Class;
                yield return (field.Name, TryGet(() => obj.GetFieldValue(cls.Raw, new mdFieldDef(field.Token))));
            }
        }
    }

    public CorDebugValue? GetFieldByName(CorDebugValue value, string name)
    {
        foreach ((string fieldName, CorDebugValue? fieldValue) in EnumerateFields(value))
            if (fieldName == name)
                return fieldValue;
        return null;
    }

    private CorDebugValue? GetField(CorDebugObjectValue obj, CorDebugType type, string name)
    {
        CorDebugClass cls = type.Class;
        ModuleMetadata? metadata = _getMetadata(cls.Module);
        if (metadata == null)
            return null;
        foreach (FieldDescription field in metadata.GetFields((int)cls.Token.Value))
            if (field.Name == name && !field.IsStatic)
                return obj.GetFieldValue(cls.Raw, new mdFieldDef(field.Token));
        return null;
    }

    internal static string CleanFieldName(string name)
    {
        // <Name>k__BackingField -> Name
        if (name.StartsWith('<') && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
            return name[1..name.IndexOf('>')];
        return name;
    }

    // ---------------------------------------------------------------- reading values

    /// <summary>Follows references and boxes down to the value that holds the data.</summary>
    internal static CorDebugValue? Unwrap(CorDebugValue value, out bool isNull)
    {
        isNull = false;
        for (int depth = 0; depth < 8; depth++)
        {
            if (value is CorDebugReferenceValue reference && value.Type is not (CorElementType.Ptr or CorElementType.FnPtr))
            {
                if (reference.IsNull)
                {
                    isNull = true;
                    return null;
                }
                value = reference.Dereference();
                continue;
            }
            if (value is CorDebugBoxValue box)
            {
                value = box.Object;
                continue;
            }
            return value;
        }
        return value;
    }

    /// <summary>
    /// Converts a value to a host object where that is meaningful: primitives, strings, decimals, enums (as
    /// <see cref="EnumBits"/>), nullables and boxes of those. Returns false for other objects.
    /// </summary>
    public bool TryReadHostValue(CorDebugValue value, out object? result)
    {
        result = null;
        CorDebugValue? target = Unwrap(value, out bool isNull);
        if (isNull || target == null)
            return true;

        switch (target.Type)
        {
            case CorElementType.String:
            case CorElementType.Class when IsString(target):
                result = ReadStringValue(target);
                return true;
            case CorElementType.Class or CorElementType.Object or CorElementType.SZArray or CorElementType.Array
                or CorElementType.Ptr or CorElementType.ByRef or CorElementType.FnPtr:
                return false;
            case CorElementType.ValueType:
            {
                var obj = target.As<CorDebugObjectValue>();
                CorDebugType type = target.ExactType;
                string typeName = TypeName(type);
                if (IsEnum(type))
                {
                    CorDebugValue raw = GetField(obj, type, "value__")!;
                    result = new EnumBits(type.Class, ReadIntegerBits(raw), ReadPrimitive(raw)!);
                    return true;
                }
                if (typeName == "decimal")
                {
                    result = TryReadDecimal(obj, type);
                    return result != null;
                }
                if (typeName.StartsWith("System.Nullable<", StringComparison.Ordinal))
                {
                    if (ReadIntegerBits(GetField(obj, type, "hasValue")!) == 0)
                        return true;
                    return TryReadHostValue(GetField(obj, type, "value")!, out result);
                }
                if (GetField(obj, type, "m_value") is { } primitive && primitive.Type != CorElementType.ValueType)
                    return TryReadHostValue(primitive, out result);
                return false;
            }
            default:
                result = ReadPrimitive(target);
                return result != null;
        }
    }

    /// <summary>An enum value read from the debuggee: its type, raw bits and the boxed underlying value.</summary>
    internal sealed record EnumBits(CorDebugClass Class, ulong Bits, object Underlying);

    public string FormatEnum(CorDebugClass enumClass, ulong bits) =>
        _getMetadata(enumClass.Module)?.GetEnumName((int)enumClass.Token.Value, bits) ?? ((long)bits).ToString(CultureInfo.InvariantCulture);

    public string? ReadString(CorDebugValue? value)
    {
        if (value == null)
            return null;
        CorDebugValue? target = Unwrap(value, out bool isNull);
        return isNull || target == null || !IsString(target) ? null : ReadStringValue(target);
    }

    private bool IsString(CorDebugValue target) =>
        target.Type == CorElementType.String || (target.Type == CorElementType.Class && SafeTypeName(target) == "System.String");

    // one character more than what is shown: that is how FormatHost knows the string was longer
    private static int StringLimit(DisplayOptions options) => options.FullStrings ? int.MaxValue : MaxStringLength + 1;

    /// <summary>
    /// Reads the characters out of the debuggee's memory: ICorDebugStringValue::GetString cannot return a part of a
    /// string, and strings reached through their address are not typed as strings by ICorDebug at all (see IsString).
    /// </summary>
    private string ReadStringValue(CorDebugValue target, int limit = int.MaxValue)
    {
        // Layout of System.String: method table pointer, int length, UTF-16 characters.
        ulong address = target.Address.Value;
        int length = target.Type == CorElementType.String
            ? target.As<CorDebugStringValue>().Length
            : BitConverter.ToInt32(_host.ReadMemory(address + (ulong)IntPtr.Size, 4));
        int read = Math.Clamp(length, 0, limit);
        return read == 0 ? "" : System.Text.Encoding.Unicode.GetString(_host.ReadMemory(address + (ulong)IntPtr.Size + 4, read * 2));
    }

    public string GetTypeName(CorDebugValue value)
    {
        CorDebugValue? target = Unwrap(value, out _);
        return SafeTypeName(target ?? value) ?? "?";
    }

    /// <summary>Reflection-style name of the runtime type ("System.Int32", "TestApp.Person", "System.String[]").</summary>
    public string? GetClrTypeName(CorDebugValue value)
    {
        CorDebugValue? target = Unwrap(value, out bool isNull);
        return isNull || target == null ? null : ClrTypeName(target.ExactType);
    }

    public string ClrTypeName(CorDebugType type) => type.Type switch
    {
        CorElementType.Class or CorElementType.ValueType =>
            _getMetadata(type.Class.Module)?.GetTypeName((int)type.Class.Token.Value) ?? "?",
        CorElementType.SZArray => ClrTypeName(type.FirstTypeParameter) + "[]",
        CorElementType.Array => ClrTypeName(type.FirstTypeParameter) + "[" + new string(',', type.Rank - 1) + "]",
        CorElementType.I => "System.IntPtr",
        CorElementType.U => "System.UIntPtr",
        CorElementType.I1 => "System.SByte",
        CorElementType.U1 => "System.Byte",
        CorElementType.I2 => "System.Int16",
        CorElementType.U2 => "System.UInt16",
        CorElementType.I4 => "System.Int32",
        CorElementType.U4 => "System.UInt32",
        CorElementType.I8 => "System.Int64",
        CorElementType.U8 => "System.UInt64",
        CorElementType.R4 => "System.Single",
        CorElementType.R8 => "System.Double",
        _ => "System." + type.Type,
    };

    private bool IsEnum(CorDebugType type)
    {
        if (type.Type != CorElementType.ValueType)
            return false;
        CorDebugClass cls = type.Class;
        return _getMetadata(cls.Module)?.IsEnum((int)cls.Token.Value) == true;
    }

    private decimal? TryReadDecimal(CorDebugObjectValue obj, CorDebugType type)
    {
        try
        {
            int flags = (int)ReadIntegerBits(GetField(obj, type, "_flags")!);
            uint hi = (uint)ReadIntegerBits(GetField(obj, type, "_hi32")!);
            ulong lo64 = ReadIntegerBits(GetField(obj, type, "_lo64")!);
            return new decimal((int)(uint)lo64, (int)(uint)(lo64 >> 32), (int)hi, flags < 0, (byte)(flags >> 16));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The bytes of a primitive value, exactly as many as it has.</summary>
    internal static byte[] ReadRawBytes(CorDebugValue value) => ReadBytes(value)[..value.Size];

    private static byte[] ReadBytes(CorDebugValue value)
    {
        var generic = value.As<CorDebugGenericValue>();
        var buffer = new byte[Math.Max(value.Size, 8)];
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            generic.GetValue(handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
        return buffer;
    }

    public static void WriteBytes(CorDebugValue value, byte[] bytes)
    {
        var generic = value.As<CorDebugGenericValue>();
        GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            generic.SetValue(handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    private static ulong ReadIntegerBits(CorDebugValue value)
    {
        byte[] b = ReadBytes(value);
        return value.Type switch
        {
            CorElementType.Boolean or CorElementType.U1 => b[0],
            CorElementType.I1 => (ulong)(sbyte)b[0],
            CorElementType.I2 => (ulong)BitConverter.ToInt16(b),
            CorElementType.U2 or CorElementType.Char => BitConverter.ToUInt16(b),
            CorElementType.I4 => (ulong)BitConverter.ToInt32(b),
            CorElementType.U4 => BitConverter.ToUInt32(b),
            _ => BitConverter.ToUInt64(b),
        };
    }

    /// <summary>Reads a primitive into the equivalent host type; null for anything else.</summary>
    public static object? ReadPrimitive(CorDebugValue value)
    {
        if (value.Type is not (CorElementType.Boolean or CorElementType.Char or CorElementType.I1 or CorElementType.U1
            or CorElementType.I2 or CorElementType.U2 or CorElementType.I4 or CorElementType.U4 or CorElementType.I8
            or CorElementType.U8 or CorElementType.R4 or CorElementType.R8 or CorElementType.I or CorElementType.U))
        {
            return null;
        }

        byte[] b = ReadBytes(value);
        return value.Type switch
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
            CorElementType.I => value.Size == 4 ? (nint)BitConverter.ToInt32(b) : (nint)BitConverter.ToInt64(b),
            _ => value.Size == 4 ? (nuint)BitConverter.ToUInt32(b) : (nuint)BitConverter.ToUInt64(b),
        };
    }

    /// <summary>C#-literal-like text of a host value and the C# name of its type.</summary>
    public static (string Text, string? Type) FormatHost(object? value, DisplayOptions? options = null)
    {
        options ??= DisplayOptions.Default;
        CultureInfo inv = CultureInfo.InvariantCulture;
        if (options.Hex && value is not null)
        {
            string? hex = value switch
            {
                sbyte v => "0x" + v.ToString("X2", inv),
                byte v => "0x" + v.ToString("X2", inv),
                short v => "0x" + v.ToString("X4", inv),
                ushort v => "0x" + v.ToString("X4", inv),
                int v => "0x" + v.ToString("X8", inv),
                uint v => "0x" + v.ToString("X8", inv),
                long v => "0x" + v.ToString("X16", inv),
                ulong v => "0x" + v.ToString("X16", inv),
                char v => "0x" + ((int)v).ToString("X4", inv) + " " + FormatChar(v),
                _ => null,
            };
            if (hex != null)
                return (hex, FormatHost(value).Type);
        }
        return value switch
        {
            null => ("null", null),
            bool b => (b ? "true" : "false", "bool"),
            char c => (options.NoQuotes ? c.ToString() : FormatChar(c), "char"),
            string { Length: > MaxStringLength } s when !options.FullStrings => FormatHost(s[..MaxStringLength] + "...", options with { FullStrings = true }),
            string s => (options.NoQuotes ? s : Quote(s), "string"),
            float f => (f.ToString("R", inv), "float"),
            double d => (d.ToString("R", inv), "double"),
            decimal m => (m.ToString(inv), "decimal"),
            sbyte v => (v.ToString(inv), "sbyte"),
            byte v => (v.ToString(inv), "byte"),
            short v => (v.ToString(inv), "short"),
            ushort v => (v.ToString(inv), "ushort"),
            int v => (v.ToString(inv), "int"),
            uint v => (v.ToString(inv), "uint"),
            long v => (v.ToString(inv), "long"),
            ulong v => (v.ToString(inv), "ulong"),
            nint v => ("0x" + v.ToString("X"), "nint"),
            nuint v => ("0x" + v.ToString("X"), "nuint"),
            _ => (Convert.ToString(value, inv) ?? "", value.GetType().FullName),
        };
    }

    private static string FormatChar(char c) => c switch
    {
        '\'' => "'\\''",
        '\\' => "'\\\\'",
        '\0' => "'\\0'",
        '\n' => "'\\n'",
        '\r' => "'\\r'",
        '\t' => "'\\t'",
        _ when char.IsControl(c) => $"'\\u{(int)c:x4}'",
        _ => "'" + c + "'",
    };

    private static string Quote(string s)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (char c in s)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\0' => "\\0",
                _ => c.ToString(),
            });
        }
        return sb.Append('"').ToString();
    }

    private string? SafeTypeName(CorDebugValue value) => TryGet(() => TypeName(value.ExactType));

    /// <summary>C# style name of a type, e.g. "System.Collections.Generic.List&lt;int&gt;".</summary>
    public string TypeName(CorDebugType type)
    {
        switch (type.Type)
        {
            case CorElementType.Void: return "void";
            case CorElementType.Boolean: return "bool";
            case CorElementType.Char: return "char";
            case CorElementType.I1: return "sbyte";
            case CorElementType.U1: return "byte";
            case CorElementType.I2: return "short";
            case CorElementType.U2: return "ushort";
            case CorElementType.I4: return "int";
            case CorElementType.U4: return "uint";
            case CorElementType.I8: return "long";
            case CorElementType.U8: return "ulong";
            case CorElementType.R4: return "float";
            case CorElementType.R8: return "double";
            case CorElementType.I: return "nint";
            case CorElementType.U: return "nuint";
            case CorElementType.String: return "string";
            case CorElementType.Object: return "object";
            case CorElementType.SZArray: return TypeName(type.FirstTypeParameter) + "[]";
            case CorElementType.Array: return TypeName(type.FirstTypeParameter) + "[" + new string(',', type.Rank - 1) + "]";
            case CorElementType.Ptr: return TypeName(type.FirstTypeParameter) + "*";
            case CorElementType.ByRef: return "ref " + TypeName(type.FirstTypeParameter);
            case CorElementType.Class:
            case CorElementType.ValueType:
            {
                CorDebugClass cls = type.Class;
                string name = _getMetadata(cls.Module)?.GetTypeName((int)cls.Token.Value) ?? $"<type 0x{cls.Token.Value:X8}>";
                if (name == "System.Decimal")
                    return "decimal";
                CorDebugType[] typeArgs = TypeArgumentsOf(type);
                return typeArgs.Length == 0 ? name : ApplyTypeArguments(name, typeArgs.Select(TypeName).ToArray());
            }
            default:
                return type.Type.ToString();
        }
    }

    // "Outer`1.Inner`1" + [A, B] -> "Outer<A>.Inner<B>"
    private static string ApplyTypeArguments(string name, string[] typeArgs)
    {
        var sb = new System.Text.StringBuilder();
        int next = 0;
        foreach (string segment in name.Split('.'))
        {
            if (sb.Length > 0)
                sb.Append('.');
            int tick = segment.IndexOf('`');
            if (tick < 0 || !int.TryParse(segment[(tick + 1)..], out int arity))
            {
                sb.Append(segment);
                continue;
            }
            sb.Append(segment, 0, tick).Append('<');
            sb.AppendJoin(", ", typeArgs.Skip(next).Take(arity));
            sb.Append('>');
            next += arity;
        }
        return sb.ToString();
    }

    private static T? TryGet<T>(Func<T?> func) where T : class
    {
        try
        {
            return func();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? TryGet(Func<int> func)
    {
        try
        {
            return func();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
