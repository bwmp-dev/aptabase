# MultiShock GlitchTip retirement acceptance

**Decision: do not retire GlitchTip yet.** The collector and dashboard support the tested Windows managed-error path under one Aptabase login. The remaining gates are application runtime, actual-app envelope inventory, and production cutover/restore evidence. Android ELF support does not establish MultiShock compatibility.

This is the current acceptance matrix; older checkpoint totals in `monitoring-roadmap.md` are historical. No MultiShock source/configuration, live GlitchTip service or private issue content was changed or copied. No production DSN is stored here.

## Owner history waiver - 2026-11-12

Source: owner message relayed in this task as trusted conversation history: **“I don’t care about current glitchtip history what is needed from me”**. The accompanying instruction explicitly waives current GlitchTip history and forbids deletion.

History export, migration, preservation, historical search and GlitchTip archive-restore verification are **not retirement acceptance gates**. No further history decision or private-data access is needed for this scope. Existing data, volumes, backups and services remain untouched; this waiver authorizes no deletion or service shutdown. The earlier [history assessment](glitchtip-history-preservation.md) is optional reference only. Recovery of **new Aptabase data** and a short operational rollback window remain required.

## Minimal remaining owner path

| Order | Owner decision or action | Ready asset / next engineering action | What is still unproven |
| --- | --- | --- | --- |
| 1 | **Decision:** name the intended server and HTTPS hostname; say whether Aptabase is already running there and whether PostgreSQL, ClickHouse, reverse proxy/TLS, login email and persistent storage already exist. No credentials in chat. | Tested collector/dashboard, production configuration and synthetic recovery checks are ready locally. Once separately authorized, inspect the chosen environment and fill only missing setup. | Actual availability, capacity, TLS/mail, deployment topology and recovery at that destination. No new hosting purchase is assumed. |
| 2 | **Decision:** confirm the consent rule. Recommended: `EnableAnalytics=false` stops all monitoring transmission (errors, performance, sessions and client reports), removes pending monitoring cache, and does not backfill opted-out activity on re-enable. Existing error-only `BeforeSend` is insufficient. | Session/report support remains enabled when consent permits. Implement the agreed rule only in an authorized candidate; retain release/environment and PII-off behavior. | Owner policy and candidate behavior, including cache revocation. This recommendation is not an accepted decision or permission to change MultiShock. |
| 3 | **Action when at the PC:** run the already prepared, hash-checked reduced harness using [the manual instructions](maui-unhandled-manual-test.md), first `ui-unhandled`; retain the result for inspection, then coordinate background/shutdown/offline-replay modes. | Unchanged binary, exact hashes, loopback setup and pass/fail assertions are prepared; handled mode already passed twice. | Automatic WinUI/background capture and cross-process durability. Automated launch remains policy-denied; repeated safety assurances do not authorize a workaround or another retry. Reduced-harness success alone cannot certify MultiShock. |
| 4 | **Action/authorization:** provide the exact MultiShock candidate and matching Portable PDBs through its normal build, or authorize separate candidate work with access to the correct PluginSdk0.16.0 dependency (missing from the inspected cache). Keep the current app untouched. | SDK6.8 serializer, diagnostic-field, session and replay acceptance fixtures are ready. Validate actual integrations/envelope types and the agreed consent rule in the candidate. Engineering must propose a bounded HTTP-loss policy for owner acceptance: tested SDK6.8 does not durably retry HTTP errors. | Published-app hooks, packaging, actual metadata and delivery behavior; dependencies cannot be substituted merely to obtain a build. No app patch or full-app launch is authorized by this document. |
| 5 | **Authorization after a concrete plan:** approve deployment to the named test/staging destination, including login-mail verification. Accept explicit retention/capacity and delivery-loss limits after they are demonstrated. | Rehearse HTTPS login/permissions, capture, failure visibility, retention and new-data backup/restore using synthetic data; then report the candidate and operational evidence. Optional Android symbols are not a prerequisite for managed MultiShock frames. | Intended-host production operation. Local tests are not a deployment or approval to contact real mail/services. |
| 6 | **Authorization after acceptance:** approve the specific MultiShock DSN/release cutover and observation/rollback window; later approve stopping GlitchTip ingestion/service. | Keep the prior DSN/configuration in the owner's secret store for operational rollback, not in this repository. Verify new traffic before retiring the old service. | Cutover and retirement remain unexecuted. Historical data need not be imported or archived for acceptance; deleting any old data remains a separate, ungranted action. |

