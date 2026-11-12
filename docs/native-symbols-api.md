# Native symbols: function-only ARM64 acceptance

Owner approved text crash reports plus owner-uploaded symbols; raw dumps remain excluded. The backend implements ELF64 little-endian ARM64 ET_DYN with a GNU build ID and sized function symbols. Function names/ranges only: no DWARF lines/inlining/demangling, Apple dSYMs, PDBs, Breakpad, source maps, ZIP/TAR, minidumps, uploaded executable invocation or external symbol fetching. Backend and frontend are integrated and accepted locally at the boundaries recorded below.

## Owner artifact API

All artifact routes require authenticated app ownership. Shared readers and public ingestion keys cannot manage artifacts. No raw artifact download or source preview exists. Explicit metadata projections never return bytes or the private function index.

- `GET /api/v0/apps/{appId}/native-symbols?buildMode=release|debug` returns `{enabled,artifacts:[{id,buildId,status,sizeBytes,createdAt,expiresAt}],limits:{maxArtifactBytes:8388608,maxAppBytes:33554432,maxAppArtifacts:8}}`. UUID id; buildId is null until validated; status pending/ready/invalid. Eight artifacts and 32 MiB per app across both modes. `enabled=false` disables new uploads, but metadata/deletion remain available.
- `POST` same route: raw `application/octet-stream`, maximum 8 MiB, no filename/path. Returns202 `{artifact:...}`. Duplicate app/build/content is idempotent. Basic ELF header/type/architecture validation happens before storage; detailed parsing is isolated and asynchronous. Wrong mode/header=>400, too large=>413, wrong content type=>415, app budget=>409, processor unavailable=>503. Never treat a pending artifact as ready.
- `DELETE /api/v0/apps/{appId}/native-symbols/{id}?buildMode=...` returns204 or404 if absent in that scope. Removes the artifact/index; original reports/history remain. UI confirms the build ID or pending artifact ID.
- Anonymous=>401; non-owner=>403. No binary retrieval API, even for owners.

Retention follows the app's 1-30-day monitoring retention from receipt. Shortening retention affects reads immediately; raising it never extends original artifact expiry. Cleanup removes at most100 expired/deleted-app artifacts every five seconds, even when processing is disabled. Physical app deletion cascades. Confirmed monitoring purge includes artifacts/indices; preview `counts.symbols` counts stored artifacts in both modes. The app row lock serializes uploads and budgets with purge/settings changes. Rejected artifacts retain metadata and original budget charge until deleted/expired; their bytes are erased.

## Isolated processing

The managed worker reads only `/input/artifact.elf`, produces a function index and never executes uploaded code, decompresses debug sections, reads source paths or contacts symbol servers. Docker receives an argument list, a pinned already-local image ID and `--pull never`. It has no network, read-only root/mounts, non-root UID/GID65534, all capabilities dropped, no privilege escalation, 128 MiB memory with no additional swap, 0.5 CPU, 32 PIDs, a 12-second worker self-exit deadline (survives collector failure), a 15-second dispatcher wall deadline and bounded stdout/stderr (16 Mi/64 Ki characters). One database advisory transaction lock allows only one worker across collector instances.

Worker failure/timeout marks an upload invalid; delete/re-upload to retry. Collector shutdown cancels and rolls pending work back for retry. Container removal has a separate five-second deadline. If removal cannot be confirmed, that processor disables itself until restart and logs only the generic failure type, instead of launching another container. Scratch names are generated and removed without recursive traversal. Persistence failure rolls back the pending job. No generic uploaded executable or client-selected command is invoked.

Binary contents and function indices are private PostgreSQL data, not public webroot files. Treat the database and backups as sensitive: ELF files can contain private paths/debug information and function names can be confidential. Authorized app readers see sanitized function names only via a retained error occurrence. No source code is extracted or exposed. This is not encryption-at-rest or forensic-erasure functionality.

### Operator setup: explicit opt-in

