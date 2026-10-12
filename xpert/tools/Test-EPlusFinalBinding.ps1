[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$FinalBuild,[Parameter(Mandatory=$true)][string]$ReplayDirectory)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$final=[IO.Path]::GetFullPath($FinalBuild).TrimEnd('\');$qa=[IO.Path]::GetFullPath($ReplayDirectory).TrimEnd('\')
if(-not $final.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)-or-not $qa.StartsWith($root+'\qa\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use owned final build and replay QA roots.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded path helper.'}
$node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq 'Assert-RegressionUnlinkedPath')
if($node.Count-ne 1){throw 'Missing bounded path helper.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
foreach($p in @($final,$qa)){Assert-RegressionUnlinkedPath $p}
$output=Join-Path $qa 'verification-final-binding.json';if(Test-Path -LiteralPath $output){throw 'Do not overwrite any prior proof.'}
$checks=0
function Check([bool]$Pass,[string]$Why){$script:checks++;if(-not $Pass){throw $Why}}
function Hash([string]$Path){Assert-RegressionUnlinkedPath $Path;(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function Json([string]$Path){Assert-RegressionUnlinkedPath $Path;if((Get-Item -LiteralPath $Path).Length-gt 32MB){throw 'Oversized QA JSON.'};Get-Content -Raw -LiteralPath $Path -Encoding UTF8|ConvertFrom-Json}
$integrationPath=Join-Path $qa 'verification.json';$integration=Json $integrationPath
$native=Json $integration.pythonProof;$managed=Json $integration.managedProof
Check ($integration.passed-and$integration.nativeExecuted-and$native.passed-and$native.nativeExecuted-and$managed.passed) 'Actual native/managed replay did not pass.'
Check (-not $integration.hardwareAccessed-and-not $integration.productStarted-and-not $integration.physicalValidation) 'Invalid replay execution boundary.'
$modulePath=Join-Path $final 'module-manifest.json';$modules=Json $modulePath
$buildPath=Join-Path $final 'build-manifest.json';$build=Json $buildPath
Check ($modules.ProtectedRelease-and$build.ProtectedRelease) 'Final candidate must remain protected.'
$entries=@{};foreach($entry in $modules.Modules){$entries[$entry.Path.Replace('/','\')]=$entry}
$analysis=Join-Path $final 'modules\DataAnalysis\HVX.PdReview.V1.0.dll'
$algorithm=Join-Path $final 'modules\DataAnalysis\fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'
Check ((Hash $analysis)-ceq$managed.dllSha256) 'Managed replay did not use exact final DataAnalysis bytes.'
Check ((Hash (Join-Path $qa 'tested-managed\HVX.PdReview.V1.0.dll'))-ceq(Hash $analysis)) 'Managed private test copy differs from final.'
Check ((Hash (Join-Path $qa 'tested-managed\fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'))-ceq(Hash $algorithm)) 'Cache fingerprint does not match final algorithm.'
Check (-not $integration.algorithmFingerprintInvoked) 'The separate algorithm is a fingerprint only in this replay.'
$sourceReceiptPath=Join-Path $final $modules.SourceResearchRuntime.ManifestPath;$sourceReceipt=Json $sourceReceiptPath
Check ((Hash $sourceReceiptPath)-ceq$modules.SourceResearchRuntime.ManifestSHA256) 'Actual final research receipt is not manifest bound.'
$pinsPath=Join-Path $final 'research-pins.json';$pins=Json $pinsPath
$nativeRoot=Join-Path $final 'modules\r71';$nativeFiles=@(Get-ChildItem -LiteralPath $nativeRoot -Recurse -File|Sort-Object FullName)
$inputs=@($PSCommandPath,$helper,$integrationPath,$integration.pythonProof,$integration.managedProof,$modulePath,$buildPath,$sourceReceiptPath,$pinsPath,$analysis,$algorithm)
foreach($file in $nativeFiles){
 $rel=$file.FullName.Substring($nativeRoot.Length+1).Replace('\','/');$hash=Hash $file.FullName
 Check ($null-ne$pins.PSObject.Properties[$rel]-and$hash-ceq$pins.$rel) ('Final native dependency pin mismatch: '+$rel)
 $moduleRelative='modules\r71\'+$rel.Replace('/','\')
 Check ($entries.ContainsKey($moduleRelative)-and$hash-ceq$entries[$moduleRelative].Sha256) ('Final native module manifest mismatch: '+$rel)
 if($file.Name-like'q*.pyd'){Check ($hash-ceq$sourceReceipt.newNativeModules.($file.Name)) ('Compiled native receipt mismatch: '+$file.Name)}
 $inputs+=$file.FullName
}
foreach($entry in $integration.inputHashes){Check ((Hash $entry.path)-ceq$entry.sha256) 'Replay integration input changed.';$inputs+=$entry.path}
foreach($entry in $native.inputHashes.PSObject.Properties){$path=Join-Path $root $entry.Name;Check ((Hash $path)-ceq$entry.Value) 'Replay native source binding changed.';$inputs+=$path}
foreach($fixture in @(Json (Join-Path $qa 'same-inputs\inputs.json'))){
 Check ((Hash $fixture.path)-ceq$fixture.sourceSha256) 'Original LOC changed.'
 $cable=Join-Path (Split-Path $fixture.path) 'CABLE.in';Check ((Hash $cable)-ceq$fixture.cableSha256) 'Original project geometry changed.'
 $inputs+=@($fixture.path,$cable,(Join-Path $qa ('same-inputs\'+$fixture.id+'.input.json')))
}
foreach($rel in @('modules\DataAnalysis\HVX.PdReview.V1.0.dll','modules\DataAnalysis\fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll')){Check ((Hash (Join-Path $final $rel))-ceq$entries[$rel].Sha256) 'Final managed module manifest mismatch.'}
$before=@($inputs|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_;SHA256=(Hash $_)}})
$drift=@($before|Where-Object{(Hash $_.Path)-cne$_.SHA256});Check ($drift.Count-eq 0) 'Final binding inputs changed during audit.'
[ordered]@{Schema='HVX.EPlusPacket.FinalRuntimeBinding/1';UTC=[DateTime]::UtcNow.ToString('o');Status='PASS';SourceUnchanged=$true;Inputs=$before;FinalBuild=$final;
 BindingChecks=$checks;NativePinnedFiles=$nativeFiles.Count;NativeReplayChecks=$native.checks;ManagedAdaptChecks=$managed.checks;OriginalReaderChecks=3;
 NativeReplayModes=@('observed_spectral','reference_correlation');HistoricalLOCCount=3;NamedCandidateVersion='Eplus-final-raw-packet-score-r01';FloatAbsoluteTolerance=1e-10;FloatRelativeTolerance=1e-10;DecisionsAndIndices='exact';
 HardwareAccessed=$false;SDKCalled=$false;ProductEntryExecuted=$false;ActualWorkerStarted=$false;UserProjectWritten=$false;PhysicalAccuracyVerified=$false;FullRegression=$false;
 EarlierAttempt='Windows PowerShell 5.1 decoded the existing UTF-8 no-BOM Chinese registration path as ANSI and rejected the missing path before any fixture ran. Re-run used PowerShell 7; no system policy or product gate changed.';
 Limitations=@('Three selected historical LOC records, not complete project/physical replay.','Task17 final module is a cache fingerprint only and is not invoked here.','Auxiliary 10/20 MHz lowpass and Task28 research statistics were not executed.','Named-candidate observed_spectral near-EOF modulo limitation retained, not physical accuracy verified.')
}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $output -Encoding UTF8
Write-Output ('EPLUS_FINAL_BINDING_PASS='+$checks+';PROOF='+$output)
