# Full MultiShock reporting candidate: source and runtime evidence

Status: source-configured DSNs built and reviewed; two separately authorized modified full-app runs completed. The production observer confirmed the handled event and trace, but found no unhandled event and no completed/crashed session. Local lifecycle corrections are built and tested, not launched or deployed. This is not a launch procedure and does not authorize or replace the previously denied reduced-harness launch. Automated DSN transfer remains blocked by Windows browser URL-confidence policy. Do not retry that route without separately established grounds. The user completed the manual transfer. The latest explicit instruction authorizes these two ingestion-only DSNs in source; the test candidate no longer reads the private transfer file.

Target: the actual MultiShock checkout at `acb85ed0ddedfea586d0e1b96d62fddc09f4c796`, using Sentry.Maui 6.8.0. The proposed candidate is a modified full app, not the unchanged published app and not the reduced harness. Keep the existing installation and real profile unchanged while preparing it. The candidate source now selects the approved combined collector; the old endpoint remains recoverable from the committed source. No push or release.

## Prepared patch

1. **`MultiShock.csproj`:** import the explicit `MultiShockReportingAcceptance=true` build gate, permitted only for Windows and a dedicated `ReportingAcceptance` configuration. Normal Debug and Release exclude the trigger source and call sites. Reject mismatched flag/configuration, trimmed publication and missing/conflicting source DSNs before compilation. Runtime validation compares the selected DSN with the source release/debug metadata, requiring HTTPS, projects 1/2 and distinct derived keys on the same origin. Normal Debug selects debug; other configurations select release. No fingerprint, runtime transfer file or Actions secret is required. Explicit settings: `DebugType=portable`, `DebugSymbols=true`, `Optimize=false`, `PublishTrimmed=false`, `InvariantGlobalization=true`, `SatelliteResourceLanguages=en`, `KeepCultures=en;en-US`; the culture-pruning condition includes the dedicated configuration. This deliberately differs from optimized Release code. It does not define `DEBUG`: the first real candidate must use the verified release project and the existing release-channel environment logic. Separate debug-project acceptance remains future work. The dedicated configuration supplies distinct app/Core output directories; the normal-build verification used a separate `BaseOutputPath`.
2. **`Platforms/Windows/App.xaml.cs`:** inside the test compile guard, validate exact opt-in arguments before `VelopackApp.Build().Run()`. Require one mode (`handled` or `ui-unhandled`), one canonical GUID run ID and a valid evidence directory; reject duplicates, unknown test arguments, or absent opt-in. Validate the compiled DSN against the reviewed source collector origin, project, and key; never print the DSN. A test candidate must refuse to start ordinary application services when validation fails, using a distinct validation exit code rather than throwing a test exception or falling through. Generated WinUI framework initialization already precedes this constructor: this is not pre-framework isolation.
3. **`Core/App.xaml.cs`:** inside the test guard, attach a one-shot callback to the existing `Window.Created` event. Keep the existing `MainPage`, startup coordinator, service registrations, MAUI lifecycle, and Sentry registration. Capture the actual UI dispatcher there. Await the existing `StartupCoordinator.Completion` with a bounded timeout; require completed successful recorded steps and an uncancelled coordinator before injecting. Record login state separately, since `DeviceManager.InitializeAsync()` returning false is logged rather than thrown and therefore does not fail its coordinator step.
4. **New test-only C# helper:** parse/validate opt-in, coordinate the one-shot trigger, write minimal evidence, and request bounded shutdown. It must not call devices, serial APIs, flows, plugin actions, or other messaging APIs. Prefer a small parser/policy unit that can be linked into the existing xUnit project without starting MAUI.
5. **`Core/MauiProgram.cs`:** only within the test guard, use a fresh test-local Sentry cache and check SDK enablement. This prevents replay of the user's existing cached reports into the new collector. Preserve `UseSentry`, automatic session tracking and the SDK's real WinUI/AppDomain hooks. Do not replace transport or install a catch-all exception handler. Do not silently alter release/environment; record the actual values without account/profile data.

No UI/Razor/JavaScript change is proposed. The trigger code belongs to the backend/C# scope. All additions must be removable by removing the test flag and patch; no changes to existing profile settings are part of this design.

## Two separate runs, once launch is separately coordinated