Only steps 1 and 2 need answers now. Steps 3 and 4 supply runtime/candidate evidence; steps 5 and 6 are later approvals of concrete plans and results. No passing test needs repeating just to remove the history gate.

## Verified usage and acceptance matrix

Read-only source inventory: MultiShock checkout `a27d356d38f5a403486ea71f69c690df53510cea`, Windows target `net10.0-windows10.0.22621.0`, Sentry.Maui **6.8.0**, MAUI **10.0.11**, Portable PDB publishing. `Core/MauiProgram.cs` registers `UseSentry`, sets `multishock@<version>` release, debug/release-channel environment and `SendDefaultPii=false`; `BeforeSend` suppresses errors when `General.EnableAnalytics` is false. Aptabase crash capture is disabled. Searches of application source found no explicit Sentry transaction, attachment or manual capture call; this does **not** inventory envelopes generated by default SDK integrations.

| Replacement requirement | Implemented / verified evidence | Remaining gate |
| --- | --- | --- |
| One login, existing app permissions | Production registration/magic-link session integration covers analytics, raw errors, issues, performance, releases and settings. Owner/shared-reader/stranger/revocation tests pass. All views use existing console providers/navigation. | Production-pipeline synthetic checks now verify Secure/HttpOnly/Strict cookies, single-use links, logout cookie clearing, security headers and restored cookie keys. Intended HTTPS proxy/certificates and real mail delivery still require authorized staging; in-process HTTPS is not a TLS test. |
| Managed method, basename, line and column | Core Sentry6.8 Windows probe and **two user-operated reduced MAUI handled runs** preserve `RuntimeProbe.ThrowSynthetic()` / `SyntheticMauiProbe.cs:353:17`, SDK6.8.0, release and environment. | Ship matching Portable PDBs in the actual MultiShock release; verify real app capture without copying private source or full paths. |
| Automatic unhandled capture | Core SDK terminating thread exception verified. Prepared MAUI harness contains actual `UseSentry` and WinUI exception integration. | **Not verified:** automatic WinUI and background hooks in the reduced MAUI runtime, then the actual app. A handled capture is not evidence of hook attachment. |
| Offline and shutdown delivery | `tests/SdkReliability` uses real SDK6.8 cache/transport code: disk replay after SDK reinitialization, same ID after lost acknowledgment, no replay after successful acknowledgment, dispose drains a queued event. All3 tests pass without sockets/UI. Server repeated-ID and transaction deduplication already covered by HTTP/PostgreSQL tests. The new core6.8 mixed event/crashed-session test additionally proves real-collector replay after simulated offline and lost acknowledgment; no duplicate occurrence/session, late-init coverage and crash monotonicity. | Same-process SDK reinitialization is not process restart or abrupt-crash durability. HTTP500/503/429/403/413/422 discard rather than durably replay with the tested SDK6.8, despite disk cache; transport exceptions differ. MAUI offline/replay and shutdown modes remain unrun. Capacity eviction, long offline periods and real network loss are not accepted. |
| Release, environment and opt-out | Ingestion, UI filters and app/build isolation tests pass; synthetic MAUI preserves release/environment. | New SDK6.8 tests prove error-only `BeforeSend => null` still permits sessions/client reports. A future authorized candidate needs whole-SDK consent gating, including cached-data behavior on revocation, while keeping metadata support when enabled. Preserve release-channel behavior; never bypass consent to force acceptance. |
| Actual SDK envelope inventory | Bounded mixed error/transaction, individual session and client-report envelopes now persist atomically. Real core SDK6.8 serializers produce accepted forms, including default client reports and enabled session tracking. MAUI6.8 enables session tracking by default; reduced handled harness deliberately disables both. | Inventory actual application runtime output before a DSN switch. Aggregate `sessions`, attachments, profiles, replays and unknown types remain rejected. No full Sentry compatibility claim. |
| Failure visibility | Sentry action failures now feed existing bounded rejection summaries: malformed, credentials/policy, size, encoding, unsupported, metadata conflict and backend failure. No event text, keys, IP or user-agent is added. Nine unit cases plus real ingestion assertions pass. | Counters are per-process/in-memory and the reporter emits only top10 entries each minute. Not a durable audit, delivery receipt or external alert. Proxy/Kestrel rejection before the action filter and transport/network failure require separate status monitoring. |
| Data retention and privacy | Existing app capture/raw-forwarding switches,1–30day detail retention, scoped purge and local-alert controls tested. No external alert sender exists. Source locations retain basenames/numbers, not source code. | Raw ClickHouse logs keep their separate180day TTL. Purging PostgreSQL is not full historical personal-data deletion. Choose operational retention before cutover. |
| Session health and SDK discarded events | Private app/build-scoped session lifecycle, terminal-state monotonicity, concurrent replay, late init, quotas, rollback and retention tested. Completed-session crash-free rates exclude missing starts, active and abnormal sessions; no user metric is inferred. Reports expose bounded reason/category totals, not loss percentages. | Actual MAUI lifecycle/crash updates and production coverage still require runtime evidence. Metadata-only reports with identical timestamps/counters can collide; see `session-health-api.md`. Integrated UI passes real-backend desktop/mobile acceptance. |
| Production operation | Full backend integration suite passes with PostgreSQL16, ClickHouse26.8.36.3, local mail catcher and bounded native worker. PKCS dependency patched to10.0.13. A synthetic PostgreSQL dump/restore matched all22 public tables and proved post-restore authorization, DSN identity, replay, retention and monitoring behavior. | Synthetic native ClickHouse backup/restore and production authentication/key restoration now pass. `/readyz` checks PostgreSQL/configured ClickHouse, including refused/stalled connections; `/healthz` remains liveness. No deployed Aptabase restore/load, TLS/mail or worker-topology acceptance. See `production-acceptance.md`. |
| Current GlitchTip history | Owner waived history on 2026-11-12; see the quoted source above. Existing public-source assessment is optional reference. | **Waived:** no export/import/archive-restore or historical-search gate. No deletion authorized. Operational rollback and new Aptabase data recovery remain separate requirements. |

