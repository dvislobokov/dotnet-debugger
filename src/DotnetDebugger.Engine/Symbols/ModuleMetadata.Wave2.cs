using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography;
using System.Text;

namespace DotnetDebugger.Engine.Symbols;

// Interfaces, type proxies, reflection names, embedded sources, document checksums, call targets.
internal sealed partial class ModuleMetadata
{
    private const string DebuggerTypeProxyAttribute = "System.Diagnostics.DebuggerTypeProxyAttribute";

    private static readonly Guid s_embeddedSourceKind = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
    private static readonly Guid s_sha1 = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");
    private static readonly Guid s_sha256 = new("8829d00f-11b8-4213-878b-770e8597ac16");

    // ---------------------------------------------------------------- types

    /// <summary>Full names (generic arity included, type arguments not) of the interfaces a type lists itself.</summary>
    public List<string> GetInterfaceNames(int typeDefToken)
    {
        var result = new List<string>();
        TypeDefinition type = _md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(typeDefToken));
        foreach (InterfaceImplementationHandle handle in type.GetInterfaceImplementations())
        {
            EntityHandle iface = _md.GetInterfaceImplementation(handle).Interface;
            if (GetTypeHandleName(iface) is { } name)
                result.Add(name);
        }
        return result;
    }

    private string? GetTypeHandleName(EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                return GetTypeName((TypeDefinitionHandle)handle);
            case HandleKind.TypeReference:
                return SignatureNames.Instance.GetTypeFromReference(_md, (TypeReferenceHandle)handle, 0);
            case HandleKind.TypeSpecification:
            {
                // generic instantiation: keep the definition name only ("IEnumerable`1<!0>" -> "IEnumerable`1")
                string decoded = _md.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(SignatureNames.Instance, this);
                int bracket = decoded.IndexOf('<');
                return bracket < 0 ? decoded : decoded[..bracket];
            }
            default:
                return null;
        }
    }

    public bool IsValueType(int typeDefToken)
    {
        TypeDefinition type = _md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(typeDefToken));
        return !type.BaseType.IsNil && GetTypeHandleName(type.BaseType) is "System.ValueType" or "System.Enum";
    }

    /// <summary>Name as System.Type.GetType() wants it: '+' between nested types.</summary>
    public string GetReflectionName(int typeDefToken)
    {
        var handle = (TypeDefinitionHandle)MetadataTokens.Handle(typeDefToken);
        TypeDefinition type = _md.GetTypeDefinition(handle);
        string name = _md.GetString(type.Name);
        if (!type.GetDeclaringType().IsNil)
            return GetReflectionName(MetadataTokens.GetToken(type.GetDeclaringType())) + "+" + name;
        string ns = _md.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public string AssemblyName => _md.IsAssembly ? _md.GetString(_md.GetAssemblyDefinition().Name) : System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>The proxy type named by [DebuggerTypeProxy], as a '.'-separated full name; null if there is none.</summary>
    public string? GetDebuggerTypeProxy(int typeDefToken)
    {
        TypeDefinition type = _md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(typeDefToken));
        if (FindAttribute(type.GetCustomAttributes(), DebuggerTypeProxyAttribute) is not { } attribute)
            return null;

        // Both constructor overloads (Type and string) serialize as a string: "Namespace.Outer+Proxy`1, Assembly, ..."
        BlobReader reader = _md.GetBlobReader(attribute.Value);
        if (reader.Length <= 2 || reader.ReadUInt16() != 1 || reader.ReadSerializedString() is not { } typeName)
            return null;
        int comma = typeName.IndexOf(',');
        return (comma < 0 ? typeName : typeName[..comma]).Trim().Replace('+', '.');
    }

    /// <summary>Non-public members, by name: a type proxy only shows its public surface.</summary>
    public HashSet<string> GetNonPublicMembers(int typeDefToken)
    {
        var result = new HashSet<string>();
        TypeDefinition type = _md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(typeDefToken));
        foreach (FieldDefinitionHandle handle in type.GetFields())
        {
            FieldDefinition field = _md.GetFieldDefinition(handle);
            if ((field.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Public)
                result.Add(_md.GetString(field.Name));
        }
        foreach (PropertyDefinitionHandle handle in type.GetProperties())
        {
            PropertyDefinition property = _md.GetPropertyDefinition(handle);
            MethodDefinitionHandle getter = property.GetAccessors().Getter;
            if (getter.IsNil || (_md.GetMethodDefinition(getter).Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public)
                result.Add(_md.GetString(property.Name));
        }
        return result;
    }

    /// <summary>Explicit interface implementations: methods named "Namespace.IInterface.Name".</summary>
    public List<MethodDescription> GetExplicitImplementations(int typeDefToken, string name)
    {
        TypeDefinition type = _md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(typeDefToken));
        var result = new List<MethodDescription>();
        foreach (MethodDefinitionHandle handle in type.GetMethods())
        {
            string methodName = _md.GetString(_md.GetMethodDefinition(handle).Name);
            if (methodName.EndsWith("." + name, StringComparison.Ordinal))
                result.AddRange(GetMethods(typeDefToken, methodName));
        }
        return result;
    }

    // ---------------------------------------------------------------- IL call targets

    /// <summary>"Type.Method" of the method called by the call instruction at <paramref name="ilOffset"/>.</summary>
    public string? GetCalledMethodName(int methodToken, int ilOffset)
    {
        MethodDefinition method = _md.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(methodToken));
        byte[]? il = method.RelativeVirtualAddress == 0 ? null : _pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
        if (il == null || ilOffset + 5 > il.Length)
            return null;

        EntityHandle target = MetadataTokens.EntityHandle(BitConverter.ToInt32(il, ilOffset + 1));
        if (target.Kind == HandleKind.MethodSpecification)
            target = _md.GetMethodSpecification((MethodSpecificationHandle)target).Method;
        switch (target.Kind)
        {
            case HandleKind.MethodDefinition:
                return GetMethodName(MetadataTokens.GetToken(target));
            case HandleKind.MemberReference:
            {
                MemberReference member = _md.GetMemberReference((MemberReferenceHandle)target);
                string? owner = GetTypeHandleName(member.Parent);
                return owner == null ? null : StripArity(owner) + "." + _md.GetString(member.Name);
            }
            default:
                return null;
        }
    }

    // ---------------------------------------------------------------- documents

    private DocumentHandle? FindDocument(string path)
    {
        if (_pdb == null)
            return null;
        string wanted = NormalizePath(path);
        foreach (DocumentHandle handle in _pdb.Documents)
        {
            if (NormalizePath(_pdb.GetString(_pdb.GetDocument(handle).Name)).Equals(wanted, PathComparison))
                return handle;
        }
        return null;
    }

    public bool HasEmbeddedSource(string documentPath) => GetEmbeddedSourceBlob(documentPath) != null;

    private BlobHandle? GetEmbeddedSourceBlob(string documentPath)
    {
        if (FindDocument(documentPath) is not { } document)
            return null;
        foreach (CustomDebugInformationHandle handle in _pdb!.GetCustomDebugInformation(document))
        {
            CustomDebugInformation info = _pdb.GetCustomDebugInformation(handle);
            if (_pdb.GetGuid(info.Kind) == s_embeddedSourceKind)
                return info.Value;
        }
        return null;
    }

    /// <summary>Text of a source file embedded into the PDB (EmbedAllSources / EmbeddedFiles), or null.</summary>
    public string? GetEmbeddedSource(string documentPath)
    {
        if (GetEmbeddedSourceBlob(documentPath) is not { } blob)
            return null;

        // int32 uncompressed size (0: stored as is), then the (deflated) bytes
        byte[] bytes = _pdb!.GetBlobBytes(blob);
        int uncompressedSize = BitConverter.ToInt32(bytes, 0);
        var content = new MemoryStream(bytes, 4, bytes.Length - 4);
        if (uncompressedSize == 0)
            return ReadText(content);
        using var deflate = new DeflateStream(content, CompressionMode.Decompress);
        return ReadText(deflate);

        static string ReadText(Stream stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
    }

    /// <summary>
    /// Compares a file on disk with the checksum the compiler recorded for the document.
    /// Returns null when that cannot be told (no checksum, unknown algorithm, unreadable file).
    /// </summary>
    public bool? MatchesChecksum(string documentPath, string localFile)
    {
        if (FindDocument(documentPath) is not { } handle)
            return null;
        Document document = _pdb!.GetDocument(handle);
        if (document.Hash.IsNil)
            return null;

        Guid algorithm = _pdb.GetGuid(document.HashAlgorithm);
        try
        {
            byte[] content = File.ReadAllBytes(localFile);
            byte[]? actual = algorithm == s_sha256 ? SHA256.HashData(content) : algorithm == s_sha1 ? SHA1.HashData(content) : null;
            return actual?.AsSpan().SequenceEqual(_pdb.GetBlobBytes(document.Hash));
        }
        catch (IOException)
        {
            return null;
        }
    }
}
