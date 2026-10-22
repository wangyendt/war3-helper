# Build and run the version-switch sandbox test. ASCII-only (see build.ps1 note).
param([string]$HighFpsPackage, [string]$FpsVersionsRoot)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$obj = Join-Path $root "obj"
New-Item -ItemType Directory -Force $obj | Out-Null
& (Join-Path $root 'build-native.ps1') -Test

$refs = @("/r:System.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll",
          "/r:System.Web.Extensions.dll", "/r:System.IO.Compression.dll",
          "/r:System.IO.Compression.FileSystem.dll", "/r:System.Core.dll",
          "/r:Microsoft.VisualBasic.dll")

$srcs = @(Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object { $_.FullName })
$srcs += @(Get-ChildItem (Join-Path $root "tools") -Filter Test*.cs | ForEach-Object { $_.FullName })
$exe = Join-Path $obj "Tests.exe"
$args = @("/nologo", "/target:exe", "/main:TestMain", ("/out:" + $exe),
    ("/resource:" + (Join-Path $obj 'native\War3HelperFps.mix') + ',War3Helper.HighFpsModule'),
    ("/resource:" + (Join-Path $obj 'native\War3HelperBackground.mix') + ',War3Helper.BackgroundModule')) + $refs + $srcs
& $csc $args
if ($LASTEXITCODE -ne 0) { throw "test build failed" }
$testArgs = @()
if ($HighFpsPackage) { $testArgs += @('--fps-package', $HighFpsPackage) }
if ($FpsVersionsRoot) { $testArgs += @('--fps-versions', $FpsVersionsRoot) }
& $exe $testArgs
exit $LASTEXITCODE
