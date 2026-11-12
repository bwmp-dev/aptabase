function ConvertTo-MauiProbeEvidenceJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$Run,
        [Parameter(Mandatory)][DateTime]$StartedUtc,
        [Nullable[int]]$ExitCode,
        [Parameter(Mandatory)][bool]$ForcedTermination,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExeSha256,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$DllSha256,
        [Parameter(Mandatory)][string]$LogPath,
        [ValidateRange(0, 256)][int]$Before = 0,
        [AllowEmptyCollection()][object[]]$Events = @()
    )
    $ErrorActionPreference = 'Stop'
    if ($Events.Count -gt 10) { throw 'Evidence exceeds 10 events.' }

    # Get-Content strings carry PSDrive/PSProvider graphs in Windows PowerShell 5.1.
    # Read bounded plain text; never serialize those extended properties.
    $reader = [IO.File]::OpenText($LogPath)
    try {
        $buffer = New-Object char[] 65537
        $count = $reader.ReadBlock($buffer, 0, $buffer.Length)
        if ($count -gt 65536) { throw 'Evidence log exceeds 64 Ki characters.' }
        $text = [string]::new($buffer, 0, $count)
    } finally { $reader.Dispose() }
    $lines = @($text -split '\r?\n')
    if ($lines.Count -gt 0 -and $lines[-1] -eq '') { $lines = @($lines | Select-Object -SkipLast 1) }
    if ($lines.Count -gt 256 -or $Before -gt $lines.Count) { throw 'Invalid evidence log line count.' }

    $safeEvents = @(foreach ($event in $Events) {
        $fields = [ordered]@{}
        foreach ($field in @('eventId', 'timestamp', 'release', 'environment', 'sdkVersion', 'severity', 'kind', 'stackTrace')) {
            $property = $event.PSObject.Properties[$field]
            if ($null -eq $property -or $null -eq $property.Value) { $fields[$field] = $null; continue }
            $value = $property.Value
            $limit = if ($field -eq 'stackTrace') { 8192 } else { 256 }
            if ($value -isnot [string] -or $value.Length -gt $limit) { throw "Invalid evidence field: $field" }
            $fields[$field] = [Text.Encoding]::UTF8.GetString([Text.Encoding]::UTF8.GetBytes($value))
        }
        [pscustomobject]$fields
    })
    $json = [ordered]@{
        run = $Run; mode = 'ui-unhandled'
        startedUtc = $StartedUtc.ToUniversalTime().ToString('o')
        finishedUtc = [DateTime]::UtcNow.ToString('o')
        exitCode = $ExitCode; forcedTermination = $ForcedTermination
        exeSha256 = $ExeSha256; dllSha256 = $DllSha256
        logSegment = @($lines | Select-Object -Skip $Before)
        events = $safeEvents
    } | ConvertTo-Json -Depth 4
    if ([Text.Encoding]::UTF8.GetByteCount($json) -gt 131072) { throw 'Evidence JSON exceeds 128 KiB.' }
    return $json
}
