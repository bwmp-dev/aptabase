$ErrorActionPreference = 'Stop'
$base = 'http://127.0.0.1:55440'
$sdkVersion = '10.77.1'

function Invoke-Bounded([string]$FileName, [string[]]$Arguments, [int]$TimeoutSeconds, [hashtable]$Environment = @{}) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $FileName
    $info.Arguments = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $info.WorkingDirectory = $PSScriptRoot
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($name in @($info.EnvironmentVariables.Keys)) {
        if ($name -match '^(SENTRY_|NODE_OPTIONS$|NODE_PATH$|APTABASE_PROBE_)') { $info.EnvironmentVariables.Remove($name) }
    }
    foreach ($entry in $Environment.GetEnumerator()) { $info.EnvironmentVariables[$entry.Key] = $entry.Value }
    $process = [System.Diagnostics.Process]::Start($info)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        throw "$FileName exceeded $TimeoutSeconds seconds."
    }
    $process.WaitForExit()
    if ($stdout.Result) { Write-Host $stdout.Result.TrimEnd() }
    if ($stderr.Result) { Write-Host $stderr.Result.TrimEnd() }
    return $process.ExitCode
}

$probeRoot = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\', '/')

# Returns the absolute path only if it lies strictly below this probe directory
# and no existing component below the probe directory is a reparse point.
function Resolve-ProbeOutput([string]$RelativePath) {
    $target = [System.IO.Path]::GetFullPath((Join-Path $probeRoot $RelativePath)).TrimEnd('\', '/')
    if (!$target.StartsWith($probeRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing probe output path outside the Node probe directory: $RelativePath"
    }
    $current = $probeRoot
    foreach ($segment in $target.Substring($probeRoot.Length + 1).Split([System.IO.Path]::DirectorySeparatorChar)) {
        $current = Join-Path $current $segment
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing probe output path through a reparse point: $RelativePath"
        }
    }
    return $target
}

# Windows PowerShell 5.1 Remove-Item -Recurse can traverse directory links, so
# the whole tree is checked for reparse points before anything is deleted.
function Remove-ProbeOutput([string]$RelativePath) {
    $target = Resolve-ProbeOutput $RelativePath
    if (!(Test-Path -LiteralPath $target)) { return }
    $pending = New-Object System.Collections.Generic.Stack[string]
    $pending.Push($target)
    while ($pending.Count -gt 0) {
        foreach ($entry in [System.IO.Directory]::GetFileSystemEntries($pending.Pop())) {
            $attributes = [System.IO.File]::GetAttributes($entry)
            if ($attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to delete probe output containing a reparse point: $RelativePath"
            }
            if ($attributes -band [System.IO.FileAttributes]::Directory) { $pending.Push($entry) }
        }
    }
    Remove-Item -LiteralPath $target -Recurse -Force
}

function Get-Api([string]$Path) {
    Invoke-RestMethod "$base$Path" -WebSession $owner -TimeoutSec 5
}

Push-Location $PSScriptRoot
try {
    $health = Invoke-RestMethod "$base/test/health" -TimeoutSec 5
    if (!$health.ready) { throw 'Start the disposable MonitoringHost first.' }
    Invoke-RestMethod "$base/test/login/owner" -Method Post -SessionVariable owner -TimeoutSec 5 | Out-Null
    $projects = Invoke-RestMethod "$base/api/v0/apps/test-app/sentry-projects" -Method Post -WebSession $owner -TimeoutSec 5
    $dsn = ($projects.projects | Where-Object buildMode -eq 'release').dsn
    if ($dsn -cnotmatch '^http://[0-9a-f]{64}@127\.0\.0\.1:55440/[0-9]+$') { throw "Provisioned release DSN is not a standard derived-key loopback DSN." }

    $deps = Resolve-ProbeOutput 'bin/deps'
    New-Item -ItemType Directory -Force $deps | Out-Null
    Copy-Item package.json, package-lock.json $deps -Force
    $node = (Get-Command node -CommandType Application | Select-Object -First 1).Source
    $npmCli = Join-Path (Split-Path $node) 'node_modules/npm/bin/npm-cli.js'
    if ((Invoke-Bounded $node @($npmCli, 'ci', '--prefix', $deps, '--ignore-scripts', '--no-audit', '--no-fund', '--fetch-timeout=60000', '--fetch-retries=1') 240) -ne 0) {
        throw 'npm ci failed for the scoped probe dependencies.'
    }
    Remove-ProbeOutput 'bin/fixture'
    Remove-ProbeOutput 'bin/envelopes'
    if ((Invoke-Bounded $node @("$deps/node_modules/typescript/bin/tsc", '-p', 'fixture/tsconfig.json') 60) -ne 0) {
        throw 'Synthetic fixture compilation failed.'
    }

    $release = 'node-probe-' + [guid]::NewGuid().ToString('N')
    $exit = Invoke-Bounded $node @('--max-old-space-size=128', 'probe.cjs') 45 @{ APTABASE_PROBE_DSN = $dsn; APTABASE_PROBE_RELEASE = $release }
    if ($exit -ne 0) { throw "Node probe failed with exit $exit (see FAIL lines above); captured synthetic envelopes, if any, are in bin/envelopes." }
    $issues = Get-Api "/api/v0/apps/test-app/issues?release=$release"
    if ($issues.pagination.total -ne 1) { throw 'Expected one stored Node issue.' }
    $issue = $issues.issues[0]
    $events = Get-Api "/api/v0/apps/test-app/issues/$($issue.id)/events?release=$release"
    $occurrence = $events.events[0]
    if ($events.pagination.total -ne 1 -or $occurrence.sdkVersion -ne $sdkVersion -or $occurrence.kind -ne 'handled' -or $occurrence.platform -ne 'node') {
        throw 'Stored Node occurrence metadata mismatch.'
    }
    $marker = Select-String -Path fixture/thrower.ts -SimpleMatch 'SOURCE-MAP-PROBE-THROW'
    $line = $marker.LineNumber
    $column = 1 + $marker.Line.IndexOf('new Error')
    $top = ($occurrence.stackTrace -split "`n")[-1]
    if ($top -ne "at loadSyntheticSettings (thrower.ts) line ${line}:$column" -or $occurrence.stackTrace -match 'thrower\.js') {
        throw "Source-mapped location was not retained: $top"
    }
    if ($issue.errorMessage -notlike '*[[]EMAIL_REDACTED[]]*') { throw 'Message redaction missing.' }
    $transactions = Get-Api "/api/v0/apps/test-app/transactions?release=$release"
    if ($transactions.pagination.total -ne 1 -or $transactions.transactions[0].name -ne 'node.startup' -or $transactions.transactions[0].operation -ne 'app.startup') {
        throw 'Expected one stored Node transaction.'
    }
    $detail = Get-Api "/api/v0/apps/test-app/transactions/$($transactions.transactions[0].eventId)"
    if ($detail.spans.Count -ne 1 -or $detail.spans[0].operation -ne 'file.read' -or $detail.spans[0].parentSpanId) { throw 'Node span mapping mismatch.' }
    $retained = @($issue, $events, $detail) | ConvertTo-Json -Depth 20
    if ($retained -match 'private-probe|owner@example\.test|app:///') { throw 'Private probe fields or SDK paths escaped the collector boundary.' }

    $release = 'node-probe-' + [guid]::NewGuid().ToString('N')
    $exit = Invoke-Bounded $node @('--max-old-space-size=128', 'probe.cjs', 'unhandled') 45 @{ APTABASE_PROBE_DSN = $dsn; APTABASE_PROBE_RELEASE = $release }
    if ($exit -ne 1) { throw "Expected the SDK to terminate the uncaught-exception process with exit 1, got $exit." }
    $uncaught = Get-Api "/api/v0/apps/test-app/issues?release=$release"
    if ($uncaught.pagination.total -ne 1) { throw 'Expected one stored uncaught Node exception.' }
    $uncaughtEvents = Get-Api "/api/v0/apps/test-app/issues/$($uncaught.issues[0].id)/events?release=$release"
    if ($uncaughtEvents.pagination.total -ne 1 -or $uncaughtEvents.events[0].kind -ne 'unhandled' -or $uncaughtEvents.events[0].severity -ne 'fatal') {
        throw 'Actual uncaught Node exception was not classified as fatal/unhandled.'
    }
    $noTransactions = Get-Api "/api/v0/apps/test-app/transactions?release=$release"
    if ($noTransactions.pagination.total -ne 0) { throw 'Unexpected transaction from the uncaught-exception process.' }
    Write-Output "PASS: Sentry Node $sdkVersion on Node $(& $node --version): handled source-mapped exception, actual uncaught process exception, transaction/span; loopback-only transport, privacy, scoped reads."
} finally {
    Pop-Location
}
