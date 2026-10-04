using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.DiaSymReader;

namespace DotnetDebugger.Engine.Symbols;

/// <summary>
/// Converts a Windows PDB to a portable one in memory, keeping what the engine reads from symbols: documents (with
/// checksums and embedded sources), sequence points, local scopes with the names of the locals, async stepping
/// information and the element names of tuple locals. Imports, constants, dynamic locals and Edit and Continue data are
/// left out. The Windows PDB is read by Microsoft.DiaSymReader.Native, the reader of that format. Modelled on the
/// Windows-to-portable half of Microsoft.DiaSymReader.Converter (github.com/dotnet/symreader-converter, MIT), which is
/// not published on nuget.org.
/// </summary>
internal static class WindowsPdbConverter
{
    private static readonly Guid s_asyncMethodSteppingInformation = new("54FD2AC5-E925-401A-9C2A-F94F171072F8");
    private static readonly Guid s_tupleElementNames = new("ED9FDF71-8879-4747-8ED3-FE5EDE3CE710");
    private static readonly Guid s_embeddedSource = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
    private static readonly Guid s_visualBasic = new("3A12D0B8-C26C-11D0-B442-00A0244A1DD2");

    // custom debug information of a method in a Windows PDB ("MD2" attribute), as written by Roslyn
    private const byte CdiVersion = 4;
    private const int CdiGlobalHeaderSize = 4;
    private const int CdiRecordHeaderSize = 8;
    private const byte CdiTupleElementNames = 8;

    private const int MaxLine = 0x1FFFFFFF;
    private const int MaxColumn = ushort.MaxValue;

    /// <exception cref="InvalidDataException">The PDB belongs to another build of the module.</exception>
    /// <exception cref="COMException">The PDB is damaged or not a PDB at all.</exception>
    public static MetadataReaderProvider Convert(PEReader pe, Stream pdb, DebugDirectoryEntry codeViewEntry)
    {
        CodeViewDebugDirectoryData codeView = pe.ReadCodeViewDebugDirectoryData(codeViewEntry);
        MetadataReader metadata = pe.GetMetadataReader();
        ISymUnmanagedReader5 symReader = SymUnmanagedReaderFactory.CreateReader<ISymUnmanagedReader5>(pdb, new MetadataProvider(metadata));
        try
        {
            Marshal.ThrowExceptionForHR(symReader.MatchesModule(codeView.Guid, codeViewEntry.Stamp, codeView.Age, out bool matches));
            if (!matches)
                throw new InvalidDataException("The PDB does not match the module.");

            MetadataBuilder builder = Convert(symReader, pe, metadata);
            var rowCounts = new int[MetadataTokens.TableCount];
            for (int table = 0; table < rowCounts.Length; table++)
                rowCounts[table] = metadata.GetTableRowCount((TableIndex)table);
            int entryPoint = UserEntryPoint(symReader);
            var pdbBuilder = new PortablePdbBuilder(builder, ImmutableArray.Create(rowCounts),
                entryPoint == 0 ? default : MetadataTokens.MethodDefinitionHandle(entryPoint & 0xFFFFFF),
                // the id of the Windows PDB, which the module names in its CodeView entry
                _ => new BlobContentId(codeView.Guid, codeViewEntry.Stamp));
            var image = new BlobBuilder();
            pdbBuilder.Serialize(image);
            return MetadataReaderProvider.FromPortablePdbImage(image.ToImmutableArray());
        }
        finally
        {
            ((ISymUnmanagedDispose)symReader).Destroy();
        }
    }

