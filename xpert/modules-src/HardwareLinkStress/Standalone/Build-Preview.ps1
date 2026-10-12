[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$sourceRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$moduleRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot '..'))
$projectRoot = [IO.Path]::GetFullPath((Join-Path $moduleRoot '..\..'))
$compiler = Join-Path $projectRoot 'tools\compiler\csc.exe'
$outputRoot = Join-Path $sourceRoot 'build\preview'
$exe = Join-Path $outputRoot 'Xiongxin.HardwareLinkStress.Preview.exe'
$icon = Join-Path $sourceRoot 'assets\xiongxin.ico'
$logo = Join-Path $sourceRoot 'assets\xiongxin-original.png'
$manifest = Join-Path $sourceRoot 'app.manifest'

$sources = @(
    (Join-Path $projectRoot 'src\PicoCaptureCadence.cs'),
    (Join-Path $moduleRoot 'Core\StressContracts.cs'),
    (Join-Path $moduleRoot 'Core\TransferTierPlanner.cs'),
    (Join-Path $moduleRoot 'src\StressMonitorControl.cs'),
    (Join-Path $sourceRoot 'Program.cs'),
    (Join-Path $sourceRoot 'HostRegistration.Preview.cs'),
    (Join-Path $sourceRoot 'StressStandaloneForm.cs')
)
foreach ($required in @($compiler, $icon, $logo, $manifest) + $sources) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "构建文件缺失：$required" }
}

# This script intentionally includes neither native adapters nor a live host.
# Running it builds only an inert UI preview, never a hardware test release.
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$compilerArgs = @(
    '/nologo', '/noconfig', '/langversion:7.3', '/target:winexe',
    '/platform:x64', '/optimize+', '/r:System.dll', '/r:System.Core.dll',
    '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
    ('/win32icon:' + $icon), ('/win32manifest:' + $manifest),
    ('/resource:' + $icon + ',Brand.ico'),
    ('/resource:' + $logo + ',Brand.png'),
    ('/out:' + $exe)
)
& $compiler @compilerArgs @sources
if ($LASTEXITCODE -ne 0) { throw "离线预览版编译失败：$LASTEXITCODE" }
Get-FileHash -LiteralPath $exe -Algorithm SHA256 | Select-Object Path, Hash
