using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace DotnetDebugger.Engine.Symbols;

/// <param name="ParameterTypes">CLR-style type names ("System.Int32", "!0" for type and "!!0" for method type variables).</param>
internal sealed record MethodDescription(int Token, string Name, bool IsStatic, string[] ParameterTypes, int GenericParameterCount);

internal sealed record PropertyDescription(string Name, int GetterToken, int SetterToken, bool IsStatic, bool IsIndexer);

/// <summary>Await points of an async method: where it suspends and where it continues afterwards.</summary>
internal sealed record AsyncSteppingInfo(List<(int YieldOffset, int ResumeOffset)> Awaits);

// Member lookup used by the expression evaluator and the variables view.
internal sealed partial class ModuleMetadata
{
    private static readonly Guid s_asyncSteppingInfoKind = new("54FD2AC5-E925-401A-9C2A-F94F171072F8");

    private Dictionary<string, int>? _typesByName;

    /// <summary>Token of the type with the given full name (nested types use '.', generic arity stays: "List`1").</summary>
    public int? FindType(string fullName)
    {
        lock (_sync)
        {
            if (_typesByName == null)
            {
                _typesByName = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (TypeDefinitionHandle handle in _md.TypeDefinitions)
                    _typesByName.TryAdd(GetTypeName(handle), MetadataTokens.GetToken(handle));
            }
            return _typesByName.TryGetValue(fullName, out int token) ? token : null;
        }
    }

    public string GetTypeNamespace(int typeDefToken)
    {
        var handle = (TypeDefinitionHandle)MetadataTokens.Handle(typeDefToken);
        while (true)
        {
            TypeDefinition type = _md.GetTypeDefinition(handle);
            if (type.GetDeclaringType().IsNil)
                return _md.GetString(type.Namespace);
            handle = type.GetDeclaringType();
        }
    }