- **Handled:** dispatch a real throw from a stable `[MethodImpl(MethodImplOptions.NoInlining)]` method, catch only that synthetic exception, and call `SentrySdk.CaptureException` once. Include the run ID in the synthetic message, never private profile values. Bound `FlushAsync` to five seconds with an outer eight-second wait, then request normal MAUI shutdown on the UI dispatcher. Flush completion is not proof of server delivery.
- **UI unhandled (corrected after the first run):** enqueue creation of a retained one-shot XAML DispatcherTimer, then call the stable throw method from its Tick handler after stopping and detaching the timer. Do not wrap that callback in a catch, an observed task, an async-void continuation, or manual `CaptureException`. Do not set `UnhandledExceptionEventArgs.Handled`. The SDK must capture through its actual WinUI hook. A failure to enqueue is a preparation failure, not a successful test.

Use the real Portable PDB sequence point to establish expected method, basename, line and column independently of the collector response. Do not manufacture line 353/column 17 using `#line`; those were a previous fixture's coordinates, not a requirement to disguise a new source location. Retain the candidate/PDB hashes and source commit with the result.

Sentry 6.8.0's pinned [WinUI integration source](https://github.com/getsentry/sentry-dotnet/blob/fee4a67e5beb41530de8e1bfbe84096eeb56a4bc/src/Sentry/Integrations/WinUIUnhandledExceptionIntegration.cs) reads the existing `Handled` value, attaches the WinUI mechanism, captures internally and flushes on an unhandled event. It does not establish that this particular candidate successfully attached that hook. Verify the delivered mechanism and handled classification; do not infer attachment from package presence.

## Bounds and evidence

- Start a process-local deadline after validation and before ordinary authored startup. Sixty seconds is the proposed deadline; a dedicated failure exit code 124 denotes watchdog termination, never successful crash or shutdown acceptance. Do not let synchronous evidence-file I/O prevent termination. An in-process watchdog cannot guarantee termination if the process/runtime is completely hung. The future approved observer must record actual exit and timeout independently; no launcher is provided here.
- For handled mode, request graceful shutdown first. Record close-request and window-destroying separately; neither establishes process exit. For UI-unhandled mode, leave crash termination/SDK flush to the real runtime. Do not promise a particular Windows crash code in advance.
- Write only bounded primitive JSON: run ID, mode, timestamps, candidate/PDB identities, startup step counts/results, login boolean, trigger stage, handled event ID, flush completion/timeout, close request and watchdog state. Never serialize a `Process`, exception object, profile, logs, cookies, DSN or credentials. Use a new test-local evidence directory, refuse reparse paths and existing result files, and preserve previous runs.
- The cache is test-local, but normal app storage, plugins, devices, flows and network behavior remain real. This is not OS network isolation and does not guarantee synthetic-only traffic. Startup logging can create unrelated reports; correlate the synthetic run ID and minimize exported evidence.
- Normal last-window shutdown explicitly ends the SDK session as Exited before flushing. The SDK preserves a pending unhandled/crashed state; tests must not force a crash status to manufacture runtime acceptance. Verify the outcome received by the collector. Offline replay requires a separately coordinated scenario; this design does not disable networking or change the user's services.

## Verification before any launch proposal

1. Parser tests: absent/duplicate/unknown arguments, invalid run IDs, each mode, missing/foreign/old DSN, origin/project/key disagreement; no secret values in failure output.
2. Build checks: normal Debug/Release assemblies omit trigger; flag/config/platform mismatches fail; dedicated candidate includes trigger and matching Portable PDB. Verify separate outputs and no fallback endpoint. These checks may build code but must not execute the app.
3. Unit tests with injected callbacks: trigger once, startup fault/timeout, completed steps with login false, dispatcher rejection, bounded flush failure, and evidence projections. Unit tests must not throw into a real UI loop or terminate their test runner.
4. Fresh independent read-only source review of the implemented patch before any actual full-app launch proposal. Design review alone is not source/runtime acceptance.
5. Parent supplies privately verified DSNs, coordinates production observation and explicit launch scope. Privately back up configuration before actual profile changes or launch, preserve the original installation/endpoint, and do not stop GlitchTip.

The normal startup sequence itself can connect devices and initialize plugins. The owner has accepted those ordinary real-profile startup risks; no test action may deliberately activate devices or flows. No fresh safety guarantee follows from the fact that no saved flows were marked running in the earlier read-only snapshot.

## Historical preparation evidence, superseded where noted below

