# Desktop/mobile SDK and native-symbol assessment

This extends checkpoint `7424a81`. It is a capability assessment and tested SDK increment, not a claim of native crash support. No device, emulator, simulator, editor, or visible window was launched. No major toolchain was installed. No upstream implementation source was copied.

## Installed capabilities inspected on 2026-11-11

| Local capability | Verified use | Boundary |
| --- | --- | --- |
| .NET 8.0.401, 9.0.200, 10.0.102/10.0.401 | Existing Sentry .NET 6.10.0 integration runs under .NET 10; now throws a real managed exception and checks PDB-derived source position | Other target frameworks, MAUI/Unity, trimming/AOT and physical devices are not verified |
| Android 36.1.12, iOS 26.2.10191, MacCatalyst 26.2.10191, MAUI Windows 10.0.1 workloads | Read-only workload inventory | An installed iOS workload on Windows is not a usable Mac/Xcode signing/build host; no Apple build was attempted |
| Java 25.0.1 and Maven | Actual Sentry Java 8.64.0 HTTP envelopes, exception capture, uncaught process exception, transaction and span persisted by collector | JVM desktop behavior only; Android lifecycle, ANRs, R8 mappings and NDK integration are not verified |
| Android SDK platforms 28–31, 33–36; NDK 29.0.13846066 / LLVM 21 | Compiled a tiny ARM64 object and resolved an address to its function/source line offline | No executable/device crash, unwind, dump parsing, collector upload or server-side worker |
| Node 24.19.0 | Available for a separate JavaScript SDK/source-map acceptance increment | Availability alone proves no SDK compatibility |
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

## Symbolication decisions still needed

Recommended first server phase: text-only reports plus owner-uploaded build artifacts; defer memory dumps. The owner has been asked whether this is the desired boundary. No answer is presumed.

Before accepting artifacts, decide:

1. **First target and format:** JavaScript source maps with debug IDs, Android R8 mappings, ELF/DWARF build IDs, Windows PDB GUID/age, or Apple dSYM UUIDs. A filename or release string alone cannot safely identify the matching binary. The current ingestion adapter discards debug metadata, so native resolution needs a versioned structured frame/artifact contract, not a formatter patch.
2. **Storage and deletion:** app-scoped immutable artifact metadata, private storage, owner upload authentication distinct from public ingestion keys, upload/decompressed/member-count limits, quotas/disk budget, retention and purge semantics. Source maps can embed complete source; symbols can contain paths and private names. Do not retain arbitrary `sourcesContent` or dump memory by default.
3. **Execution and license:** an isolated, resource-limited parser worker with no external network access or attacker-directed symbol URLs, bounded jobs/cache, recorded engine/version and predictable failure status. Do not run uploaded binaries or invoke a shell with client-derived arguments. Current upstream Symbolicator is FSL-1.1-MIT, not an assumption of unrestricted MIT for the latest release. Select and review a specific release/license and intended distribution before integration; no Symbolicator was installed/copied here.
4. **Issue identity:** store raw bounded frames separately from resolved display frames. Symbol upload/reprocessing must not silently merge, split, reopen or renumber existing issues. Define retention for both and cross-app artifact isolation tests before changing grouping.
5. **Crash-specific privacy:** a minidump may carry process memory and secrets that text regex redaction cannot remove. Collection consent, upload limits, quarantine, retention/deletion, authorization, parser isolation and supported crash reporters need explicit decisions before a dump endpoint exists.

Acceptance for that phase must include wrong-build/missing-symbol behavior, malicious/oversized archives, cross-app reads, redaction, expiry/purge, stable grouping, no outbound fetching, worker time/memory bounds, and real platform-produced artifacts. Native minidumps, symbol uploads, source maps, profiles, replay and standalone spans remain unsupported today. These are outstanding decisions/work, not silently marked completed or permanently excluded from the owner's monitoring goal.

Sources consulted: [Sentry error/mechanism contract](https://develop.sentry.dev/sdk/telemetry/errors/), [stack-frame contract](https://develop.sentry.dev/sdk/foundations/envelopes/event-payloads/stacktrace/), [Java options](https://docs.sentry.io/platforms/java/configuration/options/), [Java 8.64.0 artifact/license metadata](https://repo.maven.apache.org/maven2/io/sentry/sentry/8.64.0/sentry-8.64.0.pom), [Symbolicator architecture](https://getsentry.github.io/symbolicator/advanced/system-architecture/), [symbol lookup](https://getsentry.github.io/symbolicator/advanced/symbol-lookup/), [current Symbolicator license](https://raw.githubusercontent.com/getsentry/symbolicator/master/LICENSE.md). These sources informed the assessment; no implementation code was copied.
