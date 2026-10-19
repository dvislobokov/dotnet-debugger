using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace DotnetDebugger.Engine.Symbols;

internal enum CallKind
{
    /// <summary>call, callvirt</summary>
    Call,

    /// <summary>newobj</summary>
    NewObject,

    /// <summary>ldftn, ldvirtftn: a delegate is being made, of a lambda most of the time</summary>
    LoadFunction,
}

/// <summary>A call instruction of a method body and what it calls.</summary>
internal sealed record CallInstruction(int Offset, CallKind Kind, EntityHandle Target);

/// <param name="TypeName">Full name of the declaring type, arity stripped ("System.Collections.Generic.List").</param>
/// <param name="TypeDisplayName">The type as the user writes it, without namespace ("List", "Calculator.Inner").</param>
/// <param name="MethodName">The IL name ("get_Count", ".ctor", "Where").</param>
/// <param name="ParameterNames">Known for methods defined in this module.</param>
/// <param name="MethodToken">The MethodDef token when the method is defined in this module.</param>
internal sealed record CallTarget(string TypeName, string TypeDisplayName, string MethodName, string[] ParameterTypes, string[]? ParameterNames, int? MethodToken)
{
    public bool IsPropertyGetter => MethodName.StartsWith("get_", StringComparison.Ordinal);
    public bool IsPropertySetter => MethodName.StartsWith("set_", StringComparison.Ordinal);
    public bool IsConstructor => MethodName == ".ctor";
}

// Call sites of a method as "step into targets" need them: every call with what it calls.
internal sealed partial class ModuleMetadata
{
    /// <summary>The call, newobj and ldftn instructions of a method, in IL order.</summary>
    public List<CallInstruction> GetCallInstructions(int methodToken)
    {
        var result = new List<CallInstruction>();
        MethodDefinition method = _md.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(methodToken));
        if (method.RelativeVirtualAddress == 0)
            return result;
        byte[]? il = _pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
        if (il == null)
            return result;

        int offset = 0;
        while (offset < il.Length)
        {
            int start = offset;
            ushort opcode = il[offset++];
            if (opcode == 0xFE && offset < il.Length)
                opcode = (ushort)(0xFE00 | il[offset++]);
            if (!s_operandTypes.TryGetValue(opcode, out System.Reflection.Emit.OperandType operand))
                break;

            CallKind? kind = opcode switch
            {
                0x28 or 0x6F => CallKind.Call,
                0x73 => CallKind.NewObject,
                0xFE06 or 0xFE07 => CallKind.LoadFunction,
                _ => null,
            };
            if (kind != null && offset + 4 <= il.Length)
                result.Add(new CallInstruction(start, kind.Value, MetadataTokens.EntityHandle(BitConverter.ToInt32(il, offset))));

            if (operand == System.Reflection.Emit.OperandType.InlineSwitch)
            {
                if (offset + 4 > il.Length)
                    break;
                offset += 4 + 4 * BitConverter.ToInt32(il, offset);
                continue;
            }
            offset += operand switch
            {
                System.Reflection.Emit.OperandType.InlineNone => 0,
                System.Reflection.Emit.OperandType.ShortInlineBrTarget or System.Reflection.Emit.OperandType.ShortInlineI
                    or System.Reflection.Emit.OperandType.ShortInlineVar => 1,
                System.Reflection.Emit.OperandType.InlineVar => 2,
                System.Reflection.Emit.OperandType.InlineI8 or System.Reflection.Emit.OperandType.InlineR => 8,
                _ => 4,
            };
        }
        return result;
    }

    /// <summary>What a call instruction calls; null for things that are not methods (calli, a field).</summary>
    public CallTarget? DescribeCallTarget(EntityHandle target)
    {
        if (target.Kind == HandleKind.MethodSpecification)
            target = _md.GetMethodSpecification((MethodSpecificationHandle)target).Method;
        switch (target.Kind)
        {
            case HandleKind.MethodDefinition:
            {
                var handle = (MethodDefinitionHandle)target;
                MethodDefinition method = _md.GetMethodDefinition(handle);
                TypeDefinitionHandle type = method.GetDeclaringType();
                MethodSignature<string> signature = method.DecodeSignature(SignatureNames.Instance, this);
                int token = MetadataTokens.GetToken(handle);
                return new CallTarget(StripArity(GetTypeName(type)), GetTypeDisplayName(type), _md.GetString(method.Name),
                    signature.ParameterTypes.ToArray(), GetMethodParameters(token).ParameterNames, token);
            }
            case HandleKind.MemberReference:
            {
                MemberReference member = _md.GetMemberReference((MemberReferenceHandle)target);
                if (member.GetKind() != MemberReferenceKind.Method || GetTypeHandleName(member.Parent) is not { } owner)
                    return null;
                MethodSignature<string> signature = member.DecodeMethodSignature(SignatureNames.Instance, this);
                return new CallTarget(StripArity(owner), GetTypeDisplayName(member.Parent), _md.GetString(member.Name),
                    signature.ParameterTypes.ToArray(), null, null);
            }
            default:
                return null;
        }
    }

    // "Outer.Inner" without namespace or arity, for a type defined here or referenced from elsewhere
    private string GetTypeDisplayName(EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
            {
                TypeDefinition type = _md.GetTypeDefinition((TypeDefinitionHandle)handle);
                string name = StripArity(_md.GetString(type.Name));
                return type.GetDeclaringType().IsNil ? name : GetTypeDisplayName(type.GetDeclaringType()) + "." + name;
            }
            case HandleKind.TypeReference:
            {
                TypeReference type = _md.GetTypeReference((TypeReferenceHandle)handle);
                string name = StripArity(_md.GetString(type.Name));
                return type.ResolutionScope.Kind == HandleKind.TypeReference ? GetTypeDisplayName(type.ResolutionScope) + "." + name : name;
            }
            case HandleKind.TypeSpecification:
            {
                string? full = GetTypeHandleName(handle);
                if (full == null)
                    return "?";
                full = StripArity(full);
                return full[(full.LastIndexOf('.') + 1)..];
            }
            default:
                return "?";
        }
    }

    /// <summary>IL offset of the first line of a method, or null when it has no (visible) sequence points.</summary>
    public int? GetFirstLineOffset(int methodToken)
    {
        var points = GetSequencePoints(methodToken);
        if (points == null)
            return null;
        foreach (SequencePoint point in points)
            if (!point.IsHidden)
                return point.Offset;
        return null;
    }
}
