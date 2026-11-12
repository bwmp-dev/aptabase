using System.Globalization;
using System.Text.Json;
using Aptabase.Features.ErrorReporting;
using Aptabase.Features.Performance;

namespace Aptabase.Features.Sentry;

public record SentryEnvelope(string? Dsn, string? EventId, ErrorBody? Error, TransactionBody? Transaction,
    IReadOnlyList<SentrySessionUpdate> Sessions, SentryClientReport? ClientReport);
public class UnsupportedSentryItemException : Exception;

public static class SentryEnvelopeParser
{
    public const int MaxBytes = 262144;

    public static SentryEnvelope Parse(byte[] data)
    {
        if (data.Length > MaxBytes) throw new FormatException("Envelope too large");
        var offset = 0;
        var header = ParseObject(ReadLine(data, ref offset));
        JsonElement body = default;
        string? type = null;
        var sessions = new List<SentrySessionUpdate>();
        SentryClientReport? clientReport = null;
        var itemCount = 0;
        while (offset < data.Length)
        {
            if (++itemCount > 32) throw new FormatException("Too many envelope items");
            var item = ParseObject(ReadLine(data, ref offset));
            var itemType = Text(item, "type");
            if (itemType is not ("event" or "transaction" or "session" or "client_report")) throw new UnsupportedSentryItemException();
            ReadOnlyMemory<byte> payload;
            if (item.TryGetProperty("length", out var lengthNode))
            {
                if (lengthNode.ValueKind != JsonValueKind.Number || !lengthNode.TryGetInt32(out var length) || length < 0 || length > data.Length - offset) throw new FormatException("Invalid item length");
                payload = data.AsMemory(offset, length);
                offset += length;
                if (offset < data.Length && data[offset++] != (byte)'\n') throw new FormatException("Invalid item delimiter");
            }
            else payload = ReadLine(data, ref offset);
            if (itemType == "session")
            {
                if (payload.Length > 8192 || sessions.Count >= 30) throw new FormatException("Session limit exceeded");
                sessions.Add(SentryDeliveryMetadata.Session(ParseObject(payload)));
            }
            else if (itemType == "client_report")
            {
                if (payload.Length > 16384 || clientReport is not null) throw new FormatException("Client report limit exceeded");
                clientReport = SentryDeliveryMetadata.ClientReport(ParseObject(payload));
            }
            else
            {
                if (type is not null) throw new FormatException("Only one event or transaction per envelope is supported");
                type = itemType;
                body = ParseObject(payload);
            }
        }
        if (itemCount == 0) throw new FormatException("Empty envelope");
        if (type is null)
        {
            var metadataId = Text(header, "event_id");
            if (metadataId is not null && !Guid.TryParse(metadataId, out _)) throw new FormatException("Invalid event ID");
            return new(Text(header, "dsn"), null, null, null, sessions, clientReport);
        }
        var id = Text(body, "event_id") ?? Text(header, "event_id");
        if (!(Guid.TryParseExact(id, "N", out var guid) || Guid.TryParseExact(id, "D", out guid)) || guid == Guid.Empty) throw new FormatException("Invalid event ID");
        var headerId = Text(header, "event_id");
        if (headerId is not null && (!Guid.TryParse(headerId, out var headerGuid) || headerGuid != guid)) throw new FormatException("Event ID mismatch");
        var canonicalId = guid.ToString();
        var bodyType = Text(body, "type");
        if (type == "event" && bodyType is not (null or "error" or "default")) throw new FormatException("Event type mismatch");
        if (type == "transaction" && bodyType != "transaction") throw new FormatException("Transaction type required");
        var error = type == "event" ? Error(body, canonicalId) : null;
        var transaction = type == "transaction" ? Transaction(body, canonicalId) : null;
        if (error is not null && !error.IsValid() || transaction is not null && !transaction.IsValid()) throw new FormatException("Invalid mapped event");
        return new(Text(header, "dsn"), canonicalId, error, transaction, sessions, clientReport);
    }