- Focused C# tests: **40 passed**, zero failed/skipped. Includes parser/DSN validation, one-shot execution, startup fault/timeout, dispatcher rejection, unhandled callback propagation, flush fault/timeout, shutdown failure handling and bounded JSON parsing.
- Build-gate checks: **8 passed** (one allowed case, seven expected refusals). Debug and Release compile selections contain no acceptance source, including the execution helper added during review.
- The separately built normal Release DLL contains no acceptance-runtime metadata marker. The candidate does; its PDB has the Portable PDB header. Exact sequence points have not yet been verified.
- Corrected candidate build: **0 warnings/errors**. Separate normal Release build before the test-only runtime corrections: **0 warnings/errors**; no normal-build source changed in those corrections.
- Candidate compilation used a synthetic `collector.invalid` DSN. **Never launch this artifact.** It is not the endpoint-configured runtime candidate.
- Independent review found and then cleared shutdown evidence I/O escaping into WinUI, the unbounded DSN-file read, and dispatcher acquisition outside error handling. The corrected implementation reads at most 4,097 bytes, rejects files over 4 KiB and duplicate/extra/wrong-type JSON fields, and classifies shutdown evidence failure as exit code 5 without interrupting subsequent subscribers.
- Manual setup script: syntax and **12 synthetic cases passed**, including an actual temporary file with current-user/SYSTEM-only ACL, round-trip and overwrite refusal. Initial test runs exposed test-driver array-expression and mock-scope mistakes; those were corrected. No real keys or profile were used. Independent read-only script review found no blocking issue; its cancellation-handling suggestion was incorporated and the synthetic suite rerun.
- No app execution, staging, commit, production mutation, or automatic browser-transfer retry was performed for these checks. Synthetic test files remain in their reported temporary directories.

MultiShock paths changed (all uncommitted):

```text
Acceptance/ReportingAcceptance.targets
Acceptance/ReportingAcceptanceOptions.cs
Acceptance/ReportingAcceptanceExecution.cs
Acceptance/ReportingAcceptanceRuntime.cs
Core/App.xaml.cs
Core/MauiProgram.cs
Platforms/Windows/App.xaml.cs
MultiShock.csproj
Tests/MultiShock.Tests.csproj
Tests/Services/ReportingAcceptanceTests.cs
Tests/Services/ReportingAcceptanceExecutionTests.cs
Services/BuildInfo.cs
README.md
```

## Historical user-operated private DSN save (completed; no rerun needed)

In the authenticated collector, open **Multishock > Settings > Monitoring > Sentry SDK DSNs**. Run Windows PowerShell 5.1 with `tests/SdkProbes/MultiShockPreparation/save-fullapp-dsns.ps1 -CollectorHost <verified-lowercase-hostname>`. At its hidden prompts, use **Copy Release builds DSN**, then **Copy Debug builds DSN**. Do not paste values into commands or chat.

The script saves only `%LOCALAPPDATA%\MultiShockAcceptance\collector-dsns.json`, refuses an existing file, validates HTTPS/host/projects 1 and 2/distinct lowercase-hex keys, and creates a protected current-user/SYSTEM file ACL. It does not launch anything. This is plaintext protected by file permissions, not encrypted storage. Current clipboard contents are replaced after each prompt; clipboard history/sync and managed-memory copies are not guaranteed erased. Removal of the real test DSN file after use remains a separately tracked approved cleanup step, not performed by this setup script.

## Source-DSN increment

Owner instruction: "just put these is the csproj or something so they dont need to be put as secrets for the actions worker and can be update with a source code update". Only the two derived client ingestion DSNs are approved for source inclusion, not dashboard/account credentials. The server binds each derived key to app/project/build and keeps dashboard authentication separate. Rotating the underlying app key invalidates installed clients until updated DSNs reach them.

The private transfer file was validated without printing its values; its ACL is restricted to the current user and SYSTEM. A private real-profile backup contains 237 files, each copy SHA256-verified, and remains outside both repositories. This is not a complete framework/profile or installed-program backup. The source defaults preserve the explicit `SentryDsn` build override. The acceptance build refuses a selected DSN different from its source release value. Obsolete fingerprint metadata, runtime private-file parsing and eight corresponding parser tests were removed; validation is now source-based. No telemetry transport, device behavior or ordinary startup sequence was replaced.

Current checks: 316 full-suite tests passed (38 acceptance tests); three MSBuild configuration/compile-selection checks and nine build-gate checks passed. Final candidate and separate normal Release builds passed with zero warnings/errors; fresh targeted independent review cleared the final cancellation fix. The normal Release assembly excludes the acceptance runtime. Historical synthetic or fingerprint-bound candidates must not be used for this increment. Server-side automatic WinUI capture/classification, real delivery and offline replay remain unverified for this full app. Actual process exits are recorded below. The approved private transfer file was removed after its values matched source; the backup is preserved.

