using Aptabase.Features.ErrorReporting;
using Xunit;

namespace Aptabase.UnitTests.Features.ErrorReporting;

public class IssueTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", false)]
    [InlineData("not-an-id", false)]
    [InlineData("00000000-0000-0000-0000-000000000000", false)]
    [InlineData("a328eb5ef1fc4fa582221e24881369f0", true)]
    [InlineData("a328eb5e-f1fc-4fa5-8222-1e24881369f0", true)]
    [InlineData("{a328eb5e-f1fc-4fa5-8222-1e24881369f0}", false)]
    public void Validates_Event_Id(string? eventId, bool valid)
    {
        Assert.Equal(valid, new ErrorBody { EventId = eventId, ErrorType = "Type", ErrorMessage = "message" }.IsValid());
    }

    [Fact]
    public void Fingerprint_Is_Stable_And_Normalizes_Whitespace()
    {
        Assert.Equal(IssueFingerprint.Create(" Type ", "ignored", " at A()\r\n at B()"),
            IssueFingerprint.Create("Type", "different message", "at A() at B()"));
        Assert.Matches("^[a-f0-9]{64}$", IssueFingerprint.Create("Type", "message", null));
    }

    [Fact]
    public void Fingerprint_Distinguishes_Type_Stack_And_Message_Fallback()
    {
        var original = IssueFingerprint.Create("Type", "message", "at A()");
        Assert.NotEqual(original, IssueFingerprint.Create("Other", "message", "at A()"));
        Assert.NotEqual(original, IssueFingerprint.Create("Type", "message", "at B()"));
        Assert.NotEqual(IssueFingerprint.Create("Type", "first", null), IssueFingerprint.Create("Type", "second", " "));
        Assert.NotEqual(IssueFingerprint.Create("Type", "at A()", null), original);
    }

    [Fact]
    public void Sanitized_Identifiers_Do_Not_Split_Issues()
    {
        var sanitizer = new PiiSanitizer();
        Assert.Equal(IssueFingerprint.Create("Type", sanitizer.Sanitize("Failed for alice@example.com"), null),
            IssueFingerprint.Create("Type", sanitizer.Sanitize("Failed for bob@example.com"), null));
    }

    [Fact]
    public void Rejects_Oversized_Whitespace_Fields()
    {
        Assert.False(new ErrorBody { ErrorType = "Type", ErrorMessage = "message", StackTrace = new string(' ', 10001) }.IsValid());
        Assert.False(new ErrorBody { ErrorType = "Type", ErrorMessage = "message", Platform = new string(' ', 31) }.IsValid());
    }
}
