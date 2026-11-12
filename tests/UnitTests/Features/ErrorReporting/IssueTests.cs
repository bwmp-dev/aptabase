using Aptabase.Features.ErrorReporting;
using Xunit;

namespace Aptabase.UnitTests.Features.ErrorReporting;

public class IssueTests
{
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