Independent review caught the incorrect exact-four progress count: the real app includes plugin discovery and nested device steps. The gate now accepts the actual completed progress while rejecting failed/unfinished steps, and explicitly checks `StartupCoordinator.IsCancellationRequested` because cancellation is swallowed into a completed task. A read-only property exposes that existing state without changing startup behavior. Regression tests use the real coordinator for discovery plus nested steps, partial/failing progress, early cancellation and cancellation inside the final flow stage. That property in `MultiShock.Core/Services/StartupCoordinator.cs` is one additional changed source path. The actual coordinator is started before `CreateMauiApp` returns, before the later `Window.Created` acceptance callback.

## Modified full-app runs, 2026-11-13 UTC

These runs used the real profile and ordinary startup, with owner approval of the startup risks. The test made no deliberate device/flow commands. This is an unpackaged modified full app with a test-only trigger, not acceptance of an unchanged published/installed release. No old reduced harness or browser transfer was retried. The first attempted launch precheck stopped before process creation because PowerShell omitted `-Force` when inspecting hidden AppData; fixing that filesystem inspection was a technical correction, not an approval-denial retry.

Historical candidate for the two runs below (subsequently rebuilt; these hashes and coordinates must not identify the corrected candidate): `bin/ReportingAcceptance/net10.0-windows10.0.22621.0/win-x64/MultiShock.exe` in the MultiShock checkout. Portable PDB identity matched the DLL; the synthetic throw is `ReportingAcceptanceRuntime.ThrowSynthetic`, `ReportingAcceptanceRuntime.cs:129:9`.

| Artifact | SHA256 |
|---|---|
| MultiShock.exe | `850a09ef9af502b01f26c906efc73818c7dffa4745996065ccbb23373600d8d5` |
| MultiShock.dll | `f5331635228636fc106f1ea49765e5751eb1fa3c1c16174e586554683c5eb264` |
| MultiShock.pdb | `8bf520764e564afc5f96c18cd96a39ee032f7147d685005f24864b0508faa2f1` |
| MultiShock.Core.dll | `4b3551931d042fdefc9c7ddb0746f898a95270c6a1612507290e488ef7383af6` |

- Handled run `0d336410-570b-4315-ae6a-255e7f27feb9`: process 68116, started 03:43:14.692 UTC and exited by 03:43:30.530, exit **0**, no observer timeout. Startup completed with login true; local SDK event ID `38f68f58d27f45409d9305c6ad5bb4db` at 03:43:29.538. Flush returned, quit requested, and window-destroying were recorded. This does not by itself establish HTTP acceptance or delivered fields.
- UI-unhandled run `f724e35e-4604-4b4f-94bf-f94ce1cb2d20`: process 71264, started 03:44:01.244 UTC and exited by 03:44:14.043, exit **-1073741189**, no observer timeout. Successful startup/login and synchronous unhandled callback queueing at 03:44:08.685 were recorded. This path has no manual CaptureException. Verify the delivered event and SDK mechanism before claiming automatic capture acceptance; crash termination alone is insufficient.

Primitive observer results are under `%LOCALAPPDATA%/MultiShockAcceptance/observations/<run-id>.json`; stage evidence/cache is under `runs/<run-id>`. No process objects, profile logs or DSNs were serialized into observer evidence. Both app processes exited; no user process was terminated and no temporary service was started. The private profile backup remains intact. No profile restore/reset, source commit, push, app release, server deployment or GlitchTip stop occurred.

Historical remaining gates before the observer report below: independently verify both synthetic events in the combined collector (method, basename, line/column, release/environment and handled classification), observe actual session outcomes/client reports if present, and separately exercise supported offline replay. Existing SDK/server synthetic tests do not substitute for those full-app checks. A fresh cache per run prevents old-user-cache replay but does not test offline durability. The production observer must avoid exporting unrelated startup reports or account information.

## Investigation and local corrections, 2026-11-13 UTC

The production observer confirmed handled event `38f68f58d27f45409d9305c6ad5bb4db` and its trace. After refresh, the unhandled run `f724e35e-4604-4b4f-94bf-f94ce1cb2d20` had no event; both sessions remained active, with zero crashed/completed. The unhandled process exit is therefore **not successful telemetry acceptance**.

