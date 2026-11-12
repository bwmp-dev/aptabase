using System.Buffers.Binary;
using System.Text;

namespace Aptabase.Features.Symbolication;

public record NativeFunction(ulong Address, ulong Size, string Name);
public record NativeSourceLocation(string Function, string File, int Line);
public record NativeSourceAddress(ulong Address, NativeSourceLocation[] Locations);
public record NativeSymbolIndex(string BuildId, NativeFunction[] Functions, NativeSourceAddress[]? Sources = null, string SourceStatus = "unconfigured")
{
    public NativeFunction? Resolve(ulong address) => Functions.FirstOrDefault(f => address >= f.Address && address - f.Address < f.Size);
}

// Deliberately limited to ELF64 little-endian ARM64 shared libraries. Debug/source
// sections are never decompressed, interpreted, or returned by this first reader.
public static class ElfSymbols
{
    public const int MaxBytes = 8 * 1024 * 1024;
    private const int MaxSymbols = 65536;

    public static bool IsSupportedHeader(ReadOnlySpan<byte> data) => data.Length is >= 64 and <= MaxBytes &&
        data[..7].SequenceEqual(new byte[] { 127, 69, 76, 70, 2, 1, 1 }) &&
        BinaryPrimitives.ReadUInt16LittleEndian(data[16..]) == 3 &&
        BinaryPrimitives.ReadUInt16LittleEndian(data[18..]) == 183 &&
        BinaryPrimitives.ReadUInt32LittleEndian(data[20..]) == 1 &&
        BinaryPrimitives.ReadUInt16LittleEndian(data[52..]) == 64 &&
        BinaryPrimitives.ReadUInt16LittleEndian(data[58..]) == 64;

    public static NativeSymbolIndex Read(byte[] bytes)
    {
        var reader = new Reader(bytes);
        return reader.Read();
    }

    private sealed class Reader(byte[] data)
    {
        private readonly record struct Section(uint Type, ulong Flags, ulong Address, ulong Offset, ulong Size, uint Link, ulong EntrySize);
        private ReadOnlySpan<byte> Slice(ulong offset, ulong length)
        {
            if (offset > (ulong)data.Length || length > (ulong)data.Length - offset) throw new FormatException("ELF range outside artifact");
            return data.AsSpan((int)offset, (int)length);
        }
        private ushort U16(ulong offset) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(offset, 2));
        private uint U32(ulong offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(offset, 4));
        private ulong U64(ulong offset) => BinaryPrimitives.ReadUInt64LittleEndian(Slice(offset, 8));

        public NativeSymbolIndex Read()
        {
            if (!IsSupportedHeader(data))
                throw new FormatException("Only ELF64 little-endian ARM64 shared libraries are supported");
            var sectionOffset = U64(40);
            var count = U16(60);
            if (count is 0 or > 4096) throw new FormatException("Invalid ELF section count");
            Slice(sectionOffset, (ulong)count * 64);
            var sections = new Section[count];
            for (uint i = 0; i < count; i++)
            {
                var offset = sectionOffset + i * 64;
                sections[i] = new(U32(offset + 4), U64(offset + 8), U64(offset + 16), U64(offset + 24), U64(offset + 32), U32(offset + 40), U64(offset + 56));
                if (sections[i].Type != 8) Slice(sections[i].Offset, sections[i].Size);
            }
            string? buildId = null;
            var functions = new List<NativeFunction>();
            var entries = 0UL;
            foreach (var section in sections)
            {
                if (section.Type == 7)
                {
                    var note = Slice(section.Offset, section.Size);
                    while (note.Length > 0)
                    {
                        if (note.Length < 12) throw new FormatException("Truncated ELF note");
                        var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(note);
                        var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(note[4..]);
                        var type = BinaryPrimitives.ReadUInt32LittleEndian(note[8..]);
                        var namePadded = ((ulong)nameLength + 3) & ~3UL;
                        var valuePadded = ((ulong)valueLength + 3) & ~3UL;
                        var length = 12 + namePadded + valuePadded;
                        if (length > (ulong)note.Length) throw new FormatException("Invalid ELF note length");
                        if (type == 3 && nameLength == 4 && note.Slice(12, 4).SequenceEqual("GNU\0"u8))
                        {
                            if (buildId is not null || valueLength is < 16 or > 32) throw new FormatException("Ambiguous or invalid build ID");
                            buildId = Convert.ToHexString(note.Slice(12 + (int)namePadded, (int)valueLength)).ToLowerInvariant();
                        }
                        note = note[(int)length..];
                    }
                }
                if (section.Type is not (2 or 11)) continue;
                if ((section.Flags & 0x800) != 0 || section.EntrySize != 24 || section.Size % 24 != 0 || section.Link >= sections.Length)
                    throw new FormatException("Unsupported symbol table");
                entries += section.Size / 24;
                if (entries > MaxSymbols) throw new FormatException("Too many symbols");
                var strings = sections[section.Link];
                if (strings.Type != 3 || (strings.Flags & 0x800) != 0) throw new FormatException("Invalid string table");
                for (var offset = section.Offset; offset < section.Offset + section.Size; offset += 24)
                {
                    var name = U32(offset);
                    var type = Slice(offset + 4, 1)[0] & 15;
                    var index = U16(offset + 6);
                    if (type != 2 || index == 0) continue;
                    if (index >= sections.Length || name >= strings.Size) throw new FormatException("Invalid function reference");
                    var address = U64(offset + 8);
                    var size = U64(offset + 16);
                    if (size == 0) continue;
                    if (size > ulong.MaxValue - address) throw new FormatException("Invalid function extent");
                    var code = sections[index];
                    if (code.Type != 1 || (code.Flags & 4) == 0 || address < code.Address || address - code.Address > code.Size || size > code.Size - (address - code.Address))
                        throw new FormatException("Function outside executable section");
                    var raw = Slice(strings.Offset + name, Math.Min(512UL, strings.Size - name));
                    var end = raw.IndexOf((byte)0);
                    if (end < 1) throw new FormatException("Invalid symbol name");
                    var text = new UTF8Encoding(false, true).GetString(raw[..end]);
                    if (text.Any(char.IsControl)) throw new FormatException("Invalid symbol name");
                    functions.Add(new(address, size, text));
                }
            }
            if (buildId is null || functions.Count == 0) throw new FormatException("Build ID and sized function symbols are required");
            var ordered = functions.Distinct().OrderBy(f => f.Address).ThenBy(f => f.Size).ThenBy(f => f.Name, StringComparer.Ordinal).ToArray();
            return new(buildId, ordered);
        }
    }
}
