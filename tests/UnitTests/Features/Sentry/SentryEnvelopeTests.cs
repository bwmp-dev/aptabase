using System.Text;
using System.Text.Json;
using Aptabase.Features.Sentry;
using Xunit;

namespace Aptabase.UnitTests.Features.Sentry;

public class SentryEnvelopeTests
{
    private const string Id = "11111111111111111111111111111111";
    private static byte[] Envelope(object payload, string type = "event", bool length = false)
    {
        var body = JsonSerializer.Serialize(payload);
        var header = length ? JsonSerializer.Serialize(new { type, length = Encoding.UTF8.GetByteCount(body) }) : JsonSerializer.Serialize(new { type });
        return Encoding.UTF8.GetBytes("{\"event_id\":\"" + Id + "\"}\n" + header + "\n" + body + "\n");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Maps_Unicode_Message_And_Strips_Source_Paths_And_Context(bool length)
    {
        var value = SentryEnvelopeParser.Parse(Envelope(new {
            timestamp = "2026-01-01T00:00:00Z", release = "1.0", environment = "desktop",
            exception = new { values = new[] { new { type = "Failure", value = "?chec", stacktrace = new { frames = new[] {
                new { filename = "C:\\Users\\private\\App.cs", function = "Run", vars = new { secret = "secret" } }
            } } } } }, user = new { email = "discard@example.test" }
        }, length: length));
        Assert.Equal("?chec", value.Error!.ErrorMessage);
        Assert.Equal("at Run (App.cs)", value.Error.StackTrace);
        Assert.Equal("1.0", value.Error.Release);
        Assert.Null(value.Error.SessionId);
    }

    [Theory]
    [InlineData("attachment")]
    [InlineData("session")]
    [InlineData("sessions")]
    [InlineData("profile")]
    [InlineData("profile_chunk")]
    [InlineData("client_report")]
    [InlineData("replay_recording")]
    [InlineData("check_in")]
    public void Rejects_Unsupported_Item_Types(string type) => Assert.Throws<UnsupportedSentryItemException>(() => SentryEnvelopeParser.Parse(Envelope(new {}, type)));

    [Theory]
    [InlineData("{\"type\":\"event\",\"length\":\"bad\"}")]
    [InlineData("{\"type\":\"event\",\"length\":-1}")]
    [InlineData("{\"type\":\"event\",\"length\":9999}")]
    [InlineData("{\"type\":\"event\",\"type\":\"event\"}")]
    public void Rejects_Invalid_Headers(string item) => Assert.Throws<FormatException>(() => SentryEnvelopeParser.Parse(Encoding.UTF8.GetBytes("{}\n" + item + "\n{}")));

    [Fact]
    public void Rejects_Multiple_Items_And_Mismatched_Event_IDs()
    {
        var bytes = Envelope(new { message = "test" });
        Assert.Throws<UnsupportedSentryItemException>(() => SentryEnvelopeParser.Parse(bytes.Concat(Encoding.UTF8.GetBytes("{\"type\":\"attachment\"}\nsecret")).ToArray()));
        Assert.Throws<FormatException>(() => SentryEnvelopeParser.Parse(bytes.Concat(Encoding.UTF8.GetBytes("{\"type\":\"event\"}\n{}")).ToArray()));
        Assert.Throws<FormatException>(() => SentryEnvelopeParser.Parse(Envelope(new { event_id = Guid.NewGuid().ToString("N"), message = "test" })));
        Assert.Throws<FormatException>(() => SentryEnvelopeParser.Parse(new byte[SentryEnvelopeParser.MaxBytes + 1]));
    }

    [Fact]
    public void Maps_Root_And_Child_Spans_Without_Descriptions()
    {
        var value = SentryEnvelopeParser.Parse(Envelope(new {
            type = "transaction", transaction = "app.startup", start_timestamp = 1800000000.0, timestamp = 1800000000.5,
            contexts = new { trace = new { span_id = "1111111111111111", op = "ui.load", status = "ok" } },
            spans = new[] { new { span_id = "2222222222222222", parent_span_id = "1111111111111111", op = "file.read", description = "secret", start_timestamp = 1800000000.1, timestamp = 1800000000.2 } }
        }, "transaction"));
        Assert.Equal(500, value.Transaction!.DurationMs);
        var span = Assert.Single(value.Transaction.Spans!);
        Assert.Null(span.ParentSpanId);
        Assert.Equal("file.read", span.Name);
        Assert.InRange(span.DurationMs, 99.9, 100.1);
    }
}