Read-only local evidence: both run caches retained only initial `.session` updates (`init=true`, sequence/errors/duration zero, no terminal status), plus installation metadata; neither contained an error-envelope file. The application logs contained no matching SDK diagnostic or shutdown entries. No session/user identifiers or profile log contents were exported. Evidence remains under `%LOCALAPPDATA%/MultiShockAcceptance/observations/<run-id>.json` and `runs/<run-id>/`; all caches and the private profile backup are preserved.

Three separate findings and corrections:

1. The original raw DispatcherQueue callback can bypass the XAML global exception event, as documented in [Microsoft issue 8940](https://github.com/microsoft/microsoft-ui-xaml/issues/8940). This is consistent with the observed crash but does not establish the exact native failure or SDK hook attachment. The test-only trigger now throws from a retained one-shot XAML timer. A native event observer records the existing Handled flag without changing it; a test-only Sentry BeforeSend callback records the synthetic event UUID and returns the same event. Neither path manually captures the unhandled exception. Both require a new separately coordinated runtime check.
2. Flush alone does not end a session. The real SDK regression reproduces this. Normal Windows last-window destruction now calls EndSession(Exited) once, followed by a two-second SDK flush with a three-second outer wait. Failure/timeout is observable without logging exception details. The flush wait is bounded; synchronous session cache I/O is not a hard OS deadline. Actual Windows Destroying delivery and shutdown completion remain unverified.
3. Sentry 6.8 serializes a distinct `unhandled` session status for a nonterminal unhandled exception. The old collector rejected its nine-character value. The parser, storage and migration 0025 now preserve it separately, with monotonic severity `crashed > unhandled > abnormal > exited > ok`. It is neither a confirmed crash nor an eligible crash-free completion. The health API adds `unhandled` to summary/group counts; the UI now displays it separately, with matching eligibility copy. Migration rollback refuses to narrow the schema while unhandled rows remain. This compatibility fix is local, not production-deployed, and is not evidence that it caused the missing raw-dispatcher event.

Verified without launching an app:

- Full MultiShock suite: **318 passed, 0 failed/skipped**; focused acceptance/shutdown subset: **40 passed**.
- Corrected acceptance candidate and separate normal Release builds: **0 warnings/errors** each.
- Full SDK reliability suite: **30 passed, 0 failed, 1 skipped**. The collector-host replay case skipped because its explicit disposable host was not configured. Ten targeted cases pass, including six new lifecycle/cache cases and actual SDK terminal serialization.
- PostgreSQL session integration/migration tests: **2 passed, 0 failed/skipped**. Coverage includes concurrent mixed replay, status monotonicity, error preservation, build separation, invalid-batch atomicity, 24-to-25 row preservation and refusal of lossy rollback. An initial seed omitted required `has_events`; corrected and rerun successfully.
- The SDK lifecycle cases use a socket-free transport and the real SDK disk cache. They verify flush versus EndSession, offline terminal-event/session replay, acknowledged replay removal, and SDK-derived crashed/unhandled states from synthetic exception mechanism flags. They do not exercise WinUI hooks, process crashes or the real app's cross-process replay.
- Fresh independent source review found no blocking defect. Both repository diff-whitespace checks passed.

The newly created synthetic PostgreSQL fixture was stopped and removed after its exact identity was checked. Previously blocked cleanup targets were not touched. No app was launched, no production access/change occurred, and nothing was staged, committed or pushed for these corrections. Existing startup device/plugin risks and prior launch/transfer denials remain unchanged.

The UI increment is complete; next acceptance requires coordinated review/deployment of migration 0025, followed by separately authorized corrected full-app runs with production observation. Re-establish candidate/PDB hashes and real sequence points first; the historical hashes above are superseded. Correlate native-hook evidence, SDK BeforeSend UUID, delivered mechanism/classification, terminal session and actual process exit. Offline replay needs a separately coordinated same-cache scenario; fresh-per-run caches do not prove it. GlitchTip remains running.

## Integrated verification follow-up

Opus 5.5 high completed the frontend increment after a provider interruption: 133/133 full unit tests, typecheck/build and 8/8 mocked session-health browser tests passed, with no failures/skips. The initial interrupted full-suite exit 4 did not reproduce. The UI counts Unhandled separately and does not substitute zero when the API field is absent; ship backend and frontend together. No new real-backend browser run has occurred.

Astra ran the full service-free Aptabase unit suite: 346/346 passed. Previously completed unchanged SDK, MultiShock and focused database checks were not repeated. The image acceptance script now expects schema25 and asserts mixed error/unhandled-session replay, unchanged error count, authenticated health access, denominator exclusion and session persistence after replacing the container. Its syntax passed after correctly decoding the existing UTF-8 BOM; an initial string-compilation check had rejected that BOM. The older database recovery test's latest-schema assertion was also updated from24 to25. These updated checks subsequently passed in the isolated acceptance below.

Historical environment blocker, now resolved: Docker's Linux-engine pipe was absent. Under the parent's explicit direction, standard `docker desktop start --timeout 45` succeeded with no settings/security changes. No unrelated service restart command was issued; Docker itself brought up three existing containers on startup, which were left untouched.

Migration25 widens session status to varchar(9), replaces its allowed-status constraint and leaves existing data intact. It needs the normal PostgreSQL DDL lock during startup. Apply the matching backend and frontend together, with a current restorable backup and coordinated rollout. Rolling an old binary back after new unhandled writes is not accepted: old ingestion rejects these envelopes and old summaries do not explain their status. Down migration explicitly refuses such rows; do not delete or coerce them to force rollback. An approved restore would have its own post-backup data-loss implications. No production migration, deployment or rollback was performed here.

## Final isolated server acceptance, 2026-11-13 UTC

- Full integration suite: **83 passed, 0 failed, 1 skipped**, exit0. Included PostgreSQL15 and ClickHouse23.8.4.69 synthetic backup/restore, existing analytics/identity preservation, migration24-to25 and lossy-downgrade refusal. The skipped native-worker case requires explicitly configured worker images and fixtures; it was not implicitly substituted. Results: `%TEMP%/aptabase-unhandled-full-integration.trx`.
- Production image stages built sequentially: server, webapp, final. Local tag `aptabase-local:unhandled-session-candidate`, Linux/amd64, built from current uncommitted worktree at HEAD `b2e759ce5636a7369fa4e890cae34b7c801655d0`. It is not a remotely published or same-commit CI artifact.
- Exact Docker local image/index identity: `sha256:55db95ec9c59eddb12a17252c2c8ef230dffd1451fd73f0f0317975401cb3641`.
- Platform manifest: `sha256:680e7154a476f4e9a054d9664dac136869ad153920e318367dce513f6c2db917`; config: `sha256:f362be7352287cb069a27d0505fcb77b42321cfdaeb75c3bd8f0e4a6d47fca4c`. These are distinct identities; no registry digest or anonymous pull acceptance is claimed.
- The exact local image passed **9/9 grouped image checks**, exit0, run `aptabase-image-251997957d13`: production readiness/migrations, system rejection of the private test certificate, schema25/analytics tables, real synthetic mail-catcher login and token replay protection, headers, ingestion/dedup/auth, mixed unhandled-session replay/denominator exclusion, mounted-key/login/session/receipt preservation through container replacement, and logout.
- TLS used a temporary one-hour self-signed certificate for127.0.0.1, trusted only by the test client; normal hostname/certificate verification remained on. No OS trust changes. This does not verify public ingress, Coolify or its TLS configuration.
- Build logs: `%TEMP%/aptabase-unhandled-{server,webapp,final}-build.log`; runtime log: `%TEMP%/aptabase-unhandled-image-acceptance.log`. The runtime base remains pinned to .NET10.0.15. No fresh OS vulnerability scan was performed; previous residual-advisory decisions are not superseded by passing application tests.
- Fresh Opus read-only review found no semantic/regression defect; cached React Doctor0.9.22 changed-file and full-touched-file diagnostics both exited0 with zero errors/warnings. Score calculation was disabled. No successful frontend tests/builds were repeated for that review.

Cleanup completed for all newly created integration/image containers and image-test networks. Read-only checks found no remaining matching containers or listeners on55470/55471/55539-55542. Test images, logs and the small synthetic fixture configuration remain local. Docker remains running; unrelated containers were not stopped. Previously denied cleanup targets, real profile, run caches and backup were untouched. No MultiShock launch, commit, push, publication, production change or GlitchTip stop occurred.

Remaining acceptance: coordinated reviewed-source/CI/publication and production migration approval; actual corrected MultiShock WinUI hook delivery, terminal-session delivery, process shutdown and same-cache offline replay. The real-backend browser suite was not rerun for this increment; mocked browser coverage and actual-container HTTP acceptance are separate evidence. Full application/device/runtime parity is not established.
