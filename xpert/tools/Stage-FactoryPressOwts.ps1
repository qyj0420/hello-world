param(
    [Parameter(Mandatory=$true)][string]$CompileOutputRoot,
    [Parameter(Mandatory=$true)][string]$DesktopBuildRoot
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compile=[IO.Path]::GetFullPath($CompileOutputRoot)
$desktop=[IO.Path]::GetFullPath($DesktopBuildRoot)
$lib=Join-Path $compile 'Grok.Pressure.CompileOnly.dll'
if(-not (Test-Path -LiteralPath $lib)){throw ('Missing '+$lib+' — run Build-GrokPressCompileOnly.ps1 first.')}
$dest=Join-Path $desktop 'modules\FactoryPressOwts'
New-Item -ItemType Directory -Path $dest -Force|Out-Null
Copy-Item -LiteralPath $lib -Destination (Join-Path $dest 'Grok.Pressure.CompileOnly.dll') -Force
# Also stage next to PressureRuntime for discovery
$rt=Join-Path $desktop 'modules\PressureRuntime'
if(Test-Path -LiteralPath $rt){
    Copy-Item -LiteralPath $lib -Destination (Join-Path $rt 'Grok.Pressure.CompileOnly.dll') -Force
}
[ordered]@{
    Schema='HVX.FactoryPressOwts.Stage/1'
    Brand='雄新电力 / 雄新 OWTS'
    CompletedUtc=[DateTime]::UtcNow.ToString('o')
    LibrarySHA256=(Get-FileHash -LiteralPath $lib).Hash
    DesktopModulesFactoryPressOwts=$dest
    HardwareStack=@('DeviceSdkLease','LiveEmergencyMonitor','SessionFault','StatusCopy','Task6RangeAdvisor','FactoryPressApi')
    IntegratedIntoDesktopHost=$true
    Notes='ModuleHost.FactoryPressure prefers FactoryPressApi when this DLL is present.'
}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $dest 'stage-manifest.json') -Encoding UTF8
Write-Output ('STAGED_FACTORY_PRESS_OWTS='+(Join-Path $dest 'Grok.Pressure.CompileOnly.dll'))
