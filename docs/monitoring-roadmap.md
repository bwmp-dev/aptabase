# Aptabase monitoring roadmap

Status: active local implementation on `feat/error-issues`. Owner-confirmed target: self-hosted desktop/mobile application errors and app performance. Website uptime, hosted SaaS expansion and billing features are out of scope. This is not a promise of full GlitchTip or Sentry parity. Existing Aptabase analytics, authentication, app ownership/sharing, ClickHouse and Tinybird support remain intact.

## Assessment (2026-11-11)

GlitchTip's official guides describe grouped errors with SDK ingestion and alerts, source maps, transactions/spans with latency aggregates, and HTTP or heartbeat uptime checks. This fork already has raw error ingestion, sanitization, quotas, debug separation and analytics. The first slice adds PostgreSQL issue groups and owner-only lifecycle controls. It has no Sentry envelope intake, release catalog, event context browser, alert rules, monitors or tracing store. PostgreSQL is appropriate for control state and low-volume issue aggregates; analytics/time-series storage must continue supporting both existing backends.

Sources consulted (feature descriptions only; no source copied):
- https://glitchtip.com/documentation/error-tracking/
- https://glitchtip.com/documentation/performance/
- https://glitchtip.com/documentation/uptime-monitoring/
- https://glitchtip.com/documentation/integrations/
- https://develop.sentry.dev/sdk/foundations/envelopes/
- https://gitlab.com/glitchtip/glitchtip-backend/-/raw/master/LICENSE (MIT, copyright 2019 GlitchTip; inspected before considering reuse)

The fork's LICENSE and attribution remain unchanged. Any future copied source requires reviewing that exact component/version's license and retaining required notices. No GlitchTip source is currently incorporated.

## Phases and acceptance checklist

### 0. Existing slice (complete)
- [x] App/build-scoped deterministic grouping, list/detail, owner resolve/reopen, shared read access.
- [x] Preserve raw logs and analytics; sanitized representative issue data only.
- [x] Backend unit/isolated PostgreSQL HTTP tests; frontend unit tests/typecheck/build.

### 1. Reliability and acceptance (in progress)
- [x] Optional UUID eventId: retries counted and quota-charged once per app/build within a documented retention window. Concurrent retries, independent apps/builds, invalid IDs and exhausted quota tested.
- [x] Receipt, quota and issue update commit atomically; failures roll back. No SDK changes required for legacy senders, whose requests remain distinct.
- [x] Auto-reopen a resolved issue only for a newly accepted report whose normalized event timestamp is after resolvedAt. Delayed pre-resolution reports and duplicate retries must not reopen it. Explicit owner reopen remains supported.
- [x] Literal bounded issue search with scoped pagination and query validation.
- [x] HTTP ingestion through real filters/controllers to PostgreSQL; owner/shared/stranger, malformed bodies, lifecycle, concurrent ingestion and rollback.
- [x] Nonintrusive headless desktop/mobile visual and keyboard checks; mocked-network and real HTTP/database suites reported separately.
- [x] Durable PostgreSQL outbox committed with issue/receipt/quota; bounded retry batches and deduplicated raw reads for ClickHouse/Tinybird. Transport remains at-least-once; no exactly-once network claim.

### 2. Triage context, environments and releases
- [x] Backend bounded sanitized event context/detail, latest/recent occurrences linked to issue; no request bodies, cookies, headers, user IDs or session identities by default.
- [x] Backend explicit environment and release labels; preserve debug/release build mode as a separate dimension. Issue filters: status, literal search, environment and release. Raw logs retain their existing type/severity/time filters; performance has time/name/operation filters.
- [x] Backend observed release/environment catalog from retained errors/transactions.
- [x] Release/context frontend and linked filtering tested through real HTTP/PostgreSQL. Deeper release regression comparisons are deferred; observed labels are not deployment records.
- [x] Acceptance: same fingerprint across releases remains one issue; environment filter semantics documented; access checks on every detail/query; context sizes and retained fields bounded. Release/occurrence links covered by headless tests.

### 3. Sentry SDK intake subset
- [x] Owner-provisioned app/build DSNs with explicit project/key binding. Reuse existing ingestion keys; separate credential rotation is outside this subset.
- [x] Strict bounded envelope parser and event/exception mapping into the same validated ingestion service; event_id reuse reaches the same deduplication path.
- [x] Test real SDK-generated fixtures, framing/length/compression limits, auth mismatches, duplicate events and unsupported item types.
- [x] Publish a tested compatibility matrix (`sentry-compatibility.md`). Initial subset excludes attachments, minidumps, replay, profiles, source-map uploads/symbolication and Sentry management APIs. Do not advertise universal Sentry compatibility.