Default processing is disabled and uploads return503. Publish trusted repository code with `dotnet publish tools/NativeSymbolWorker --no-self-contained -m:1 -o <dedicated-worker-directory>`. Set `NATIVE_SYMBOL_WORKER_DIRECTORY` to that absolute directory (commas unsupported by Docker mount syntax), and `NATIVE_SYMBOL_WORKER_IMAGE` to an exact local `sha256:` image ID with a compatible .NET10 runtime. The acceptance run used cached ASP.NET10 image `sha256:1fa23fc4872d95fd71c2833ebe65d7e84a43b2d51a31d119516852f13d9505a7`; this is local evidence, not a universal installation digest. Keep only trusted published code/dependencies in that directory.

The process needs pre-existing Docker access and paths understood by that daemon. This feature does not mount a daemon socket, change permissions, pull images or install services. Docker access is a privileged operator trust boundary: do not expose its API or grant it to an untrusted webapp as a shortcut. Local Windows/Docker Desktop was tested; a separate remote dispatcher, rootless production topology and production packaging are not implemented or verified. Leave processing disabled until the self-host deployment supplies this boundary. Text reports are still accepted and explicitly unresolved without symbols.

## Text reports and reads

Native JSON `POST /api/v0/error` accepts `nativeFrames:[{buildId,address}]` with an explicit eventId. Build IDs are lowercase GNU hex, 16-32 bytes. Addresses are lowercase `0x` hex ELF virtual addresses, not process absolute PCs; 1-100 frames. No arbitrary tombstone/text parser or Sentry native adapter is implied.

```json
{
  "eventId":"4df0e1ab-f9b0-4a8c-8180-62c5fa9ed785",
  "errorType":"SIGSEGV",
  "errorMessage":"Synthetic native failure",
  "kind":"crash",
  "severity":"fatal",
  "platform":"android",
  "isDebug":false,
  "nativeFrames":[{"buildId":"bbe2c0d49e25325c5e51f939ec1eac37cc159800","address":"0x2e0"}]
}
```

Use the existing App-Key header. This fixture resolves to `aptabase_symbol_probe +4`. Actual clients must capture/unwind elsewhere and normalize PCs using the correct module load bias. Do not assume every crash reporter uses the same relative-PC convention. `nativeFrames` replaces a supplied free-text stack for storage/grouping. Retries preserve the first payload. Raw frame order/build IDs/normalized addresses determine grouping; uploading/deleting symbols never merges, splits, reopens or renumbers issues. A different binary build/address can create a different issue; cross-build semantic grouping is not implemented.

Occurrence rows include `hasNativeFrames:boolean`. On expansion, GET `/api/v0/apps/{appId}/issues/{issueId}/events/{eventId}/native-frames?buildMode=release|debug` returns `{capability:"function-only",frames:[{buildId,address,status,function,offset}]}`. Status is resolved/unresolved/ambiguous. Function and numeric byte offset are null unless resolved. App readers may call this endpoint;403 denies app access,404 means no retained native report for that exact app/build/issue/event. Nothing returns artifact bytes, paths, source or debug data. Resolution is read-time: deletion/expiry immediately removes it without changing history.

Pending/missing/expired/wrong-build symbols or missing functions remain unresolved. Distinct artifacts claiming one build ID are ambiguous rather than guessed. Sentry address-only frames still reject422; this native JSON extension is not Sentry native SDK compatibility. Existing analytics/raw-error behavior is preserved; delivered raw logs retain their separate180-day TTL.

## Acceptance ledger