## Current runtime evidence

Latest saved user-operated handled result: UTC **2026-11-12T06:52:24.353671Z**, run `9e76e56b82ad4336aaf7ec4e33fbc76b`, event `14c6a8d5-f04a-458c-afba-fc45ff80b119`. `probe-data/handled-result.json` contains one `handled` / `error` event, SDK6.8.0, `synthetic` environment and the exact method/basename/353:17. The local log contains two handled sequences: SDK enabled, event item, HTTP200, shutdown requested on .NET10.0.12. It has no per-entry timestamps/run IDs; do not use it alone to correlate a new run or claim a process exit code.

This executable is a **reduced source-derived harness**, not the current published MultiShock app. Normal configuration, device/plugin/serial/WebSocket/flow/update/Velopack initialization is absent. It uses a test-local SDK cache and loopback-only, proxy/redirect-disabled transport. Framework profile writes were not measured; no personal profile contents were read. The full application also needs PluginSdk0.16.0, absent from the inspected cache; no substitution was made.

Published harness remains at `tests/SdkProbes/MultiShockPreparation/bin/publish/Aptabase.Isolated.MultiShockProbe.exe`:

- EXE SHA256: `1F394041FB061A8D138B4A650D6CE815785CDF234C603E870917B21010086438`
- DLL SHA256: `65BD26152811FD6C451E87688880BEC1DBDEFFD8CB6A3275F3A385DFC81ADFB8`

