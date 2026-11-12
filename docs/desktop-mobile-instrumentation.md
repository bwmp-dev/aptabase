# Instrument desktop/mobile applications

This collector offers native JSON error/app-performance endpoints and a tested, bounded Sentry envelope subset described in `sentry-compatibility.md`. It is not a universal drop-in Sentry backend. No symbolication, source maps, attachments, crash dumps, session replay or distributed tracing support is implied.

## Collector setup for this fork

Use the repository's existing self-hosted deployment architecture: PostgreSQL plus either ClickHouse or Tinybird, with the built webapp served by the ASP.NET application. Configure `BASE_URL` to the collector's HTTPS origin, `DATABASE_URL`, `AUTH_SECRET` (a unique randomly generated secret, never a published example), and the selected analytics connection. Existing email/OAuth dashboard sign-in configuration still applies. This work does not create external services or configure real email delivery. Do not use the test host or its test-only login endpoints as a deployment.

The normal application migration runner applies PostgreSQL migrations 0015-0021 for monitoring. Back up an existing database before applying migrations in an actual deployment; no production deployment was performed here. Build the webapp using `npm ci` and `npm run build` in `src`, then build/run using the repository's .NET10 tooling and existing packaging instructions. The full local integration suite exercised the actual application startup/migrations with PostgreSQL16 and ClickHouse26.8.36.3; this is not production or physical-device acceptance.

After signing in as an app owner, use Settings > Monitoring to choose capture, raw forwarding, detail retention and optional local alerts. For Sentry SDKs, provision the release/debug DSNs there and follow `sentry-compatibility.md`, including disabling session/client-report/attachment/profile features. Use the native App-Key endpoint contract for other clients. Existing usage analytics remain independent of these switches. Alerts are an on-server inbox only: no external email/webhook delivery is implemented.

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

## JVM and native-toolchain evidence

The real Sentry Java 8.64.0 SDK now has a repeatable Windows/JVM acceptance probe for managed exceptions, actual uncaught process termination and app transactions/spans. See `sdk-native-assessment.md` for its privacy settings and required shutdown flush. This is useful for JVM desktop applications; it is not Android lifecycle/ANR/NDK acceptance. The installed Android NDK also passes an offline ARM64 symbol-resolution probe, but the collector has no symbol-upload or native-dump endpoint. Native/source-map architecture and owner decisions remain open in that assessment.

For Node-based app stacks, `javascript-sdk-probe.md` provides verified Sentry Node10.77.1 settings and an executable probe for handled/uncaught exceptions and manual app transactions. Always use the newly provisioned DSN unchanged; legacy raw Aptabase keys contain hyphens rejected by standard JavaScript SDK parsers. The probe demonstrates local source maps with no embedded source, enabled before loading the application module. This requires shipping the map with that client and does not provide server symbolication or establish Electron/React Native/Hermes/browser support.

## Error reports

Use `POST /api/v0/error` with the App-Key header and JSON containing `eventId`, `errorType`, `errorMessage`, optional `stackTrace`, `timestamp`, `release` (falls back to `appVersion`), `environment`, `platform`, OS/SDK versions, `severity`, `kind`, and `isDebug`. See `error-api-openapi.yaml` for bounds and semantics. Send structured diagnostic labels only; no user/session IDs are needed for issue tracking.

New post-resolution occurrences reopen the issue. Replayed IDs and delayed reports dated before resolution do not. Groups are app/build-wide across releases/environments; those labels filter retained occurrences but do not create independent resolution state. Displayed issue totals remain lifetime totals. Receipt/context retention follows the app setting (1-30 days, default 30); reuse after expiry may count again.

## Performance reports

See `app-performance-api.md` for the complete transaction/span contract. Child spans reference local parent span IDs and use monotonic offsets inside their parent/root durations. Record only important operations; do not create one span per tight-loop iteration or capture unbounded UI events. A transaction with `status=error` contributes to performance error counts but does not itself create an error issue; submit a separate sanitized error report if useful.

## Privacy and retention

Allowlist diagnostic fields at the source. Regex redaction catches common identifiers but cannot guarantee arbitrary exception messages or source paths are free of personal data. Avoid capturing payloads, credentials, headers, cookies and personal filesystem paths. Issue occurrence details and performance samples follow the app retention setting (1-30 days, default 30), with eventual bounded cleanup; issue summaries persist until cleared. The existing raw error store retains its original 180-day TTL. Durable delivery temporarily retains existing raw fields until acknowledgement or the app detail retention deadline, and read APIs deduplicate replayed event IDs. Do not confuse the longer raw-error retention with the shorter idempotency/context window.

## Acceptance boundaries

Native HTTP, PostgreSQL storage, validation, authorization, retry behavior and synthetic performance aggregates are tested locally. Actual Sentry .NET 6.10.0 exception/transaction envelopes are verified through SDK serialization, compression and authentication to a local TestServer/database; Java8.64.0 and Node10.77.1 are verified through a loopback HTTP collector. Managed source line/column positions are retained without altering the previous grouping input. Other SDK/platform versions, native crash handling, offline mobile queues, server source symbolication, physical-device energy overhead and production load are unverified. See `monitoring-roadmap.md` for remaining work.