### 4. Privacy and retention
- [x] Owner capture switches enforced transactionally on both native/Sentry ingestion; no cached-policy bypass.
- [x] Owner 1..30-day detail/transaction retention, bounded expiry, app/build isolation, disabled-capture prerequisite for confirmed PostgreSQL monitoring purge with preview counts.
- [x] Raw-forwarding opt-out and pending outbox suppression. Both analytics backends retain their existing 180-day raw TTL; retroactive raw-log erasure is explicitly excluded from PostgreSQL purge. Issue summaries persist until explicit purge.
- [x] Allowlisted Sentry context discards request/user/extra/local variables, absolute source paths and span descriptions. No new affected-user identifiers. Existing sanitizer tests cover common PII; arbitrary free text still requires SDK-side minimization.
- [x] Acceptance: tenant retention/purge isolation, missing settings/invalid bounds, shared-reader denial, soft deletion intake denial, physical-delete cascades, migration down/up and retry window semantics tested/documented.
- [ ] Owner controls frontend and headless acceptance (Opus queued).

### 5. Local app error alerts
- [x] Owner opt-in new-issue/regression alerts with per-issue/build cooldown and disable switch. Native/Sentry issue grouping/reopen semantics apply; no synthetic backlog on enable.
- [x] Transactional durable local inbox, read state, 30-day expiry, bounded owner-only list, retry deduplication; no error free text copied into alerts.
- [x] Acceptance: concurrent duplicate reports, cooldown suppression, eligible regression, disabled rules, read idempotency, wrong-build access, retention and cross-app cleanup tested locally. No external delivery invoked.
- [ ] Owner inbox/configuration frontend and headless acceptance (Opus queued).

Rate/threshold rules, external delivery queues/backoff and channel configuration are deferred pending owner channel/volume requirements. The implemented delivery target is the local inbox, not an email/webhook sender. No external integration is silently simulated or advertised.

### 6. App performance and instrumentation (priority after reliability/context)
- [x] Bounded sampled transaction/span ingestion for desktop/mobile operations (startup, navigation, rendering, local work and app network calls); no automatic unsampled collection.
- [x] Self-hosted PostgreSQL storage first, with 30-day event retention and indexed app/build/time queries. Preserve existing ClickHouse/Tinybird analytics unchanged. Reassess storage against measured volume before claiming scale.
- [x] App-scoped latency summaries (count, errors, p50/p95) and transaction/span details, filtered by environment/release/time. No request payloads, user identifiers or full URLs retained by default.
- [x] Backend acceptance: duration/span/count/name bounds, retry deduplication, malformed parent relationships, tenant access and percentile fixtures tested.
- [x] Desktop/mobile instrumentation docs and examples explaining stable operation names, sampling, monotonic durations, offline retry IDs and privacy boundaries.
- [x] Headless frontend-to-backend tests and narrow/desktop visual checks with disposable local data.

## Owner decisions and scope boundaries

Confirmed: self-hosted desktop/mobile app error tracking and performance; not hosted SaaS or website uptime. Preserve existing app authorization and quotas, but do not expand billing or SaaS features. No uptime monitors or public status pages are planned.

Implemented defaults: owner-configurable 1..30-day detailed event/transaction retention (30 by default); issue summaries persist until explicitly deleted. App/build scopes remain authorization-neutral grouping boundaries. Releases/environments label occurrences; a post-resolution occurrence in any environment can reopen the group. Document this explicitly before exposing filters.

Remaining input useful but not blocking independent work:
- First desktop/mobile SDK languages to prioritize; initial examples can cover .NET and generic HTTP. Sentry compatibility remains a tested subset, not universal.
- Expected event/trace volumes and disk budget; local PostgreSQL is the initial self-hosted choice, not a scalability claim.
- Whether alert delivery is wanted and which channels; rule processing can be tested locally, but real delivery requires explicit authorization.
- Source maps/native symbolication, attachments and crash dumps remain separate large projects and are not included in this initial scope.

## Working policy

Backend: GPT-6 Astra high. All frontend code: Claude Opus 5.5 high via T3. Local commits only; no pushes, PRs, deployments, visible windows or focus changes. Run one bounded test/build job at a time. Any headless listener must bind loopback and be stopped after tests. No real alerts, external services or permission changes.

## Verification ledger

Baseline: 232 backend unit tests and one PostgreSQL/HTTP scenario passed; 14 frontend tests, TypeScript and Vite build passed. Full historical integration suite and browser-to-real-backend checks were not run. Existing NuGet vulnerability warnings and React Doctor findings remain outside monitoring scope. Record subsequent exact commands/results here as phases progress; unchecked items are not complete.

