using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.DiaSymReader.Tools;

namespace DotnetDebugger.Engine.Symbols;

/// <summary>
/// Windows ("full"/"pdbonly") PDBs, the default of old non-SDK .NET Framework projects. They are converted in memory to
/// a portable PDB (Microsoft.DiaSymReader.Converter), so that everything downstream - sequence points, local scopes,
/// async stepping information, source paths - is read the one way. The converter reads Windows PDBs through the native
/// Microsoft.DiaSymReader.Native.&lt;arch&gt;.dll, which only exists for Windows; elsewhere such PDBs mean "no symbols".
/// </summary>
internal static class WindowsPdb
{
    // The MSF 7.00 superblock that every Windows PDB (and nothing else we read) starts with.
    private static ReadOnlySpan<byte> Signature => "Microsoft C/C++ MSF 7.00\r\n\x1A"u8;

    public static bool IsWindowsPdb(Stream stream)
    {
        if (!stream.CanSeek)
            return false;
        long position = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[Signature.Length];
            return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length && header.SequenceEqual(Signature);
        }
        finally
        {
            stream.Position = position;
        }
    }

    /// <summary>
    /// Converts <paramref name="pdb"/> (a Windows PDB of the module read by <paramref name="pe"/>) to a portable PDB.
    /// Null, with a log line, when that is impossible: not Windows, the native reader missing, a damaged PDB, or a PDB
    /// that belongs to another build of the module.
    /// </summary>
    public static MetadataReaderProvider? TryConvert(PEReader pe, Stream pdb, string pdbPath, Action<string>? log)
    {
        if (!OperatingSystem.IsWindows())
        {
            log?.Invoke($"Windows PDBs can only be read on Windows; no symbols from '{pdbPath}'.");
            return null;
        }

        if (GetCodeView(pe) is not { } codeView)
            return null;

        try
        {
            using var portable = new MemoryStream();
            PdbConverter.Default.ConvertWindowsToPortable(pe, pdb, portable);
            var provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create(portable.GetBuffer(), 0, (int)portable.Length));

            // The converted PDB keeps the GUID of the Windows one, which the module names in its CodeView entry.
            MetadataReader reader = provider.GetMetadataReader();
            if (reader.DebugMetadataHeader == null || new BlobContentId(reader.DebugMetadataHeader.Id).Guid != codeView.Guid)
            {
                provider.Dispose();
                log?.Invoke($"Symbols '{pdbPath}' do not match the module.");
                return null;
            }
            // Public (stripped) PDBs, such as those of .NET Framework itself on the Microsoft symbol server, have no
            // source information at all: no better than none.
            if (reader.Documents.Count == 0)
            {
                provider.Dispose();
                log?.Invoke($"Symbols '{pdbPath}' have no source information.");
                return null;
            }
            return provider;
        }
        catch (Exception e)
        {
            // DllNotFoundException (no native reader), COMException and InvalidDataException (a damaged or foreign PDB),
            // BadImageFormatException...: whatever goes wrong, the module simply has no symbols.
            log?.Invoke($"Cannot convert the Windows PDB '{pdbPath}': {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>The CodeView entry of a module whose PDB is a Windows one.</summary>
    public static CodeViewDebugDirectoryData? GetCodeView(PEReader pe)
    {
        foreach (DebugDirectoryEntry entry in pe.ReadDebugDirectory())
        {
            if (entry.Type == DebugDirectoryEntryType.CodeView && !entry.IsPortableCodeView)
                return pe.ReadCodeViewDebugDirectoryData(entry);
        }
        return null;
    }

    /// <summary>
    /// Finds and converts the Windows PDB next to the module, or at the path the module was built with. Never throws.
    /// </summary>
    public static MetadataReaderProvider? TryOpenAssociated(PEReader pe, string peFilePath, Action<string>? log)
    {
        try
        {
            if (GetCodeView(pe) is not { } codeView || string.IsNullOrEmpty(codeView.Path))
                return null;

            string fileName = Path.GetFileName(codeView.Path.Replace('\\', '/'));
            var candidates = new List<string>();
            if (Path.GetDirectoryName(peFilePath) is { Length: > 0 } directory)
                candidates.Add(Path.Combine(directory, fileName));
            if (Path.IsPathFullyQualified(codeView.Path))
                candidates.Add(codeView.Path);

            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate))
                    continue;
                using FileStream stream = File.OpenRead(candidate);
                if (IsWindowsPdb(stream))
                    return TryConvert(pe, stream, candidate, log);
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException)
        {
            log?.Invoke($"Cannot read the Windows PDB of '{peFilePath}': {e.Message}");
            return null;
        }
    }
}
