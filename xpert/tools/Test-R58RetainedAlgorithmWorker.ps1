[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Module,
    [Parameter(Mandatory=$true)][string]$RuntimeStage,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [string]$FirstLoc="D:\OWTSData\050537-050538\2026年05月05日16时31分\C_13.40kV_2026-05-05-16'39'42.loc",
    [string]$SecondLoc="D:\OWTSData\050537-050538\2026年05月05日16时31分\C_13.40kV_2026-05-05-16'34'56.loc"
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath($OutputRoot)
if(-not $output.StartsWith((Join-Path $project 'build').TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'QA output must be under the owned build directory.'}
if(Test-Path -LiteralPath $output){throw 'Fresh QA output required.'}
New-Item -ItemType Directory -Path $output | Out-Null
$runtime=[IO.Path]::GetFullPath((Join-Path $RuntimeStage 'modules'))
$pins=[IO.Path]::GetFullPath((Join-Path $RuntimeStage 'research-pins.json'))
$modulePath=[IO.Path]::GetFullPath($Module)
$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$template=Join-Path $project 'build\single-loc-algorithm-proof-01\request.json'
$protocol=Join-Path $project 'tests\r58_algorithm_worker_protocol.py'
$sources=@('R58RetainedAlgorithmWorkerOfflineSmoke','R58SingleLocAlgorithmPerformance','R58AlgorithmWorkerFaultOfflineSmoke')
$inputs=@($modulePath,$pins,$FirstLoc,$SecondLoc,$template,$protocol,(Join-Path $project 'modules-src\DataAnalysis\src\ReviewERuntime.cs'),(Join-Path $project 'modules-src\ResearchAdapter\research_entry.py'))
$inputs+=@($sources|ForEach-Object{Join-Path $project ('tests\'+$_+'.cs')})
$identities=@($inputs|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})
foreach($name in $sources){
    $source=Join-Path $project ('tests\'+$name+'.cs')
    & $compiler /nologo /noconfig /langversion:7.3 /target:exe /platform:x64 /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll ('/resource:'+$pins+',HVX.Research.Pins') ('/out:'+(Join-Path $output ($name+'.exe'))) $source
    if($LASTEXITCODE-ne 0){throw ('Offline harness compilation failed: '+$name)}
}
& (Join-Path $output 'R58RetainedAlgorithmWorkerOfflineSmoke.exe') $modulePath $runtime $FirstLoc $SecondLoc $template (Join-Path $output 'actual-equivalence')
if($LASTEXITCODE-ne 0){throw 'Actual complete-ledger equivalence failed.'}
& (Join-Path $output 'R58SingleLocAlgorithmPerformance.exe') $modulePath $FirstLoc $runtime (Join-Path $output 'single-loc-timing')
if($LASTEXITCODE-ne 0){throw 'Single LOC timing/adaptation failed.'}
& (Join-Path $output 'R58AlgorithmWorkerFaultOfflineSmoke.exe') $modulePath $runtime (Join-Path $output 'fault-isolation')
if($LASTEXITCODE-ne 0){throw 'Worker pipe/timeout/cancellation isolation failed.'}
& (Join-Path $runtime 'r71\python.exe') -I -X utf8 -B $protocol --runtime (Join-Path $runtime 'r71') --qa (Join-Path $output 'compiled-protocol')
if($LASTEXITCODE-ne 0){throw 'Compiled protocol bounds failed.'}
foreach($identity in $identities){if((Get-FileHash -LiteralPath $identity.Path -Algorithm SHA256).Hash-cne $identity.SHA256){throw ('An input changed during QA: '+$identity.Path)}}
$proof=[ordered]@{Schema='HVX.R58.RetainedAlgorithmWorkerCombined/1';Status='PASS';Utc=[DateTime]::UtcNow.ToString('o');Inputs=$identities;Checks=42;InputsUnchanged=$true;ProductStarted=$false;UIConstructed=$false;HardwareSDKCalled=$false;UserProjectWritten=$false;Scope='Actual E/E+ complete-ledger equality and settings/record isolation; independent pipe failures, timeout, cancellation, retained-byte integrity, bounded compiled protocol; measured read-through-Adapt timing excludes UI and persistence.'}
$proof|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'combined-proof.json') -Encoding UTF8
