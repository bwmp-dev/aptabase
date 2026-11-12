using System.Text;
using System.Text.Json;
using Aptabase.Features.Sentry;
using Xunit;

namespace Aptabase.UnitTests.Features.Sentry;

public class SentryMetadataTests
{
    private static string Session(string extra = "") => """
        {"sid":"11111111111111111111111111111111","started":"2026-11-01T00:00:00Z",
         "timestamp":"2026-11-01T00:00:02Z","attrs":{"release":"synthetic@1"}
        """ + extra + "}";

    private static byte[] Wire(params (string Type, string Body)[] items) => Encoding.UTF8.GetBytes("{}\n" + string.Concat(items.Select(i =>
        JsonSerializer.Serialize(new { type = i.Type, length = Encoding.UTF8.GetByteCount(i.Body) }) + "\n" + i.Body + "\n")));

    [Fact]
    public void Accepts_Standalone_And_Mixed_Metadata_Without_Retaining_Personal_Fields()
    {
        var parsed = SentryEnvelopeParser.Parse(Wire(("session", Session(",\"did\":\"private user\",\"ip_address\":\"private\",\"status\":\"crashed\",\"errors\":1,\"init\":true")),
            ("client_report", """{"timestamp":1800000000,"discarded_events":[{"reason":"before_send","category":"error","quantity":3},{"reason":"private","category":"secret","quantity":2}]}"""),
            ("event", """{"event_id":"22222222222222222222222222222222","message":"synthetic"}""")));
        Assert.NotNull(parsed.Error);
        Assert.Equal("crashed", Assert.Single(parsed.Sessions).Status);
        Assert.Contains(parsed.ClientReport!.Counters, c => c.Reason == "other" && c.Category == "other" && c.Quantity == 2);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(parsed));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(parsed));
        Assert.Null(SentryEnvelopeParser.Parse(Wire(("session", Session()))).EventId);
    }

    [Theory]
    [InlineData(",\"status\":\"unknown\"")]
    [InlineData(",\"errors\":-1")]
    [InlineData(",\"errors\":1.5")]
    [InlineData(",\"seq\":-1")]
    [InlineData(",\"duration\":-1")]
    [InlineData(",\"duration\":31536001")]
    [InlineData(",\"init\":\"true\"")]
    [InlineData(",\"sid\":\"22222222222222222222222222222222\"")]
    public void Rejects_Malformed_Session_Fields(string extra) =>
        Assert.Throws<FormatException>(() => SentryEnvelopeParser.Parse(Wire(("session", Session(extra)))));

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"reason\":\"before_send\",\"category\":\"error\",\"quantity\":0}]")]
    [InlineData("[{\"reason\":\"before_send\",\"category\":\"error\",\"quantity\":1000000001}]")]
    [InlineData("[{\"reason\":\"before_send\",\"category\":\"error\",\"quantity\":\"1\"}]")]
    public void Rejects_Malformed_Client_Report(string counters) => Assert.Throws<FormatException>(() =>
        SentryEnvelopeParser.Parse(Wire(("client_report", "{\"timestamp\":1800000000,\"discarded_events\":" + counters + "}"))));

    [Fact]
    public void Rejects_Item_Count_And_Size_Overflow_And_Unsupported_Tails()
    {
        Assert.Throws<FormatException>(() => SentryEnvelopeParser.Parse(Wire(Enumerable.Repeat(("session", Session()), 31).ToArray())));
        Assert.Throws<FormatException>(() => SentryEnvelopeParser.Parse(Wire(("session", Session(",\"ignored\":\"" + new string('x', 8192) + "\"")))));
        Assert.Throws<UnsupportedSentryItemException>(() => SentryEnvelopeParser.Parse(Wire(("session", Session()), ("attachment", "secret"))));
    }

    [Fact]
    public void Canonical_Report_Counters_Ignore_Order_And_Combine_Duplicate_Labels()
    {
        const string first = """{"timestamp":1800000000,"discarded_events":[{"reason":"before_send","category":"error","quantity":1},{"reason":"queue_overflow","category":"error","quantity":4},{"reason":"before_send","category":"error","quantity":2}]}""";
        const string second = """{"timestamp":1800000000,"discarded_events":[{"reason":"queue_overflow","category":"error","quantity":4},{"reason":"before_send","category":"error","quantity":3}]}""";
        var a = SentryEnvelopeParser.Parse(Wire(("client_report", first))).ClientReport!;
        var b = SentryEnvelopeParser.Parse(Wire(("client_report", second))).ClientReport!;
        Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
        Assert.Equal(2, a.Counters.Length);
    }

    [Fact]
    public void Crash_Free_Rate_Uses_Only_Eligible_Completed_Sessions()
    {
        Assert.Null(new SessionHealthCounts { Observed = 10 }.CrashFreeRate);
        var counts = new SessionHealthCounts { Observed = 5, Eligible = 2, EligibleCrashed = 1, Crashed = 2 };
        Assert.Equal(50, counts.CrashFreeRate);
        Assert.Equal(3, counts.Incomplete);
    }
}
