param([Parameter(Mandatory=$true)][string]$ModulesRoot,[ValidateSet('Debug','Release')][string]$Configuration='Release')
# 雄新电力 · 雄新 OWTS: build the FactoryPress bridge (net48) and stage it as modules\FactoryPressOwts.
# Requires the .NET SDK (dotnet) for the SDK-style vendor/factory-press projects. No device is opened.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$modules=[IO.Path]::GetFullPath($ModulesRoot)
$extract=Join-Path $root 'tools\extract-owts-factorypress.py'
& python $extract --check
if($LASTEXITCODE -ne 0){throw 'Press-only OwtsServices is stale; rerun tools/extract-owts-factorypress.py and review the diff.'}
$project=Join-Path $root 'build-sdk\factorypress\Xiongxin.OWTS.FactoryPressBridge\Xiongxin.OWTS.FactoryPressBridge.csproj'
& dotnet build $project -c $Configuration -f net48 -nologo
if($LASTEXITCODE -ne 0){throw 'FactoryPress bridge build failed.'}
$bin=Join-Path $root ('build-sdk\factorypress\Xiongxin.OWTS.FactoryPressBridge\bin\'+$Configuration+'\net48')
$target=Join-Path $modules 'FactoryPressOwts'
New-Item -ItemType Directory -Path $target -Force | Out-Null
foreach($name in @('Xiongxin.OWTS.FactoryPressBridge.dll','Xiongxin.Owts.HostAdapters.FactoryPress.dll','Xiongxin.Owts.Contracts.dll','Xiongxin.Owts.Acquisition.dll')){
    $source=Join-Path $bin $name
    if(-not (Test-Path -LiteralPath $source -PathType Leaf)){throw "FactoryPress bridge output missing: $name"}
    Copy-Item -LiteralPath $source -Destination (Join-Path $target $name) -Force
}
Write-Output ('FactoryPressOwts staged: '+$target)
