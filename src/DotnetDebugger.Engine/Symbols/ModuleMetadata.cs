using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DotnetDebugger.Engine.Symbols;

internal readonly record struct SourceLocation(string Path, int Line, int Column, int EndLine, int EndColumn);

internal readonly record struct ResolvedBreakpoint(int MethodToken, int ILOffset, int Line, int Column, int EndLine, int EndColumn);

internal readonly record struct FieldDescription(int Token, string Name, bool IsStatic, bool IsLiteral);

/// <summary>
/// ECMA-335 metadata and portable PDB of one module, read from disk with System.Reflection.Metadata.
/// </summary>
internal sealed partial class ModuleMetadata : IDisposable
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly PEReader _pe;
    private MetadataReaderProvider? _pdbProvider;
    private readonly MetadataReader _md;
    private MetadataReader? _pdb;
    private readonly object _sync = new();
    private Dictionary<DocumentHandle, List<(int MethodToken, SequencePoint Point)>>? _pointsByDocument;

    public string Path { get; }
    public bool HasSymbols => _pdb != null;

    private ModuleMetadata(string path, PEReader pe, MetadataReaderProvider? pdbProvider)
    {
        Path = path;
        _pe = pe;
        _md = pe.GetMetadataReader();
        _pdbProvider = pdbProvider;
        _pdb = pdbProvider?.GetMetadataReader();
    }

    public static ModuleMetadata? TryOpen(string path)
    {
        PEReader? pe = null;
        try
        {
            if (!File.Exists(path))
                return null;
            pe = new PEReader(File.OpenRead(path));
            if (!pe.HasMetadata)
            {
                pe.Dispose();
                return null;
            }

            MetadataReaderProvider? pdb = null;
            try
            {
                pe.TryOpenAssociatedPortablePdb(path, p => File.Exists(p) ? File.OpenRead(p) : null, out pdb, out _);
            }
            catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                // Windows PDBs and unreadable files simply mean "no symbols".
            }
            return new ModuleMetadata(path, pe, pdb);
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            pe?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// For modules that are not files: assemblies inside a single-file bundle, Assembly.Load(byte[]). The image is
    /// copied out of the debuggee; the PDB is looked for next to where the module claims to be, or embedded.
    /// </summary>
    /// <param name="isLoadedImage">Sections are laid out as the OS loader does, rather than as in the file.</param>
    public static ModuleMetadata? TryOpenFromMemory(string nominalPath, byte[] image, bool isLoadedImage)
    {
        PEReader? pe = null;
        try
        {
            pe = new PEReader(new MemoryStream(image, writable: false), isLoadedImage ? PEStreamOptions.IsLoadedImage : PEStreamOptions.Default);
            if (!pe.HasMetadata)
            {
                pe.Dispose();
                return null;
            }

            MetadataReaderProvider? pdb = null;
            try
            {
                pe.TryOpenAssociatedPortablePdb(nominalPath, p => File.Exists(p) ? File.OpenRead(p) : null, out pdb, out _);
            }
            catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException or ArgumentException)
            {
            }
            return new ModuleMetadata(nominalPath, pe, pdb);
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or InvalidOperationException)
        {
            pe?.Dispose();
            return null;
        }
    }

    // ---------------------------------------------------------------- sequence points

    private static string NormalizePath(string path) =>
        OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;

    private Dictionary<DocumentHandle, List<(int MethodToken, SequencePoint Point)>> PointsByDocument
    {
        get
        {
            lock (_sync)
            {
                if (_pointsByDocument != null)
                    return _pointsByDocument;
                var map = new Dictionary<DocumentHandle, List<(int MethodToken, SequencePoint Point)>>();
                foreach (MethodDebugInformationHandle handle in _pdb!.MethodDebugInformation)
                {
                    int token = MetadataTokens.GetToken(handle.ToDefinitionHandle());
                    foreach (SequencePoint sp in _pdb.GetMethodDebugInformation(handle).GetSequencePoints())
                    {
                        if (sp.IsHidden || sp.Document.IsNil)
                            continue;
                        if (!map.TryGetValue(sp.Document, out var list))
                            map[sp.Document] = list = [];
                        list.Add((token, sp));
                    }
                }
                return _pointsByDocument = map;
            }
        }
    }

    /// <summary>
    /// Finds the best IL location for a breakpoint requested at <paramref name="line"/>. With a column the innermost
    /// sequence point containing that position wins, which is how a lambda body inside a statement gets selected.
    /// </summary>
    public ResolvedBreakpoint? ResolveBreakpoint(string sourcePath, int line, int? column = null)
    {
        if (_pdb == null)
            return null;

        string wanted = NormalizePath(sourcePath);
        (int Token, SequencePoint Point)? best = null;
        (int Token, SequencePoint Point)? bestAtColumn = null;
        foreach (DocumentHandle docHandle in _pdb.Documents)
        {
            string docPath = NormalizePath(_pdb.GetString(_pdb.GetDocument(docHandle).Name));
            if (!docPath.Equals(wanted, PathComparison) || !PointsByDocument.TryGetValue(docHandle, out var points))
                continue;

            foreach (var candidate in points)
            {
                SequencePoint p = candidate.Point;
                if (p.EndLine < line)
                    continue;
                if (best == null || IsBetter(p, best.Value.Point, line))
                    best = candidate;
                if (column is { } c && Contains(p, line, c) && (bestAtColumn == null || Span(p) < Span(bestAtColumn.Value.Point)))
                    bestAtColumn = candidate;
            }
        }

        return (bestAtColumn ?? best) is { } b
            ? new ResolvedBreakpoint(b.Token, b.Point.Offset, b.Point.StartLine, b.Point.StartColumn, b.Point.EndLine, b.Point.EndColumn)
            : null;

        static bool Contains(SequencePoint p, int line, int column) =>
            (p.StartLine < line || (p.StartLine == line && p.StartColumn <= column)) &&
            (p.EndLine > line || (p.EndLine == line && column < p.EndColumn));

        static long Span(SequencePoint p) => (p.EndLine - p.StartLine) * 10000L + (p.EndColumn - p.StartColumn);
    }

    // Preference: a point starting on the line; then the innermost point spanning the line
    // (lambda bodies inside a multi-line statement); then the first point after the line.
    private static bool IsBetter(SequencePoint a, SequencePoint b, int line)
    {
        int ra = Rank(a, line), rb = Rank(b, line);
        if (ra != rb)
            return ra < rb;
        return ra switch
        {
            0 => (a.StartColumn, a.Offset) .CompareTo((b.StartColumn, b.Offset)) < 0,
            1 => a.StartLine != b.StartLine ? a.StartLine > b.StartLine : a.EndLine < b.EndLine,
            _ => (a.StartLine, a.StartColumn).CompareTo((b.StartLine, b.StartColumn)) < 0,
        };

        static int Rank(SequencePoint p, int line) => p.StartLine == line ? 0 : p.StartLine < line ? 1 : 2;
    }

    private List<SequencePoint>? GetSequencePoints(int methodToken)
    {
        if (_pdb == null)
            return null;
        var handle = ((MethodDefinitionHandle)MetadataTokens.Handle(methodToken)).ToDebugInformationHandle();
        if (MetadataTokens.GetRowNumber(handle) > _pdb.MethodDebugInformation.Count)
            return null;
        var points = _pdb.GetMethodDebugInformation(handle).GetSequencePoints().ToList();
        return points.Count == 0 ? null : points;
    }

    /// <summary>Maps an IL offset to source. Returns null for code without (visible) line info.</summary>
    public SourceLocation? GetSourceLocation(int methodToken, int ilOffset, out bool isHidden)
    {
        isHidden = false;
        var points = GetSequencePoints(methodToken);
        if (points == null)
            return null;

        SequencePoint? current = null;
        foreach (SequencePoint sp in points)
        {
            if (sp.Offset > ilOffset)
                break;
            current = sp;
        }
        current ??= points[0];
        if (current.Value.IsHidden)
        {
            isHidden = true;
            return null;
        }

        SequencePoint p = current.Value;
        string path = _pdb!.GetString(_pdb.GetDocument(p.Document).Name);
        return new SourceLocation(path, p.StartLine, p.StartColumn, p.EndLine, p.EndColumn);
    }

    /// <summary>IL range [start, end) of the statement containing <paramref name="ilOffset"/>.</summary>
    public (int Start, int End)? GetStepRange(int methodToken, int ilOffset, int ilCodeSize)
    {
        var points = GetSequencePoints(methodToken);
        if (points == null)
            return null;

        int index = -1;
        for (int i = 0; i < points.Count && points[i].Offset <= ilOffset; i++)
            index = i;
        if (index < 0)
            return (0, points[0].Offset);

        int end = ilCodeSize;
        for (int i = index + 1; i < points.Count; i++)
        {
            if (!points[i].IsHidden)
            {
                end = points[i].Offset;
                break;
            }
        }
        return (points[index].Offset, end);
    }

    /// <summary>Named locals whose scope covers <paramref name="ilOffset"/>, as (slot index, name).</summary>
    public List<(int Index, string Name)> GetLocals(int methodToken, int ilOffset)
    {
        var result = new List<(int, string)>();
        if (_pdb == null)
            return result;

        var method = (MethodDefinitionHandle)MetadataTokens.Handle(methodToken);
        foreach (LocalScopeHandle scopeHandle in _pdb.GetLocalScopes(method))
        {
            LocalScope scope = _pdb.GetLocalScope(scopeHandle);
            if (ilOffset < scope.StartOffset || ilOffset >= scope.EndOffset)
                continue;
            foreach (LocalVariableHandle localHandle in scope.GetLocalVariables())
            {
                LocalVariable local = _pdb.GetLocalVariable(localHandle);
                result.Add((local.Index, _pdb.GetString(local.Name)));
            }
        }
        result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return result;
    }

    // ---------------------------------------------------------------- metadata

    public string GetTypeName(int typeDefToken)
    {
        var handle = (TypeDefinitionHandle)MetadataTokens.Handle(typeDefToken);
        return GetTypeName(handle);
    }

    private string GetTypeName(TypeDefinitionHandle handle)
    {
        TypeDefinition type = _md.GetTypeDefinition(handle);
        string name = _md.GetString(type.Name);
        TypeDefinitionHandle declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
            return GetTypeName(declaring) + "." + name;
        string ns = _md.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>
    /// "Namespace.Type.Method" as the user knows it: state machines, lambdas and local functions are folded back
    /// into the method that contains them, generic parameters are shown (with <paramref name="typeArguments"/>
    /// substituted where the runtime knows the exact type; class parameters first, then method parameters).
    /// </summary>
    public string GetMethodName(int methodToken, IReadOnlyList<string?>? typeArguments = null)
    {
        MethodDefinition method = _md.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(methodToken));
        string name = _md.GetString(method.Name);
        TypeDefinitionHandle typeHandle = method.GetDeclaringType();
        bool generated = false;

        // Walk out of compiler generated types, remembering the user method encoded in their names:
        // <Foo>d__3.MoveNext -> Foo;  <>c__DisplayClass1_0.<Foo>b__0 -> <Foo>b__0
        while (true)
        {
            TypeDefinition type = _md.GetTypeDefinition(typeHandle);
            string typeName = _md.GetString(type.Name);
            if (!typeName.StartsWith('<') || type.GetDeclaringType().IsNil)
                break;
            generated = true;
            int close = typeName.LastIndexOf('>');
            if (name == "MoveNext" && close > 1)
                name = typeName[1..close];
            typeHandle = type.GetDeclaringType();
        }

        // get_Name -> Name.get, the way a user thinks of a property accessor
        if ((method.Attributes & MethodAttributes.SpecialName) != 0 && (name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal)))
            name = name[4..] + "." + name[..3];

        string prettyName = PrettifyGeneratedName(name);
        generated |= prettyName != name;
        string result = GetTypeName(typeHandle);

        // Type arguments only line up with the declared parameters for code that is not compiler generated.
        if (!generated)
        {
            string[] typeParameters = GetTypeGenericParameters(MetadataTokens.GetToken(typeHandle));
            string[] methodParameters = GetMethodGenericParameters(methodToken);
            result = ApplyGenericParameters(result, typeParameters, typeArguments, 0);
            prettyName += FormatGenericParameters(methodParameters, typeArguments, typeParameters.Length);
        }
        else
        {
            result = StripArity(result);
        }
        return result + "." + prettyName;
    }

    // <Run>b__0_1 -> Run.AnonymousMethod__0_1;  <Run>g__Local|0_1 -> Run.Local;
    // nested: <<Run>b__0>g__Inner|1 -> Run.AnonymousMethod__0.Inner
    private static string PrettifyGeneratedName(string name)
    {
        if (!name.StartsWith('<'))
            return name;
        int depth = 0, close = -1;
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == '<')
            {
                depth++;
            }
            else if (name[i] == '>' && --depth == 0)
            {
                close = i;
                break;
            }
        }
        if (close < 2)
            return name;
        string containing = PrettifyGeneratedName(name[1..close]);
        if (close + 3 >= name.Length || name[close + 2] != '_' || name[close + 3] != '_')
            return close == name.Length - 1 ? containing : name;

        char kind = name[close + 1];
        string rest = name[(close + 4)..];
        return kind switch
        {
            'b' => containing + ".AnonymousMethod__" + rest,
            'g' => containing + "." + (rest.Contains('|') ? rest[..rest.IndexOf('|')] : rest),
            _ => name,
        };
    }

    private static string StripArity(string typeName) =>
        string.Join('.', typeName.Split('.').Select(s => s.Contains('`') ? s[..s.IndexOf('`')] : s));

    private static string ApplyGenericParameters(string typeName, string[] parameters, IReadOnlyList<string?>? arguments, int offset)
    {
        if (parameters.Length == 0)
            return typeName;
        var sb = new System.Text.StringBuilder();
        int next = 0;
        foreach (string segment in typeName.Split('.'))
        {
            if (sb.Length > 0)
                sb.Append('.');
            int tick = segment.IndexOf('`');
            if (tick < 0 || !int.TryParse(segment[(tick + 1)..], out int arity))
            {
                sb.Append(segment);
                continue;
            }
            sb.Append(segment, 0, tick).Append(FormatGenericParameters(parameters.Skip(next).Take(arity).ToArray(), arguments, offset + next));
            next += arity;
        }
        return sb.ToString();
    }

    private static string FormatGenericParameters(string[] parameters, IReadOnlyList<string?>? arguments, int offset)
    {
        if (parameters.Length == 0)
            return "";
        return "<" + string.Join(", ", parameters.Select((p, i) =>
            arguments != null && offset + i < arguments.Count && arguments[offset + i] is { } known ? known : p)) + ">";
    }

    /// <summary>True for methods of closures, state machines and other types the compiler invented.</summary>
    public bool IsDeclaredInCompilerGeneratedType(int methodToken)
    {
        MethodDefinition method = _md.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(methodToken));
        return _md.GetString(_md.GetTypeDefinition(method.GetDeclaringType()).Name).StartsWith('<');
    }

    /// <summary>
    /// First user-visible statement of the program: the entry point method, or for "async Main"
    /// (whose entry point is a synthesized stub without line info) the body of its state machine.
    /// </summary>
    public (int MethodToken, int ILOffset)? GetEntryPoint()
    {
        int entryToken = _pe.PEHeaders.CorHeader?.EntryPointTokenOrRelativeVirtualAddress ?? 0;
        if (_pdb == null || entryToken >> 24 != 0x06)
            return null;

        if (FirstVisibleOffset(entryToken) is { } offset)
            return (entryToken, offset);

        MethodDefinition entry = _md.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(entryToken));
        string entryName = _md.GetString(entry.Name);
        if (!entryName.StartsWith('<'))
            return (entryToken, 0);

        // <Main> stub -> Main -> <Main>d__N.MoveNext
        string userName = entryName.Trim('<', '>');
        TypeDefinition declaringType = _md.GetTypeDefinition(entry.GetDeclaringType());
        foreach (TypeDefinitionHandle nestedHandle in declaringType.GetNestedTypes())
        {
            TypeDefinition nested = _md.GetTypeDefinition(nestedHandle);
            if (!_md.GetString(nested.Name).StartsWith("<" + userName + ">d__", StringComparison.Ordinal))
                continue;
            foreach (MethodDefinitionHandle mh in nested.GetMethods())
            {
                if (_md.GetString(_md.GetMethodDefinition(mh).Name) != "MoveNext")
                    continue;
                int token = MetadataTokens.GetToken(mh);
                if (FirstVisibleOffset(token) is { } moveNextOffset)
                    return (token, moveNextOffset);
            }
        }
        return (entryToken, 0);

        int? FirstVisibleOffset(int methodToken)
        {
            var points = GetSequencePoints(methodToken);
            if (points == null)
                return null;
            foreach (SequencePoint sp in points)
                if (!sp.IsHidden)
                    return sp.Offset;
            return null;
        }
    }

    public (bool IsStatic, string[] ParameterNames) GetMethodParameters(int methodToken)
    {
        MethodDefinition method = _md.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(methodToken));
        var names = new SortedDictionary<int, string>();
        foreach (ParameterHandle ph in method.GetParameters())
        {
            Parameter p = _md.GetParameter(ph);
            if (p.SequenceNumber > 0)
                names[p.SequenceNumber - 1] = _md.GetString(p.Name);
        }
        int count = names.Count == 0 ? 0 : names.Keys.Max() + 1;
        var result = new string[count];
        for (int i = 0; i < count; i++)
            result[i] = names.TryGetValue(i, out string? n) ? n : $"arg{i}";
        return ((method.Attributes & MethodAttributes.Static) != 0, result);
    }

    public List<FieldDescription> GetFields(int typeDefToken)
    {
        TypeDefinition type = _md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(typeDefToken));
        var result = new List<FieldDescription>();
        foreach (FieldDefinitionHandle fh in type.GetFields())
        {
            FieldDefinition field = _md.GetFieldDefinition(fh);
            result.Add(new FieldDescription(
                MetadataTokens.GetToken(fh),
                _md.GetString(field.Name),
                (field.Attributes & FieldAttributes.Static) != 0,
                (field.Attributes & FieldAttributes.Literal) != 0));
        }
        return result;
    }

    /// <summary>Name of the enum member equal to <paramref name="value"/>, or a flags combination, or null.</summary>
    public string? GetEnumName(int typeDefToken, ulong value)
    {
        TypeDefinition type = _md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(typeDefToken));
        var members = new List<(string Name, ulong Value)>();
        foreach (FieldDefinitionHandle fh in type.GetFields())
        {
            FieldDefinition field = _md.GetFieldDefinition(fh);
            if ((field.Attributes & FieldAttributes.Literal) == 0 || field.GetDefaultValue().IsNil)
                continue;
            Constant constant = _md.GetConstant(field.GetDefaultValue());
            BlobReader reader = _md.GetBlobReader(constant.Value);
            ulong memberValue = constant.TypeCode switch
            {
                ConstantTypeCode.SByte => (ulong)reader.ReadSByte(),
                ConstantTypeCode.Byte => reader.ReadByte(),
                ConstantTypeCode.Int16 => (ulong)reader.ReadInt16(),
                ConstantTypeCode.UInt16 => reader.ReadUInt16(),
                ConstantTypeCode.Int32 => (ulong)reader.ReadInt32(),
                ConstantTypeCode.UInt32 => reader.ReadUInt32(),
                ConstantTypeCode.Int64 => (ulong)reader.ReadInt64(),
                ConstantTypeCode.UInt64 => reader.ReadUInt64(),
                _ => 0,
            };
            members.Add((_md.GetString(field.Name), memberValue));
        }

        foreach (var m in members)
            if (m.Value == value)
                return m.Name;

        // [Flags]-style decomposition
        ulong remaining = value;
        var parts = new List<string>();
        foreach (var m in members.Where(m => m.Value != 0).OrderByDescending(m => m.Value))
        {
            if ((remaining & m.Value) == m.Value)
            {
                parts.Insert(0, m.Name);
                remaining &= ~m.Value;
            }
        }
        return remaining == 0 && parts.Count > 0 ? string.Join(" | ", parts) : null;
    }

    public void Dispose()
    {
        _pdbProvider?.Dispose();
        _pe.Dispose();
    }
}
