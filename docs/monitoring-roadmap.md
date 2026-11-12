# Aptabase monitoring roadmap

Current phase: complete the one-login Windows monitoring acceptance slice after checkpoint `40e1e19`. Analytics, errors, performance, releases and symbol controls share Aptabase authentication/navigation and app ownership. The current production-auth integration scenario passes, and Sentry.NET6.8.0 preserves the synthetic method/basename/line353/column17 target. Sentry.Maui6.8.0 Windows compilation is verified; its actual runtime and published MultiShock integration are not. See `windows-monitoring-migration.md` for a safe migration plan and explicit replacement gates. Optional bounded ARM64 source-line/inlining is implemented independently; it is not Windows binary symbolication. Earlier ?final? sections below describe historical checkpoints, not blanket parity.

Status: continuing SDK/native-readiness work after accepted checkpoint `7424a81` on `feat/error-issues`. The checkpoint ledger below is historical, not a declaration that all desktop/mobile monitoring work is finished. Owner-confirmed target: self-hosted desktop/mobile application errors and app performance. Website uptime, hosted SaaS expansion and billing features are out of scope. This is not a promise of full GlitchTip or Sentry parity. Existing Aptabase analytics, authentication, app ownership/sharing, ClickHouse and Tinybird support remain intact.

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

### 7. Text crash reports and owner-uploaded native symbols (approved)
- [x] First verified format: ELF64 little-endian ARM64 shared library, exact GNU build ID and bounded sized functions; no raw dumps/archives accepted.
- [x] Owner-only artifact metadata/upload/delete,8 MiB file/32 MiB app/8 artifact limits, app/build isolation, idempotency, private PostgreSQL contents and no binary API.
- [x] Opt-in pinned-local-image worker with network disabled, readonly/non-root/capability and CPU/memory/PID limits,15-second deadline, bounded output, global single dispatcher and cleanup failure halt.
- [x] Explicit bounded native frames through existing HTTP ingestion; original grouping and receipt/quota/outbox transaction preserved; exact-build function-only resolution with missing/expired/ambiguous states.
- [x] Retention shortening/deletion/purge, malformed input/core rejection, concurrent budgets/workers, quota rollback, worker fault/cancel and existing full integration suite accepted locally.
- [x] Opus owner controls, occurrence diagnostics, purge counts and desktop/mobile headless real acceptance:97unit/34mocked/34real tests, typecheck/build passed. Stale DSN assertions and retention-refresh regression fixed; final0failed/skipped.
- [ ] Production deployment topology for worker isolation; Docker access is an explicit privileged operator prerequisite, not an automatic permission grant.
- [ ] Android device crash capture/unwind/PC normalization and offline delivery; no device coverage inferred from a synthetic ELF.
- [ ] Apple dSYM source artifacts/toolchain fixtures; no usable Apple host or fixture supplied. No dSYM support claim.
- [ ] Source-line/inlining/demangling, R8 mappings, PDBs and server source maps need separate format-specific verified increments. Raw dumps remain a separately approved future policy/design.

## Owner decisions and scope boundaries

Confirmed: self-hosted desktop/mobile app error tracking and performance; not hosted SaaS or website uptime. Preserve existing app authorization and quotas, but do not expand billing or SaaS features. No uptime monitors or public status pages are planned.

Implemented defaults: owner-configurable 1..30-day detailed event/transaction retention (30 by default); issue summaries persist until explicitly deleted. App/build scopes remain authorization-neutral grouping boundaries. Releases/environments label occurrences; a post-resolution occurrence in any environment can reopen the group. Document this explicitly before exposing filters.

Remaining input useful but not blocking independent work:
- First desktop/mobile SDK languages to prioritize; initial examples can cover .NET and generic HTTP. Sentry compatibility remains a tested subset, not universal.
- Expected event/trace volumes and disk budget; local PostgreSQL is the initial self-hosted choice, not a scalability claim.
- Whether alert delivery is wanted and which channels; rule processing can be tested locally, but real delivery requires explicit authorization.
- Source maps/native symbolication, attachments and crash dumps remain unimplemented follow-on work requiring explicit storage, privacy, parser-isolation and format decisions. See `sdk-native-assessment.md`; do not treat the initial checkpoint's exclusions as closure of this work.

## Working policy

Backend: GPT-6 Astra high. All frontend code: Claude Opus 5.5 high via T3. Local commits only; no pushes, PRs, deployments, visible windows or focus changes. Run one bounded test/build job at a time. Any headless listener must bind loopback and be stopped after tests. No real alerts, external services or permission changes.

## SDK/native readiness continuation after 7424a81

