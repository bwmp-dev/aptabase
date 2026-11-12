# Next user-operated MAUI check

**The mode changes from `handled` to `ui-unhandled`, with a fresh run ID and `ui-unhandled-result.json` evidence filename.** These are user-operated instructions only; do not rerun the harness to apply the evidence-serialization correction below.

This is a reduced MultiShock-derived **Sentry.Maui/core6.8.0** harness, not the published MultiShock application. A blank window may appear and the process should crash. Normal app startup/device/plugin/serial/WebSocket/flow/update/Velopack code is absent. SDK state stays in `bin/publish/probe-data/sentry-cache`; framework profile writes have not been measured. No personal profile contents are inspected. Do not run the full app or end any existing app process.

Prerequisites already checked locally: Docker is available with cached `postgres:16-alpine`; the test-host DLL and built web bundle exist; the harness's static guard verification passes. Services are **not left running for this handoff**. Ports 55439/55440 must be free. Do not rebuild the harness. Its exact hashes are checked below; stop on any mismatch. The collector uses a disposable database and a test-only login route, and must never be exposed publicly.

## Evidence-only correction after the manual runs

Do not rerun the harness to apply this correction. Two manual runs already delivered automatic WinUI unhandled events; their exit evidence was not saved. Window B's former PowerShell process is now absent. The old evidence statement serialized `Get-Content` strings at depth15; Windows PowerShell5.1 attaches PSDrive/PSProvider object graphs to those strings. One line expanded to25,362 characters even at depth2 versus34 for a plain string. That is a confirmed serialization defect consistent with the observed8.3GiB memory use; no exact suspended instruction or exit code was recovered.

The blocks below retain the same launch operation for a future parent-coordinated manual check only. Their evidence step now calls `evidence.ps1`, a serializer with **no process launch, network request or file write**. It reads at most64Ki characters/256 log lines, accepts at most10 events with allowlisted scalar fields, converts timestamps to ISO text and caps JSON at128KiB. It preserves unknown exit codes as null and watchdog/forced-termination evidence unchanged. `test-evidence.ps1` exercises only synthetic data; it never launches the app. No `run.ps1` has been created.

## Window A: same disposable services

Open a PowerShell window and paste this. Wait for `Monitoring test host: http://127.0.0.1:55440` before Window B. Keep it open until results have been saved. These commands contain no live credentials; the passwordless database is restricted to loopback and disposable.

```powershell
$ErrorActionPreference = 'Stop'
Set-Location 'D:\Coding\public\aptabase'
if (Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in 55439,55440 }) { throw 'Test ports are occupied; stop and ask, do not end another process.' }
if (Get-Process -Name 'Aptabase.Isolated.MultiShockProbe' -ErrorAction SilentlyContinue) { throw 'A probe is already running.' }
if (docker ps -aq --filter 'name=^/aptabase-maui-manual-test$') { throw 'Named test container already exists; inspect it first.' }
$hostDll = Join-Path $PWD 'tests\MonitoringHost\bin\Debug\net10.0\MonitoringHost.dll'
if (!(Test-Path -LiteralPath $hostDll) -or !(Test-Path 'src\wwwroot\index.html')) { throw 'Prepared collector or bundle missing; ask agent to prepare, do not rebuild the MAUI app.' }
$ownedContainer = docker run --detach --rm --pull never --name aptabase-maui-manual-test --cpus .5 --memory 256m -p 127.0.0.1:55439:5432 -e POSTGRES_HOST_AUTH_METHOD=trust postgres:16-alpine
if ($LASTEXITCODE -ne 0 -or $ownedContainer -cnotmatch '^[a-f0-9]{64}$') { throw 'Database start failed; inspect before retrying.' }
try {
    $ready = $false
    for ($i=0; $i -lt 20; $i++) {
        docker exec $ownedContainer pg_isready -U postgres *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (!$ready) { throw 'Disposable database did not become ready.' }
    $env:APTABASE_ISSUE_TEST_DATABASE_URL = 'Host=127.0.0.1;Port=55439;Username=postgres'
    $env:MONITORING_TEST_PORT = '55440'
    $env:MONITORING_TEST_WEBROOT = Join-Path $PWD 'src\wwwroot'
    Remove-Item Env:NATIVE_SYMBOL_WORKER_DIRECTORY,Env:NATIVE_SYMBOL_WORKER_IMAGE,Env:NATIVE_SOURCE_WORKER_IMAGE -ErrorAction SilentlyContinue
    dotnet $hostDll
} finally {
    docker stop --timeout 5 $ownedContainer
    Remove-Item Env:APTABASE_ISSUE_TEST_DATABASE_URL,Env:MONITORING_TEST_PORT,Env:MONITORING_TEST_WEBROOT -ErrorAction SilentlyContinue
}
```