    public int GetDeclaringType(int methodToken) =>
        MetadataTokens.GetToken(_md.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(methodToken)).GetDeclaringType());

    /// <summary>For closures and state machines: the type the user wrote that contains them.</summary>
    public int GetEnclosingUserType(int typeDefToken)
    {
        var handle = (TypeDefinitionHandle)MetadataTokens.Handle(typeDefToken);
        while (true)
        {
            TypeDefinition type = _md.GetTypeDefinition(handle);
            if (!_md.GetString(type.Name).StartsWith('<') || type.GetDeclaringType().IsNil)
                return MetadataTokens.GetToken(handle);
            handle = type.GetDeclaringType();
        }
    }

    public bool IsEnum(int typeDefToken)
    {
        TypeDefinition type = _md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(typeDefToken));
        if (type.BaseType.Kind != HandleKind.TypeReference)
            return false;
        TypeReference baseType = _md.GetTypeReference((TypeReferenceHandle)type.BaseType);
        return _md.GetString(baseType.Name) == "Enum" && _md.GetString(baseType.Namespace) == "System";
    }

    public List<MethodDescription> GetMethods(int typeDefToken, string name)
    {
        TypeDefinition type = _md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(typeDefToken));
        var result = new List<MethodDescription>();
        foreach (MethodDefinitionHandle handle in type.GetMethods())
        {
            MethodDefinition method = _md.GetMethodDefinition(handle);
            if (_md.GetString(method.Name) != name)
                continue;
            MethodSignature<string> signature = method.DecodeSignature(SignatureNames.Instance, this);
            result.Add(new MethodDescription(
                MetadataTokens.GetToken(handle), name, (method.Attributes & MethodAttributes.Static) != 0,
                signature.ParameterTypes.ToArray(), signature.GenericParameterCount));
        }
        return result;
    }

    public List<PropertyDescription> GetProperties(int typeDefToken)
    {
        TypeDefinition type = _md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(typeDefToken));
        var result = new List<PropertyDescription>();
        foreach (PropertyDefinitionHandle handle in type.GetProperties())
        {
            PropertyDefinition property = _md.GetPropertyDefinition(handle);
            PropertyAccessors accessors = property.GetAccessors();
            if (accessors.Getter.IsNil)
                continue;
            MethodDefinition getter = _md.GetMethodDefinition(accessors.Getter);
            result.Add(new PropertyDescription(
                _md.GetString(property.Name),
                MetadataTokens.GetToken(accessors.Getter),
                accessors.Setter.IsNil ? 0 : MetadataTokens.GetToken(accessors.Setter),
                (getter.Attributes & MethodAttributes.Static) != 0,
                getter.DecodeSignature(SignatureNames.Instance, this).ParameterTypes.Length > 0));
        }
        return result;
    }

    /// <summary>Value of a const field; <paramref name="bits"/> carries integral values for enum formatting.</summary>
    public object? GetConstant(int fieldToken, out ulong? bits)
    {
        bits = null;
        FieldDefinition field = _md.GetFieldDefinition((FieldDefinitionHandle)MetadataTokens.Handle(fieldToken));
        if (field.GetDefaultValue().IsNil)
            return null;
        Constant constant = _md.GetConstant(field.GetDefaultValue());
        BlobReader reader = _md.GetBlobReader(constant.Value);
        object? value = constant.TypeCode switch
        {
            ConstantTypeCode.Boolean => reader.ReadBoolean(),
            ConstantTypeCode.Char => reader.ReadChar(),
            ConstantTypeCode.SByte => reader.ReadSByte(),
            ConstantTypeCode.Byte => reader.ReadByte(),
            ConstantTypeCode.Int16 => reader.ReadInt16(),
            ConstantTypeCode.UInt16 => reader.ReadUInt16(),
            ConstantTypeCode.Int32 => reader.ReadInt32(),
            ConstantTypeCode.UInt32 => reader.ReadUInt32(),
            ConstantTypeCode.Int64 => reader.ReadInt64(),
            ConstantTypeCode.UInt64 => reader.ReadUInt64(),
            ConstantTypeCode.Single => reader.ReadSingle(),
            ConstantTypeCode.Double => reader.ReadDouble(),
            ConstantTypeCode.String => reader.ReadUTF16(reader.Length),
            _ => null,
        };
        if (value is not (null or bool or char or float or double or string))
            bits = unchecked((ulong)Convert.ToInt64(value is ulong u ? (long)u : value));
        return value;
    }

    public string[] GetTypeGenericParameters(int typeDefToken) =>
        _md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(typeDefToken)).GetGenericParameters()
            .Select(h => _md.GetString(_md.GetGenericParameter(h).Name)).ToArray();

    public string[] GetMethodGenericParameters(int methodToken) =>
        _md.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(methodToken)).GetGenericParameters()
            .Select(h => _md.GetString(_md.GetGenericParameter(h).Name)).ToArray();

    /// <summary>User methods named <paramref name="name"/>: "Method", "Type.Method" or "Namespace.Type.Method".</summary>
    public List<(int MethodToken, int ILOffset)> FindMethodsByName(string name)
    {
        var result = new List<(int, int)>();
        if (_pdb == null)
            return result;
        string simpleName = name[(name.LastIndexOf('.') + 1)..];
        foreach (MethodDefinitionHandle handle in _md.MethodDefinitions)
        {
            MethodDefinition method = _md.GetMethodDefinition(handle);
            if (_md.GetString(method.Name) != simpleName)
                continue;
            int token = MetadataTokens.GetToken(handle);
            string fullName = GetTypeName(method.GetDeclaringType()) + "." + simpleName;
            if (fullName != name && !fullName.EndsWith("." + name, StringComparison.Ordinal))
                continue;
            var points = GetSequencePoints(token);
            if (points == null)
                continue;
            result.Add((token, points.FirstOrDefault(p => !p.IsHidden).Offset));
        }
        return result;
    }

    public AsyncSteppingInfo? GetAsyncSteppingInfo(int methodToken)
    {
        if (_pdb == null)
            return null;
        foreach (CustomDebugInformationHandle handle in _pdb.GetCustomDebugInformation(MetadataTokens.EntityHandle(methodToken)))
        {
            CustomDebugInformation info = _pdb.GetCustomDebugInformation(handle);
            if (_pdb.GetGuid(info.Kind) != s_asyncSteppingInfoKind)
                continue;

            BlobReader reader = _pdb.GetBlobReader(info.Value);
            reader.ReadUInt32(); // catch handler offset + 1
            var awaits = new List<(int, int)>();
            while (reader.RemainingBytes > 0)
            {
                int yieldOffset = (int)reader.ReadUInt32();
                int resumeOffset = (int)reader.ReadUInt32();
                reader.ReadCompressedInteger(); // resume method, always MoveNext itself for C#
                awaits.Add((yieldOffset, resumeOffset));
            }
            return awaits.Count == 0 ? null : new AsyncSteppingInfo(awaits);
        }
        return null;
    }

    private sealed class SignatureNames : ISignatureTypeProvider<string, ModuleMetadata>
    {
        public static readonly SignatureNames Instance = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode;
        public string GetTypeFromSpecification(MetadataReader reader, ModuleMetadata context, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, context);
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
        public string GetByReferenceType(string elementType) => "ref " + elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPinnedType(string elementType) => elementType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericMethodParameter(ModuleMetadata context, int index) => "!!" + index;
        public string GetGenericTypeParameter(ModuleMetadata context, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            TypeName(reader, handle);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            TypeName(reader, handle);

        private static string TypeName(MetadataReader reader, EntityHandle handle)
        {
            if (handle.Kind == HandleKind.TypeDefinition)
            {
                TypeDefinition type = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
                string name = reader.GetString(type.Name);
                if (!type.GetDeclaringType().IsNil)
                    return TypeName(reader, type.GetDeclaringType()) + "." + name;
                string ns = reader.GetString(type.Namespace);
                return ns.Length == 0 ? name : ns + "." + name;
            }
            TypeReference reference = reader.GetTypeReference((TypeReferenceHandle)handle);
            string refName = reader.GetString(reference.Name);
            if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
                return TypeName(reader, (TypeReferenceHandle)reference.ResolutionScope) + "." + refName;
            string refNs = reader.GetString(reference.Namespace);
            return refNs.Length == 0 ? refName : refNs + "." + refName;
        }
    }
}
