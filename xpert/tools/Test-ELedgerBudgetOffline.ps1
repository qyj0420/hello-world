[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ModulesRoot,[Parameter(Mandatory=$true)][string]$QaRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$qa=[IO.Path]::GetFullPath($QaRoot)
if(-not $qa.StartsWith($projectRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'QA output must stay inside the owned project.'}
New-Item -ItemType Directory -Path $qa -Force | Out-Null
$dll=Join-Path ([IO.Path]::GetFullPath($ModulesRoot)) 'DataAnalysis\HVX.PdReview.V1.0.dll'
$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$before=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
$sources=@('ReviewCandidateEvidence.cs','ReviewAnalysisMetadataBudget.cs','ReviewEAnalysis.cs','ReviewAcceptancePresentation.cs','ReviewViewedState.cs','ReviewProjectAnalysis.cs','PdReviewControl.cs','ReviewAnalysisSession.cs','ReviewArchiveRollback.cs','ReviewLayout.cs','ReviewHybridAi.cs')
$sourcePaths=@($sources|ForEach-Object{Join-Path $projectRoot ('modules-src\DataAnalysis\src\'+$_)})
$sourcePaths+=Join-Path $projectRoot 'modules-src\DataAnalysis\vendor\Spectrum\SpectrumClassification.cs'
$sourcePaths+=Join-Path $projectRoot 'modules-src\DataAnalysis\vendor\PdMap\PdMapControl.cs'
$testNames=@('LocationELedgerBudgetOfflineSmoke','ReviewAcceptancePresentationOfflineSmoke','LocalAiSuggestionFeedbackOfflineSmoke')
$boundPaths=@($sourcePaths)+@($dll,$compiler,$PSCommandPath)+@($testNames|ForEach-Object{Join-Path $projectRoot ('tests\'+$_+'.cs')})
$inputs=@($boundPaths|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})
$records=New-Object System.Collections.Generic.List[object]
foreach($name in $testNames){
    $source=Join-Path $projectRoot ('tests\'+$name+'.cs')
    $exe=Join-Path $qa ($name+'.exe')
    $compile=@(& $compiler /nologo /noconfig /langversion:7.3 /target:exe /platform:x64 /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll ('/out:'+$exe) $source 2>&1 | ForEach-Object {$_.ToString()})
    $compile | Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
    if($LASTEXITCODE-ne 0){throw ($name+' compilation failed: '+($compile-join "`n"))}
    $inputs+=[ordered]@{Path=$exe;SHA256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash}
    # These executables load exact managed methods by reflection and use an
    # uninitialized carrier, deliberately skipping all control constructors.
    $testArguments=@($dll)
    if($name-eq 'LocationELedgerBudgetOfflineSmoke'){$testArguments+=Join-Path $qa 'synthetic-session'}
    $output=@(& $exe @testArguments 2>&1 | ForEach-Object {$_.ToString()})
    $exitCode=$LASTEXITCODE
    $output | Set-Content -LiteralPath (Join-Path $qa ($name+'.stdout.txt')) -Encoding UTF8
    $output | ForEach-Object {Write-Host $_}
    $records.Add([ordered]@{Test=$name;ExitCode=$exitCode;SourceSha256=(Get-FileHash -LiteralPath $source).Hash;ExeSha256=(Get-FileHash -LiteralPath $exe).Hash;Output=$output})
    if($exitCode-ne 0){throw ($name+' failed')}
}
$after=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
if($before-cne $after){throw 'Tested module changed during the checks.'}
$inputMismatchCount=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -cne $_.SHA256}).Count
if($inputMismatchCount-ne 0){throw 'A source, test, compiler, script or tested runtime changed during the checks.'}
$sourceHashes=@($sources | ForEach-Object {$path=Join-Path $projectRoot ('modules-src\DataAnalysis\src\'+$_);[ordered]@{Path=$path;SHA256=(Get-FileHash -LiteralPath $path).Hash}})
$spectrum=Join-Path $projectRoot 'modules-src\DataAnalysis\vendor\Spectrum\SpectrumClassification.cs'
$sourceHashes+=[ordered]@{Path=$spectrum;SHA256=(Get-FileHash -LiteralPath $spectrum).Hash}
$pdmap=Join-Path $projectRoot 'modules-src\DataAnalysis\vendor\PdMap\PdMapControl.cs'
$sourceHashes+=[ordered]@{Path=$pdmap;SHA256=(Get-FileHash -LiteralPath $pdmap).Hash}
[ordered]@{Status='PASS';Utc=[DateTime]::UtcNow.ToString('o');Module=$dll;ModuleSha256=$before;Inputs=$inputs;InputMismatchCount=$inputMismatchCount;InputsUnchanged=$true;Runtime=@([ordered]@{Path=$dll;SHA256=$before});RuntimeUnchanged=$true;Tests=$records.ToArray();CurrentSourceHashes=$sourceHashes;SyntheticOnly=$true;ProductConstructed=$false;ProductStarted=$false;WorkerUsed=$false;HardwareUsed=$false;HardwareAccessed=$false;SDKCalled=$false;PeakDeviceMemoryValidated=$false;BudgetMeaning='512 MiB is cumulative validated decoded ledger work; every ledger is expanded and discarded separately, bounded at 16 MiB. It is not a claim of 512 MiB resident or validated physical memory.'} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $qa 'offline-proof.json') -Encoding UTF8
Write-Host ('Proof: '+(Join-Path $qa 'offline-proof.json'))
