using System.Text.RegularExpressions;

namespace Aptabase.Features.Performance;

public class TransactionBody
{
    public string? EventId { get; set; }
    public string? Name { get; set; }
    public string? Operation { get; set; }
    public DateTime? Timestamp { get; set; }
    public double DurationMs { get; set; }
    public string Status { get; set; } = "ok";
    public string? Release { get; set; }
    public string? Environment { get; set; }
    public string? Platform { get; set; }
    public bool IsDebug { get; set; }
    public List<TransactionSpan>? Spans { get; set; } = new();

    public bool IsValid()
    {
        if (!(Guid.TryParseExact(EventId, "D", out var id) || Guid.TryParseExact(EventId, "N", out id)) || id == Guid.Empty)
            return false;
        if (!ValidText(Name, 200) || !ValidText(Operation, 50) || !Timestamp.HasValue || !ValidDuration(DurationMs) ||
            Status is not ("ok" or "error" or "cancelled") || Release?.Length > 100 || Environment?.Length > 50 || Platform?.Length > 30 ||
            Spans is null || Spans.Count > 100)
            return false;
        var byId = new Dictionary<string, TransactionSpan>(StringComparer.Ordinal);
        foreach (var span in Spans)
        {
            if (span is null || !Regex.IsMatch(span.SpanId ?? "", "^[0-9a-f]{16}$") || span.SpanId == "0000000000000000" ||
                !byId.TryAdd(span.SpanId!, span) || !ValidText(span.Name, 200) || !ValidText(span.Operation, 50) ||
                !ValidDuration(span.StartOffsetMs) || !ValidDuration(span.DurationMs) ||
                End(span) > (decimal)DurationMs || span.Status is not ("ok" or "error" or "cancelled"))
                return false;
        }
        foreach (var span in Spans)
        {
            var seen = new HashSet<string>();
            var current = span;
            while (current.ParentSpanId is not null)
            {
                if (!seen.Add(current.SpanId!) || !byId.TryGetValue(current.ParentSpanId, out var parent)) return false;
                if (current.StartOffsetMs < parent.StartOffsetMs || End(current) > End(parent))
                    return false;
                current = parent;
            }
        }
        return true;
    }

    private static bool ValidText(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    // Values are finite and bounded before this conversion. Decimal addition avoids a false
    // overrun for millisecond boundaries such as 0.1 + 0.2 == 0.3, without a time tolerance.
    private static decimal End(TransactionSpan span) => (decimal)span.StartOffsetMs + (decimal)span.DurationMs;
    private static bool ValidDuration(double value) => double.IsFinite(value) && value >= 0 && value <= 86_400_000;
}

public class TransactionSpan
{
    public string? SpanId { get; set; }
    public string? ParentSpanId { get; set; }
    public string? Name { get; set; }
    public string? Operation { get; set; }
    public double StartOffsetMs { get; set; }
    public double DurationMs { get; set; }
    public string Status { get; set; } = "ok";
}
