# Bounded Sentry SDK compatibility

This collector implements an explicit subset, not the Sentry product or all Sentry SDK features. No GlitchTip or Sentry implementation source was copied. The official [envelope specification](https://develop.sentry.dev/sdk/foundations/envelopes/) informed the protocol adapter. Sentry .NET 6.10.0 is a test-only dependency, not a server runtime dependency.

## Provisioning and authentication

An authenticated app owner calls `POST /api/v0/apps/{appId}/sentry-projects` once (idempotent), or `GET` to inspect existing projects. Shared viewers cannot obtain these DSNs. Response: `{projects:[{id,buildMode,dsn}]}` with separate numeric projects for release and debug. A project binds the app and build mode on the server; payload fields cannot select another app or build mode. DSNs use the configured `BASE_URL` authority and the existing app ingestion key. Host the collector at the origin root. These keys authorize ingestion only, not dashboard reads or management. There is no separate Sentry credential rotation API; rotating an existing app key also invalidates its old DSNs. Do not expose owner cookies in SDKs.

`POST /api/{projectId}/envelope/` accepts a key in the envelope DSN, `X-Sentry-Auth: Sentry sentry_key=...`, or `?sentry_key=...`. When multiple credentials are supplied they must agree, and the DSN project must match the URL. Deleted apps and locked owners cannot ingest. HTTP 200 returns `{id}`; retries with the same event UUID use the native app/build deduplication path. The existing opt-in error quota still applies.

## Supported wire format

- One JSON `event` or `transaction` item per envelope; UTF-8 JSON header lines, explicit byte lengths or newline-delimited payloads, optional final newline. Duplicate header/body properties, invalid types/lengths/IDs and malformed JSON are rejected.
- Identity or gzip HTTP content encoding; 128 KiB HTTP request limit, 256 KiB decompressed limit, JSON nesting at most 32. Invalid envelopes return 400, unsupported item types 422, authentication failures 403, oversized bodies 413, unsupported encoding 415.
- Entire envelope validation precedes all writes. In particular an event plus attachment is rejected as a whole. This is deliberately stricter than Sentry's recommendation to skip unknown item types: it avoids silently accepting unsupported crash data.
- UUID `event_id` in header or event, ISO-8601 or numeric seconds timestamps, release/environment/platform and bounded SDK/OS labels.
- Error message/logentry or the final exception in a chain of at most 10; at most 100 frames. Only function names and file basenames are kept. Absolute source paths, source lines, frame locals, request/response bodies, cookies, users, breadcrumbs, tags and arbitrary extra/context objects are not retained. Retained strings pass through the existing sanitizer. Regex sanitization is not a guarantee that arbitrary prose contains no sensitive data; omit it at the SDK boundary.
- Transactions: name, start/end timestamps, root trace operation/status, at most 100 spans with 16-hex nonzero IDs, local parents, bounded offsets/durations, no cycles. A parent equal to the root span becomes a top-level span. Cross-transaction orphan span parents are rejected. Span names derive from operations; descriptions, URLs, database queries and arbitrary span data are discarded. Status is `ok`, `cancelled`, or `error` for other Sentry statuses. Native duration/name/label bounds also apply.

Unsupported: legacy `/store/`, sessions/release-health, attachments, minidumps, native crash ingestion/symbolication, source maps, debug symbols/PDB/dSYM upload, profiling, replay, logs/metrics envelopes, standalone spans, check-ins, OpenTelemetry/OTLP, and Sentry management APIs. Unsupported routes are not compatibility promises. No affected-user counts or distributed cross-service trace reconstruction.

## SDK configuration and evidence

The integration test uses the real **Sentry .NET 6.10.0 SDK on Windows/.NET 10** with its normal envelope serializer, compression and authentication, routed by an HTTP handler exclusively to ASP.NET TestServer and a disposable PostgreSQL database. It captures an exception and a transaction with a child span, flushes within 10 seconds, checks both HTTP responses and stored rows. This is actual SDK wire-format verification, not physical mobile-device/native-crash verification.

Required settings for that supported subset:

```csharp
SentrySdk.Init(options => {
    options.Dsn = configuredDsn; // obtained by the app owner
    options.SendDefaultPii = false;
    options.AutoSessionTracking = false;
    options.SendClientReports = false;
    options.TracesSampleRate = 0.1; // choose a deliberate app sampling rate
    options.Release = "desktop-app@1.2.0";
    options.Environment = "production";
});
```

Disable attachments, profiling, replay and automatic integrations that emit unsupported items. Do not attach files or native crash dumps. Apply a before-send policy for sensitive free text and high-cardinality transaction names. Keep spans within their transaction and use stable operation names. Configure offline queues and retry bounds deliberately; deduplication follows the app retention setting (1-30 days from receipt, default 30); expiry and explicit cleanup remove that history. Owner capture switches also apply to this adapter.

Passed: parser unit tests; HTTP tests for retries, credential mismatch, unsupported secondary items with no partial writes, invalid length types, gzip, deleted app, project provisioning idempotency; actual SDK error/transaction ingestion. Unrun: Android, iOS, MAUI/Unity native integrations, Rust, JavaScript and other SDK versions, physical devices, release builds with trimming/AOT, reverse-proxy deployment. These require platform-specific acceptance before claiming support.
