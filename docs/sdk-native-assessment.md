# Desktop/mobile SDK and native-symbol assessment

This extends checkpoint `7424a81`. It is a capability assessment and tested SDK increment, not a claim of native crash support. No device, emulator, simulator, editor, or visible window was launched. No major toolchain was installed. No upstream implementation source was copied.

## Installed capabilities inspected on 2026-11-11

| Local capability | Verified use | Boundary |
| --- | --- | --- |
| .NET 8.0.401, 9.0.200, 10.0.102/10.0.401 | Existing Sentry .NET 6.10.0 integration runs under .NET 10; now throws a real managed exception and checks PDB-derived source position | Other target frameworks, MAUI/Unity, trimming/AOT and physical devices are not verified |
| Android 36.1.12, iOS 26.2.10191, MacCatalyst 26.2.10191, MAUI Windows 10.0.1 workloads | Read-only workload inventory | An installed iOS workload on Windows is not a usable Mac/Xcode signing/build host; no Apple build was attempted |
| Java 25.0.1 and Maven | Actual Sentry Java 8.64.0 HTTP envelopes, exception capture, uncaught process exception, transaction and span persisted by collector | JVM desktop behavior only; Android lifecycle, ANRs, R8 mappings and NDK integration are not verified |
| Android SDK platforms 28–31, 33–36; NDK 29.0.13846066 / LLVM 21 | Compiled a tiny ARM64 object and resolved an address to its function/source line offline | Synthetic ELF upload/function-only isolated worker now verified; no device crash, unwind or dump parsing (see native-symbols-api.md) |
| Node 24.19.0 | Real Sentry Node10.77.1 normal DSN parsing, handled/actual uncaught errors, transaction/span and local source-map enrichment passed | No Electron/browser/React Native/Hermes or server source-map support; see `javascript-sdk-probe.md` |
| Rust cargo/rustc | Commands installed | No Rust SDK/native crash acceptance yet |
| Windows Debuggers directory | dbghelp/dbgcore/symsrv DLLs present | No command-line dump debugger found there; DLL presence does not establish a crash-analysis pipeline |

## Managed diagnostics implemented

The Sentry adapter retains positive integer line/column locations alongside sanitized function names and file basenames. It still discards absolute paths, source code, locals, arbitrary contexts and mechanism data. Negative/zero JVM source positions mean unknown and are omitted. Explicit `mechanism.handled=false` maps to `unhandled`; missing/null/true retains the previous `handled` default. This is not proof of a native crash or of successful delivery after every process failure.

Grouping uses the previous adapter stack representation, excluding the newly displayed positions. This internal input cannot be supplied through native JSON, is sanitized before hashing, and is not serialized into the raw outbox. Existing issue IDs remain stable when lines move between builds. Historical representative issue stacks are not backfilled; recent occurrences show the new locations. Exception chains still use the final exception, not a full causal-tree view. Address-bearing frames without function names now return 422 before writes, rather than collapsing unrelated unresolved native crashes into an `unknown` group.

Fractional millisecond containment now compares decimal sums after finite/range validation, preventing binary addition from rejecting a span that ends exactly at its parent/root boundary. There is no intentional timing tolerance; a genuinely later child is still rejected.

## Reproduce SDK probes

Use only the standalone `tests/MonitoringHost` with a disposable PostgreSQL database. Set `APTABASE_ISSUE_TEST_DATABASE_URL` to that isolated database server and `MONITORING_TEST_WEBROOT` to the absolute built `src/wwwroot` path. Start with `dotnet run --project tests/MonitoringHost --no-restore`; the host binds `127.0.0.1:55440` and expires after 45 minutes. Do not use its synthetic accounts in production.

Run `tests/SdkProbes/Java/run.ps1` in PowerShell. It provisions a synthetic release DSN, resolves the pinned Maven dependency, compiles with `javac`, and runs two small JVMs sequentially with 128 MB heaps and one active processor. The second process intentionally exits 1 from an uncaught managed exception; the runner only passes after its fatal/unhandled occurrence is found. It checks normal error and transaction/span storage, SDK version, source location, redaction and discarded extra/span-description fields. It restores its process environment. The SDK transport rejects DSNs outside the exact disposable loopback target. Synthetic envelopes/classes/dependency metadata stay under ignored `bin`; they are diagnostic test artifacts, not a durable production SDK queue.

The SDK uses its real serializer, compression, authentication and `AsyncHttpTransport`. A test decorator saves synthetic envelopes without modifying them, then delegates to that transport. The uncaught test requires both `UncaughtExceptionHandlerIntegration` and `ShutdownHookIntegration`: removing the shutdown flush caused the test to fail even though capture was invoked. Keep bounded shutdown flushing in desktop applications; do not infer Android crash durability, power-loss durability or offline replay from this test.

Java 8.64.0's data-collection categories are explicitly disabled (user info, paths, SQL data, bodies, cookies, query strings, headers and GraphQL content); session/client reports and extra integrations are disabled. Only the test deliberately adds synthetic PII to verify collector redaction. Applications should scrub free text before capture and select their own sampling rate rather than copy the probe's 100% trace sampling. Use the small probe as executable configuration evidence, not as a packaged mobile SDK. The Maven artifact declares MIT; the dependency is test-only. No Java server runtime dependency was added.

