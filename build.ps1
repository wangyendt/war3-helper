# Build War3Helper. Requires only the in-box .NET Framework 4.x compiler.
# NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI,
# and mangled multi-byte comment characters can swallow the following line.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
$out = Join-Path $root "bin"
$obj = Join-Path $root "obj"
New-Item -ItemType Directory -Force $out | Out-Null
New-Item -ItemType Directory -Force $obj | Out-Null

$refs = @("/r:System.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll",
          "/r:System.Web.Extensions.dll", "/r:System.IO.Compression.dll",
          "/r:System.IO.Compression.FileSystem.dll", "/r:System.Core.dll",
          "/r:Microsoft.VisualBasic.dll")

# Step 1: generate the application icon
$icoTool = Join-Path $obj "MakeIcon.exe"
$ico = Join-Path $obj "app.ico"
$iconArgs = @("/nologo", "/target:exe", ("/out:" + $icoTool), "/r:System.dll", "/r:System.Drawing.dll",
              (Join-Path $root "src\IconGen.cs"), (Join-Path $root "tools\MakeIcon.cs"))
& $csc $iconArgs
if ($LASTEXITCODE -ne 0) { throw "icon tool build failed" }
& $icoTool $ico
if ($LASTEXITCODE -ne 0) { throw "icon generation failed" }

# Step 2: compile the main program
$srcs = @(Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object { $_.FullName })
if ($srcs.Count -eq 0) { throw "no source files found under src\" }
$mainArgs = @("/nologo", "/target:winexe", "/optimize+", "/platform:anycpu",
              ("/out:" + (Join-Path $out "War3Helper.exe")), ("/win32icon:" + $ico)) + $refs + $srcs
Write-Host "compiling $($srcs.Count) source files..."
& $csc $mainArgs
if ($LASTEXITCODE -eq 0) { Write-Host "Build OK: $out\War3Helper.exe" } else { throw "build failed" }
