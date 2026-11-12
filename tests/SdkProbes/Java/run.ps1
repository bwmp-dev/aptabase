$ErrorActionPreference = 'Stop'
$base = 'http://127.0.0.1:55440'
Push-Location $PSScriptRoot
$previousDsn = $env:APTABASE_PROBE_DSN
$previousRelease = $env:APTABASE_PROBE_RELEASE
try {
    $health = Invoke-RestMethod "$base/test/health" -TimeoutSec 5
    if (!$health.ready) { throw 'Start the disposable MonitoringHost first.' }
    Invoke-RestMethod "$base/test/login/owner" -Method Post -SessionVariable owner -TimeoutSec 5 | Out-Null
    $projects = Invoke-RestMethod "$base/api/v0/apps/test-app/sentry-projects" -Method Post -WebSession $owner -TimeoutSec 5
    $env:APTABASE_PROBE_DSN = ($projects.projects | Where-Object buildMode -eq 'release').dsn
    $env:APTABASE_PROBE_RELEASE = 'jvm-probe-' + [guid]::NewGuid().ToString('N')
    & mvn -B -q dependency:build-classpath '-Dmdep.outputFile=bin/classpath.txt'
    if ($LASTEXITCODE -ne 0) { throw 'Maven dependency resolution failed.' }
    $classpath = (Get-Content bin/classpath.txt -Raw).Trim()
    & javac -cp $classpath -d bin Probe.java
    if ($LASTEXITCODE -ne 0) { throw 'Java compilation failed.' }
    & java -Xmx128m -XX:ActiveProcessorCount=1 -cp "bin;$classpath" Probe
    if ($LASTEXITCODE -ne 0) { throw 'Java probe failed.' }
    $issues = Invoke-RestMethod "$base/api/v0/apps/test-app/issues?release=$env:APTABASE_PROBE_RELEASE" -WebSession $owner -TimeoutSec 5
    if ($issues.pagination.total -ne 1) { throw 'Expected one stored JVM issue.' }
    $issue = $issues.issues[0]
    $events = Invoke-RestMethod "$base/api/v0/apps/test-app/issues/$($issue.id)/events?release=$env:APTABASE_PROBE_RELEASE" -WebSession $owner -TimeoutSec 5
    if ($events.pagination.total -ne 1 -or $events.events[0].sdkVersion -ne '8.64.0' -or $events.events[0].kind -ne 'handled') {
        throw 'Stored JVM occurrence metadata mismatch.'
    }
    if ($events.events[0].stackTrace -notmatch 'Probe.java\) line \d+' -or $issue.errorMessage -notlike '*[[]EMAIL_REDACTED[]]*') {
        throw 'Source locations or message redaction missing.'
    }
    $transactions = Invoke-RestMethod "$base/api/v0/apps/test-app/transactions?release=$env:APTABASE_PROBE_RELEASE" -WebSession $owner -TimeoutSec 5
    if ($transactions.pagination.total -ne 1 -or $transactions.transactions[0].name -ne 'jvm.startup') { throw 'Expected one stored JVM transaction.' }
    $detail = Invoke-RestMethod "$base/api/v0/apps/test-app/transactions/$($transactions.transactions[0].eventId)" -WebSession $owner -TimeoutSec 5
    if ($detail.spans.Count -ne 1 -or $detail.spans[0].operation -ne 'file.read' -or $detail.spans[0].parentSpanId) { throw 'JVM span mapping mismatch.' }
    $retained = @($events, $detail) | ConvertTo-Json -Depth 20
    if ($retained -match 'private-probe|owner@example.test') { throw 'Private probe fields escaped redaction.' }
    $env:APTABASE_PROBE_RELEASE = 'jvm-probe-' + [guid]::NewGuid().ToString('N')
    & java -Xmx128m -XX:ActiveProcessorCount=1 -cp "bin;$classpath" Probe unhandled
    if ($LASTEXITCODE -ne 1) { throw 'Expected the disposable JVM to terminate with exit 1.' }
    $uncaught = Invoke-RestMethod "$base/api/v0/apps/test-app/issues?release=$env:APTABASE_PROBE_RELEASE" -WebSession $owner -TimeoutSec 5
    if ($uncaught.pagination.total -ne 1) { throw 'Expected one stored uncaught JVM exception.' }
    $uncaughtEvents = Invoke-RestMethod "$base/api/v0/apps/test-app/issues/$($uncaught.issues[0].id)/events?release=$env:APTABASE_PROBE_RELEASE" -WebSession $owner -TimeoutSec 5
    if ($uncaughtEvents.pagination.total -ne 1 -or $uncaughtEvents.events[0].kind -ne 'unhandled' -or $uncaughtEvents.events[0].severity -ne 'fatal') {
        throw 'Actual uncaught JVM exception was not classified as fatal/unhandled.'
    }
    Write-Output 'PASS: Sentry Java 8.64.0 handled exception, actual uncaught process exception, transaction/span; locations, metadata, privacy, scoped reads.'
} finally {
    $env:APTABASE_PROBE_DSN = $previousDsn
    $env:APTABASE_PROBE_RELEASE = $previousRelease
    Pop-Location
}
