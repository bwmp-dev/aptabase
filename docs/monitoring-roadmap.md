# Aptabase monitoring roadmap

Status: scoped local implementation accepted on `feat/error-issues`; see final acceptance below. This does not imply production readiness, native crash support or universal SDK parity. Owner-confirmed target: self-hosted desktop/mobile application errors and app performance. Website uptime, hosted SaaS expansion and billing features are out of scope. This is not a promise of full GlitchTip or Sentry parity. Existing Aptabase analytics, authentication, app ownership/sharing, ClickHouse and Tinybird support remain intact.

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

### 1. Reliability and acceptance (accepted locally)
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
- [x] Owner controls frontend and desktop/mobile headless acceptance, including preview failure/refresh gating before deletion.

### 5. Local app error alerts
- [x] Owner opt-in new-issue/regression alerts with per-issue/build cooldown and disable switch. Native/Sentry issue grouping/reopen semantics apply; no synthetic backlog on enable.
- [x] Transactional durable local inbox, read state, 30-day expiry, bounded owner-only list, retry deduplication; no error free text copied into alerts.
- [x] Acceptance: concurrent duplicate reports, cooldown suppression, eligible regression, disabled rules, read idempotency, wrong-build access, retention and cross-app cleanup tested locally. No external delivery invoked.
- [x] Owner inbox/configuration frontend and desktop/mobile headless acceptance, including keyboard focus and read state.

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

## Historical verification ledger (superseded by final acceptance below)

Baseline: 232 backend unit tests and one PostgreSQL/HTTP scenario passed; 14 frontend tests, TypeScript and Vite build passed. Full historical integration suite and browser-to-real-backend checks were not run. Existing NuGet vulnerability warnings and React Doctor findings remain outside monitoring scope. Record subsequent exact commands/results here as phases progress; unchecked items are not complete.

### Reliability increment

- Passed: `dotnet test tests/UnitTests --no-restore -m:1` (240 tests).
- Passed: `dotnet test tests/IntegrationTests --no-restore --filter "FullyQualifiedName~IssueIngestionTests|FullyQualifiedName~IssueTrackingTests" -m:1` (2 scenarios), with APTABASE_ISSUE_TEST_DATABASE_URL pointing to a disposable loopback PostgreSQL container limited to 0.5 CPU/256 MB. Tests create/drop their own databases.
- Covered: real ingestion auth filter/body validation/controller/database; concurrent retry deduplication, quota exhaustion/rollback, cross-app/build IDs, sanitizer, recurrence, literal search and receipt expiry. Raw analytics sink and real login flow are outside these two scenarios.
- CI test job now supplies its own PostgreSQL connection for isolated monitoring tests. No workflow was run remotely.
- 30-day receipt retention stores only event UUID/app/build/reception time; cleanup is bounded to 10,000 rows per five minutes. Issue summaries retain lifetime counts.
- Existing NuGet security warnings remain. Frontend/headless checks were pending at this checkpoint; final results are below.

### App performance backend increment

- Implemented native transaction/span HTTP ingestion, per-app/build retry identity, bounded names/spans/durations and parent-tree validation, sanitized labels, 30-day batched retention, scoped list/detail and p50/p95 aggregates.
- Passed: 248 backend unit tests and both expanded isolated PostgreSQL/HTTP scenarios. Performance ingestion deduplication, sanitizer, release filters, percentile fixture (100/200 ms => p50 150/p95 195), retention, HTTP read authorization and debug isolation verified.
- A recurrence assertion initially depended on Docker/Windows clock agreement. The fixture now uses an explicit past resolution boundary; rerun passed. Production policy still compares normalized client event time to database resolution time, so clocks should be synchronized.
- Contract and desktop/mobile instrumentation guidance: `docs/app-performance-api.md`. Frontend implementation was delegated to Opus and subsequently accepted below. No performance load/scale claim.

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

