using Aptabase.Features.ErrorReporting;
using Aptabase.Features.Symbolication;
using Xunit;

namespace Aptabase.UnitTests.Features.Sentry;

public class NativeSourceOutputTests
{
    [Fact]
    public void Reads_Historical_Function_Only_Index()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<NativeSymbolIndex>("{\"BuildId\":\"id\",\"Functions\":[]}")!;
        Assert.Null(old.Sources);
        Assert.Equal("unconfigured", old.SourceStatus);
    }
    private readonly PiiSanitizer sanitizer = new();
    [Fact]
    public void Keeps_Inline_Order_And_Only_Basenames_And_Positive_Lines()
    {
        var result = NativeSourceOutput.Parse("0x10\nleaf\n/private/source/probe.c:2\nouter\nC:\\private\\probe.c:8 (discriminator 2)\n0x14\n??\n??:0\n", new ulong[] { 16, 20 }, sanitizer);
        var frame = Assert.Single(result);
        Assert.Equal(new[] { new NativeSourceLocation("leaf", "probe.c", 2), new NativeSourceLocation("outer", "probe.c", 8) }, frame.Locations);
    }
    [Theory]
    [InlineData("0x11\nf\na.c:1\n")]
    [InlineData("0x10\nf\na.c:0\n")]
    [InlineData("0x10\nf\na.c:2147483648\n")]
    [InlineData("0x10\nf\nsecret@example.test:1\n")]
    [InlineData("0x10\nf\nsource code {x}:1\n")]
    [InlineData("0x10\nf\n")]
    [InlineData("0x10\nf\na.c:1\n0x14\n??\n??:0\n")]
    public void Rejects_Malformed_Or_Unexpected_Output(string value) => Assert.Throws<FormatException>(() => NativeSourceOutput.Parse(value, new ulong[] { 16 }, sanitizer));
    [Fact]
    public void Bounds_Addresses_Inline_Depth_And_Output()
    {
        Assert.Null(NativeSourceOutput.Addresses(new("id", new[] { new NativeFunction(0, 262145, "f") })));
        Assert.Null(NativeSourceOutput.Addresses(new("id", new[] { new NativeFunction(1, 4, "f") })));
        Assert.Null(NativeSourceOutput.Addresses(new("id", new[] { new NativeFunction(0, 262144, "f"), new NativeFunction(0, 262144, "alias") })));
        Assert.Equal(new ulong[] { 4, 8 }, NativeSourceOutput.Addresses(new("id", new[] { new NativeFunction(4, 8, "f"), new NativeFunction(4, 4, "alias") })));
        Assert.Throws<FormatException>(() => NativeSourceOutput.Parse("0x10\n" + string.Concat(Enumerable.Repeat("f\na.c:1\n", 17)), new ulong[] {16}, sanitizer));
        Assert.Throws<FormatException>(() => NativeSourceOutput.Parse(new string('x', NativeSourceOutput.MaxOutput + 1), new ulong[] {16}, sanitizer));
    }
}
