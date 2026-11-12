# Desktop/mobile app performance API

Native self-hosted app-performance API. The separately documented Sentry adapter accepts a bounded transaction subset; see `sentry-compatibility.md`. All event/transaction data is scoped to the authenticated App-Key. No cookies, user IDs, request bodies or arbitrary context are retained. Use stable operation names, not paths, usernames or URLs; regex redaction is defense in depth, not a guarantee that arbitrary text contains no personal information.

## Collect

`POST /api/v0/transaction`, JSON, `App-Key` header. Maximum request body 128 KiB. Returns 202 for a valid new transaction or duplicate. Invalid payload 400, unknown key 401, rate limit 429. Authentication runs before parsing. No error-quota charges are added for performance events.

Fields:
- `eventId`: required nonzero UUID, 32 hex or hyphenated. Generate once per operation and retain across retries. First accepted payload wins per app/build.
- `name`: required stable operation name, <=200 characters, e.g. `app.startup` or `screen.open`.
- `operation`: required category, <=50 characters, e.g. `ui.load`, `db.query` or `file.read`.
- `timestamp`: required UTC ISO 8601 operation start. Future dates are clamped to collector time.
- `durationMs`: finite monotonic duration, 0..86400000 (24 hours).
- `status`: `ok` (default), `error`, or `cancelled`.
- `release`, `environment`, `platform`: optional labels bounded at 100, 50, 30 characters. Empty string when omitted. `isDebug`: false by default, separate from environment.
- `spans`: optional array (default empty), at most 100. Each has `spanId` (nonzero 16 lowercase hex), optional `parentSpanId` referencing another span in this transaction, required bounded `name`/`operation`, `startOffsetMs`, `durationMs`, and `status` as above. IDs must be unique, relationships acyclic, spans within transaction and parent bounds. No external-parent/distributed-trace semantics in this subset.

Transactions and spans use the app retention setting (1-30 days, default 30) from reception, then are purged in bounded batches. Idempotency expires with that record. PostgreSQL storage is for modest self-hosted volume; benchmark against actual traffic before increasing collection rates.

## Read

Authenticated owner/shared-reader endpoints:
- `GET /api/v0/apps/{appId}/transactions`: `{transactions: Transaction[], pagination:{offset,limit,total}}`, newest first.
- `GET /api/v0/apps/{appId}/performance/summary`: `{groups:[{name,operation,count,errorCount,avgMs,p50Ms,p95Ms}]}`, top 100 groups ordered by count then name/operation. Counts reflect collected samples, not extrapolated total traffic. Error count includes status `error` only.
- `GET /api/v0/apps/{appId}/transactions/{eventId}?buildMode=release|debug`: `{transaction:Transaction,spans:Span[]}`.

Transaction has `eventId`, `timestamp`, `name`, `operation`, `durationMs`, `status`, `release`, `environment`, `platform`. No app/user IDs in the response payload. Dates are ISO UTC.

List and summary accept `buildMode` (release default), `startDate`/`endDate` (default last seven days, maximum interval 31 days), exact `environment`, `release`, `name`, `operation`. List adds `offset>=0`, `limit=1..100` (default 50). Invalid queries return 400; unauthenticated 401; denied app 403; absent detail 404. Endpoints preserve existing authentication, no public sharing.

## Instrumentation and sampling

Sample before collecting spans, for example keep 10% of operations using a random choice per root operation. On .NET use `Stopwatch` for durations, `DateTime.UtcNow` for the start timestamp, and `Guid.NewGuid()` for the event ID. Do not calculate durations by subtracting wall-clock timestamps. Keep the same event ID/body through offline retries; retry 429 and transient transport/5xx failures with bounded backoff, drop invalid/auth failures after surfacing diagnostics locally. Persist queued reports with a bounded local retention policy appropriate to the app. Disable capture of PII at source.

Example payload (sample data):
```json
{"eventId":"a328eb5ef1fc4fa582221e24881369f0","name":"app.startup","operation":"ui.load","timestamp":"2026-11-12T00:00:00Z","durationMs":120,"status":"ok","release":"1.0.0","environment":"production","platform":"desktop","spans":[{"spanId":"1111111111111111","name":"load.preferences","operation":"file.read","startOffsetMs":5,"durationMs":15,"status":"ok"}]}
```

An error report uses the existing `/api/v0/error` endpoint with its own persistent `eventId`. A transaction's status is not an error report and does not create an issue. App performance and existing Aptabase usage analytics remain independent.