264 unit tests and both focused PostgreSQL/HTTP scenarios passed after adding capture/retention/purge and local inbox. New checks cover full required settings validation, shared-reader denial, concurrent duplicate alerts, eligible/suppressed regression, opt-out, pending raw outbox suppression,1-day versus 30-day app isolation, exact purge confirmation, disabled capture prerequisites, no cross-app deletion and compressed-envelope expansion limits. A new test seed initially omitted required `has_events`; corrected and rerun successfully. No external delivery exists. Frontend implementation/real headless acceptance was still in progress at this checkpoint; it is accepted below.

Latest privacy checks:270 unit tests passed. Added URL/short credential redaction; corrected an initial JWT-prefix regression and reran the entire unit suite. Soft-deleted app monitoring cleanup and expired raw-outbox cleanup are bounded independently of sink success. Integration coverage includes a forced alert insert failure proving receipt/quota/issue/outbox rollback before a successful retry.

## Final scoped acceptance gate (closed locally)

| Area | Current evidence | Remaining gate |
| --- | --- | --- |
| Native errors and issues | Atomic deduplication/quota/outbox, recurrence, scoped search/context; HTTP/PostgreSQL and real headless tests passed | Accepted on rebuilt final backend |
| App performance | Bounded spans, sampled counts, p50/p95, labels, permissions; HTTP/PostgreSQL and real desktop/mobile headless tests passed | Accepted; retention copy, server-error retry and 100-level span layout covered |
| Sentry subset | Actual .NET6.10.0 exception and transaction through serializer/compression/auth to TestServer; unsupported envelopes rejected before writes | Platform-specific SDKs and physical-device/native scenarios are explicitly unverified |
| Privacy and retention | Capture switches, shortened retention isolation, confirmed scoped purge, soft-delete cleanup, raw suppression/expiry, PII regression suite passed | Controls UI accepted; delivered raw data keeps separate 180-day TTL |
| Local alerts | Transactional new/regression inbox, duplicate/cooldown/rollback/disable/read/expiry tests passed | Inbox UI accepted; external channels deliberately excluded |
| Frontend | 62 unit tests, typecheck/build; 26 fixture and 24 real headless tests passed, with final affected 6 fixture/8 real checks rerun after preview gating | Accepted; one low-impact duplicate-header diagnostic remains |
| Repository | Frontend and backend integrated locally on feat/error-issues; licenses unchanged | No push, PR or deployment authorized/performed |

Latest backend commands: `dotnet test tests/UnitTests --no-restore -m:1` passed270 tests; `dotnet test tests/IntegrationTests --no-restore -m:1 --filter 'FullyQualifiedName~IssueIngestionTests|FullyQualifiedName~IssueTrackingTests'` passed both focused scenarios against isolated PostgreSQL. A subsequent targeted ingestion run also passed JSON response-contract assertions for purge preview and local alerts. No unresolved failure in these checks. Existing NU1902/NU1903 warnings for System.Security.Cryptography.Pkcs 10.0.0 remain. At that checkpoint the full historical integration suite was unrun; it subsequently passed 74/74 (below). Hosted Tinybird, production deployment/load, physical devices and external alert delivery remain unrun. Do not infer these from the local checks.

### Performance/context frontend checkpoint

Opus completed performance summaries, filtered transactions, span detail, release catalog and recent error occurrences. Reported36 unit tests across5 files,18 mocked-network production-bundle checks and16 real backend checks on desktop/Pixel7, plus typecheck/build all passing. Real tests demonstrate3 samples/1 error/p50=200ms, nested spans and focus return, release-linked occurrences, build separation and shared-reader/stranger boundaries. Parent inspected saved mobile performance/transaction and desktop release screenshots without opening a browser. One low-impact react-doctor duplicate-header warning remains. Controls/inbox/Sentry provision UI was still in progress at this checkpoint; it is accepted below; copy must reflect configurable1?30-day retention and separate raw180-day TTL.

