[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($PSVersionTable.PSEdition -ne 'Desktop'){throw 'Use Windows PowerShell 5.1 and the target Framework CLR.'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$qa=[IO.Path]::GetFullPath($OutputRoot)
if(-not $qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'QA output must remain beneath build.'}
$boundedRunner=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($boundedRunner,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded runner.'}
$helper=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq 'Assert-RegressionUnlinkedPath')
if($helper.Count -ne 1){throw 'Missing unlinked-path helper.'}
. ([ScriptBlock]::Create($helper[0].Extent.Text))
Assert-RegressionUnlinkedPath $qa -AllowMissing
if(Test-Path -LiteralPath $qa){throw 'A fresh QA directory is required; previous evidence is retained.'}
Set-Location -LiteralPath $root
$uiBuild=Join-Path $PSScriptRoot 'Build-UiModules.ps1'
$locationBuild=Join-Path $PSScriptRoot 'Build-LocationAlgorithm.ps1'
$saveRunner=Join-Path $PSScriptRoot 'Test-PressureSaveBackgroundFocused.ps1'
$queueRunner=Join-Path $PSScriptRoot 'Test-PressureBackgroundAnalysisFocused.ps1'
$inputPaths=@($PSCommandPath,$boundedRunner,$uiBuild,$locationBuild,$saveRunner,$queueRunner,
 (Join-Path $PSScriptRoot 'Test-LocationCandidate.ps1'),(Join-Path $PSScriptRoot 'Test-PressureTask39Contract.ps1'),
 (Join-Path $PSScriptRoot 'compiler\csc.exe'),(Join-Path $root 'assets\xiongxin.ico'),
 (Join-Path $root 'modules-src\LocationABCD\ABRelease\manifest.json'),
 (Join-Path $root 'modules-src\LocationABCD\ABRelease\confirmed-legacy-inputs.json'),
 (Join-Path $root 'modules-src\Task39InputWorker\provenance\source-manifest.json'))
foreach($directory in @('src','modules-src\DataAnalysis','modules-src\PressureTest\src','modules-src\FactoryWorker','modules-src\LocationABCD\ABRelease\AB\source','modules-src\LocationAlgorithmOverlay\RecordedPhaseV2','modules-src\ConclusionAlgorithm\EvidenceV3')){
 $inputPaths+=@(Get-ChildItem -LiteralPath (Join-Path $root $directory) -File -Filter '*.cs' -Recurse|ForEach-Object FullName)
}
$inputPaths+=@(Get-ChildItem -LiteralPath (Join-Path $root 'modules-src\LocationABCD\ABRelease\AB\runtime') -File -Filter '*.dll'|ForEach-Object FullName)
$inputPaths+=@('PressureTrialIsolationSmoke.cs','PressureBackgroundAnalysisSmoke.cs','PressureAnalysisHostOptionsSmoke.cs','PressureCumulativeAnalysisSmoke.cs'|ForEach-Object{Join-Path $root ('tests\'+$_)})
$inputPaths=@($inputPaths|Sort-Object -Unique)
function CurrentInventory { @($inputPaths|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}}) }
$inputs=@(CurrentInventory)
$started=[DateTime]::UtcNow.ToString('o')
New-Item -ItemType Directory -Path $qa|Out-Null
# Execute only the reviewed first module's compile prefix. The boundary is
# checked exactly: no later UI module, Main, worker, SDK or device is invoked.
$uiSource=[IO.File]::ReadAllText($uiBuild)
$boundary='$creationResources = @('
$offset=$uiSource.IndexOf($boundary,[StringComparison]::Ordinal)
if($offset -lt 0 -or $uiSource.IndexOf($boundary,$offset+1,[StringComparison]::Ordinal) -ge 0){throw 'Analysis-only compile boundary changed.'}
$analysisPrefix=$uiSource.Substring(0,$offset)
if(-not $analysisPrefix.Contains("`$analysis = Build-Module 'DataAnalysis' 'HVX.PdReview.V1.0'")){throw 'First module identity changed.'}
# A dynamically created ScriptBlock has no source-file PSScriptRoot. Bind only
# that lexical path to the original reviewed build script's directory.
if([regex]::Matches($analysisPrefix,'\$PSScriptRoot').Count -ne 2){throw 'Compile-prefix script-root contract changed.'}
$analysisPrefix=$analysisPrefix.Replace('$PSScriptRoot',("'"+$PSScriptRoot.Replace("'","''")+"'"))
$currentModules=Join-Path $qa 'current-modules'
. ([ScriptBlock]::Create($analysisPrefix)) -OutputRoot $currentModules -MaintenancePassword
@($records.ToArray())|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'analysis-current-build.json') -Encoding UTF8
$currentAnalysis=Join-Path $currentModules 'DataAnalysis\HVX.PdReview.V1.0.dll'
$currentAlgorithmDirectory=Join-Path $qa 'current-algorithm'
& $locationBuild -OutputDirectory $currentAlgorithmDirectory
$currentAlgorithm=Join-Path $currentAlgorithmDirectory 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'
$runtimeInputs=@($currentAnalysis,$currentAlgorithm|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
& $saveRunner -OutputDirectory ((Join-Path $qa 'save').Substring($root.Length+1))
$queueLines=@(& $queueRunner -AnalysisLibrary $currentAnalysis.Substring($root.Length+1) -AlgorithmLibrary $currentAlgorithm.Substring($root.Length+1))
$queueLines|Write-Output
$proofLines=@($queueLines|Where-Object{$_ -is [string] -and $_.StartsWith('BACKGROUND_ANALYSIS_PROOF=',[StringComparison]::Ordinal)})
if($proofLines.Count -ne 1){throw 'Missing focused queue proof path.'}
$queueProof=$proofLines[0].Substring('BACKGROUND_ANALYSIS_PROOF='.Length)
$saveProof=Join-Path $qa 'save\verification-save-background.json'
$mismatches=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256})
$runtimeMismatches=@($runtimeInputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256})
$saveResult=Get-Content -LiteralPath $saveProof -Raw|ConvertFrom-Json
$queueResult=Get-Content -LiteralPath $queueProof -Raw|ConvertFrom-Json
$passed=$mismatches.Count -eq 0 -and $runtimeMismatches.Count -eq 0 -and $saveResult.SourceUnchanged -and $queueResult.SourceUnchanged -and $saveResult.Result.ExitCode -eq 0 -and $queueResult.Result.ExitCode -eq 0 -and $queueResult.HostIntegrationResult.ExitCode -eq 0
[ordered]@{Schema='HVX.Pressure.SaveQueue.CurrentSource/1';Status=$(if($passed){'PASS'}else{'FAIL'});StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Inputs=$inputs;SourceUnchanged=($mismatches.Count -eq 0);InputMismatchCount=$mismatches.Count;Mismatches=$mismatches;Runtime=$runtimeInputs;RuntimeUnchanged=($runtimeMismatches.Count -eq 0);SaveProof=$saveProof;QueueProof=$queueProof;ProofHashes=@($saveProof,$queueProof|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}});ProductEntryExecuted=$false;WorkerEntryExecuted=$false;SDKCalled=$false;HardwareAccessed=$false;UserProjectWritten=$false;FinalDeliveryBytes=$false;Scope='Current DataAnalysis and frozen AB plus recorded-phase local overlays compiled from current sources; synthetic save isolation, queue and managed host-options fixtures only. No field latency or physical high-voltage acceptance.'}|ConvertTo-Json -Depth 14|Set-Content -LiteralPath (Join-Path $qa 'proof.json') -Encoding UTF8
Write-Output ('CURRENT_SOURCE_SAVE_QUEUE_PROOF='+(Join-Path $qa 'proof.json'))
if(-not $passed){throw 'Source binding or focused regression failed; evidence retained.'}
