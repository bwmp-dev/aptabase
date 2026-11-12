using System.Text.Json;

namespace Aptabase.Features.Sentry;

public record SentrySessionUpdate(Guid Id, DateTime Started, DateTime Timestamp, long Sequence, bool Init,
    string Status, long Errors, double Duration, string Release, string Environment);
public record SentryDiscardCounter(string Reason, string Category, long Quantity);
public record SentryClientReport(DateTime Timestamp, SentryDiscardCounter[] Counters);

internal static class SentryDeliveryMetadata
{
    private static readonly HashSet<string> Reasons = ["before_send", "buffer_overflow", "cache_overflow", "event_processor",
        "network_error", "queue_overflow", "send_error", "ratelimit_backoff", "sample_rate", "backpressure"];
    private static readonly HashSet<string> Categories = ["attachment", "default", "error", "feedback", "internal", "log_item",
        "metric_bucket", "monitor", "security", "session", "span", "trace_metric", "transaction", "profile"];

    internal static SentrySessionUpdate Session(JsonElement body)
    {
        var id = Text(body, "sid", 36);
        if (!(Guid.TryParseExact(id, "N", out var guid) || Guid.TryParseExact(id, "D", out guid)) || guid == Guid.Empty)
            throw new FormatException("Invalid session ID");
        var started = Time(body, "started");
        var timestamp = Time(body, "timestamp");
        if (timestamp < started) throw new FormatException("Invalid session time order");
        var status = Text(body, "status", 8, false) ?? "ok";
        if (status is not ("ok" or "exited" or "crashed" or "abnormal")) throw new FormatException("Invalid session status");
        var init = false;
        if (body.TryGetProperty("init", out var initNode))
        {
            if (initNode.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException("Invalid init flag");
            init = initNode.GetBoolean();
        }
        var duration = (timestamp - started).TotalSeconds;
        if (body.TryGetProperty("duration", out var durationNode) &&
            (durationNode.ValueKind != JsonValueKind.Number || !durationNode.TryGetDouble(out duration)))
            throw new FormatException("Invalid session duration");
        if (!double.IsFinite(duration) || duration < 0 || duration > 31536000) throw new FormatException("Invalid session duration");
        if (!body.TryGetProperty("attrs", out var attrs) || attrs.ValueKind != JsonValueKind.Object) throw new FormatException("Session attributes required");
        Unique(attrs);
        return new(guid, started, timestamp, Integer(body, "seq", 0, long.MaxValue, false), init, status,
            Integer(body, "errors", 0, 1_000_000_000, false), duration,
            Text(attrs, "release", 100)!, Text(attrs, "environment", 50, false) ?? "");
    }

    internal static SentryClientReport ClientReport(JsonElement body)
    {
        var timestamp = Time(body, "timestamp");
        if (!body.TryGetProperty("discarded_events", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is < 1 or > 100)
            throw new FormatException("Invalid client report counters");
        var counts = new Dictionary<(string Reason, string Category), long>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new FormatException("Invalid client report counter");
            Unique(item);
            var reason = Text(item, "reason", 64)!;
            var category = Text(item, "category", 64)!;
            reason = Reasons.Contains(reason) ? reason : "other";
            category = Categories.Contains(category) ? category : "other";
            var key = (reason, category);
            counts[key] = counts.GetValueOrDefault(key) + Integer(item, "quantity", 1, 1_000_000_000, true);
        }
        return new(timestamp, counts.OrderBy(p => p.Key.Reason, StringComparer.Ordinal).ThenBy(p => p.Key.Category, StringComparer.Ordinal)
            .Select(p => new SentryDiscardCounter(p.Key.Reason, p.Key.Category, p.Value)).ToArray());
    }

    private static string? Text(JsonElement value, string name, int max, bool required = true)
    {
        if (!value.TryGetProperty(name, out var node))
            return !required ? null : throw new FormatException("Required metadata field missing");
        if (node.ValueKind != JsonValueKind.String) throw new FormatException("Invalid metadata string");
        var text = node.GetString()!;
        if (text.Length > max || (required && string.IsNullOrWhiteSpace(text))) throw new FormatException("Invalid metadata string length");
        return text;
    }

    private static long Integer(JsonElement value, string name, long min, long max, bool required)
    {
        if (!value.TryGetProperty(name, out var node) && !required) return 0;
        if (node.ValueKind != JsonValueKind.Number || !node.TryGetInt64(out var number) || number < min || number > max)
            throw new FormatException("Invalid metadata integer");
        return number;
    }

    private static DateTime Time(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var node)) throw new FormatException("Metadata timestamp required");
        DateTime time;
        if (node.ValueKind == JsonValueKind.String && node.TryGetDateTimeOffset(out var date)) time = date.UtcDateTime;
        else if (node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out var seconds) && double.IsFinite(seconds)
            && seconds >= 0 && seconds < 253402300799) time = DateTime.UnixEpoch.AddSeconds(seconds);
        else throw new FormatException("Invalid metadata timestamp");
        // PostgreSQL stores microseconds; normalize before identity/conflict comparisons.
        return new DateTime(time.Ticks / 10 * 10, DateTimeKind.Utc);
    }

    private static void Unique(JsonElement body)
    {
        if (body.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1)) throw new FormatException("Duplicate metadata properties");
    }
}