- [x] Inspect installed SDK/toolchains without launching device/UI or installing major tooling. Details and architecture decisions: `sdk-native-assessment.md`.
- [x] Preserve managed source line/column positions and unhandled mechanism status; keep previous grouping IDs and discard private context. Verify a truly thrown .NET exception with source locations.
- [x] Add a pinned real JVM SDK probe: handled exception, actual uncaught process exit, transaction/span, stored privacy/context assertions through loopback HTTP/PostgreSQL.
- [x] Correct fractional span end-boundary comparisons, with a regression failing before the fix and rejecting real overruns afterward.
- [x] Reject unresolved native address frames before writes; continue rejecting binary/unsupported envelope items.
- [x] Demonstrate installed NDK ARM64 DWARF symbol resolution offline without accepting arbitrary binaries or claiming collector support.
- [x] Extend real JavaScript SDK/source-map evidence on the available Node runtime. Opus 5.5 high verified normal derived-key DSN parsing, actual handled/uncaught exceptions and transaction/span storage, local source maps without embedded source, and loopback-only network guards. This is Node evidence, not Electron/React Native/browser acceptance.
- [ ] Choose initial server symbolication formats, artifact storage/retention, isolated worker and license. Owner asked about text reports plus owner-uploaded symbols first versus dump assessment or SDK-side-only diagnostics. No response presumed; this is a product/architecture decision, not an access denial.
- [ ] Implement and verify the selected artifact/symbolication phase only after those decisions are concrete. Current APIs intentionally do not accept artifacts/minidumps.
- [ ] Physical Android/iOS, native crashes, R8/AOT/trimming, offline-device durability and resource overhead remain open platform acceptance, not passed by desktop JVM or offline compiler tests.

### Continuation verification ledger

- `dotnet test tests/UnitTests --no-restore -m:1`: **280 passed, 0 failed, 0 skipped**.
- Focused PostgreSQL/HTTP tests: **2 passed**. Subsequently reran the **entire integration suite: 74 passed, 0 failed, 0 skipped**, 19 seconds, sequential with isolated PostgreSQL16, ClickHouse26.8.36.3 and local MailCatcher0.8.2. TRX: `%TEMP%/aptabase-sdk-acceptance/sdk-integration.trx`.
- `tests/SdkProbes/Java/run.ps1`: passed handled exception, actual uncaught JVM termination and transaction/span acceptance with Sentry Java8.64.0/Java25. It initially failed uncaught delivery with the shutdown integration removed; retaining the bounded shutdown flush fixed it. One short transaction was rejected during exploration; a deterministic fractional-boundary regression was reproduced/fixed and the actual SDK probe reran successfully. The failed transaction's exact envelope was not captured, so the observed rejection is not asserted to have a proven unique cause.
- `tests/SdkProbes/Native/run.ps1`: passed local Android ARM64 object symbol/function/source-line resolution with NDK29/LLVM21. A PowerShell native-argument quoting warning was corrected and the probe reran without it.
- A first MonitoringHost launch omitted its absolute webroot and exited before database creation; restarted with `MONITORING_TEST_WEBROOT` pointing at the existing production bundle. No frontend code changed in this increment; previous 62 unit/26 fixture/24 real acceptance evidence remains historical, not a rerun. No frontend typecheck/build was needed for these backend/test-only changes.
- Existing NU1902/NU1903 package advisories remain. No production deployment, mobile simulator/device, native crash dump, server symbolication, hosted Tinybird or load test was run. No real external alerts exist or were enabled.

### Standard JavaScript DSN compatibility fix

The real Node SDK probe found that its standard DSN parser rejects hyphens in Aptabase app keys and sends nothing. A temporary component-object diagnostic proved the remaining pipeline but was removed; it is not supported setup or acceptance evidence for normal DSNs.

The server now emits 64-character lowercase hexadecimal HMAC-SHA256 keys bound to app/project/build and derived from the current app ingestion key. Legacy raw-key DSNs/headers/query credentials remain accepted. Both forms are revoked by underlying app-key rotation; no second credential table, migration, parser bypass or permission change was introduced. Mixed raw/derived credentials in one request remain rejected. A derived key works only at its Sentry project, not native App-Key ingestion or management APIs.

Passed after this fix: **286 unit tests**, both focused PostgreSQL/HTTP scenarios, then **74/74 full integration tests (0 failed/skipped)** in 17 seconds, sequential. Coverage includes all credential transports, old DSNs, mismatches, app/build isolation, reader denial, owner lock, native endpoint rejection, rotation and unchanged project IDs. The first new lock fixture supplied four characters to the existing one-character column; fixed the fixture and reran successfully. Final TRX: `%TEMP%/aptabase-sdk-acceptance/dsn-integration.trx`. Actual .NET6.10.0 SDK passes through the new DSN in the integration suite; actual Java8.64.0 handled/uncaught/performance probe also passes against the rebuilt host with the new DSN.

