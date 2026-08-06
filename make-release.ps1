# Build a release zip. ASCII-only (see build.ps1 note about PowerShell 5.1 encoding).
#   powershell -ExecutionPolicy Bypass -File make-release.ps1 -Version 1.0.0
param([Parameter(Mandatory=$true)][string]$Version)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "=== building ==="
& powershell -ExecutionPolicy Bypass -File (Join-Path $root "build.ps1")
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "=== running tests ==="
& powershell -ExecutionPolicy Bypass -File (Join-Path $root "build-test.ps1") > $null 2>&1
if ($LASTEXITCODE -ne 0) { throw "tests failed - refusing to package" }
Write-Host "all tests passed"

$exe = Join-Path $root "bin\War3Helper.exe"
if (-not (Test-Path $exe)) { throw "War3Helper.exe not found" }

$dist = Join-Path $root "dist"
$stage = Join-Path $dist ("stage-" + $Version)
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

Copy-Item $exe $stage

# The quick-start file is named in Chinese inside the zip. This script MUST stay ASCII
# (PowerShell 5.1 decodes BOM-less UTF-8 as ANSI), so build the name from code points
# instead of writing the literal - a literal here silently produces a mojibake filename.
$docName = [string]([char]0x4F7F + [char]0x7528 + [char]0x8BF4 + [char]0x660E) + ".txt"
$notes = Join-Path $root "release-notes.txt"
if (Test-Path $notes) { Copy-Item $notes (Join-Path $stage $docName) }

$zip = Join-Path $dist ("War3Helper-v" + $Version + ".zip")
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
Remove-Item $stage -Recurse -Force

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
$size = (Get-Item $zip).Length
Write-Host ""
Write-Host "=== package ready ==="
Write-Host ("  file   : " + $zip)
Write-Host ("  size   : {0:n0} bytes" -f $size)
Write-Host ("  sha256 : " + $hash)
Write-Host ""
Write-Host "contents:"
$z = [IO.Compression.ZipFile]::OpenRead($zip)
$names = @()
$z.Entries | ForEach-Object {
    $names += $_.FullName
    Write-Host ("  {0,-24} {1,10:n0} bytes" -f $_.FullName, $_.Length)
}
$z.Dispose()

# Self-check: verify the Chinese entry name really is U+4F7F U+7528 U+8BF4 U+660E.
# Console output cannot be trusted here (codepage 936 renders correct UTF-8 as mojibake),
# so compare code points instead.
$doc = $names | Where-Object { $_ -like "*.txt" } | Select-Object -First 1
if ($doc) {
    $stem = [IO.Path]::GetFileNameWithoutExtension($doc)
    $want = @(0x4F7F, 0x7528, 0x8BF4, 0x660E)
    $got = @([int[]][char[]]$stem)
    $ok = ($got.Count -eq $want.Count)
    if ($ok) { for ($i = 0; $i -lt $want.Count; $i++) { if ($got[$i] -ne $want[$i]) { $ok = $false } } }
    if (-not $ok) {
        $hex = ($got | ForEach-Object { 'U+{0:X4}' -f $_ }) -join ' '
        throw "doc filename is mangled: $hex (expected U+4F7F U+7528 U+8BF4 U+660E)"
    }
    Write-Host "doc filename verified OK"
}
