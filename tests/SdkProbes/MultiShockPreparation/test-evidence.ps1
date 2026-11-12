$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'evidence.ps1')
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('maui-evidence-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$log = Join-Path $scratch 'runtime.log'
$logText = "old run`ncapture Microsoft.UI.Xaml.UnhandledException:False`nhttp 200`n"
[IO.File]::WriteAllText($log, $logText)
$event = [pscustomobject]@{
    eventId = 'synthetic'; release = 'maui-runtime-synthetic'; environment = 'synthetic'
    sdkVersion = '6.8.0'; severity = 'error'; kind = 'unhandled'
    stackTrace = 'RuntimeProbe.ThrowSynthetic() (SyntheticMauiProbe.cs) line 353:17'
    ignoredGraph = (Get-Content -LiteralPath $log -TotalCount 1).PSObject
}
$argsForEvidence = @{
    Run = '0123456789abcdef0123456789abcdef'; StartedUtc = [DateTime]::UtcNow
    ExitCode = -1073741819; ForcedTermination = $false
    ExeSha256 = ('a' * 64); DllSha256 = ('b' * 64)
    LogPath = $log; Before = 1; Events = @($event)
}
$checks = 0
function Assert-Rejected([string]$Message) {
    $rejected = $false
    try { ConvertTo-MauiProbeEvidenceJson @argsForEvidence | Out-Null } catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        $rejected = $true
    }
    if (!$rejected) { throw "Expected rejection: $Message" }
    $script:checks++
}
$watch = [Diagnostics.Stopwatch]::StartNew()
$json = ConvertTo-MauiProbeEvidenceJson @argsForEvidence
$result = $json | ConvertFrom-Json
if ($json.Length -gt 4096 -or $json -match 'PSDrive|PSProvider|ignoredGraph' -or
    $result.logSegment.Count -ne 2 -or $result.events[0].stackTrace -notlike '*353:17' -or
    $result.exitCode -ne -1073741819 -or $result.forcedTermination) { throw 'Evidence round trip failed.' }
$checks++
$event.stackTrace = Get-Content -LiteralPath $log -TotalCount 1
$cleanJson = ConvertTo-MauiProbeEvidenceJson @argsForEvidence
if ($cleanJson -match 'PSDrive|PSProvider|PSPath' -or $cleanJson.Length -gt 4096) {
    throw 'Decorated event text leaked filesystem metadata.'
}
$checks++
$argsForEvidence.ExitCode = $null
$argsForEvidence.Events = @()
$result = ConvertTo-MauiProbeEvidenceJson @argsForEvidence | ConvertFrom-Json
if ($null -ne $result.exitCode -or $result.events.Count -ne 0) { throw 'Missing evidence was manufactured.' }
$checks++
$argsForEvidence.ExitCode = 124
$argsForEvidence.ForcedTermination = $true
$result = ConvertTo-MauiProbeEvidenceJson @argsForEvidence | ConvertFrom-Json
if ($result.exitCode -ne 124 -or !$result.forcedTermination) { throw 'Failure evidence was lost.' }
$checks++
$argsForEvidence.Events = @(1..11 | ForEach-Object { $event })
Assert-Rejected 'exceeds 10 events'
$argsForEvidence.Events = @($event)
$event.stackTrace = 'x' * 8193
Assert-Rejected 'Invalid evidence field'
$event.stackTrace = [pscustomobject]@{ nested = 'invalid' }
Assert-Rejected 'Invalid evidence field'
$event.stackTrace = 'synthetic'
[IO.File]::WriteAllText($log, ('x' * 65537))
Assert-Rejected 'log exceeds'
[IO.File]::WriteAllText($log, ((1..257 | ForEach-Object { 'line' }) -join "`n"))
Assert-Rejected 'log line count'
[IO.File]::WriteAllText($log, $logText)
$argsForEvidence.Before = 4
Assert-Rejected 'log line count'
Write-Output "PASS: $checks synthetic evidence checks in $($watch.ElapsedMilliseconds)ms; no process launch or network. Fixture: $scratch"
