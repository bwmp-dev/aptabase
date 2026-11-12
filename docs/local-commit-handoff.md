# User-operated local commit and release handoff

Freshly reconciled on2026-11-12 from both repositories' tracked diffs and individual untracked paths. Both indexes were empty. No generated build output, package cache, credentials, SDK source checkout or temporary fixtures are included. Existing authorized telemetry/UI/dependency changes are intentionally preserved. This is a path inventory, not a byte-identity safety attestation or permission to include future changes automatically. Independent review hashes from before these corrections are superseded for changed files; rereview the current content before any commit.

The prior automated commit denial remains in force. The commands below are solely the requested user handoff, not an executable script or alternate agent route. No staging/commit/push command was run to prepare this file.

## Aptabase: 27 explicit files

Run these blocks yourself, in order, in the same PowerShell window. They are instructions only; the agent has not executed staging or commit. Stop if any guard fails or the review contains an unrelated change. Do not push afterward.

```powershell
Set-Location 'D:\Coding\public\aptabase'
if ((git branch --show-current) -ne 'feat/error-issues' -or (git rev-parse HEAD) -ne 'b22d96422320ae3bf115af77d5b87aa8391d6261') { throw 'Checkout changed; reconcile before staging.' }
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) { throw 'Index is not empty; preserve and review existing staged work first.' }
$files = @(
    '.github/workflows/ci.yml'
    '.github/workflows/fork-release.yml'
    'Dockerfile'
    'docs/coolify-upgrade-readiness.md'
    'docs/fork-release-handoff.md'
    'docs/glitchtip-retirement-readiness.md'
    'docs/image-os-assessment.md'
    'docs/local-commit-handoff.md'
    'docs/maui-unhandled-manual-test.md'
    'docs/monitoring-changed-files.txt'
    'docs/monitoring-roadmap.md'
    'docs/production-acceptance.md'
    'docs/sentry68-delivery-acceptance.md'
    'docs/windows-monitoring-migration.md'
    'src/package-lock.json'
    'src/package.json'
    'src/vite.config.ts'
    'src/webapp/components/Markdown.test.tsx'
    'src/webapp/features/analytics/LiveSessionDetailsPage.test.tsx'
    'src/webapp/features/analytics/LiveSessionDetailsPage.tsx'
    'tests/ImageAcceptance/requirements.txt'
    'tests/ImageAcceptance/run.py'
    'tests/IntegrationTests/MonitoringRecoveryTests.cs'
    'tests/IntegrationTests/SentrySessionTests.cs'
    'tests/SdkProbes/MultiShockPreparation/evidence.ps1'
    'tests/SdkProbes/MultiShockPreparation/test-evidence.ps1'
    'tests/SdkReliability/EnvelopeInventoryTests.cs'
)
git status --short
git diff --stat -- $files
git diff -- $files
```

The last diff omits untracked file contents. The next block stages only the literal paths above, then displays their complete staged diff, including new files. Review that diff before the separate commit command. Do not use the old `monitoring-changed-files.txt` to choose files.

```powershell
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) { throw 'Index changed; stop and review.' }
git add -- $files
if ($LASTEXITCODE -ne 0) { throw 'Staging failed; inspect without resetting.' }
git diff --cached --check
git diff --cached --stat
git diff --cached -- $files
```

Only after accepting that review:

```powershell
if ((git branch --show-current) -ne 'feat/error-issues' -or (git rev-parse HEAD) -ne 'b22d96422320ae3bf115af77d5b87aa8391d6261') { throw 'Checkout changed; stop.' }
$staged = @(git diff --cached --name-only)
if (@(Compare-Object ($files | Sort-Object) ($staged | Sort-Object)).Count) { throw 'Staged path set differs from the reviewed allowlist; stop and review.' }
git diff --quiet -- $files
if ($LASTEXITCODE -ne 0) { throw 'Worktree changed after staging; stop and review the content again.' }
git diff --cached --check
if ($LASTEXITCODE -ne 0) { throw 'Resolve staged whitespace errors before committing.' }
git commit -m 'Prepare verified monitoring release and bounded runtime evidence'
git status --short
git log -1 --format='%H %s'
```

A commit does not prove a clean tree: report any remaining files rather than discarding them. These instructions create no branch, publish nothing and do not change history beyond the one user-operated local commit.

## MultiShock: 10 explicit files

Run these blocks yourself, in order, in the same PowerShell window. They are instructions only; the agent has not executed staging or commit. Stop if any guard fails or the review contains an unrelated change. Do not push afterward.

```powershell
Set-Location 'D:\Coding\PiShock\MultiShock\MultiShock'
if ((git branch --show-current) -ne 'main' -or (git rev-parse HEAD) -ne 'a27d356d38f5a403486ea71f69c690df53510cea') { throw 'Checkout changed; reconcile before staging.' }
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) { throw 'Index is not empty; preserve and review existing staged work first.' }
$files = @(
    '.github/workflows/build.yml'
    '.github/workflows/test.yml'
    'Components/Pages/AppSettings.razor'
    'Core/MauiProgram.cs'
    'MultiShock.Core/MultiShock.Core.csproj'
    'NuGet.Public.Config'
    'PluginSdk.Source.json'
    'README.md'
    'scripts/test-plugin-sdk.ps1'
    'scripts/verify-plugin-sdk.ps1'
)
git status --short
git diff --stat -- $files
git diff -- $files
```

