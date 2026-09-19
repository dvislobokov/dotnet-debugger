using System.Buffers.Binary;

namespace DotnetDebugger.Engine.Launch;

/// <summary>
/// Reads what identifies a native binary on Unix: the GNU build id note of an ELF file or the LC_UUID of a Mach-O
/// file. Symbol servers and dbgshim use these ids to pair a runtime with its debugging libraries.
/// </summary>
internal static class BuildId
{
    public static byte[]? Read(string path)
    {
        try
        {
            using FileStream file = File.OpenRead(path);
            var header = new byte[64];
            if (file.Read(header, 0, header.Length) < 64)
                return null;

            if (header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
                return header[4] == 2 && header[5] == 1 ? ReadElf64(file, header) : null; // 64-bit little endian only
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
            return magic == 0xFEEDFACF ? ReadMachO64(file, header) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[]? ReadElf64(FileStream file, byte[] header)
    {
        long programHeaders = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x20));
        int entrySize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x36));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x38));

        var entry = new byte[entrySize];
        for (int i = 0; i < count; i++)
        {
            file.Position = programHeaders + (long)i * entrySize;
            if (file.Read(entry, 0, entrySize) < entrySize)
                return null;
            if (BinaryPrimitives.ReadUInt32LittleEndian(entry) != 4) // PT_NOTE
                continue;

            long offset = BinaryPrimitives.ReadInt64LittleEndian(entry.AsSpan(0x08));
            long size = BinaryPrimitives.ReadInt64LittleEndian(entry.AsSpan(0x20));
            if (size <= 0 || size > 1 << 20)
                continue;
            var notes = new byte[size];
            file.Position = offset;
            if (file.Read(notes, 0, notes.Length) < notes.Length)
                continue;

            // note: namesz, descsz, type, name (padded to 4), desc (padded to 4)
            int position = 0;
            while (position + 12 <= notes.Length)
            {
                int nameSize = BinaryPrimitives.ReadInt32LittleEndian(notes.AsSpan(position));
                int descriptionSize = BinaryPrimitives.ReadInt32LittleEndian(notes.AsSpan(position + 4));
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(notes.AsSpan(position + 8));
                int nameStart = position + 12;
                int descriptionStart = nameStart + Align4(nameSize);
                if (nameSize < 0 || descriptionSize < 0 || descriptionStart + descriptionSize > notes.Length)
                    break;
                if (type == 3 /* NT_GNU_BUILD_ID */ && nameSize == 4 && notes.AsSpan(nameStart, 3).SequenceEqual("GNU"u8))
                    return notes.AsSpan(descriptionStart, descriptionSize).ToArray();
                position = descriptionStart + Align4(descriptionSize);
            }
        }
        return null;

        static int Align4(int value) => (value + 3) & ~3;
    }

    private static byte[]? ReadMachO64(FileStream file, byte[] header)
    {
        int commands = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
        long position = 32; // sizeof(mach_header_64)
        var command = new byte[8];
        for (int i = 0; i < commands; i++)
        {
            file.Position = position;
            if (file.Read(command, 0, 8) < 8)
                return null;
            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(command);
            int size = BinaryPrimitives.ReadInt32LittleEndian(command.AsSpan(4));
            if (kind == 0x1B /* LC_UUID */)
            {
                var uuid = new byte[16];
                return file.Read(uuid, 0, 16) == 16 ? uuid : null;
            }
            if (size <= 0)
                return null;
            position += size;
        }
        return null;
    }
}