Run `tests/SdkProbes/Native/run.ps1` for the offline ARM64 DWARF check. It accepts an explicit installed `NdkPath`, compiles only the repository's tiny `probe.c`, and invokes `llvm-symbolizer` against that local object. It never runs ARM64 code, searches user binaries, downloads symbols or accepts untrusted artifacts. This proves a local toolchain component, not an upload service.

## Approved symbolication phase and remaining decisions

Approved first server phase: text-only reports plus owner-uploaded build artifacts; defer memory dumps. Owner answered "go ahead with that then". Implementation/acceptance progress and the owner-controls contract are in `native-symbols-api.md`. This approval does not imply Apple toolchain coverage or authorize raw dumps.

Implemented phase choices and remaining boundaries:

1. **First target and format:** ARM64 ELF64 little-endian shared libraries with exact GNU build IDs, function names and extents. Explicit native JSON frames carry build ID and normalized ELF address. This does not alter the Sentry native adapter. R8/source maps/PDB/dSYM and DWARF source-line/inlining need separate readers and verified fixtures.
2. **Storage and deletion:** implemented private PostgreSQL artifact/index storage, owner management separate from public ingestion keys,8 MiB/32 MiB/8-artifact bounds,1-30-day retention and purge. No archives/decompression accepted. Source maps can embed complete source; symbols can contain paths and private names. Do not retain arbitrary `sourcesContent` or dump memory by default.
3. **Execution and license:** implemented a repository-owned bounded ELF function reader in an opt-in Docker worker, without external networking or attacker-directed paths. No upstream parser implementation was copied. The local pinned image and repository commit identify the runtime/reader; no cache or external symbol engine is used. Do not run uploaded binaries or invoke a shell with client-derived arguments. Current upstream Symbolicator is FSL-1.1-MIT, not an assumption of unrestricted MIT for the latest release. Select and review a specific release/license and intended distribution before integration; no Symbolicator was installed/copied here.
4. **Issue identity:** raw frames determine immutable issue identity; resolution is computed per retained occurrence at read time. Upload/delete/expiry never change IDs/counts/status. Cross-build semantic grouping remains open; exact native build/address changes can create a new group. App/build and retention tests passed.
5. **Crash-specific privacy:** a minidump may carry process memory and secrets that text regex redaction cannot remove. Collection consent, upload limits, quarantine, retention/deletion, authorization, parser isolation and supported crash reporters need explicit decisions before a dump endpoint exists.

Acceptance for that phase must include wrong-build/missing-symbol behavior, malicious/oversized archives, cross-app reads, redaction, expiry/purge, stable grouping, no outbound fetching, worker time/memory bounds, and real platform-produced artifacts. Native minidumps, source maps, profiles, replay, standalone spans and Apple symbolication remain unsupported. ARM64 ELF function-only artifact uploads/text diagnostics are the bounded implemented exception. These are outstanding decisions/work, not silently marked completed or permanently excluded from the owner's monitoring goal.

Sources consulted: [Sentry error/mechanism contract](https://develop.sentry.dev/sdk/telemetry/errors/), [stack-frame contract](https://develop.sentry.dev/sdk/foundations/envelopes/event-payloads/stacktrace/), [Java options](https://docs.sentry.io/platforms/java/configuration/options/), [Java 8.64.0 artifact/license metadata](https://repo.maven.apache.org/maven2/io/sentry/sentry/8.64.0/sentry-8.64.0.pom), [Symbolicator architecture](https://getsentry.github.io/symbolicator/advanced/system-architecture/), [symbol lookup](https://getsentry.github.io/symbolicator/advanced/symbol-lookup/), [current Symbolicator license](https://raw.githubusercontent.com/getsentry/symbolicator/master/LICENSE.md). These sources informed the assessment; no implementation code was copied.


## Windows priority and source-location increment (2026-11-12)

The owner clarified the practical goal as one Aptabase login replacing a separate GlitchTip dashboard for desktop apps. `windows-monitoring-migration.md` is the current acceptance/migration guide: Sentry.NET6.8.0 Release/Portable-PDB handled method/basename/line353/column17 and actual process-unhandled delivery passed; Sentry.Maui6.8.0 Windows library compilation passed without launching an app. Windows Rust1.98 actual panic file/line/column and retry deduplication also passed. No current MultiShock/Luma integration was modified. Android ELF does not cover either Windows app; actual MAUI/WinUI and Tauri runtime acceptance remains the priority gate.

Read-only T3 device inventory found only stopped Android emulators (Medium Phone API35 and Medium Tablet API34), no physical device; none was opened. Local iOS was unavailable because it requires macOS/Xcode. T3 links do expose a reachable darwin/arm64 Mac identity, but no command/device-host route was available to inspect Xcode. No Apple build, dSYM, simulator or personal-device interaction was performed. Windows iOS workloads are not evidence of Xcode. These are unverified capabilities rather than claims that no Mac exists.

The optional server GNU2.41 source worker now resolves basenames/lines/inlined chains for bounded synthetic ARM64 ELF fixtures, with strict work/input/output/time/memory limits and no external fetches. Pending uploads remain private; processed binary contents are cleared by migration0023 and each job. See `native-symbols-api.md` for opt-in setup, function-only fallback, privacy limitations and current evidence. This supersedes the earlier source-line/inlining gap only for that narrow format, not PDB/dSYM, device-produced crashes or arbitrary application-sized DWARF.