Opus's Node10.77.1 strict-mode final acceptance passed twice on Windows/Node24.19.0 against backend `7f0685b`. The returned DSN string round-trips through the SDK's normal parser unchanged; handled error and transaction envelopes receive HTTP200, and an actual uncaught process exception is stored as fatal/unhandled before exit1. Local source mapping retained `thrower.ts` line12:11 from compiled line6 without `sourcesContent`; sensitive context and paths were excluded. Seven network/transport refusals self-test before sockets open, including HTTP, UDP and IPC. A test-guard bug where HTTP's `path:null` bypassed host checking was fixed and verified; recursive test-output cleanup now checks resolved paths and rejects reparse points. Node dependency tree is pinned to MIT/Apache-2.0 packages and installed without scripts; no 11.x CLI/FSL dependency was adopted. See `javascript-sdk-probe.md` for commands/evidence and limits. The test-only files are integrated locally; the removed diagnostic bypass is not supported setup.

Final SDK increment acceptance: **286 backend unit tests and 74 full integration tests passed**, with real .NET6.10.0, Java8.64.0 and Node10.77.1 paths verified at their documented boundaries. Backend/MonitoringHost builds and the Node TypeScript fixture compilation passed. The unchanged dashboard's earlier62unit/26fixture/24real checks and typecheck/build were not repeated; they remain checkpoint evidence. Existing NuGet advisories remain. Electron, browser/renderer, React Native/Hermes, Android/iOS device lifecycle/native crash paths, offline mobile durability, AOT/trimming and production load remain unrun. At that SDK checkpoint the symbolication question was unanswered and no artifact intake existed. Owner approval and phase7 above supersede that historical boundary. Temporary host/PostgreSQL are stopped after acceptance; ClickHouse/MailCatcher were already removed. No external alert sender exists, and no push/deployment/UI operation occurred.

## Historical verification ledger (superseded by checkpoint acceptance below)

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
- Deliberately outside that earlier checkpoint: website uptime, SaaS/billing expansion, external alert channels/rate rules, profiling/replay, debug artifacts, distributed trace reconstruction, deployment history and retroactive deletion from delivered raw analytics. PostgreSQL monitoring cleanup explicitly excludes delivered raw logs under their existing 180-day TTL.
- Local alerts default disabled; the implementation has no external alert sender. Test emails went only to the temporary local catcher. All task-owned test hosts/containers were stopped and automatically removed; unrelated services were untouched. No push, PR or deployment.

### Reproduce the full integration run safely

Use dedicated disposable services and test database, not a developer/production database. `APTABASE_TEST_DATABASE_URL`, `APTABASE_TEST_CLICKHOUSE_URL`, `APTABASE_TEST_SMTP_PORT` and `APTABASE_TEST_MAILCATCHER_URL` override the legacy suite's default local service addresses. `APTABASE_ISSUE_TEST_DATABASE_URL` is an admin connection to the isolated PostgreSQL instance; the focused scenarios create/drop separate databases there. The run used loopback ports 55439 (PostgreSQL), 55441 (ClickHouse), 55443/55444 (SMTP/catcher HTTP), with caps 0.5 CPU/256MB,0.5CPU/1GB and 0.25 CPU/128MB respectively.

```powershell
dotnet test tests/IntegrationTests --no-restore -m:1 --settings tests/IntegrationTests/sequential.runsettings
```

Full-suite results and final headless screenshots are local artifacts under `%TEMP%/aptabase-final-acceptance`; they are not committed. Initial 15-minute host expiry was an infrastructure interruption, not an accepted test failure; the bounded test host was extended to 45 minutes for combined acceptance and is now stopped.

## Native-symbol increment acceptance (supersedes earlier checkpoint totals)

Integrated locally on `feat/error-issues` at `D:\Coding\public\aptabase`: migration0022, private app/build artifacts, isolated bounded function reader, native text frames, retention/purge and owner/read-only diagnostics. Final backend302unit tests passed. Full integration76/76passed (40seconds,0failed/skipped), followed by final real worker1/1passed after self-watchdog addition. Frontend97unit,34fixture and34real headless tests passed; typecheck/build passed. Parent inspected desktop/mobile screenshots. Earlier2real DSN failures and2dependent skips are fixed and superseded by the final34/34 run. Setup, commands, test evidence, React Doctor warnings and exact unsupported formats are in `native-symbols-api.md`.

Task-owned loopback host/PostgreSQL/ClickHouse/mail catcher and parser containers are stopped/removed. Worker scratch and synthetic probe copy cleaned; acceptance screenshots/TRX retained. No external alert channel was configured, permissions changed, upstream source copied, push/PR/deployment or visible UI used. Existing NuGet security advisories and one new low-impact component-complexity warning remain. Production worker topology, real Android crash capture/unwinding/address normalization, Apple fixtures/toolchain, source-line/inlining and other native formats are actual remaining work; raw dumps remain excluded pending a separate policy.