    private static MetadataBuilder Convert(ISymUnmanagedReader5 symReader, PEReader pe, MetadataReader metadata)
    {
        var builder = new MetadataBuilder();
        var documents = new Dictionary<string, DocumentHandle>(StringComparer.Ordinal);
        bool visualBasic = false;
        foreach (ISymUnmanagedDocument document in symReader.GetDocuments())
        {
            string name = document.GetName();
            if (!documents.ContainsKey(name))
                documents.Add(name, AddDocument(builder, document, name));
            visualBasic |= document.GetLanguage() == s_visualBasic;
        }

        // one (empty) import scope for all local scopes: the engine does not read imports
        ImportScopeHandle importScope = builder.AddImportScope(default, default);
        var stateMachineMethods = new SortedDictionary<int, MethodDefinitionHandle>();

        foreach (MethodDefinitionHandle methodHandle in metadata.MethodDefinitions)
        {
            int methodToken = MetadataTokens.GetToken(methodHandle);
            ISymUnmanagedMethod? symMethod = TryGetMethod(symReader, methodToken);
            if (symMethod == null)
            {
                builder.AddMethodDebugInformation(default, default);
                continue;
            }

            MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
            MethodBodyBlock? body = method.RelativeVirtualAddress == 0 ? null : pe.GetMethodBody(method.RelativeVirtualAddress);
            int localSignature = body == null || body.LocalSignature.IsNil ? 0 : MetadataTokens.GetRowNumber(body.LocalSignature);

            ImmutableArray<SymUnmanagedSequencePoint> sequencePoints = symMethod.GetSequencePoints().ToImmutableArray();
            BlobHandle sequencePointsBlob = SerializeSequencePoints(builder, documents, localSignature, sequencePoints, out DocumentHandle singleDocument);
            builder.AddMethodDebugInformation(singleDocument, sequencePointsBlob);

            if (symMethod.AsAsyncMethod() is { } asyncMethod)
            {
                stateMachineMethods[MetadataTokens.GetRowNumber(methodHandle)] = MetadataTokens.MethodDefinitionHandle(asyncMethod.GetKickoffMethod() & 0xFFFFFF);
                builder.AddCustomDebugInformation(methodHandle, builder.GetOrAddGuid(s_asyncMethodSteppingInformation),
                    SerializeAsyncSteppingInformation(builder, asyncMethod, MetadataTokens.GetRowNumber(methodHandle)));
            }

            var scopes = new List<(int Start, int End, ISymUnmanagedVariable[] Locals)>();
            ISymUnmanagedScope rootScope = symMethod.GetRootScope();
            // C# and VB write an empty root scope with the real ones as its children; Managed C++ does not
            ISymUnmanagedScope[] topScopes = rootScope.GetLocals().Length != 0 ? [rootScope] : rootScope.GetChildren();
            foreach (ISymUnmanagedScope scope in topScopes)
                AddScopes(scopes, scope, visualBasic, isTopScope: true);

            if (scopes.Count == 0)
            {
                if (body != null)
                    AddLocalScope(builder, methodHandle, importScope, 0, body.GetILReader().Length);
                continue;
            }
            Dictionary<int, ImmutableArray<string?>> tupleNames = ReadTupleElementNames(symReader, methodToken);
            foreach ((int start, int end, ISymUnmanagedVariable[] locals) in scopes)
            {
                AddLocalScope(builder, methodHandle, importScope, start, end - start);
                foreach (ISymUnmanagedVariable local in locals)
                {
                    int slot = local.GetSlot();
                    LocalVariableHandle variable = builder.AddLocalVariable((LocalVariableAttributes)local.GetAttributes(), slot,
                        builder.GetOrAddString(local.GetName()));
                    if (tupleNames.TryGetValue(slot, out ImmutableArray<string?> names))
                        builder.AddCustomDebugInformation(variable, builder.GetOrAddGuid(s_tupleElementNames), SerializeTupleElementNames(builder, names));
                }
            }
        }

        foreach ((int moveNext, MethodDefinitionHandle kickoff) in stateMachineMethods)
            builder.AddStateMachineMethod(MetadataTokens.MethodDefinitionHandle(moveNext), kickoff);
        return builder;
    }

    private static DocumentHandle AddDocument(MetadataBuilder builder, ISymUnmanagedDocument document, string name)
    {
        Guid hashAlgorithm = document.GetHashAlgorithm();
        byte[] checksum = document.GetChecksum();
        DocumentHandle handle = builder.AddDocument(
            builder.GetOrAddDocumentName(name),
            hashAlgorithm == Guid.Empty ? default : builder.GetOrAddGuid(hashAlgorithm),
            checksum.Length == 0 ? default : builder.GetOrAddBlob(checksum),
            builder.GetOrAddGuid(document.GetLanguage()));

        // the Windows PDB keeps an embedded source in the format of the portable one
        ArraySegment<byte> source = document.GetEmbeddedSource();
        if (source.Count > 0)
            builder.AddCustomDebugInformation(handle, builder.GetOrAddGuid(s_embeddedSource), builder.GetOrAddBlob(source.ToArray()));
        return handle;
    }

