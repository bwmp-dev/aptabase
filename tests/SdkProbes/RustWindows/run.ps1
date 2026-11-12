$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & cargo build --offline --locked --jobs 1 --target-dir bin/target
    if ($LASTEXITCODE -ne 0) { throw 'Rust offline build failed' }
    $eventId = [guid]::NewGuid().ToString()
    New-Item -ItemType Directory -Force bin | Out-Null
    & ./bin/target/debug/panic-probe.exe $eventId bin/report.json
    if ($LASTEXITCODE -ne 101) { throw 'Expected actual Rust process panic exit 101' }
    $body = Get-Content -LiteralPath bin/report.json -Raw
    $report = $body | ConvertFrom-Json
    if ($report.stackTrace -notmatch '^at panic \(probe.rs\) line \d+:\d+$' -or $report.eventId -ne $eventId -or $body.Length -gt 4096) { throw 'Invalid panic diagnostics' }
    $base = 'http://127.0.0.1:55440'
    if (!(Invoke-RestMethod "$base/test/health" -TimeoutSec 5).ready) { throw 'Disposable host required' }
    1..2 | ForEach-Object { Invoke-RestMethod "$base/api/v0/error" -Method Post -Headers @{'App-Key'='TEST-APP-KEY'} -ContentType application/json -Body $body -TimeoutSec 5 | Out-Null }
    Invoke-RestMethod "$base/test/login/owner" -Method Post -SessionVariable session -TimeoutSec 5 | Out-Null
    $issues = Invoke-RestMethod "$base/api/v0/apps/test-app/issues?release=rust-panic-probe&status=all" -WebSession $session -TimeoutSec 5
    $match = @($issues.issues | Where-Object stackTrace -eq $report.stackTrace)
    if ($match.Count -ne 1) { throw 'Stored panic issue missing' }
    $events = Invoke-RestMethod "$base/api/v0/apps/test-app/issues/$($match[0].id)/events" -WebSession $session -TimeoutSec 5
    $event = @($events.events | Where-Object eventId -eq $eventId)
    if ($event.Count -ne 1 -or $event[0].kind -ne 'unhandled' -or $event[0].severity -ne 'fatal' -or $event[0].stackTrace -ne $report.stackTrace) { throw 'Panic retry deduplication or diagnostics failed' }
    Write-Output 'PASS: Windows Rust actual panic exit101, basename/line/column native JSON, post-exit local delivery and retry deduplication. No Tauri/device/native-symbolication claim.'
} finally { Pop-Location }
