# Instrument desktop/mobile applications

This collector currently offers native JSON error and app-performance endpoints. It is not yet a drop-in Sentry backend. No symbolication, source maps, attachments, crash dumps, session replay or distributed tracing support is implied.

## Start safely

1. Create an app using existing Aptabase app management. Configure its collector base URL and App-Key in your app. Use HTTPS outside loopback development. The ingestion key identifies the app; dashboard login remains separate.
2. Choose stable operation names such as `app.startup`, `screen.open`, `file.read` and `sync.refresh`. Never interpolate usernames, filenames, SQL text, URLs or account identifiers into operation names.
3. Sample root performance operations before recording spans (for example 10%, subject to your volume budget). Preserve all child spans for a chosen root within the 100-span cap. Counts on the dashboard are samples, not estimates of total traffic.
4. Use a monotonic clock (`Stopwatch` on .NET) for durations and offsets. Use UTC wall time only for the start timestamp. Ensure collector/database clocks are synchronized; error recurrence compares event time to resolution time.
5. Generate one random event UUID per occurrence/root operation. Persist the same ID and payload for offline retries. Never generate a new ID per retry or reuse an ID across unrelated reports.
6. Queue only sanitized reports with bounded size/age in your app. Keep network reporting off the UI thread. Apply backoff for 429/transient transport/5xx; do not endlessly retry malformed/authentication failures. Do not emit new monitoring errors when monitoring itself fails.

## .NET example

`samples/DotNetMonitoring` compiles without third-party dependencies. It measures a clearly labeled sample workload and prints the JSON payload by default:

```powershell
dotnet run --project samples/DotNetMonitoring
```

The sample sends nothing unless `--send` is supplied. For an explicitly authorized local collector test, set `APTABASE_COLLECTOR_URL` and `APTABASE_APP_KEY` in the process environment, then run with `--send`. Do not commit or print the key. This example demonstrates stable retry identity and bounded backoff; it is not a packaged mobile SDK or a durable device-side queue. No physical desktop/mobile device acceptance has been performed.

## Error reports

Use `POST /api/v0/error` with the App-Key header and JSON containing `eventId`, `errorType`, `errorMessage`, optional `stackTrace`, `timestamp`, `release` (falls back to `appVersion`), `environment`, `platform`, OS/SDK versions, `severity`, `kind`, and `isDebug`. See `error-api-openapi.yaml` for bounds and semantics. Send structured diagnostic labels only; no user/session IDs are needed for issue tracking.

New post-resolution occurrences reopen the issue. Replayed IDs and delayed reports dated before resolution do not. Groups are app/build-wide across releases/environments; those labels filter retained occurrences but do not create independent resolution state. Displayed issue totals remain lifetime totals. Receipt/context retention is at least 30 days; reuse after expiry may count again.

## Performance reports

See `app-performance-api.md` for the complete transaction/span contract. Child spans reference local parent span IDs and use monotonic offsets inside their parent/root durations. Record only important operations; do not create one span per tight-loop iteration or capture unbounded UI events. A transaction with `status=error` contributes to performance error counts but does not itself create an error issue; submit a separate sanitized error report if useful.

## Privacy and retention

Allowlist diagnostic fields at the source. Regex redaction catches common identifiers but cannot guarantee arbitrary exception messages or source paths are free of personal data. Avoid capturing payloads, credentials, headers, cookies and personal filesystem paths. Issue occurrence details and performance samples are retained for at least 30 days; issue summaries persist. The existing raw error store retains its original 180-day TTL. Durable delivery temporarily retains existing raw fields until acknowledgement, and read APIs deduplicate replayed event IDs. Do not confuse the longer raw-error retention with the shorter idempotency/context window.

## Acceptance boundaries

Native HTTP, PostgreSQL storage, validation, authorization, retry behavior and synthetic performance aggregates are tested locally. Real SDK-generated Sentry envelopes, native crash handling, offline mobile queues, source symbolication, device energy overhead and production load are not yet accepted. See `monitoring-roadmap.md` for remaining work.