Post-integration backend review found that redaction markers could expand an accepted label beyond its query bound. Stored metadata/operation labels are now bounded after sanitization. Added HTTP regression assertions proving an expanded environment remains filterable for both issues and transactions; both PostgreSQL scenarios passed. No frontend API contract changed. The final headless host was rebuilt with this fix and the complete 24-test real suite passed.

## Final verification and limits

- Backend unit suite: **270 passed, 0 failed, 0 skipped**.
- Entire existing integration suite: **74 passed, 0 failed, 0 skipped**, in 22 seconds, including legacy auth, ownership/access, analytics/error ingestion, live view, privacy queries and new monitoring. Used sequential.runsettings, isolated PostgreSQL16, ClickHouse26.8.36.3 (same version as CI), and MailCatcher0.8.2. Connection overrides are test-only; CI defaults are preserved. No existing local service was redirected or stopped.
- Frontend: **62 unit tests passed** (7 files), typecheck and Vite production build passed. **26/26** mocked-network desktop/mobile checks passed; the final preview-submission fix then passed all 62 units and the affected **6/6** fixture checks.
- Real production-bundle/headless acceptance: parent ran **24/24** on the rebuilt final backend; the final cleanup-preview UI fix then passed affected **8/8** real monitoring checks. Includes local inbox, owner/read-only separation, DSN provisioning, capture refusal, confirmed PostgreSQL-only purge and settings restoration. No browser request left loopback. Saved controls/alerts and performance/release screenshots were inspected without visible UI.
- Actual Sentry .NET 6.10.0 error and transaction/span serialization, compression and auth are verified through TestServer/PostgreSQL. The standalone .NET instrumentation example built and ran offline. Raw ClickHouse duplicate-query fixture checks passed; the full suite now also exercises the actual 26.8 server. Hosted Tinybird was not executed.
- All failures encountered during implementation were corrected and relevant checks rerun. Existing NU1902/NU1903 warnings for System.Security.Cryptography.Pkcs 10.0.0 remain; no dependency upgrade was silently bundled. A low-impact repeated header markup diagnostic remains in IssueDetailModal.
- Not verified: physical Android/iOS devices, MAUI/Unity/native crash capture, minidumps/symbolication/source maps, other Sentry SDK versions/platforms, device-side offline queue durability/energy overhead, AOT/trimming, production deployment/load or hosted Tinybird. No blanket mobile SDK or Sentry compatibility claim.
- Deliberately outside this scope: website uptime, SaaS/billing expansion, external alert channels/rate rules, profiling/replay, debug artifacts, distributed trace reconstruction, deployment history and retroactive deletion from delivered raw analytics. PostgreSQL monitoring cleanup explicitly excludes delivered raw logs under their existing 180-day TTL.
- Local alerts default disabled; the implementation has no external alert sender. Test emails went only to the temporary local catcher. All task-owned test hosts/containers were stopped and automatically removed; unrelated services were untouched. No push, PR or deployment.

### Reproduce the full integration run safely

Use dedicated disposable services and test database, not a developer/production database. `APTABASE_TEST_DATABASE_URL`, `APTABASE_TEST_CLICKHOUSE_URL`, `APTABASE_TEST_SMTP_PORT` and `APTABASE_TEST_MAILCATCHER_URL` override the legacy suite's default local service addresses. `APTABASE_ISSUE_TEST_DATABASE_URL` is an admin connection to the isolated PostgreSQL instance; the focused scenarios create/drop separate databases there. The run used loopback ports 55439 (PostgreSQL), 55441 (ClickHouse), 55443/55444 (SMTP/catcher HTTP), with caps 0.5 CPU/256MB,0.5CPU/1GB and 0.25 CPU/128MB respectively.

```powershell
dotnet test tests/IntegrationTests --no-restore -m:1 --settings tests/IntegrationTests/sequential.runsettings
```

Full-suite results and final headless screenshots are local artifacts under `%TEMP%/aptabase-final-acceptance`; they are not committed. Initial 15-minute host expiry was an infrastructure interruption, not an accepted test failure; the bounded test host was extended to 45 minutes for combined acceptance and is now stopped.
