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
- [ ] Nonintrusive headless desktop/mobile visual and keyboard checks; distinguish mocked-network UI checks from real HTTP/database end-to-end coverage.
- [ ] Durable delivery to raw error storage. Current in-memory raw buffer can lose accepted events on crashes or sink failure; issue persistence alone is not exactly-once end-to-end delivery. Design outbox and downstream deduplication together for ClickHouse and Tinybird.

### 2. Triage context, environments and releases
- [ ] Bounded sanitized event context/detail, latest/recent occurrences linked to issue; no request bodies, cookies, headers, user IDs or session identities by default.
- [ ] Explicit environment and release labels; preserve debug/release build mode as a separate dimension. Filter by environment/release, type/severity and first/last seen; parameterized search.
- [ ] Release catalog and issue appearance/regression indicators derived from received reports, not fabricated deployment metadata.
- [ ] Acceptance: same fingerprint across releases remains one issue; environment filter semantics documented; access checks on every detail/query; oversized/high-cardinality context rejected or bounded.

### 3. Sentry SDK intake subset
- [ ] App-scoped DSN credentials with rotation and explicit project binding; do not treat an arbitrary URL project ID as authority.
- [ ] Strict bounded envelope parser and event/exception mapping into the same validated ingestion service; event_id reuse reaches the same deduplication path.
- [ ] Test real SDK-generated fixtures, framing/length/compression limits, auth mismatches, duplicate events and unsupported item types.
- [ ] Publish a tested compatibility matrix. Initial subset excludes attachments, minidumps, replay, profiles, source-map uploads/symbolication and Sentry management APIs. Do not advertise universal Sentry compatibility.

### 4. Privacy and retention
- [ ] Automatic bounded receipt expiry; scoped issue/event deletion and app deletion cleanup.
- [ ] Retention policies for raw events, context, issues and future traces, implemented consistently on both analytics backends; preview/count before user-requested destructive purge.
- [ ] Sanitization tests for URLs, source paths and nested allowlisted context. No new personal-data identifiers to compute affected-user counts.
- [ ] Acceptance: retention cannot cross tenants; counts and deduplication guarantees describe expiry honestly; migrations and cleanup tested.

### 5. Optional app error alerts (local delivery only until authorized)
- [ ] Owner-managed rules for new issues/regressions and event-rate thresholds, environment filters, cooldown/deduplication and mute.
- [ ] Transactional delivery queue with retry/backoff, bounded payloads, delivery status and secret-safe logging.
- [ ] Test via local/mocked sinks only. Real email/webhook delivery needs explicit authorization. Webhook targets need SSRF protection, DNS/redirect validation and timeouts before activation.
- [ ] Acceptance: retries do not generate duplicate notifications, revoked ownership cannot modify rules, disabled rules cannot send.

### 6. App performance and instrumentation (priority after reliability/context)
- [x] Bounded sampled transaction/span ingestion for desktop/mobile operations (startup, navigation, rendering, local work and app network calls); no automatic unsampled collection.
- [x] Self-hosted PostgreSQL storage first, with 30-day event retention and indexed app/build/time queries. Preserve existing ClickHouse/Tinybird analytics unchanged. Reassess storage against measured volume before claiming scale.
- [ ] App-scoped latency summaries (count, errors, p50/p95) and transaction/span details, filtered by environment/release/time. Never collect request payloads, user identifiers or full URLs by default.
- [ ] Acceptance: duration/span/count/name bounds, retry deduplication, malformed parent relationships, tenant access and percentile fixtures tested.
- [ ] Desktop/mobile instrumentation docs and examples explaining stable operation names, sampling, monotonic durations, offline retry IDs and privacy boundaries.
- [ ] Headless frontend-to-backend tests and narrow/desktop visual checks with local fixture data.

## Owner decisions and scope boundaries

Confirmed: self-hosted desktop/mobile app error tracking and performance; not hosted SaaS or website uptime. Preserve existing app authorization and quotas, but do not expand billing or SaaS features. No uptime monitors or public status pages are planned.

Proposed implementation defaults: 30-day detailed event/transaction retention; issue summaries persist until explicitly deleted. App/build scopes remain authorization-neutral grouping boundaries. Releases/environments label occurrences; a post-resolution occurrence in any environment can reopen the group. Document this explicitly before exposing filters.

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
