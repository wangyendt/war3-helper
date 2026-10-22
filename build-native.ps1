# Build the optional x86 Miles modules. Requires Visual C++ Build Tools and Windows SDK.
param([switch]$Test)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual C++ Build Tools and the Windows SDK to build the FPS module.' }
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual C++ x86 compiler not found.' }
$version = (Get-Content -LiteralPath (Join-Path $vs 'VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
$vc = Join-Path $vs ('VC\Tools\MSVC\' + $version)
$kit = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$sdk = Get-ChildItem -LiteralPath (Join-Path $kit 'Include') -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'um\Windows.h') } | Sort-Object Name -Descending | Select-Object -First 1
if (-not $sdk) { throw 'Windows SDK headers not found.' }
$output = Join-Path $root 'obj\native'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$args = @('/nologo','/LD','/MT','/O2','/W4','/WX','/EHsc','/DUNICODE','/D_UNICODE',
    ('/Fo' + (Join-Path $output 'HighFps.obj')),('/I' + (Join-Path $vc 'include')),
    ('/I' + (Join-Path $sdk.FullName 'ucrt')),('/I' + (Join-Path $sdk.FullName 'shared')),('/I' + (Join-Path $sdk.FullName 'um')),
    (Join-Path $root 'native\HighFps.cpp'),'/link','/MACHINE:X86','/INCREMENTAL:NO',
    ('/OUT:' + (Join-Path $output 'War3HelperFps.mix')),('/LIBPATH:' + (Join-Path $vc 'lib\x86')),
    ('/LIBPATH:' + (Join-Path $kit ('Lib\' + $sdk.Name + '\ucrt\x86'))),
    ('/LIBPATH:' + (Join-Path $kit ('Lib\' + $sdk.Name + '\um\x86'))),'kernel32.lib','version.lib','user32.lib')
& (Join-Path $vc 'bin\Hostx64\x86\cl.exe') $args
if ($LASTEXITCODE -ne 0) { throw 'FPS module build failed.' }

$backgroundArgs = @($args | ForEach-Object { $_.Replace('HighFps.cpp', 'BackgroundRun.cpp').Replace('HighFps.obj', 'BackgroundRun.obj').Replace('War3HelperFps.mix', 'War3HelperBackground.mix') })
$backgroundArgs += @('comctl32.lib', 'shell32.lib')
& (Join-Path $vc 'bin\Hostx64\x86\cl.exe') $backgroundArgs
if ($LASTEXITCODE -ne 0) { throw 'Background module build failed.' }

if ($Test) {
    $testArgs = @($backgroundArgs | Where-Object { $_ -ne '/LD' } | ForEach-Object {
        $_.Replace('native\BackgroundRun.cpp', 'tools\TestBackgroundNative.cpp').Replace('BackgroundRun.obj', 'TestBackgroundNative.obj').Replace('War3HelperBackground.mix', 'TestBackgroundNative.exe')
    })
    & (Join-Path $vc 'bin\Hostx64\x86\cl.exe') $testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Background native test build failed.' }
    & (Join-Path $output 'TestBackgroundNative.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Background native tests failed.' }
}
