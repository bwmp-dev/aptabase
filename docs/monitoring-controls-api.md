# Owner monitoring controls and local alerts

All routes below require dashboard authentication and current app ownership (shared viewers get 403, anonymous users 401). Existing analytics permissions are unchanged. Errors and transactions share app-wide capture policy across debug/release; lists remain build-scoped.

## Settings

`GET /api/v0/apps/{appId}/monitoring/settings` returns the object below. `PUT` replaces the full object; all six properties are required. Invalid bounds/missing fields =>400, success=>200 with settings.

```json
{"captureErrors":true,"capturePerformance":true,"forwardRawErrors":true,"monitoringRetentionDays":30,"localAlertsEnabled":false,"alertCooldownMinutes":60}
```

Retention is an integer 1..30 days from server receipt for PostgreSQL error events/receipt deduplication, transactions, sessions and client reports. Sessions require error capture; client reports require error or performance capture. Cooldown is an integer 1..1440 minutes per issue/build. Defaults preserve existing capture and raw forwarding; alerts are opt-in. Changes are enforced in the database ingestion transaction for native and Sentry events, without relying on the cached App-Key metadata. Disabling capture rejects new requests, including retries, without quota/count changes. A settings mutation waits for in-flight writes; later ingestion sees the new policy. Existing analytics remain enabled independently.

Turning off raw forwarding prevents new error outbox entries; the worker discards pending entries for disabled forwarding/deleted apps. A forwarding batch already holding the app lock may finish before the policy update succeeds. Previously delivered raw logs stay in ClickHouse/Tinybird under the existing 180-day TTL. This setting is not retroactive erasure. New retained error/performance text redacts HTTP(S) URLs entirely and explicit short password/token/key or Bearer/Basic values, in addition to the existing PII patterns. Stored metadata/operation labels are bounded again after redaction so replacement markers cannot exceed dashboard filter limits; no unsanitized suffix is retained. This can change grouping for newly received messages/stacks containing such text; historical records are not rewritten. Sanitization remains a best-effort safeguard, not a substitute for SDK-side minimization. Raw logs retain the legacy sessionId if supplied; native detailed monitoring and Sentry mapping do not. Prefer disabling raw forwarding for a new privacy-focused deployment that needs only issue/error/performance monitoring.

Cleanup jobs run every 5 minutes in bounded 10000-row batches; expiry is eventual under backlog. Undelivered raw outbox payloads also expire after the app detail retention window, even when the raw sink is unavailable; this bounds retry storage and can lose raw deliveries beyond that window. Issue summaries are lifetime aggregates and persist until explicit purge, including sanitized representative text. Alert inbox/state expiry is 30 days. Reducing retention shortens the retry-deduplication window; after expiry, an old retried event ID can count again. Re-enabling capture after a purge also starts a new deduplication history.

## Preview and clear PostgreSQL monitoring data

`GET /api/v0/apps/{appId}/monitoring/data` returns `{counts:{issues,events,transactions,queued,alerts,symbols,sessions,clientReports},excludesDeliveredRawLogs:true}` across both build modes. Counts are a preview, not a reservation. Purge also deletes sessions/client reports and rotates their private per-app session hashing salt; the next accepted session starts a new identity history.

`DELETE` on the same route with JSON `{confirmAppId:"exact-app-id"}` clears those PostgreSQL rows, private native symbol artifacts/indices and alert cooldown state transactionally. It requires all three capture/raw-forwarding booleans disabled first (otherwise409); mismatched confirmation=>400, success=>204. Use a visible confirmation with preview counts and an explicit warning that delivered raw logs and analytics are not erased. This is local monitoring cleanup, not a full personal-data deletion/export API. Physical app deletion cascades its monitoring rows; soft-deleted apps cannot ingest/read and bounded cleanup removes their PostgreSQL monitoring records, including summaries, pending raw payloads, local alerts and Sentry project bindings. Delivered raw logs retain their separate TTL.

## Local alert inbox

When enabled, newly grouped issues and automatic post-resolution regressions create an inbox entry in the same transaction as ingestion. A duplicate retry produces no alert; explicit/manual reopen does not trigger an alert. The per-issue/build cooldown starts at the last alert of any kind, including the new-issue alert. A regression within that interval is suppressed rather than queued for later delivery; this deliberately avoids two rapid alerts for the same issue. After the interval, a new eligible recurrence may create a regression alert. No backlog is synthesized when enabled. A changed environment/release is not by itself a regression; the issue grouping/reopen policy remains app/build-wide.

Delivery is the durable local PostgreSQL inbox itself, with no email, webhooks, external networking, destination secrets or background external sender. A failed ingestion transaction rolls back the alert too; a successful retry cannot duplicate it. Threshold/rate alerts and external delivery are deferred until channel/volume requirements are agreed.

`GET /api/v0/apps/{appId}/monitoring/alerts?buildMode=release|debug&offset=0&limit=50` returns `{alerts:[{eventId,issueId,kind,createdAt,readAt}],pagination:{offset,limit,total}}`, newest first. Kind is `new_issue` or `regression`, timestamps are UTC, readAt nullable. No free-text error message is copied into alerts. Limit 1..100, offset>=0; invalid query=>400. `PATCH /api/v0/apps/{appId}/monitoring/alerts/{eventId}/read?buildMode=release` marks read idempotently with204; missing/wrongbuild=>404. The existing issue detail can be opened using issueId. Read does not resolve the issue.

## UI acceptance

Owner-only settings surface integrated with existing app settings or monitoring navigation; preserve non-owner read-only issue/performance UI. Full settings form with pending/errors/success, bounds, explanation of retention/raw limits; Sentry DSN provision/view can use `sentry-compatibility.md`. Local alert list with pagination, unread/new/regression text, mark-read, issue detail navigation, build reset, empty/loading/error states. Clear-data action must fetch preview, require exact app ID, explain raw exclusion, and only enable after capture is disabled. Test owner/read-only, failures, validation, app/build switches, keyboard and narrow overflow. No external channels or fabricated data.