### Reliability increment

- Passed: `dotnet test tests/UnitTests --no-restore -m:1` (240 tests).
- Passed: `dotnet test tests/IntegrationTests --no-restore --filter "FullyQualifiedName~IssueIngestionTests|FullyQualifiedName~IssueTrackingTests" -m:1` (2 scenarios), with APTABASE_ISSUE_TEST_DATABASE_URL pointing to a disposable loopback PostgreSQL container limited to 0.5 CPU/256 MB. Tests create/drop their own databases.
- Covered: real ingestion auth filter/body validation/controller/database; concurrent retry deduplication, quota exhaustion/rollback, cross-app/build IDs, sanitizer, recurrence, literal search and receipt expiry. Raw analytics sink and real login flow are outside these two scenarios.
- CI test job now supplies its own PostgreSQL connection for isolated monitoring tests. No workflow was run remotely.
- 30-day receipt retention stores only event UUID/app/build/reception time; cleanup is bounded to 10,000 rows per five minutes. Issue summaries retain lifetime counts.
- Existing NuGet security warnings remain. Frontend/headless checks for this increment are pending.

### App performance backend increment

- Implemented native transaction/span HTTP ingestion, per-app/build retry identity, bounded names/spans/durations and parent-tree validation, sanitized labels, 30-day batched retention, scoped list/detail and p50/p95 aggregates.
- Passed: 248 backend unit tests and both expanded isolated PostgreSQL/HTTP scenarios. Performance ingestion deduplication, sanitizer, release filters, percentile fixture (100/200 ms => p50 150/p95 195), retention, HTTP read authorization and debug isolation verified.
- A recurrence assertion initially depended on Docker/Windows clock agreement. The fixture now uses an explicit past resolution boundary; rerun passed. Production policy still compares normalized client event time to database resolution time, so clocks should be synchronized.
- Contract and desktop/mobile instrumentation guidance: `docs/app-performance-api.md`. Frontend implementation delegated to Opus; not yet accepted. No performance load/scale claim.

### Error context backend increment

Retained occurrences now carry sanitized message/stack and allowlisted app/OS/SDK/severity/kind/release/environment fields, without session/user IDs. This supersedes the minimal receipt-only retention description above: detailed context now shares the 30-day receipt lifetime. Issue filters match retained occurrences but return lifetime aggregate counts; resolution remains app/build-wide across environments. Added paginated event detail and an observed release/environment catalog (not deployment history). Tests cover sanitized details, filters, lifetime group scope and HTTP authorization. Frontend integration pending.

### Headless real-API test host

`tests/MonitoringHost` is a standalone loopback-only executable, excluded from production registration. It requires an explicit isolated PostgreSQL connection, creates/drops its own database, serves built webapp files and terminates after 45 minutes (extended from 15 to avoid interrupting the combined headless acceptance suite). It uses real monitoring controllers and cookie authentication, with test-only login/bootstrap endpoints; no production auth or external email is used. Raw analytics sink reads are intentionally unsupported. Build and loopback health check passed. Browser assertions are delegated to Opus.

### Durable raw delivery increment

Replaced the in-memory error buffer with a transactional PostgreSQL outbox. A worker locks at most 200 queued rows, applies a 30-second delivery deadline, and removes them only after full acknowledgement. Failures/partial acknowledgement roll back; restart retries from the database. Delivered payloads are deleted immediately. Undelivered payloads preserve the existing raw fields (including legacy sessionId) for delivery, now bounded by the app detail retention window; reports past the existing raw 180-day TTL are removed without sending. Retained issue details still exclude sessionId. This does not change usage-event buffering.

Passed: 248 unit tests and both PostgreSQL/HTTP scenarios, now including lost acknowledgements, partial success and concurrent outbox workers. `python scripts/test-error-query-dedup.py` passed actual ClickHouse query templates against duplicate fixtures using a network-disabled cached ClickHouse 24.8 local process (0.5 CPU/512MB, no server). Matching Tinybird templates changed; hosted Tinybird execution remains unrun. Older ClickHouse local verifies syntax/behavior, not a full production 26.8 deployment.

A build initially found a migration/service class name collision; the migration was renamed and checks passed. Existing NuGet vulnerability warnings remain.

### Instrumentation example

Added native desktop/mobile instrumentation guidance and a dependency-free .NET sample. `dotnet run --project samples/DotNetMonitoring --verbosity quiet` built and ran successfully in offline mode, printing sample JSON only. No alert, webhook, email or external telemetry was sent. Device-specific SDK and physical-device validation remain open.