Automatic launch remains denied by approval policy. After explicit new authorization, exactly one combined PowerShell runner-creation/ui-unhandled launch retry was submitted and rejected before execution with only `blocked by policy`. It included canonical executable-path and EXE/DLL SHA256 checks. The runner was not created, no harness launched and no new runtime evidence exists. Task-owned collector/database were removed. No alternate route, computer controls or further retry was used; the remaining WinUI check is blocked, not a technical runtime failure.

## Next smallest user-operated runtime check

**Complete, syntax-checked two-window instructions:** [maui-unhandled-manual-test.md](maui-unhandled-manual-test.md). The minimal delta remains `ui-unhandled`, a fresh run ID and a distinct evidence file. Both published binary hashes were reverified unchanged; no launch was attempted.

Use the same two PowerShell sequences that produced the handled result, with the same verified executable/DLL hashes and isolated PostgreSQL/collector. They are currently stopped; the service sequence must be run first. Do not launch the full MultiShock executable. The reduced harness may show a blank window and a synthetic unhandled failure may terminate it. Its internal watchdog is25seconds; keep the outer30second process limit and kill only the process returned by that explicit launch. No device integrations or live endpoints are involved.

For the next run, change only these values in the already-used launch/query sequence (commands below are **instructions for the user**, not an automatic launch):

```powershell
$env:APTABASE_PROBE_MODE = 'ui-unhandled'
$env:APTABASE_PROBE_RUN = [guid]::NewGuid().ToString('N')
# Keep the provisioned loopback-only APTABASE_PROBE_DSN and explicit window opt-in.
# Save the query result as probe-data/ui-unhandled-result.json instead of handled-result.json.
```

Retain the new run ID, child exit code, appended log segment and exact release-filtered collector result. The expected outcome is crash termination: a nonzero signed32-bit Windows exit code, with no exact code assumed. Exit124 is the internal watchdog and is not success; any outer forced termination is also a failed completion check. A zero exit needs investigation, not automatic acceptance.

Acceptance requires automatic capture with `kind=unhandled`, SDK6.8.0 and the known method/basename/353:17. Verify `capture Microsoft.UI.Xaml.UnhandledException:False` in the new log segment for the WinUI-specific hook; an AppDomain fallback alone does not prove the WinUI hook. The `ui-unhandled` branch throws before/outside the handled branch's `try/CaptureException`; its observer only logs and never marks the exception handled. This matches the upstream [6.8.0 WinUI hook](https://github.com/getsentry/sentry-dotnet/blob/6.8.0/src/Sentry/Integrations/WinUIUnhandledExceptionIntegration.cs). A local observer message without a delivered event, an old handled result, HTTP200 alone, or watchdog timeout is not acceptance. Do not relabel the SDK's severity to force a pass. Afterward stop the foreground test collector and only its named disposable PostgreSQL container, as in the service sequence.

After inspecting that result, the same existing harness has further modes; do not treat them as passed:

| Mode | Required evidence |
| --- | --- |
| `background-unhandled` | Real terminating background thread, automatic unhandled SDK event and stored frame metadata; record exit code. |
| `shutdown` | No explicit flush before `SentrySdk.Close`; event delivered before process exit, no watchdog timeout. |
| `offline`, then `replay` | Keep the same test DSN/cache/run ID and collector database across both user-operated processes. Offline mode deliberately fails before opening a socket; expect no collector event. Replay mode captures no new event; expect the cached original once after restart. Repeat replay and require unchanged occurrence count. This simulates transport failure, not real network disconnection. |

Do not clear the cache between offline/replay, change Windows networking or touch the original app's cache. Preserve synthetic result files for verification before cleanup. Test-local cache is `bin/publish/probe-data/sentry-cache`; no SDK cache is copied from the user's app.