## Current one-login/source-location acceptance (supersedes earlier totals)

- [x] Production Aptabase magic-link registration/session shared by analytics/raw errors/issues/performance/releases and owner controls. New integration scenario verifies one cookie, unauthorized ingestion-key reads, other-user denial, shared read access, owner-only management and share revocation. No second login/user system was introduced. Frontend route/provider/navigation inspection confirms all views are under the existing console.
- [x] Sentry.NET6.8.0 Windows Release with Portable PDB: synthetic `SyntheticManagedSite.ThrowHandled()` / `SyntheticManagedProbe.cs:353:17`, actual terminating thread exception (`unhandled` / SDK severity `error`), release/environment and manual transaction accepted. No private issue/site accessed or content copied.
- [x] Sentry.Maui6.8.0 Windows library compiled offline against installed MAUI10.0.11, Release Portable PDB; no app/UI launched. Windows Rust actual panic exit101, basename/line/column and twice-submitted event deduplication accepted. All new probes live in Aptabase only.
- [x] Optional pinned GNU2.41 source extraction inside the existing isolated dispatcher. Basename/line and innermost-first inline chains, bounded inspected PCs (including aliases), inline depth/output/time/memory, explicit unavailable/unconfigured/limit fallback. No uploaded artifact execution, external symbols or source code indexing; original bytes cleared after processing and historical processed bytes cleared by migration0023.
- [x] Final backend unit suite **312 passed,0 failed,0 skipped**. Full sequential integration suite **77 passed,0 failed,0 skipped,41seconds**, including real Docker fault/timeout/output/cancellation/cleanup, PostgreSQL authorization/quota/retention and existing analytics/auth/ClickHouse tests. TRX `%TEMP%/aptabase-source-acceptance/source-final-integration.trx`. Backend/MonitoringHost and both Windows .NET probe builds passed; existing NU1902/NU1903 PKCS warnings remain in the application.
- [x] Opus5.5high frontend source-location display/settings copy: **99 units**, typecheck/build, **38 mocked headless** and **34 real headless** tests passed. A substring label selector initially caused2 failures/8dependent skips; exact matching fixed it, full34/34 rerun passed. Affected mocked native12/12 reran. Real acceptance covers desktop/mobile inline order, shared-reader boundaries, delete/unresolved behavior and symbol purge.
- [x] Final targeted native real acceptance after both backend hardenings and fixture-manifest refresh: **10/10 passed,0 failed/skipped**, desktop/mobile, against the rebuilt host. Exact function/address/source expectations stayed independent of collector responses. The full34-test real suite predates these final worker-only hardenings; only the affected10 reran, plus final typecheck. No unresolved test failure remains.
- [ ] Actual published MultiShock/Sentry.Maui6.8.0 WinUI UI-thread/background hooks, shutdown/offline/restart durability, all configured integrations and matching PDB shipping. Core SDK and compile-only evidence are insufficient to approve replacement.
- [ ] Production HTTPS/session/reverse-proxy/load/backup/worker deployment validation; no deployment was performed. Decide whether historical GlitchTip data or unsupported Sentry features are required before any cutover. No history importer or external alert channel is implemented.

Deferred from this Windows goal: device-produced Android crashes, Apple dSYM/Xcode verification, Windows PE/PDB server symbolication, Tauri native/abort crashes, generic large-DWARF support and cross-build semantic grouping. Raw dumps remain explicitly excluded. No device install/launch, external service change or real notification occurred. Safe future migration is staged in `windows-monitoring-migration.md`; the current GlitchTip/MultiShock setup remains untouched.


Final review corrected a Docker auto-removal race by requiring independently verified container absence within the existing cleanup deadline. The source preprocessor also counts overlapping ranges toward its work bound, and GNU timeout escalates to KILL after1second. Regression and full integration checks passed after these changes. The synthetic6.8.0 runner initially expected fatal severity; actual SDK output is error/unhandled, and the assertion was corrected to verify the real semantics while keeping exact method/file/353:17 checks. No collector data was changed to satisfy a test.

Parent inspected the saved mobile inline-frame screenshot: order/labels are readable and long build IDs wrap without sideways overflow. React Doctor's new positional inline-row key warning is intentional for an immutable ordered chain with possible repeated frames; the earlier low-impact NativeSymbols complexity warning remains. No unrelated refactor was bundled.

Cleanup verified: task-owned MonitoringHost, PostgreSQL, ClickHouse and mail catcher stopped/removed; no listeners remain on55439/55440/55441/55443/55444/4317, no parser containers or scratch directories remain. Unrelated containers were untouched. Probe build outputs/manifests and screenshots/TRX remain in ignored bin or temporary evidence directories. No external alert delivery exists/enabled, permissions changed, visible UI used, push/PR/deployment performed.
