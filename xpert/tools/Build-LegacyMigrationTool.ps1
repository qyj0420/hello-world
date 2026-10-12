param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SkipSmoke
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compiler = Join-Path $root 'tools\compiler\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw "Missing repository compiler: $compiler" }
$icon = Join-Path $root 'assets\xiongxin.ico'
$logo = Join-Path $root 'assets\xiongxin-original.png'
foreach ($asset in @($icon, $logo)) {
    if (-not (Test-Path -LiteralPath $asset -PathType Leaf)) { throw "Missing XIONGXIN asset: $asset" }
}
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File |
    Where-Object { $_.Name -ne 'Program.cs' -and $_.Name -ne 'AssemblyInfo.cs' } |
    Sort-Object FullName | ForEach-Object FullName)
if ($sources.Count -lt 8) { throw 'Migration source set is incomplete.' }
$sources += (Join-Path $PSScriptRoot 'LegacyProjectMigrationTool.cs')
$output = Join-Path $root ('build\LegacyMigrationTool\' + $Configuration)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe = Join-Path $output 'XIONGXIN.LegacyProjectMigration.exe'
$arguments = @(
    '/nologo', '/langversion:7.3', '/deterministic+', '/target:winexe', '/platform:x64',
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
    '/r:System.Xml.dll', '/r:System.Xml.Linq.dll', '/r:System.Web.Extensions.dll',
    '/r:Microsoft.CSharp.dll', '/r:System.Security.dll',
    ('/win32icon:' + $icon),
    ('/win32manifest:' + (Join-Path $root 'app.manifest')),
    ('/resource:' + $icon + ',Brand.ico'),
    ('/resource:' + $logo + ',Brand.png'),
    ('/out:' + $exe)
)
if ($Configuration -eq 'Release') { $arguments += '/optimize+' }
else { $arguments += @('/optimize-', '/debug:full') }
& $compiler @arguments @sources
if ($LASTEXITCODE -ne 0) { throw 'Standalone migration tool compilation failed.' }
Copy-Item -LiteralPath (Join-Path $root 'App.config') -Destination ($exe + '.config') -Force
$assetOutput = Join-Path $output 'assets'
New-Item -ItemType Directory -Path $assetOutput -Force | Out-Null
Copy-Item -LiteralPath $icon -Destination (Join-Path $assetOutput 'xiongxin.ico') -Force
Copy-Item -LiteralPath $logo -Destination (Join-Path $assetOutput 'xiongxin-original.png') -Force
Write-Output "Built: $exe"

if (-not $SkipSmoke) {
    $report = Join-Path $output 'smoke-result.txt'
    if (Test-Path -LiteralPath $report -PathType Leaf) { Remove-Item -LiteralPath $report -Force }
    $smokeProcess = Start-Process -FilePath $exe -ArgumentList ('--smoke "{0}"' -f $report) `
        -WindowStyle Hidden -Wait -PassThru
    if ($smokeProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report -PathType Leaf)) {
        if (Test-Path -LiteralPath $report -PathType Leaf) { Get-Content -LiteralPath $report }
        throw 'Standalone migration tool smoke check failed.'
    }
    Get-Content -LiteralPath $report
}
