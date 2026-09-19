using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DotnetDebugger.Engine.Symbols;

// Extension methods, interface instantiations, symbol identity, nearest source lines.
internal sealed partial class ModuleMetadata
{
    private const string ExtensionAttribute = "System.Runtime.CompilerServices.ExtensionAttribute";

    private List<TypeDefinitionHandle>? _extensionContainers;
    private readonly Dictionary<string, List<int>> _extensionTypesByMethod = [];

    /// <summary>Interfaces a type lists, with their type arguments: "System.Collections.Generic.IEnumerable`1&lt;!0&gt;".</summary>
    public List<string> GetInterfaceSignatures(int typeDefToken)
    {
        var result = new List<string>();
        TypeDefinition type = _md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(typeDefToken));
        foreach (InterfaceImplementationHandle handle in type.GetInterfaceImplementations())
        {
            EntityHandle iface = _md.GetInterfaceImplementation(handle).Interface;
            string? signature = iface.Kind == HandleKind.TypeSpecification
                ? _md.GetTypeSpecification((TypeSpecificationHandle)iface).DecodeSignature(SignatureNames.Instance, this)
                : GetTypeHandleName(iface);
            if (signature != null)
                result.Add(signature);
        }
        return result;
    }

    /// <summary>Tokens of the static classes that declare an extension method called <paramref name="name"/>.</summary>
    public List<int> GetExtensionTypes(string name)
    {
        lock (_sync)
        {
            if (_extensionTypesByMethod.TryGetValue(name, out List<int>? cached))
                return cached;

            // [Extension] on the class is what the compiler looks at first; it keeps this scan cheap
            _extensionContainers ??= _md.TypeDefinitions
                .Where(h => FindAttribute(_md.GetTypeDefinition(h).GetCustomAttributes(), ExtensionAttribute) != null)
                .ToList();

            var result = new List<int>();
            foreach (TypeDefinitionHandle typeHandle in _extensionContainers)
            {
                foreach (MethodDefinitionHandle methodHandle in _md.GetTypeDefinition(typeHandle).GetMethods())
                {
                    MethodDefinition method = _md.GetMethodDefinition(methodHandle);
                    if ((method.Attributes & MethodAttributes.Static) != 0 && _md.GetString(method.Name) == name
                        && FindAttribute(method.GetCustomAttributes(), ExtensionAttribute) != null)
                    {
                        result.Add(MetadataTokens.GetToken(typeHandle));
                        break;
                    }
                }
            }
            return _extensionTypesByMethod[name] = result;
        }
    }

    // ---------------------------------------------------------------- symbols found elsewhere

    /// <summary>What identifies the PDB that belongs to this module: its file name and id.</summary>
    public (string FileName, Guid Id)? GetPdbIdentity()
    {
        foreach (DebugDirectoryEntry entry in _pe.ReadDebugDirectory())
        {
            if (entry.Type != DebugDirectoryEntryType.CodeView || !entry.IsPortableCodeView)
                continue;
            CodeViewDebugDirectoryData codeView = _pe.ReadCodeViewDebugDirectoryData(entry);
            return (System.IO.Path.GetFileName(codeView.Path.Replace('\\', '/')), codeView.Guid);
        }
        return null;
    }

    /// <summary>True when the symbols did not come from next to the module (or a directory the user named).</summary>
    public bool SymbolsAreForeign { get; private set; }

    /// <summary>Adopts symbols located after the module was opened. False if they do not belong to this module.</summary>
    public bool AttachSymbols(MetadataReaderProvider provider, bool foreign)
    {
        MetadataReader reader = provider.GetMetadataReader();
        if (GetPdbIdentity() is not { } identity || reader.DebugMetadataHeader == null
            || new BlobContentId(reader.DebugMetadataHeader.Id).Guid != identity.Id)
        {
            provider.Dispose();
            return false;
        }

        lock (_sync)
        {
            _pdbProvider = provider;
            _pdb = reader;
            _pointsByDocument = null;
            SymbolsAreForeign = foreign;
        }
        return true;
    }

    // ---------------------------------------------------------------- source lines

    /// <summary>The last visible source line at or before <paramref name="ilOffset"/> (await points sit in hidden regions).</summary>
    public SourceLocation? GetNearestSourceLocation(int methodToken, int ilOffset)
    {
        var points = GetSequencePoints(methodToken);
        if (points == null)
            return null;
        SequencePoint? best = null;
        foreach (SequencePoint point in points)
        {
            if (point.Offset > ilOffset)
                break;
            if (!point.IsHidden)
                best = point;
        }
        best ??= points.Cast<SequencePoint?>().FirstOrDefault(p => !p!.Value.IsHidden);
        if (best is not { } found)
            return null;
        return new SourceLocation(_pdb!.GetString(_pdb.GetDocument(found.Document).Name), found.StartLine, found.StartColumn, found.EndLine, found.EndColumn);
    }
}