## Cutover boundaries (not executed)

The ordered owner path above replaces the former mandatory history-preservation plan. Keep current GlitchTip and MultiShock unchanged while runtime/candidate/hosting gates remain open. No private history inventory, export, import or archive restore is required. Confirm only actually required unsupported features, such as external notifications, during candidate acceptance; local inbox alerts do not provide external delivery.

Stage the authorized candidate and chosen hosting boundary before changing traffic. Record the exact app/collector versions, retained diagnostic fields, supported envelopes, consent/delivery policy, operational retention and observation/rollback window. Keep the old DSN/configuration securely available during that window. A rollback must preserve newly collected Aptabase data. Only an explicit later approval permits deployment, production DSN changes or stopping the old service. Neither the history waiver nor retirement approval implicitly permits deleting databases, volumes, objects or backups.

## Synthetic PostgreSQL recovery rehearsal

`MonitoringRecoveryTests` creates two disposable databases, migrates and seeds synthetic monitoring/authentication data, runs `pg_dump -Fc`, hashes the archive, and uses `pg_restore` into the second database. Canonical row hashes matched across all22 public tables. Post-restore assertions cover app ownership/shares, project-scoped DSNs, resolved issues, event/session/report retry deduplication, retention, transactions, native symbol indices and local alerts. No alert is delivered. The dump and both databases are removed afterward. This PostgreSQL test is not a GlitchTip backup or deployed restore rehearsal. A separate opt-in native ClickHouse rehearsal now restores synthetic analytics/raw-error rows, both views and TTL definitions; see `production-acceptance.md`.

The test is opt-in: set `APTABASE_ISSUE_TEST_DATABASE_URL` to a disposable loopback PostgreSQL16 container and `APTABASE_RECOVERY_CONTAINER` to its full64-character container ID. It refuses containers without the `aptabase.test=monitoring-recovery` label or the matching loopback-only published database port. Run `dotnet test tests/IntegrationTests --no-restore -m:1 --filter FullyQualifiedName~MonitoringRecoveryTests`. The latest accepted synthetic PostgreSQL archive SHA256 was `e5e2ca9869315c93838c03a593c0ae5a88e51ab07ea5e131433312d57c298b84`; a new rehearsal produces a new timestamped archive/hash. Keep these test services separate from manual MAUI ports55439/55440.

## Current checks and operational change

Latest independent increment: [Sentry6.8 delivery/consent acceptance](sentry68-delivery-acceptance.md). Real SDK HTTP failure/cache behavior and metadata opt-out limits are now verified, plus mixed unhandled/crashed-session replay through the disposable collector. No production implementation or frontend changed. Final21SDK tests pass; no unchanged backend/browser suite was rerun. Initial new test assumptions about metadata-only reports and429 suppression were corrected against observed SDK behavior and pinned upstream source; final failures/skips are0.

This production increment adds strict HTTPS-origin configuration, optional persistent self-hosted cookie keys and bounded storage readiness. See [production-acceptance.md](production-acceptance.md) for the configuration compatibility change, key-at-rest responsibility, exact assertions and exclusions. Full local results:346units,81integration,9SDK reliability/serialization;0failed/0skipped. MonitoringHost/app build passes with0warnings/0errors; the MAUI binary was not rebuilt. Remote CI and real TLS/production deployment were not run.

Failures corrected during development: stalled PostgreSQL initially outlived framework cancellation (provider timeout added); a pre-existing session test inherited Development exception handling (isolated test environment pinned); the new ClickHouse test initially hashed the wrong local backup path (corrected to the stock container backup directory). All final relevant tests pass.

The final ClickHouse synthetic archive SHA256 is `0cc9bda9334fbf05d4b49f337212ad482e996959c9a40853f7ad30317ea6e136`. Local backup files/databases are disposable; these hashes are evidence of a rehearsal, not retained recovery media.