The collector has a 45-minute maximum lifetime. After Window B saves evidence, press Ctrl+C in **Window A only**. Its `finally` stops only the exact container it created. If the terminal is forcibly closed and cleanup does not run, inspect `docker ps --filter name=aptabase-maui-manual-test` and stop that test container explicitly; do not stop other containers.

## Window B: unchanged binary, automatic WinUI exception

Open a second PowerShell window. No DSN or session cookie is printed/saved. The returned process object bounds cleanup to this one harness. Hidden start minimizes interruption but does not guarantee that WinUI creates no visible window.

```powershell
$ErrorActionPreference = 'Stop'
. 'D:\Coding\public\aptabase\tests\SdkProbes\MultiShockPreparation\evidence.ps1'
$publish = 'D:\Coding\public\aptabase\tests\SdkProbes\MultiShockPreparation\bin\publish'
$exe = (Resolve-Path -LiteralPath (Join-Path $publish 'Aptabase.Isolated.MultiShockProbe.exe')).ProviderPath
$dll = Join-Path $publish 'Aptabase.Isolated.MultiShockProbe.dll'
if ($exe -cne (Join-Path $publish 'Aptabase.Isolated.MultiShockProbe.exe')) { throw 'Unexpected executable path.' }
$exeHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
$dllHash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
if ($exeHash -ne '1F394041FB061A8D138B4A650D6CE815785CDF234C603E870917B21010086438' -or $dllHash -ne '65BD26152811FD6C451E87688880BEC1DBDEFFD8CB6A3275F3A385DFC81ADFB8') { throw 'Prepared binary changed; do not launch.' }
if (Get-Process -Name 'Aptabase.Isolated.MultiShockProbe' -ErrorAction SilentlyContinue) { throw 'A probe is already running.' }
$base = 'http://127.0.0.1:55440'
if (!(Invoke-RestMethod "$base/test/health" -TimeoutSec 5).ready) { throw 'Test collector not ready.' }
Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$base/test/login/owner" -SessionVariable probeWeb -TimeoutSec 5 | Out-Null
$projects = Invoke-RestMethod -Method Post -Uri "$base/api/v0/apps/test-app/sentry-projects" -WebSession $probeWeb -TimeoutSec 5
$dsn = @($projects.projects | Where-Object buildMode -eq 'release')
if ($dsn.Count -ne 1 -or $dsn[0].dsn -cnotmatch '^http://[a-f0-9]{64}@127\.0\.0\.1:55440/[1-9][0-9]*$') { throw 'Unexpected test DSN.' }
$settings = @{captureErrors=$true; capturePerformance=$false; forwardRawErrors=$false; monitoringRetentionDays=1; localAlertsEnabled=$false; alertCooldownMinutes=60} | ConvertTo-Json
Invoke-RestMethod -Method Put -Uri "$base/api/v0/apps/test-app/monitoring/settings" -WebSession $probeWeb -ContentType 'application/json' -Body $settings -TimeoutSec 5 | Out-Null
$run = [guid]::NewGuid().ToString('N')
$env:APTABASE_PROBE_DSN = $dsn[0].dsn
$env:APTABASE_MAUI_PROBE_ALLOW_WINDOW = '1'
$env:APTABASE_PROBE_MODE = 'ui-unhandled'
$env:APTABASE_PROBE_RUN = $run
$log = Join-Path $publish 'probe-data\runtime.log'
if ((Test-Path -LiteralPath $log) -and (Get-Item -LiteralPath $log).Length -gt 65536) { throw 'Existing probe log exceeds evidence limit; preserve it and ask before continuing.' }
$before = if (Test-Path -LiteralPath $log) { [IO.File]::ReadAllLines($log).Length } else { 0 }
$started = [DateTime]::UtcNow
$forced = $false
$probe = $null
try {
    $probe = Start-Process -FilePath $exe -WorkingDirectory $publish -WindowStyle Hidden -PassThru
    if (!$probe.WaitForExit(30000)) { $forced = $true; $probe.Kill(); $probe.WaitForExit(5000) | Out-Null }
    $probe.Refresh()
    $exitCode = $probe.ExitCode
    $release = [Uri]::EscapeDataString('maui-runtime-' + $run)
    $events = @()
    for ($i=0; $i -lt 10 -and $events.Count -eq 0; $i++) {
        $issues = Invoke-RestMethod "$base/api/v0/apps/test-app/issues?status=all&buildMode=release&release=$release" -WebSession $probeWeb -TimeoutSec 5
        $events = @($issues.issues | ForEach-Object {
            (Invoke-RestMethod "$base/api/v0/apps/test-app/issues/$($_.id)/events?buildMode=release&release=$release" -WebSession $probeWeb -TimeoutSec 5).events
        })
        if ($events.Count -eq 0) { Start-Sleep -Milliseconds 500 }
    }
    $result = Join-Path $publish 'probe-data\ui-unhandled-result.json'
    if (Test-Path -LiteralPath $result) { $result = Join-Path $publish "probe-data\ui-unhandled-result-$run.json" }
    $evidence = ConvertTo-MauiProbeEvidenceJson -Run $run -StartedUtc $started -ExitCode $exitCode -ForcedTermination $forced -ExeSha256 $exeHash -DllSha256 $dllHash -LogPath $log -Before $before -Events $events
    [IO.File]::WriteAllText($result, $evidence, (New-Object Text.UTF8Encoding($false)))
    Write-Output "Saved $result ; exit=$exitCode ; forced=$forced ; events=$($events.Count)"
} finally {
    if ($probe -and !$probe.HasExited) { $probe.Kill(); $probe.WaitForExit(5000) | Out-Null }
    Remove-Item Env:APTABASE_PROBE_DSN,Env:APTABASE_MAUI_PROBE_ALLOW_WINDOW,Env:APTABASE_PROBE_MODE,Env:APTABASE_PROBE_RUN -ErrorAction SilentlyContinue
    $dsn = $null; $probeWeb = $null
}
```