    private static ISymUnmanagedMethod? TryGetMethod(ISymUnmanagedReader5 symReader, int methodToken)
    {
        try
        {
            return symReader.GetMethod(methodToken);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static int UserEntryPoint(ISymUnmanagedReader5 symReader)
    {
        try
        {
            return symReader.GetUserEntryPoint();
        }
        catch (COMException)
        {
            return 0;
        }
    }

    private static void AddLocalScope(MetadataBuilder builder, MethodDefinitionHandle method, ImportScopeHandle importScope, int start, int length) =>
        builder.AddLocalScope(method, importScope,
            MetadataTokens.LocalVariableHandle(builder.GetRowCount(TableIndex.LocalVariable) + 1),
            MetadataTokens.LocalConstantHandle(builder.GetRowCount(TableIndex.LocalConstant) + 1),
            start, length);

    /// <summary>
    /// Flattens the scope tree in the order the LocalScope table wants (by start offset, an enclosing scope first).
    /// Scopes with neither locals nor child scopes are left out, the top ones excepted.
    /// </summary>
    private static void AddScopes(List<(int Start, int End, ISymUnmanagedVariable[] Locals)> scopes, ISymUnmanagedScope scope,
        bool visualBasic, bool isTopScope)
    {
        int start = scope.GetStartOffset();
        // VB writes the end of a nested scope inclusive; portable PDBs have it exclusive
        int end = scope.GetEndOffset() + (visualBasic && !isTopScope ? 1 : 0);
        ISymUnmanagedVariable[] locals = scope.GetLocals();
        scopes.Add((start, end, locals));
        int countBeforeChildren = scopes.Count;

        int previousChildEnd = start;
        foreach (ISymUnmanagedScope child in scope.GetChildren())
        {
            // properly nested scopes only: whatever follows a broken one is dropped
            if (child.GetStartOffset() < previousChildEnd || child.GetEndOffset() > end)
                break;
            previousChildEnd = child.GetEndOffset();
            AddScopes(scopes, child, visualBasic, isTopScope: false);
        }

        if (!isTopScope && locals.Length == 0 && scopes.Count == countBeforeChildren)
            scopes.RemoveAt(scopes.Count - 1);
    }

    /// <summary>The sequence points blob of the MethodDebugInformation table (ECMA-335 portable PDB format).</summary>
    private static BlobHandle SerializeSequencePoints(MetadataBuilder builder, Dictionary<string, DocumentHandle> documents,
        int localSignature, ImmutableArray<SymUnmanagedSequencePoint> sequencePoints, out DocumentHandle singleDocument)
    {
        singleDocument = default;
        if (sequencePoints.IsEmpty)
            return default;

        var handles = new DocumentHandle[sequencePoints.Length];
        for (int i = 0; i < handles.Length; i++)
            handles[i] = GetDocument(builder, documents, sequencePoints[i].Document);
        if (handles.All(h => h == handles[0]))
            singleDocument = handles[0];

        var writer = new BlobBuilder();
        writer.WriteCompressedInteger(localSignature);
        DocumentHandle previousDocument = singleDocument;
        int previousOffset = -1;
        int previousStartLine = -1;
        int previousStartColumn = -1;
        for (int i = 0; i < sequencePoints.Length; i++)
        {
            SymUnmanagedSequencePoint point = Sanitize(sequencePoints[i], previousOffset);
            if (handles[i] != previousDocument)
            {
                // the initial document in the header, a document record later on
                if (!previousDocument.IsNil)
                    writer.WriteCompressedInteger(0);
                writer.WriteCompressedInteger(MetadataTokens.GetRowNumber(handles[i]));
                previousDocument = handles[i];
            }

            writer.WriteCompressedInteger(previousOffset < 0 ? point.Offset : point.Offset - previousOffset);
            previousOffset = point.Offset;
            if (point.IsHidden)
            {
                // zero lines and zero columns
                writer.WriteInt16(0);
                continue;
            }

            int deltaLines = point.EndLine - point.StartLine;
            writer.WriteCompressedInteger(deltaLines);
            if (deltaLines == 0)
                writer.WriteCompressedInteger(point.EndColumn - point.StartColumn);
            else
                writer.WriteCompressedSignedInteger(point.EndColumn - point.StartColumn);

            if (previousStartLine < 0)
            {
                writer.WriteCompressedInteger(point.StartLine);
                writer.WriteCompressedInteger(point.StartColumn);
            }
            else
            {
                writer.WriteCompressedSignedInteger(point.StartLine - previousStartLine);
                writer.WriteCompressedSignedInteger(point.StartColumn - previousStartColumn);
            }
            previousStartLine = point.StartLine;
            previousStartColumn = point.StartColumn;
        }
        return builder.GetOrAddBlob(writer);
    }

    private static DocumentHandle GetDocument(MetadataBuilder builder, Dictionary<string, DocumentHandle> documents, ISymUnmanagedDocument document)
    {
        string name = document.GetName();
        if (!documents.TryGetValue(name, out DocumentHandle handle))
        {
            handle = AddDocument(builder, document, name);
            documents.Add(name, handle);
        }
        return handle;
    }

    /// <summary>
    /// Brings a sequence point within what the portable format can express: increasing offsets, lines and columns in
    /// range, a non-empty span (Managed C++ writes (line, 0, line, 0) for a whole line).
    /// </summary>
    private static SymUnmanagedSequencePoint Sanitize(SymUnmanagedSequencePoint point, int previousOffset)
    {
        int offset = Math.Max(point.Offset, previousOffset + 1);
        if (point.IsHidden)
            return new SymUnmanagedSequencePoint(offset, point.Document, point.StartLine, point.StartColumn, point.EndLine, point.EndColumn);

        int startLine = Math.Clamp(point.StartLine, 0, MaxLine);
        int endLine = Math.Clamp(point.EndLine, startLine, MaxLine);
        int startColumn = Math.Clamp(point.StartColumn, 0, MaxColumn);
        int endColumn = Math.Clamp(point.EndColumn, 0, MaxColumn);
        if (startLine == endLine && startColumn >= endColumn)
            endColumn = startColumn == 0 && endColumn == 0 ? MaxColumn : Math.Min(startColumn + 1, MaxColumn);
        return new SymUnmanagedSequencePoint(offset, point.Document, startLine, startColumn, endLine, endColumn);
    }

    private static BlobHandle SerializeAsyncSteppingInformation(MetadataBuilder builder, ISymUnmanagedAsyncMethod asyncMethod, int moveNextRow)
    {
        var writer = new BlobBuilder();
        // the catch handler offset + 1; 0 when there is none (-1 + 1)
        writer.WriteUInt32((uint)((long)asyncMethod.GetCatchHandlerILOffset() + 1));
        foreach (SymUnmanagedAsyncStepInfo step in asyncMethod.GetAsyncStepInfos())
        {
            writer.WriteInt32(step.YieldOffset);
            writer.WriteInt32(step.ResumeOffset);
            writer.WriteCompressedInteger(moveNextRow);
        }
        return builder.GetOrAddBlob(writer);
    }

    private static BlobHandle SerializeTupleElementNames(MetadataBuilder builder, ImmutableArray<string?> names)
    {
        var writer = new BlobBuilder();
        foreach (string? name in names)
        {
            if (name != null)
                writer.WriteUTF8(name);
            writer.WriteByte(0);
        }
        return builder.GetOrAddBlob(writer);
    }

    /// <summary>
    /// The tuple element names of the method's locals by slot, from its custom debug information. A damaged record
    /// costs the names, not the symbols.
    /// </summary>
    private static Dictionary<int, ImmutableArray<string?>> ReadTupleElementNames(ISymUnmanagedReader5 symReader, int methodToken)
    {
        var result = new Dictionary<int, ImmutableArray<string?>>();
        try
        {
            byte[]? cdi = symReader.GetCustomDebugInfo(methodToken, methodVersion: 1);
            if (cdi == null || cdi.Length < CdiGlobalHeaderSize || cdi[0] != CdiVersion)
                return result;

            int offset = CdiGlobalHeaderSize;
            while (offset <= cdi.Length - CdiRecordHeaderSize)
            {
                byte kind = cdi[offset + 1];
                int alignment = cdi[offset + 3];
                int size = BitConverter.ToInt32(cdi, offset + 4);
                if (size < CdiRecordHeaderSize || size > cdi.Length - offset)
                    break;
                if (kind == CdiTupleElementNames)
                    ReadTupleRecord(cdi, offset + CdiRecordHeaderSize, offset + size - alignment, result);
                offset += size;
            }
        }
        catch (Exception e) when (e is COMException or ArgumentException or IndexOutOfRangeException)
        {
        }
        return result;
    }

    private static void ReadTupleRecord(byte[] data, int offset, int end, Dictionary<int, ImmutableArray<string?>> result)
    {
        int count = ReadInt32(data, ref offset, end);
        for (int i = 0; i < count; i++)
        {
            int nameCount = ReadInt32(data, ref offset, end);
            var names = ImmutableArray.CreateBuilder<string?>(nameCount);
            for (int j = 0; j < nameCount; j++)
                names.Add(ReadUtf8(data, ref offset, end) is { Length: > 0 } name ? name : null);
            int slot = ReadInt32(data, ref offset, end);
            offset += 8; // scope start and end: only constants need them
            ReadUtf8(data, ref offset, end); // the local's name
            // a negative slot is a constant
            if (slot >= 0)
                result.TryAdd(slot, names.MoveToImmutable());
        }
    }

    private static int ReadInt32(byte[] data, ref int offset, int end)
    {
        if (offset + 4 > end)
            throw new ArgumentException("Truncated custom debug information.");
        int value = BitConverter.ToInt32(data, offset);
        offset += 4;
        return value;
    }

    private static string ReadUtf8(byte[] data, ref int offset, int end)
    {
        int terminator = Array.IndexOf(data, (byte)0, offset, end - offset);
        if (terminator < 0)
            throw new ArgumentException("Truncated custom debug information.");
        string value = Encoding.UTF8.GetString(data, offset, terminator - offset);
        offset = terminator + 1;
        return value;
    }

    /// <summary>What the native reader asks of the module's metadata (it decodes signatures of locals and constants).</summary>
    private sealed class MetadataProvider(MetadataReader metadata) : ISymReaderMetadataProvider
    {
        public unsafe bool TryGetStandaloneSignature(int standaloneSignatureToken, out byte* signature, out int length)
        {
            var handle = (StandaloneSignatureHandle)MetadataTokens.Handle(standaloneSignatureToken);
            if (handle.IsNil)
            {
                signature = null;
                length = 0;
                return false;
            }
            BlobReader reader = metadata.GetBlobReader(metadata.GetStandaloneSignature(handle).Signature);
            signature = reader.StartPointer;
            length = reader.Length;
            return true;
        }

        public bool TryGetTypeDefinitionInfo(int typeDefinitionToken, out string namespaceName, out string typeName, out TypeAttributes attributes)
        {
            var handle = (TypeDefinitionHandle)MetadataTokens.Handle(typeDefinitionToken);
            if (handle.IsNil)
            {
                namespaceName = typeName = null!;
                attributes = 0;
                return false;
            }
            TypeDefinition type = metadata.GetTypeDefinition(handle);
            namespaceName = metadata.GetString(type.Namespace);
            typeName = metadata.GetString(type.Name);
            attributes = type.Attributes;
            return true;
        }

        public bool TryGetTypeReferenceInfo(int typeReferenceToken, out string namespaceName, out string typeName)
        {
            var handle = (TypeReferenceHandle)MetadataTokens.Handle(typeReferenceToken);
            if (handle.IsNil)
            {
                namespaceName = typeName = null!;
                return false;
            }
            TypeReference type = metadata.GetTypeReference(handle);
            namespaceName = metadata.GetString(type.Namespace);
            typeName = metadata.GetString(type.Name);
            return true;
        }
    }
}
