using Aptabase.Features.ErrorReporting;
using Aptabase.Features.Symbolication;
using Xunit;

namespace Aptabase.UnitTests.Features.Sentry;

public class NativeFrameTests
{
    [Theory]
    [InlineData("0x0", true)]
    [InlineData("0xffffffffffffffff", true)]
    [InlineData("0x10000000000000000", false)]
    [InlineData("0x1\n", false)]
    [InlineData("0xFF", false)]
    [InlineData("../private", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Address_Validation_Is_Bounded_And_Strict(string? address, bool valid)
    {
        Assert.Equal(valid, new NativeFrame(new string('a', 40), address!).IsValid());
    }

    [Fact]
    public void Native_Frames_Require_Stable_Id_Valid_Build_And_Bounded_Count()
    {
        var frame = new NativeFrame(new string('a', 40), "0x1");
        var body = new ErrorBody { ErrorType = "native", ErrorMessage = "Synthetic", NativeFrames = new() { frame } };
        Assert.False(body.IsValid());
        body.EventId = Guid.NewGuid().ToString();
        Assert.True(body.IsValid());
        body.NativeFrames = Enumerable.Repeat(frame, 100).ToList();
        Assert.True(body.IsValid());
        body.NativeFrames.Add(frame);
        Assert.False(body.IsValid());
        body.NativeFrames.Clear();
        Assert.False(body.IsValid());
        body.NativeFrames.Add(null!);
        Assert.False(body.IsValid());
        foreach (var build in new[] { "", new string('a', 31), new string('a', 33), new string('a', 66), new string('A', 40), new string('a', 40) + "\n" })
            Assert.False(new NativeFrame(build, "0x1").IsValid());
    }
}
