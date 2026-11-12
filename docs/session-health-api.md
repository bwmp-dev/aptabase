# Session health and SDK delivery reports

Implementation contract for the approved MultiShock/Sentry.Maui6.8 increment. This file defines the backend/frontend boundary before delegation; acceptance evidence will be added after tests. No full Sentry compatibility or actual app-runtime acceptance is implied.

## Ingestion and privacy

The existing project-scoped Sentry envelope endpoint accepts at most32 items: at most one `event` or `transaction`, up to30 individual `session` updates and at most one `client_report`. Metadata-only envelopes are allowed. All items are validated before one PostgreSQL transaction; rejected/over-quota/failed bundles write nothing, so an acknowledged error cannot be separated from its session/report updates. Existing request/decoded limits remain128/256KiB; individual session/report JSON limits are8/16KiB. Attachments, aggregate `sessions`, profiles, replays, minidumps and unknown item types remain rejected with422.

Session capture follows the existing error-capture switch. Client reports require at least error or performance capture enabled. A mixed bundle must satisfy every applicable capture policy. No new delivery service or external alert channel is introduced.

Session IDs are used only in memory then HMAC-hashed with a private random per-app salt and build-mode context; neither raw IDs nor hashes appear in dashboard responses. Distinct user/device IDs, IPs, user agents and arbitrary context are discarded. The salt survives app-key rotation/backup and rotates on monitoring purge. Retained fields: start/latest update, maximum sequence/error count/duration, observed init, status, sanitized release/environment and first receipt time. Replays do not add sessions/errors; late init fills coverage; terminal states never reopen, and crash evidence cannot be downgraded. Status precedence is `crashed > abnormal > exited > ok`, independently of arrival order or sequence; an abnormal session is conservatively incomplete even if an exited update later arrives. Conflicting start/release/environment for one scoped ID is rejected. Unknown/malformed statuses fail validation.

Client reports retain timestamp, bounded known reason/category counters and optional sanitized release/environment from the accompanying event/transaction. Unknown reason/category strings become `other` instead of retaining arbitrary text. No report/user/session identifier is exposed. A canonical hash includes the accompanying event/transaction UUID (when present), report timestamp and normalized counters. Replaying that combination is deduplicated within app/build retention; identical reports attached to different event UUIDs are distinct. Metadata-only reports have no stable SDK report ID: identical timestamp/counter payloads from different clients cannot be distinguished without extra identity, so counts may undercount them. Unknown labels normalized to `other` can also coalesce. These limits are explicit; no user/device identifier is added to disambiguate reports. These are SDK-reported discarded events, not collector rejection counts or a total-traffic denominator.

Retained session and report rows each have an app-wide10,000row cap across build modes, enforced under the app lock. Existing updates/retries do not consume another row. Exhaustion returns503 with Retry-After; no partial error acknowledgment. Both use the existing1–30day app retention from first receipt (replays do not extend it), and are removed by app deletion/monitoring purge. Summary reads exclude expired rows immediately. No crash-free user metric is offered.

## Dashboard contract

`GET /api/v0/apps/{appId}/monitoring/health`

Uses the existing Aptabase authenticated session and app owner/shared-reader permissions: anonymous401, unrelated account403. No ingestion key grants read access. Query: `buildMode=release|debug`, `startDate`, `endDate` (ISO UTC, at most31days; default last7days), optional `release` (max100), `environment` (max50). Invalid filters400. Server errors use existing retry behavior; no user mutation exists here.

```json
{
  "sessions": {
    "observed": 3, "withStart": 3, "eligible": 2,
    "crashed": 1, "eligibleCrashed": 1, "abnormal": 0,
    "active": 1, "errored": 1, "incomplete": 1, "crashFreeRate": 50.0
  },
  "groups": [
    {
      "release": "app@1", "environment": "synthetic",
      "observed": 3, "withStart": 3, "eligible": 2,
      "crashed": 1, "eligibleCrashed": 1, "abnormal": 0,
      "active": 1, "errored": 1, "incomplete": 1, "crashFreeRate": 50.0
    }
  ],
  "groupsTruncated": false,
  "clientReports": {
    "reports": 2, "discardedEvents": 17,
    "groups": [
      { "reason": "before_send", "category": "error", "quantity": 5 },
      { "reason": "queue_overflow", "category": "error", "quantity": 12 }
    ]
  },
  "retentionDays": 30
}
```

Sessions are selected by start time. `eligible` includes only sessions with an observed init and an `exited` or `crashed` terminal status. `crashFreeRate` is `(eligible - eligibleCrashed) / eligible * 100`, or **null** if eligible=0. Label it **Crash-free completed sessions**, not global crash-free usage. Active, abnormal, missing-init and lost-end sessions are incomplete and excluded; show coverage counts alongside the rate. `crashed` counts all observed crashed sessions; `eligibleCrashed` is its eligible subset. `errored` counts sessions with cumulative errors>0. Groups are by release/environment, maximum100, with truncation explicitly signalled; totals include all matching rows.

Client reports are selected by report timestamp. Release/environment filters match only known accompanying-event context; metadata-only reports have empty context. The UI must state this limitation when filtering. Display quantities, not a loss percentage; categories such as `attachment` describe client discard counters, not collector support for those payloads.

The read-only Session health view is available at `/:appId/health` inside the existing app console/navigation, with24h/7d/30d, build/release/environment filters, coverage/null-denominator, empty/error/retry and responsive states. Shared readers can view it without requesting owner-only monitoring settings. Settings error-capture copy must mention sessions; clearing monitoring data includes sessions/client reports. Existing purge-preview `counts` gains numeric `sessions` and `clientReports` fields; all previous fields remain. No raw session list or identifiers are needed.

## Acceptance and remaining boundaries

Backend acceptance passes335 unit tests and79 full sequential integration tests, including real PostgreSQL lifecycle/replay/order, report collisions/concurrent retries, app-key rotation, partial invalid batches, transaction/error atomic rollback, cap/retention/purge,100-group truncation and app/build/read authorization. The final full run includes synthetic22-table dump/restore and isolated native-worker checks, with no skips. Real core SDK6.8 serializers and cache behavior are covered separately by9 tests.

Opus5.5high frontend acceptance passes114 units, typecheck/build,46 mocked and40 real-backend headless tests, all single-worker. The real suite covers mixed envelopes,50% completed-session rate, missing denominator, session-only release filters, build-mode reset and read authorization on desktop/mobile. The real browser tests construct synthetic envelopes; SDK-generated wire format is a separate non-UI check. Parent reviewed saved desktop/mobile screenshots without opening a browser. No whole-app MultiShock, automatic WinUI hook or process-restart durability claim follows from these tests.

Review fixed report deduplication across distinct accompanying event UUIDs and a linked release filter reapplying after a Session health build-mode switch. The analogous pre-existing Performance link-filter behavior was not changed in this scoped increment. Unknown/aggregate/binary envelope types remain rejected. Actual MAUI default-session lifecycle, unhandled hooks, shutdown/replay and production recovery/cutover are still open in `glitchtip-retirement-readiness.md`. The manually approved harness remains unchanged and automatic launch remains denied.

SDK6.8 acceptance nuance: `send_error` counters can describe HTTP500/503 losses, but the tested429 path does not report its losses. These aggregates are not a complete server-rejection ledger or loss denominator. `BeforeSend => null` does not suppress session/client-report emission. The separate [delivery/consent tests](sentry68-delivery-acceptance.md) document these limits and real-collector mixed-envelope replay evidence.