If a command fails, keep the error and `runtime.log`; do not report a pass or repeat launches blindly. If result collection fails after the process exits, keep Window A alive long enough for the agent to read synthetic evidence. There is no automatic success assertion in this handoff: the agent must inspect the saved values, current timestamps and release correlation.

## Exact acceptance and limits

- New log segment contains `initialized ui-unhandled`, `window-created sdk-enabled=True`, `dispatch ui-unhandled`, and **`capture Microsoft.UI.Xaml.UnhandledException:False`**. A bare observer message or AppDomain fallback is insufficient to establish the WinUI hook.
- A fresh collector occurrence for `maui-runtime-<run>` has `kind=unhandled`, SDK `6.8.0`, synthetic environment and `RuntimeProbe.ThrowSynthetic()` / `SyntheticMauiProbe.cs:353:17`. HTTP200 alone is insufficient. Do not change the SDK severity to manufacture a pass.
- The child terminates by itself with a nonzero signed 32-bit Windows exit code (range -2147483648 through 2147483647; no exact crash code assumed). **124 is the internal 25-second watchdog and fails completion.** Zero, a missing exit code or outer forced termination requires investigation and does not pass this check.
- The existing `ui-unhandled` branch throws before/outside the handled branch's `try/CaptureException`. The local WinUI observer only logs, never sets `Handled`. No direct manual `CaptureException` masks hook validation in this mode.

The unchanged binary disables SDK session/client-report defaults and only permits event envelopes. Thus this test cannot certify actual MultiShock default session emission, crash-free metrics or all configured envelope types. It also does not prove offline/process-restart replay, shutdown delivery, real network loss, full-app startup or device behavior. The separate prepared `offline`/`replay`, `shutdown` and `background-unhandled` modes need their own correlated acceptance; do not launch them as part of this single check.

After the agent reads the result, preserve the synthetic log/result/cache until any agreed replay test is finished. Stop the foreground collector and its exact disposable database. Do not delete the original MultiShock configuration, user cache or existing process. The agent's automated launch remains policy-denied; this document does not retry it.
