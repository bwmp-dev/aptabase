using System.Buffers.Binary;
using Aptabase.Features.Symbolication;
using Xunit;

namespace Aptabase.UnitTests.Features.Sentry;

public class ElfSymbolsTests
{
    private static void U16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    private static void U32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static void U64(byte[] data, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value);
    private static byte[] Fixture()
    {
        var data = new byte[1024];
        new byte[] { 127, 69, 76, 70, 2, 1, 1 }.CopyTo(data, 0);
        U16(data, 16, 3); U16(data, 18, 183); U32(data, 20, 1); U16(data, 52, 64); U64(data, 40, 64); U16(data, 58, 64); U16(data, 60, 5);
        U32(data, 128 + 4, 7); U64(data, 128 + 24, 400); U64(data, 128 + 32, 36);
        U32(data, 400, 4); U32(data, 404, 20); U32(data, 408, 3); "GNU\0"u8.CopyTo(data.AsSpan(412));
        for (var i = 0; i < 20; i++) data[416 + i] = (byte)(i + 1);
        U32(data, 192 + 4, 3); U64(data, 192 + 24, 500); U64(data, 192 + 32, 20); "\0run\0"u8.CopyTo(data.AsSpan(500));
        U32(data, 256 + 4, 2); U64(data, 256 + 24, 600); U64(data, 256 + 32, 24); U32(data, 256 + 40, 2); U64(data, 256 + 56, 24);
        U32(data, 600, 1); data[604] = 2; U16(data, 606, 4); U64(data, 608, 0x1000); U64(data, 616, 16);
        U32(data, 320 + 4, 1); U64(data, 320 + 8, 4); U64(data, 320 + 16, 0x1000); U64(data, 320 + 24, 700); U64(data, 320 + 32, 16);
        return data;
    }
    [Fact]
    public void Resolves_Only_Within_Function_And_Returns_No_Source_Contents()
    {
        var index = ElfSymbols.Read(Fixture());
        Assert.Equal("0102030405060708090a0b0c0d0e0f1011121314", index.BuildId);
        Assert.Equal("run", index.Resolve(0x100f)!.Name);
        Assert.Null(index.Resolve(0xfff)); Assert.Null(index.Resolve(0x1010));
        Assert.Single(index.Functions);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(18)]
    [InlineData(58)]
    public void Rejects_Wrong_Format(int offset)
    {
        var data = Fixture(); data[offset] = 0;
        Assert.Throws<FormatException>(() => ElfSymbols.Read(data));
    }
    [Fact]
    public void Rejects_Overflow_Truncation_And_Invalid_References()
    {
        var data = Fixture(); U64(data, 40, ulong.MaxValue); Assert.Throws<FormatException>(() => ElfSymbols.Read(data));
        data = Fixture(); U32(data, 400, uint.MaxValue); Assert.Throws<FormatException>(() => ElfSymbols.Read(data));
        data = Fixture(); U32(data, 256 + 40, 99); Assert.Throws<FormatException>(() => ElfSymbols.Read(data));
        data = Fixture(); U64(data, 616, 17); Assert.Throws<FormatException>(() => ElfSymbols.Read(data));
        data = Fixture(); U64(data, 320 + 8, 0); Assert.Throws<FormatException>(() => ElfSymbols.Read(data));
        Assert.Throws<FormatException>(() => ElfSymbols.Read(new byte[ElfSymbols.MaxBytes + 1]));
        for (var length = 0; length < 700; length += 7) Assert.Throws<FormatException>(() => ElfSymbols.Read(Fixture()[..length]));
    }
}