### Sentry adapter checkpoint

264 unit tests passed; both focused PostgreSQL/HTTP scenarios passed after correcting a test-helper connection type. The ingestion scenario then passed with actual Sentry .NET 6.10.0 exception and transaction/span delivery through TestServer. No external telemetry sent. Separate owner-provisioned release/debug projects map to existing app keys. See `sentry-compatibility.md` for exact subset and platform gaps.

### Monitoring controls and local-alert checkpoint

264 unit tests and both focused PostgreSQL/HTTP scenarios passed after adding capture/retention/purge and local inbox. New checks cover full required settings validation, shared-reader denial, concurrent duplicate alerts, eligible/suppressed regression, opt-out, pending raw outbox suppression,1-day versus 30-day app isolation, exact purge confirmation, disabled capture prerequisites, no cross-app deletion and compressed-envelope expansion limits. A new test seed initially omitted required `has_events`; corrected and rerun successfully. No external delivery exists. Frontend implementation/real headless acceptance remains in progress.

Latest privacy checks:270 unit tests passed. Added URL/short credential redaction; corrected an initial JWT-prefix regression and reran the entire unit suite. Soft-deleted app monitoring cleanup and expired raw-outbox cleanup are bounded independently of sink success. Integration coverage includes a forced alert insert failure proving receipt/quota/issue/outbox rollback before a successful retry.

## Current acceptance gate (not yet closed)

| Area | Current evidence | Remaining gate |
| --- | --- | --- |
| Native errors and issues | Atomic deduplication/quota/outbox, recurrence, scoped search/context; HTTP/PostgreSQL and real headless tests passed | Revalidate alongside final controls UI |
| App performance | Bounded spans, sampled counts, p50/p95, labels, permissions; HTTP/PostgreSQL and real desktop/mobile headless tests passed | Configurable-retention copy and server-error retry test being finished |
| Sentry subset | Actual .NET6.10.0 exception and transaction through serializer/compression/auth to TestServer; unsupported envelopes rejected before writes | Platform-specific SDKs and physical-device/native scenarios are explicitly unverified |
| Privacy and retention | Capture switches, shortened retention isolation, confirmed scoped purge, soft-delete cleanup, raw suppression/expiry, PII regression suite passed | Opus controls UI; delivered raw data keeps separate180-day TTL |
| Local alerts | Transactional new/regression inbox, duplicate/cooldown/rollback/disable/read/expiry tests passed | Opus inbox UI; external channels deliberately excluded |
| Frontend | Delegate reported36 unit tests,18 fixture headless checks and16 real headless checks passed; typecheck/build passed | Final controls/inbox tests and integrated typecheck/build/headless rerun |
| Repository | Local commits only on feat/error-issues; licenses unchanged | Review combined final diff and commit integrated frontend when delivered |

Latest backend commands: `dotnet test tests/UnitTests --no-restore -m:1` passed270 tests; `dotnet test tests/IntegrationTests --no-restore -m:1 --filter 'FullyQualifiedName~IssueIngestionTests|FullyQualifiedName~IssueTrackingTests'` passed both focused scenarios against isolated PostgreSQL. A subsequent targeted ingestion run also passed JSON response-contract assertions for purge preview and local alerts. No unresolved failure in these checks. Existing NU1902/NU1903 warnings for System.Security.Cryptography.Pkcs10.0.0 remain. Full historical integration suite, hosted Tinybird, production deployment/load, physical devices and external alert delivery are unrun. Do not infer these from the local checks.

### Performance/context frontend checkpoint

Opus completed performance summaries, filtered transactions, span detail, release catalog and recent error occurrences. Reported36 unit tests across5 files,18 mocked-network production-bundle checks and16 real backend checks on desktop/Pixel7, plus typecheck/build all passing. Real tests demonstrate3 samples/1 error/p50=200ms, nested spans and focus return, release-linked occurrences, build separation and shared-reader/stranger boundaries. Parent inspected saved mobile performance/transaction and desktop release screenshots without opening a browser. One low-impact react-doctor duplicate-header warning remains. Controls/inbox/Sentry provision UI is still in progress; copy must reflect configurable1?30-day retention and separate raw180-day TTL.

Post-integration backend review found that redaction markers could expand an accepted label beyond its query bound. Stored metadata/operation labels are now bounded after sanitization. Added HTTP regression assertions proving an expanded environment remains filterable for both issues and transactions; both PostgreSQL scenarios passed. No frontend API contract changed. The currently running headless host predates this narrow fix; the final host run must rebuild it.