- [x] NDK29/LLVM21 produced a synthetic ARM64 shared library. Independent LLVM inspection: build ID `bbe2c0d49e25325c5e51f939ec1eac37cc159800`, function `aptabase_symbol_probe` at0x2dc, size24. Isolated managed worker returned the same build ID/range. ARM64 code was never executed.
- [x] Backend unit suite:302passed. Architecture/endianness/type, overflow/truncation, notes/strings, executable section bounds, strict frame IDs/addresses/count validated.
- [x] Full integration suite:76passed,0failed,0skipped,40seconds sequential. Includes existing74 tests plus database/API and real Docker worker acceptance, using dedicated loopback PostgreSQL16, ClickHouse26.8.36.3 and local mail catcher.
- [x] Auth/retry/quota rollback, concurrent upload/count/byte budget, global dispatcher exclusion, app/build isolation, malformed/core input rejection before storage, failed-job rollback, shortened retention/expiry, purge counts/deletion and stable grouping.
- [x] Real worker timeout/output overflow/malformed JSON/nonzero exit/cancellation/malformed ELF and cleanup. No worker containers or scratch directories remained.
- [x] Opus5.5high owner controls, expanded native-frame diagnostics and purge counts integrated.97 frontend unit tests, typecheck/build,34 mocked and34 real-backend desktop/mobile headless tests passed,0failed/skipped. Parent inspected saved desktop controls/mobile frame screenshots without opening a visible browser.

No Apple claim: Windows has LLVM tools, but no xcrun/dsymutil/Apple build host was found. No dSYM fixture/crash path was tested. No device-generated Android tombstone, automatic native capture/unwind, source-line/inlining worker, native SDK delivery, production packaging/load or physical-device acceptance. Existing NuGet advisories remain.

### Reproduce backend acceptance

`tests/SdkProbes/Native/run.ps1` compiles only repository-owned synthetic C with the installed NDK, emits ignored `bin/probe.so` and checks offline LLVM source resolution. It never runs ARM64 code. Publish `tools/NativeSymbolWorker` and trusted test-only `tests/NativeSymbolFaultWorker` separately to their ignored `bin/publish` directories. Never configure the fault worker in a collector deployment.

Set `NATIVE_SYMBOL_TEST_WORKER_DIRECTORY`, `NATIVE_SYMBOL_TEST_FAULT_DIRECTORY`, `NATIVE_SYMBOL_TEST_FIXTURE` (absolute `.so` path), and `NATIVE_SYMBOL_TEST_IMAGE` (exact local image ID). The worker test is explicitly skipped without all four, including ordinary CI: a CI pass alone is not sandbox acceptance. Local acceptance supplied all four and skipped nothing. Supply isolated database/legacy service overrides from `monitoring-roadmap.md`, then run `dotnet test tests/IntegrationTests --no-restore -m:1 --settings tests/IntegrationTests/sequential.runsettings`. Local TRX: `%TEMP%/aptabase-native-acceptance/native-integration.trx`.

## Final integration notes

The first full real-browser run found an old Sentry-DSN test still expecting raw App-Key credentials (2failures and2dependent skips). Its assertion now verifies exact project-scoped64hex DSNs, distinct release/debug keys and agreement with the UI. The final full34-test real run passes with no skips. Review also found stale symbol expiry after retention saves; both build-mode lists now refresh, with a regression test that was verified to fail without the fix. The final worker was republished with its self-exit watchdog; the real worker acceptance reran successfully (1scenario,21seconds), and the final frontend real suite used that worker. Backend unit acceptance was repeated after header/parser hardening:302passed.

React Doctor ran read-only with telemetry/supply-chain calls disabled and included untracked files. No new errors; two new warnings: positional stack-frame keys (intentional to preserve recursive repeated frames), and NativeSymbolsForMode complexity (low-impact maintainability follow-up). An existing ClearMonitoringData complexity warning remains. The full baseline has99 findings and no numeric score was generated; no score-improvement claim. No unrelated frontend cleanup was included.

All task-owned hosts/containers have been stopped and removed, worker scratch directories are empty, and the temporary probe copy was deleted. A combined cleanup command was rejected before execution; fresh target inspection allowed cleanup through the exact container ID and non-recursive deletion of the four verified probe files and empty directories. No permission changes or bypass were used. Screenshots/TRX evidence remain in temporary acceptance folders. No real alert sender exists or was enabled. No push, PR or deployment occurred.
