param([string]$NdkPath = "$env:LOCALAPPDATA/Android/Sdk/ndk/29.0.13846066")
$ErrorActionPreference = 'Stop'
$toolBin = Join-Path $NdkPath 'toolchains/llvm/prebuilt/windows-x86_64/bin'
foreach ($tool in @('clang.exe', 'llvm-nm.exe', 'llvm-symbolizer.exe')) {
    if (!(Test-Path (Join-Path $toolBin $tool))) { throw "Missing installed NDK tool: $tool" }
}
Push-Location $PSScriptRoot
try {
    New-Item -ItemType Directory -Force bin | Out-Null
    & (Join-Path $toolBin 'clang.exe') --target=aarch64-linux-android28 -g -O0 '-fdebug-compilation-dir=.' -c probe.c -o bin/probe.o
    if ($LASTEXITCODE -ne 0) { throw 'Native symbol probe compile failed.' }
    $symbol = & (Join-Path $toolBin 'llvm-nm.exe') --defined-only bin/probe.o
    if ($LASTEXITCODE -ne 0 -or ($symbol -join "`n") -notmatch '(?m)^([0-9a-fA-F]+) T aptabase_symbol_probe$') { throw 'Native symbol not found.' }
    $address = '0x' + $Matches[1]
    $resolved = & (Join-Path $toolBin 'llvm-symbolizer.exe') --obj=bin/probe.o --no-inlines $address
    if ($LASTEXITCODE -ne 0 -or $resolved[0] -ne 'aptabase_symbol_probe' -or $resolved[1] -notmatch 'probe.c:[1-4]:[0-9]+$') {
        throw 'Native symbol/line resolution failed.'
    }
    Write-Output 'PASS: offline Android ARM64 DWARF function/source-line resolution. No device, crash dump, server, or artifact upload tested.'
} finally { Pop-Location }
