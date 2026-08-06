# Build and run the version-switch sandbox test. ASCII-only (see build.ps1 note).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$obj = Join-Path $root "obj"
New-Item -ItemType Directory -Force $obj | Out-Null

$refs = @("/r:System.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll",
          "/r:System.Web.Extensions.dll", "/r:System.IO.Compression.dll",
          "/r:System.IO.Compression.FileSystem.dll", "/r:System.Core.dll",
          "/r:Microsoft.VisualBasic.dll")

$srcs = @(Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object { $_.FullName })
$srcs += (Join-Path $root "tools\TestVersionSwitch.cs")
$exe = Join-Path $obj "TestVersionSwitch.exe"
$args = @("/nologo", "/target:exe", "/main:TestVersionSwitch", ("/out:" + $exe)) + $refs + $srcs
& $csc $args
if ($LASTEXITCODE -ne 0) { throw "test build failed" }
& $exe
exit $LASTEXITCODE