The last diff omits untracked file contents. The next block stages only the literal paths above, then displays their complete staged diff, including new files. Review that diff before the separate commit command. Do not use the old `monitoring-changed-files.txt` to choose files.

```powershell
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) { throw 'Index changed; stop and review.' }
git add -- $files
if ($LASTEXITCODE -ne 0) { throw 'Staging failed; inspect without resetting.' }
git diff --cached --check
git diff --cached --stat
git diff --cached -- $files
```

Only after accepting that review:

```powershell
if ((git branch --show-current) -ne 'main' -or (git rev-parse HEAD) -ne 'a27d356d38f5a403486ea71f69c690df53510cea') { throw 'Checkout changed; stop.' }
$staged = @(git diff --cached --name-only)
if (@(Compare-Object ($files | Sort-Object) ($staged | Sort-Object)).Count) { throw 'Staged path set differs from the reviewed allowlist; stop and review.' }
git diff --quiet -- $files
if ($LASTEXITCODE -ne 0) { throw 'Worktree changed after staging; stop and review the content again.' }
git diff --cached --check
if ($LASTEXITCODE -ne 0) { throw 'Resolve staged whitespace errors before committing.' }
git commit -m 'Separate diagnostics consent and pin official public SDK'
git status --short
git log -1 --format='%H %s'
```

A commit does not prove a clean tree: report any remaining files rather than discarding them. These instructions create no branch, publish nothing and do not change history beyond the one user-operated local commit.

## Public SDK source/license assessment

Authoritative public package: https://www.nuget.org/packages/PiShock.MultiShock.PluginSdk/1.16.0 (ownerPiShockInc). Public package identity/signature/hash and commit were verified in `fork-release-handoff.md`. New read-only inspection confirms no `.css`, `.razor`, `.cshtml`, `.cs`, `.pdb` or Source Link JSON entries; the only static assets are six JavaScript files. The public `https://www.nuget.org/api/v2/symbolpackage/PiShock.MultiShock.PluginSdk/1.16.0` endpoint returns404. This is not proof that every conceivable source artifact is unavailable; it means there is no verified public package/symbol source route here.

The actual Tailwind core configuration scans `../PluginSdk/**/*.{razor,html,cshtml}` and deliberately has no safelist. Package XML docs/README snippets or decompiled DLL strings are not a complete supported replacement for that markup. Do not remove the source scan or silently reuse stale CSS merely to pass CI. No JavaScript/frontend changes were made. The smallest current path is the already referenced `PUBLIC_REPO_TOKEN` reading the exact SDK source commit for the styles job only. An alternative requires the SDK publisher to release a complete stylesheet or supported class manifest/source artifact; that is a publisher change, not something the consumer can truthfully infer. No previously rejected GitHub/private-feed route was retried.

The package README states MIT; there is still no full copyright/permission notice or nuspec license expression. Obtain the authoritative notice from the SDK publisher/rights holder. Do not invent copyright ownership, a notice or a new license. Compilation/tests already use the public exact package without a token; source access is only a stylesheet CI gate.

## Publication and production gates for parent

1. **Reviewed source and remote CI:** after permitted local commits, parent coordinates the first push/CI. MultiShock `main` push can trigger Release followed by Beta Release; do not push it just to test. Existing source-token access and self-hosted runner acceptance are unverified. Aptabase upstream registry/AWS/Tinybird mutation jobs are guarded to the upstream repository; the fork publication workflow is manual and restricted to `bwmp-dev/aptabase`.
2. **Immutable fork publication:** confirm public versus private `ghcr.io/bwmp-dev/aptabase` and existing Coolify pull access before publishing. The prepared manual workflow checks a successful same-SHA, same-repository push run of `.github/workflows/ci.yml`, downloads its accepted amd64 image tar and publishes that existing artifact. It does not rebuild or deploy. Remote execution and registry permissions remain unverified. Record the registry digest after actual publication, not the local dirty-worktree digest as though it were published. The local accepted image remains `sha256:c3e05c596bf555edd60c578525d3fe1c6e92c92a034e8d07562753d6ac748f9e`.
3. **Existing production protection:** parent must reconcile pending configuration and verify recoverable backups of both stores plus configuration/key material using a consistent mechanism and isolated restore. Operational inventory, administrative identifiers and backup configuration remain in the private handoff. Synthetic local restores are not production backups.
4. **Rollback and migration:** retain the exact previous image reference in the private operational handoff. Startup migrations change schemas; reverting only the image is not verified recovery. Plan write quiescence, matched pre-upgrade backups, a restore window and post-cutover data reconciliation. Preserve app identities/credentials and review persistent cookie-key storage. Do not combine this with a database-major upgrade or expose native-worker Docker access for managed diagnostics.
5. **Runtime/cutover:** automatic WinUI capture in the reduced harness reached the collector, but exit/watchdog evidence was lost. Do not rerun without parent coordination. Full actual MultiShock runtime, final endpoint checks and candidate DSN cutover remain separate acceptance. Live GlitchTip stays untouched until the coordinated replacement is accepted; history waiver is not deletion permission.

No independent code fix is identified by this final review. Remaining actions need reviewed user commits, existing-access/attribution confirmation, remote CI/runtime evidence and parent-owned production coordination. Optional old test-container cleanup is not a release prerequisite and is not included in these commands.
