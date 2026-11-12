# Bounded Sentry SDK compatibility

This collector implements an explicit subset, not the Sentry product or all Sentry SDK features. No GlitchTip or Sentry implementation source was copied. The official [envelope specification](https://develop.sentry.dev/sdk/foundations/envelopes/) informed the protocol adapter. The .NET, Java and Node SDK dependencies below are test-only, not collector runtime dependencies.

## Provisioning and authentication

An authenticated app owner calls `POST /api/v0/apps/{appId}/sentry-projects` once (idempotent), or `GET` to inspect existing projects. Shared viewers cannot obtain these DSNs. Response: `{projects:[{id,buildMode,dsn}]}` with separate numeric projects for release and debug. A project binds the app and build mode on the server; payload fields cannot select another app or build mode. DSNs use the configured `BASE_URL` authority and a 64-character lowercase hexadecimal ingestion key derived with HMAC-SHA256 from the current Aptabase app key and versioned app/project/build context. Use the returned DSN unchanged: raw Aptabase keys contain hyphens rejected by standard JavaScript SDK DSN parsers. Host the collector at the origin root. Derived keys authorize Sentry-envelope ingestion only, not native App-Key ingestion, dashboard reads or management. There is no additional stored credential, schema migration, independent Sentry-key rotation API or added entropy claim; rotating the underlying app key invalidates both its derived and legacy representations. Do not expose owner cookies in SDKs.

`POST /api/{projectId}/envelope/` accepts a key in the envelope DSN, `X-Sentry-Auth: Sentry sentry_key=...`, or `?sentry_key=...`. Legacy raw-app-key DSNs/headers/query keys remain valid for SDKs that already accepted them. When multiple credentials are supplied their strings must agree exactly: do not mix the raw and derived forms in one request. The DSN project must match the URL. A derived release-project key does not authorize the debug project or another app; the legacy raw key preserves its original same-app project authorization. Deleted apps and locked owners cannot ingest. HTTP 200 returns `{id}`; retries with the same event UUID use the native app/build deduplication path. The existing opt-in error quota still applies. Existing clients need no migration, but JavaScript clients must obtain the newly returned DSN rather than bypass their SDK parser.

## Supported wire format

- One JSON `event` or `transaction` item per envelope; UTF-8 JSON header lines, explicit byte lengths or newline-delimited payloads, optional final newline. Duplicate header/body properties, invalid types/lengths/IDs and malformed JSON are rejected.
- Identity or gzip HTTP content encoding; 128 KiB HTTP request limit, 256 KiB decompressed limit, JSON nesting at most 32. Invalid envelopes return 400, unsupported item types 422, authentication failures 403, oversized bodies 413, unsupported encoding 415.
- Entire envelope validation precedes all writes. In particular an event plus attachment is rejected as a whole. This is deliberately stricter than Sentry's recommendation to skip unknown item types: it avoids silently accepting unsupported crash data.
- UUID `event_id` in header or event, ISO-8601 or numeric seconds timestamps, release/environment/platform and bounded SDK/OS labels.
- Error message/logentry or the final exception in a chain of at most 10; at most 100 frames. Function names, file basenames and positive integer line/column positions are kept. Positions do not change issue grouping. Absolute source paths, source code, frame locals, request/response bodies, cookies, users, breadcrumbs, tags and arbitrary extra/context objects are not retained. Retained strings pass through the existing sanitizer. Regex sanitization is not a guarantee that arbitrary prose contains no sensitive data; omit it at the SDK boundary. Explicit `mechanism.handled=false` maps to `unhandled`; missing/null/true defaults to `handled`. This does not imply native crash support. Unresolved address-bearing frames without function names return 422 before writes.
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

Actual **Sentry Java 8.64.0 on Windows/Java 25** is also verified against the disposable loopback collector/PostgreSQL using normal SDK serialization, compression, authentication and HTTP transport. The executable probe covers a caught exception, an actual terminating uncaught exception (fatal/unhandled), and a transaction/child span. It verifies stored metadata, source positions, redaction and discarded arbitrary context. Both uncaught handling and shutdown flush must remain enabled for the tested desktop path. See `sdk-native-assessment.md` and `tests/SdkProbes/Java` for reproducible settings, explicit data-collection exclusions, and platform boundaries.

Actual **Sentry Node 10.77.1 on Windows/Node 24.19.0** is verified with the ordinary owner-returned DSN string and SDK transport, without a parser bypass. Acceptance covers handled and actual uncaught exceptions, transaction/child span, privacy assertions and local TypeScript source-map enrichment (external map, no embedded source). Source mapping is performed by Node on the client before capture; no server artifact upload or symbolication is provided. See `javascript-sdk-probe.md` for exact configuration and network guards. JavaScript SDKs cannot parse the legacy hyphenated-key DSNs; obtain the new DSN through owner settings/API. The server preserves legacy DSN authentication for existing compatible SDK clients.

Passed: parser unit tests; HTTP tests for old/new DSNs, rotation, credential mismatch, app/build isolation, unsupported secondary items/unresolved native frames with no partial writes, invalid field types, gzip, deleted app, project provisioning idempotency; actual .NET/JVM/Node SDK error/transaction ingestion. Unrun: Android, iOS, MAUI/Unity native integrations, Rust, Electron/browser/React Native/Hermes, other SDK versions/platforms, physical devices, release builds with trimming/AOT, reverse-proxy deployment. Node gzip over32KiB, offline queues and promise-rejection capture are not covered by its probe. These require platform-specific acceptance before claiming support. The offline NDK symbolizer probe verifies tooling only and does not enable symbol uploads or native crash intake.