- **346/346 backend unit tests**; **81/81 full sequential integration tests**,0failed/0skipped. Includes existing analytics/login/authorization, ingestion privacy/status counters and real bounded worker fault/timeout/cancellation/cleanup tests. Disposable local services only.
- **21/21 Sentry6.8 reliability/serialization tests**,0failed/0skipped, including12new HTTP/consent/real-collector replay cases; see `sentry68-delivery-acceptance.md`. The collector case requires an explicit disposable host and skips otherwise. CI itself has not run remotely. Full backend346/81 and frontend114/46/40 results above are retained from prior acceptance, not rerun for these test/docs-only changes.
- **114/114 frontend units**, typecheck/build, **46/46 mocked** and **40/40 real-backend headless** tests passed through Opus5.5high, one worker, final bundle served on alternate loopback55450. Parent reviewed saved desktop/mobile Session health screenshots. Those frontend checks are retained from77eeb2c, not rerun in this backend-only increment. The current81-test backend suite passed after review fixes,0failed/0skipped. Tests use synthetic data and separate SDK serialization evidence; they do not prove real MultiShock emission.
- `System.Security.Cryptography.Pkcs` is explicitly pinned to **10.0.13**, the patched package in Microsoft's [CVE-2026-84008 advisory](https://github.com/dotnet/announcements/issues/468) and [CVE-2026-70162 advisory](https://github.com/dotnet/announcements/issues/454). Restore and backend suites pass without the earlier NU1902/NU1903 warnings. No global runtime/toolchain was installed or upgraded; this is not a whole-system vulnerability certification.

Reproduce without any MAUI launch:

```powershell
dotnet test tests/UnitTests --no-restore -m:1
dotnet restore tests/SdkReliability --locked-mode
dotnet test tests/SdkReliability --no-restore -m:1
# Dedicated local services and worker options: monitoring-roadmap.md and native-symbols-api.md
dotnet test tests/IntegrationTests --no-restore -m:1 --settings tests/IntegrationTests/sequential.runsettings
python tests/SdkProbes/MultiShockPreparation/verify.py
```

Local TRX evidence is under `%TEMP%/aptabase-retirement-acceptance`. No real alerts, external service changes, push or deployment occurred. Do not conclude retirement readiness from the passing checks while any applicable runtime/candidate/operation gate above is open.

Production-checkpoint cleanup (8333628): no MonitoringHost or MAUI process was launched in that production-pipeline increment. Its task-owned PostgreSQL55449, ClickHouse55451 and mail-catcher55453/55454 containers were stopped and removed. The subsequent independent SDK collector lifecycle is recorded below. Manual MAUI ports55439/55440 were not used by this increment. The prepared harness hashes and original copied MultiShock source remain unchanged; no app launch or external alert occurred.

## Remaining independent versus owner-dependent work

The feasible focused collector/SDK checks identified in this review are complete. Do not replace missing runtime evidence with another synthetic hook test or build a speculative archive importer. Further work needs one of these concrete inputs:

- **Runtime environment/review gate:** denied MAUI operation remains blocked; automatic WinUI/background hooks and cross-process offline/shutdown behavior still lack acceptance. No new retry is authorized here.
- **Authorized application candidate:** exact full MultiShock build/dependencies/PDBs and production-equivalent integrations; reviewed consent gating and HTTP-loss/retry policy; actual emitted sessions/client reports. These require application decisions/changes and runtime acceptance outside this repo's current authorization.
- **Hosting access/approval:** intended HTTPS/certificates/mail, capacity versus metadata limits, storage/worker topology and deployed restore/load evidence. Local read/write/backup tests do not establish these.
- **Cutover/retirement approval:** accept the concrete candidate and hosting evidence, then authorize staged traffic changes, rollback window and eventual service shutdown. History is waived; deletion is not authorized.

The independent test host on55450 and labelled PostgreSQL on55449 were stopped after acceptance. Manual ports55439/55440 were untouched. No MAUI process, external alert, original MultiShock modification, live GlitchTip access, push or deployment occurred.
