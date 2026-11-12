using Aptabase.Features.Performance;
using Xunit;

namespace Aptabase.UnitTests.Features.Performance;

public class TransactionBodyTests
{
    private static TransactionBody Valid() => new() { EventId = Guid.NewGuid().ToString(), Name = "app.startup", Operation = "ui.load", Timestamp = DateTime.UtcNow, DurationMs = 100 };

    [Fact]
    public void Accepts_Bounded_Transaction_And_Nested_Spans()
    {
        var body = Valid();
        body.Spans = new() {
            new() { SpanId = "1111111111111111", Name = "load", Operation = "app", DurationMs = 100 },
            new() { SpanId = "2222222222222222", ParentSpanId = "1111111111111111", Name = "read", Operation = "file", StartOffsetMs = 10, DurationMs = 20 }
        };
        Assert.True(body.IsValid());
        body.Spans[1].ParentSpanId = "2222222222222222";
        Assert.False(body.IsValid());
        body.Spans[1].ParentSpanId = "3333333333333333";
        Assert.False(body.IsValid());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(86400001)]
    public void Rejects_Invalid_Durations(double duration)
    {
        var body = Valid(); body.DurationMs = duration;
        Assert.False(body.IsValid());
    }

    [Fact]
    public void Rejects_Missing_And_Oversized_Fields()
    {
        var body = Valid(); body.EventId = null; Assert.False(body.IsValid());
        body = Valid(); body.Timestamp = null; Assert.False(body.IsValid());
        body = Valid(); body.Name = new string(' ', 201); Assert.False(body.IsValid());
        body = Valid(); body.Environment = new string('x', 51); Assert.False(body.IsValid());
        body = Valid(); body.Spans = null; Assert.False(body.IsValid());
        body = Valid(); body.Status = "whatever"; Assert.False(body.IsValid());
    }

    [Fact]
    public void Rejects_Duplicate_Ids_And_Out_Of_Bounds_Spans()
    {
        var body = Valid();
        var span = new TransactionSpan { SpanId = "1111111111111111", Name = "load", Operation = "file", DurationMs = 101 };
        body.Spans!.Add(span); Assert.False(body.IsValid());
        span.DurationMs = 100; Assert.True(body.IsValid());
        body.Spans.Add(span); Assert.False(body.IsValid());
        body.Spans = Enumerable.Repeat(span, 101).ToList(); Assert.False(body.IsValid());
    }

    [Fact]
    public void Bounds_Query_Window_And_Pagination()
    {
        Assert.True(new PerformanceQuery().TryFilter("app", out _));
        Assert.False(new PerformanceQuery { StartDate = DateTime.UtcNow.AddDays(-32) }.TryFilter("app", out _));
        Assert.False(new PerformanceQuery { EndDate = DateTime.MinValue }.TryFilter("app", out _));
        Assert.False(new PerformanceQuery { BuildMode = "other" }.TryFilter("app", out _));
        Assert.False(new PerformanceQuery { Limit = 101 }.TryFilter("app", out _));
    }

    [Fact]
    public void Accepts_Fractional_Spans_Ending_Exactly_At_Parent_Boundary()
    {
        var body = Valid(); body.DurationMs = 0.3;
        body.Spans = new() {
            new() { SpanId = "1111111111111111", Name = "parent", Operation = "app", DurationMs = 0.3 },
            new() { SpanId = "2222222222222222", ParentSpanId = "1111111111111111", Name = "child", Operation = "app", StartOffsetMs = 0.1, DurationMs = 0.2 }
        };
        Assert.True(body.IsValid());
        body.Spans[1].DurationMs = 0.200001;
        Assert.False(body.IsValid());
        body.DurationMs = 1;
        Assert.False(body.IsValid());
    }
}
