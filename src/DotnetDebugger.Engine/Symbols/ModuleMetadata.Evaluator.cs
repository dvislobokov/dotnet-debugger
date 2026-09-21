using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;

namespace DotnetDebugger.Engine.Symbols;

// What the expression evaluator needs to know beyond names and signatures.
internal sealed partial class ModuleMetadata
{
    private static readonly Guid s_tupleElementNamesKind = new("ED9FDF71-8879-4747-8ED3-FE5EDE3CE710");

    public string GetSimpleMethodName(int methodToken) =>
        _md.GetString(_md.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(methodToken)).Name);

    /// <summary>
    /// Default values of optional parameters, by zero-based parameter index. Only constants are there to be found:
    /// "= default" of a struct and "= new()" have no entry and cannot be filled in.
    /// </summary>
    public Dictionary<int, object?> GetParameterDefaults(int methodToken)
    {
        var result = new Dictionary<int, object?>();
        MethodDefinition method = _md.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(methodToken));
        foreach (ParameterHandle handle in method.GetParameters())
        {
            Parameter parameter = _md.GetParameter(handle);
            if (parameter.SequenceNumber == 0 || (parameter.Attributes & ParameterAttributes.HasDefault) == 0 || parameter.GetDefaultValue().IsNil)
                continue;
            Constant constant = _md.GetConstant(parameter.GetDefaultValue());
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
                _ => null, // NullReference
            };
            result[parameter.SequenceNumber - 1] = value;
        }
        return result;
    }

    /// <summary>
    /// Names the user gave to the elements of tuple-typed locals ("(int Id, string Name) t"), by local name. The
    /// names only exist in the symbols: the type itself is ValueTuple with Item1, Item2, ... Unnamed elements are null.
    /// </summary>
    public Dictionary<string, string?[]> GetLocalTupleElementNames(int methodToken, int ilOffset)
    {
        var result = new Dictionary<string, string?[]>();
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
                foreach (CustomDebugInformationHandle infoHandle in _pdb.GetCustomDebugInformation(localHandle))
                {
                    CustomDebugInformation info = _pdb.GetCustomDebugInformation(infoHandle);
                    if (_pdb.GetGuid(info.Kind) != s_tupleElementNamesKind)
                        continue;
                    // zero-terminated UTF-8 names, an empty one for an element without a name
                    string[] names = Encoding.UTF8.GetString(_pdb.GetBlobBytes(info.Value)).Split('\0');
                    result[_pdb.GetString(_pdb.GetLocalVariable(localHandle).Name)] =
                        names.Take(names.Length - 1).Select(n => n.Length == 0 ? null : n).ToArray();
                }
            }
        }
        return result;
    }
}
