[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$moduleRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectRoot = [IO.Path]::GetFullPath((Join-Path $moduleRoot '..\..'))
$compiler = Join-Path $projectRoot 'tools\compiler\csc.exe'
$outRoot = Join-Path $moduleRoot 'build-check'
$output = Join-Path $outRoot 'StressEngineTests.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw "C# compiler missing: $compiler" }
New-Item -ItemType Directory -Path $outRoot -Force | Out-Null

$sources = @(
    (Join-Path $projectRoot 'src\PicoCaptureCadence.cs'),
    (Join-Path $moduleRoot 'Core\StressContracts.cs'),
    (Join-Path $moduleRoot 'Core\TransferTierPlanner.cs'),
    (Join-Path $moduleRoot 'Core\StressEngine.cs'),
    (Join-Path $moduleRoot 'Core\StressEngine.Recovery.cs'),
    (Join-Path $moduleRoot 'Tests\StressEngineTests.cs')
)
foreach ($source in $sources) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Offline source missing: $source" }
}
$compilerArgs = @('/nologo', '/noconfig', '/langversion:7.3', '/target:exe', '/platform:x64',
    '/r:System.dll', '/r:System.Core.dll', ('/out:' + $output))
& $compiler @compilerArgs @sources
if ($LASTEXITCODE -ne 0) { throw "Offline stress-engine compilation failed: $LASTEXITCODE" }
& $output
if ($LASTEXITCODE -ne 0) { throw "Offline stress-engine fake suite failed: $LASTEXITCODE" }
