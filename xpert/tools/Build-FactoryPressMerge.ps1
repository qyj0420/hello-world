param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'factory-press\Xiongxin.Owts.XpertPressBridge\Xiongxin.Owts.XpertPressBridge.csproj'
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Missing factory-press bridge project: $project" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet SDK 8 is required to build the factory-press net48 bridge.' }

& dotnet build $project -c $Configuration -f net48 --nologo
if ($LASTEXITCODE -ne 0) { throw 'factory-press net48 build failed.' }

$built = Join-Path $root ("factory-press\Xiongxin.Owts.XpertPressBridge\bin\" + $Configuration + "\net48")
$bridge = Join-Path $built 'Xiongxin.Owts.XpertPressBridge.dll'
if (-not (Test-Path -LiteralPath $bridge -PathType Leaf)) { throw "net48 bridge was not produced: $bridge" }

$dist = Join-Path $root 'factory-press\dist\net48'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
Copy-Item -Path (Join-Path $built '*') -Destination $dist -Force

$deliver = if ([String]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $root ("deliverables\HV-DAC_Xpert_V1.1.2_factory-press-merge_" + $Configuration)
} else { [IO.Path]::GetFullPath($OutputDirectory) }
New-Item -ItemType Directory -Path $deliver -Force | Out-Null
Copy-Item -Path (Join-Path $built '*.dll') -Destination $deliver -Force
Get-ChildItem -LiteralPath $built -Filter '*.pdb' -File -ErrorAction SilentlyContinue | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $deliver -Force
}
Write-Output "Factory press net48: $deliver"