    private static ErrorBody Error(JsonElement body, string id)
    {
        var exception = Object(body, "exception");
        var values = Array(exception, "values");
        if (values.Length > 10) throw new FormatException("Too many exceptions");
        var last = values.LastOrDefault();
        var frames = Array(Object(last, "stacktrace"), "frames");
        if (frames.Length > 100) throw new FormatException("Too many stack frames");
        var groupingLines = frames.Select(frame => {
            if (Text(frame, "instruction_addr") is not null && string.IsNullOrWhiteSpace(Text(frame, "function")))
                throw new UnsupportedSentryItemException();
            // Retain function names and basenames, never absolute source paths or source code/locals.
            var file = Text(frame, "filename") ?? "";
            file = file.Replace('\\', '/').Split('/').Last().Split('?')[0].Split('#')[0];
            return $"at {Text(frame, "function") ?? "<unknown>"} ({file})";
        }).ToArray();
        var lines = frames.Select((frame, index) => {
            var line = PositiveInteger(frame, "lineno");
            var column = PositiveInteger(frame, "colno");
            return groupingLines[index] + (line is null ? "" : $" line {line}" + (column is null ? "" : $":{column}"));
        });
        var handled = Boolean(Object(last, "mechanism"), "handled");
        return new ErrorBody {
            EventId = id, Timestamp = Time(body, "timestamp", required: false),
            ErrorType = Text(last, "type") ?? "Message",
            ErrorMessage = Text(last, "value") ?? Text(body, "message") ?? Text(Object(body, "logentry"), "formatted") ?? Text(Object(body, "logentry"), "message") ?? "Error",
            StackTrace = string.Join("\n", lines), GroupingStackTrace = string.Join("\n", groupingLines),
            Release = Text(body, "release"), Environment = Text(body, "environment"),
            Platform = Text(body, "platform"), Severity = Text(body, "level") == "fatal" ? "fatal" : "error",
            Kind = handled is false ? "unhandled" : "handled",
            SdkVersion = Text(Object(body, "sdk"), "version"), OsName = Text(Object(Object(body, "contexts"), "os"), "name"),
            OsVersion = Text(Object(Object(body, "contexts"), "os"), "version")
        };
    }

    private static TransactionBody Transaction(JsonElement body, string id)
    {
        var start = Time(body, "start_timestamp", true)!.Value;
        var end = Time(body, "timestamp", true)!.Value;
        var trace = Object(Object(body, "contexts"), "trace");
        var rootId = Text(trace, "span_id");
        var spans = Array(body, "spans");
        if (spans.Length > 100) throw new FormatException("Too many spans");
        return new TransactionBody {
            EventId = id, Timestamp = start, DurationMs = (end - start).TotalMilliseconds,
            Name = Text(body, "transaction"), Operation = Text(trace, "op") ?? "app.operation", Status = Status(trace),
            Release = Text(body, "release"), Environment = Text(body, "environment"), Platform = Text(body, "platform"),
            Spans = spans.Select(span => {
                var spanStart = Time(span, "start_timestamp", true)!.Value;
                var spanEnd = Time(span, "timestamp", true)!.Value;
                var parent = Text(span, "parent_span_id");
                return new TransactionSpan {
                    SpanId = Text(span, "span_id"), ParentSpanId = parent == rootId ? null : parent,
                    Name = Text(span, "op") ?? "app.operation", Operation = Text(span, "op") ?? "app.operation",
                    StartOffsetMs = (spanStart - start).TotalMilliseconds, DurationMs = (spanEnd - spanStart).TotalMilliseconds,
                    Status = Status(span)
                };
            }).ToList()
        };
    }

    private static string Status(JsonElement value) => Text(value, "status") switch { null or "ok" => "ok", "cancelled" => "cancelled", _ => "error" };
    private static bool? Boolean(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Undefined || !obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException("Expected boolean field");
        return value.GetBoolean();
    }
    private static int? PositiveInteger(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw new FormatException("Expected integer location");
        // JVM native/unknown locations can be negative; they provide no source position.
        return number > 0 ? number : null;
    }
    private static JsonElement ParseObject(ReadOnlyMemory<byte> data)
    {
        using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("Expected an object");
        if (document.RootElement.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1)) throw new FormatException("Duplicate properties");
        return document.RootElement.Clone();
    }
    private static ReadOnlyMemory<byte> ReadLine(byte[] data, ref int offset)
    {
        var start = offset;
        while (offset < data.Length && data[offset] != (byte)'\n') offset++;
        var line = data.AsMemory(start, offset - start);
        if (offset < data.Length) offset++;
        return line;
    }
    private static string? Text(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Undefined || !obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new FormatException("Expected string field");
        return value.GetString();
    }
    private static JsonElement Object(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Undefined || !obj.TryGetProperty(name, out var value)) return default;
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException("Expected object field");
        return value;
    }
    private static JsonElement[] Array(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Undefined || !obj.TryGetProperty(name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array) throw new FormatException("Expected array field");
        var result = value.EnumerateArray().ToArray();
        if (result.Any(v => v.ValueKind != JsonValueKind.Object)) throw new FormatException("Expected object array");
        return result;
    }
    private static DateTime? Time(JsonElement obj, string name, bool required)
    {
        if (!obj.TryGetProperty(name, out var value)) return required ? throw new FormatException("Missing timestamp") : null;
        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return date.UtcDateTime;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds) && double.IsFinite(seconds))
        {
            try { return DateTime.UnixEpoch.AddSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { throw new FormatException("Invalid timestamp"); }
        }
        throw new FormatException("Invalid timestamp");
    }
}
