[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$BeforeModule,
    [Parameter(Mandatory=$true)][string]$AfterModule,
    [Parameter(Mandatory=$true)][string]$ActualSessionPath,
    [Parameter(Mandatory=$true)][string]$OutputRoot
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath($OutputRoot)
if(-not $output.StartsWith((Join-Path $project 'build').TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Offline QA output must be inside the owned build directory.'}
if(Test-Path -LiteralPath $output){throw 'Use a fresh offline QA directory.'}
New-Item -ItemType Directory -Path $output | Out-Null
$source=Join-Path $project 'tests\R58ActualDiagnosticsBudgetOfflineSmoke.cs'
$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$moduleBefore=[IO.Path]::GetFullPath($BeforeModule)
$moduleAfter=[IO.Path]::GetFullPath($AfterModule)
$session=[IO.Path]::GetFullPath($ActualSessionPath)
$paths=@($PSCommandPath,$source,$compiler,$moduleBefore,$moduleAfter,$session,(Join-Path $project 'modules-src\DataAnalysis\src\ReviewCandidateEvidence.cs'),(Join-Path $project 'modules-src\DataAnalysis\src\ReviewAnalysisMetadataBudget.cs'))
$inputs=@($paths|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})
$exe=Join-Path $output 'R58ActualDiagnosticsBudgetOfflineSmoke.exe'
$compile=@(& $compiler /nologo /noconfig /langversion:7.3 /target:exe /platform:x64 /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll ('/out:'+$exe) $source 2>&1|ForEach-Object{$_.ToString()})
$compile|Set-Content -LiteralPath (Join-Path $output 'compile.txt') -Encoding UTF8
if($LASTEXITCODE-ne 0){throw 'Offline diagnostics regression failed to compile.'}
$runs=New-Object System.Collections.Generic.List[object]
foreach($mode in @('old','new')){
    $module=if($mode-eq 'old'){$moduleBefore}else{$moduleAfter}
    $stdout=@(& $exe $module $session (Join-Path $output $mode) $mode 2>&1|ForEach-Object{$_.ToString()})
    $code=$LASTEXITCODE
    $stdout|Set-Content -LiteralPath (Join-Path $output ($mode+'.stdout.txt')) -Encoding UTF8
    $stdout|ForEach-Object{Write-Host $_}
    $runs.Add([ordered]@{Mode=$mode;ExitCode=$code;Module=$module;Output=$stdout})
    if($code-ne 0){throw ('Offline diagnostics regression failed in '+$mode+' mode.')}
}
$changed=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash-cne $_.SHA256})
if($changed.Count-ne 0){throw 'An input changed during the offline regression.'}
[ordered]@{Schema='HVX.R58.ActualDiagnosticsBudgetOffline/1';Status='PASS';Utc=[DateTime]::UtcNow.ToString('o');Inputs=$inputs;Tests=$runs.ToArray();InputsUnchanged=$true;SourceProjectWritten=$false;ProductConstructed=$false;ProductStarted=$false;AlgorithmExecuted=$false;WorkerUsed=$false;SDKCalled=$false;HardwareUsed=$false;FixtureMeaning='31 actual saved statuses from archive 023 replicated under distinct QA-only keys to 93 records. This reproduces the project-sized metadata quota, not physical accuracy or all original LOC analysis.'}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $output 'offline-proof.json') -Encoding UTF8
