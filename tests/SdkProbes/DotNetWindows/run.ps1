$ErrorActionPreference = 'Stop'
$previousDsn = $env:APTABASE_PROBE_DSN
$previousRelease = $env:APTABASE_PROBE_RELEASE
Push-Location $PSScriptRoot
try {
    $base = 'http://127.0.0.1:55440'
    if (!(Invoke-RestMethod "$base/test/health" -TimeoutSec 5).ready) { throw 'Disposable host required' }
    Invoke-RestMethod "$base/test/login/owner" -Method Post -SessionVariable session -TimeoutSec 5 | Out-Null
    $projects = Invoke-RestMethod "$base/api/v0/apps/test-app/sentry-projects" -Method Post -WebSession $session -TimeoutSec 5
    $env:APTABASE_PROBE_DSN = ($projects.projects | Where-Object buildMode -eq release).dsn
    & dotnet build --configuration Release --no-restore -m:1 --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Managed Windows probe build failed' }
    foreach ($mode in @('handled','unhandled')) {
        $env:APTABASE_PROBE_RELEASE = 'windows-managed-' + [guid]::NewGuid().ToString('N')
        & dotnet bin/Release/net10.0-windows/DotNetWindows.dll $mode
        if (($mode -eq 'handled' -and $LASTEXITCODE -ne 0) -or ($mode -eq 'unhandled' -and $LASTEXITCODE -eq 0)) { throw 'Unexpected managed process exit' }
        $issues = Invoke-RestMethod "$base/api/v0/apps/test-app/issues?release=$env:APTABASE_PROBE_RELEASE" -WebSession $session -TimeoutSec 5
        if ($issues.pagination.total -ne 1) { throw 'Managed occurrence not delivered' }
        $events = Invoke-RestMethod "$base/api/v0/apps/test-app/issues/$($issues.issues[0].id)/events?release=$env:APTABASE_PROBE_RELEASE" -WebSession $session -TimeoutSec 5
        $event = $events.events[0]
        if ($event.kind -ne $mode -or $event.sdkVersion -ne '6.8.0' -or $event.stackTrace -match '[A-Za-z]:\\|/Users/|/home/') { throw 'Managed classification or source privacy mismatch' }
        if ($mode -eq 'handled' -and $event.stackTrace -notmatch 'SyntheticManagedSite.ThrowHandled\(\) \(SyntheticManagedProbe.cs\) line 353:17') { throw "Synthetic method/basename/line/column mismatch: $($event.stackTrace)" }
        if ($mode -eq 'unhandled' -and ($event.severity -ne 'error' -or $event.stackTrace -notmatch 'Program.cs\) line \d+')) { throw 'Unhandled classification or PDB location missing' }
        $transactions = Invoke-RestMethod "$base/api/v0/apps/test-app/transactions?release=$env:APTABASE_PROBE_RELEASE" -WebSession $session -TimeoutSec 5
        $expected = if ($mode -eq 'handled') { 1 } else { 0 }
        if ($transactions.pagination.total -ne $expected) { throw 'Managed transaction count mismatch' }
    }
    Write-Output 'PASS: Windows Sentry.NET 6.8.0: SyntheticManagedSite.ThrowHandled(), SyntheticManagedProbe.cs:353:17 retained; actual process-unhandled exception, portable PDB and transaction. SDK reports unhandled severity error. No MAUI/WinUI/native crash claim.'
} finally {
    $env:APTABASE_PROBE_DSN = $previousDsn
    $env:APTABASE_PROBE_RELEASE = $previousRelease
    Pop-Location
}
