param([Parameter(Mandatory=$true)][string]$SourceProject,[string]$LegacyBuild='build\main-test-20261009-r39',[string]$FinalBuild='')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$legacy=[IO.Path]::GetFullPath((Join-Path $root $LegacyBuild));$source=[IO.Path]::GetFullPath($SourceProject)
$qa=Join-Path $root ('build\qa-cal-packet-contract-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing;Assert-RegressionUnlinkedPath $source
$intermediates=@(Get-ChildItem -LiteralPath $legacy -Recurse -File -Filter 'unprotected-input.dll'|Where-Object{$_.FullName.EndsWith('\location-source-build\protection\unprotected-input.dll',[StringComparison]::OrdinalIgnoreCase)})
if($intermediates.Count -ne 1){throw 'Need exactly one original same-build algorithm intermediate.'}
$old=$intermediates[0].FullName;$oldManifest=Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($old))) 'recorded-phase-overlay-source-manifest.json'
if((Get-Content -LiteralPath $oldManifest -Raw|ConvertFrom-Json).OutputSHA256 -cne (Get-FileHash -LiteralPath $old).Hash){throw 'Original algorithm not bound to its manifest.'}
$analysisSources=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','PressureRecordPublication.cs','ZeroVoltageNoiseReference.cs','DefectModelCatalog.cs','AnalysisDateText.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','NoisePulseTemplates.cs','LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','CableJointStore.cs','CableJointDialog.cs','CalibrationPacketIdentityContract.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$analysisSources+=Join-Path $root 'src\LocationScorePolicy.cs'
foreach($part in @('src','vendor\PdMap','vendor\PdMap\Common','vendor\Spectrum','vendor\Task7','vendor\Task28','vendor\LocationTree','shared')){
 $analysisSources+=@(Get-ChildItem -LiteralPath (Join-Path $root ('modules-src\DataAnalysis\'+$part)) -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
}
$analysisSources+=@('modules-src\LocationABCD\ABRelease\AB\source\PeakAnchoredLeadingEdgeMath.cs','modules-src\ConclusionAlgorithm\EvidenceV3\HVX.Task15.Embedded.cs'|ForEach-Object{Join-Path $root $_})
$legacyInput=Join-Path $root 'modules-src\LocationABCD\ABRelease\confirmed-legacy-inputs.json';$brand=Join-Path $root 'assets\xiongxin.ico';$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$names=@('AnalysisCalibrationPacketContractSmoke','AnalysisCalibrationInputScopeSmoke','AnalysisActualCalibrationPacketContractSmoke','AnalysisCalibrationPresentSidecarSmoke')
$testSources=@($names+@('AnalysisActualCalibrationLocReadonly','LocationCyclePhaseSmoke','CalibrationProtectedTestReflection')|ForEach-Object{Join-Path $root ('tests\'+$_+'.cs')})
$algorithmSources=@(Get-ChildItem -LiteralPath 'modules-src\LocationAlgorithmOverlay\RecordedPhaseV2' -File -Filter '*.cs'|ForEach-Object FullName)+@(Get-ChildItem -LiteralPath 'modules-src\LocationABCD\ABRelease\AB\source' -File -Filter '*.cs'|ForEach-Object FullName)
$sourceInputs=@(Join-Path $source 'ITEM.in';Join-Path $source 'CABLE.in')+@(Get-ChildItem -LiteralPath $source -File|Where-Object{$_.Extension -in '.cal','.loc','.dem'}|ForEach-Object FullName)
$runtimeInputs=@(Get-ChildItem -LiteralPath 'modules-src\LocationABCD\ABRelease\AB\runtime' -File -Filter '*.dll'|ForEach-Object FullName)
$inputs=$analysisSources+$algorithmSources+$testSources+$sourceInputs+$runtimeInputs+@($PSCommandPath,$helper,$old,$oldManifest,$legacyInput,$brand,$csc,
 (Join-Path $root 'src\LocationCycleRecord.cs'),(Join-Path $root 'modules-src\LocationABCD\ABRelease\manifest.json'),
 (Join-Path $PSScriptRoot 'Test-LocationCandidate.ps1'),(Join-Path $PSScriptRoot 'Build-LocationAlgorithm.ps1'),(Join-Path $PSScriptRoot 'Build-UiModules.ps1'))
$final='';$mapping='';$finalManifest='';$finalReceipt=''
if(-not [String]::IsNullOrWhiteSpace($FinalBuild)){
 $final=[IO.Path]::GetFullPath($FinalBuild);Assert-RegressionUnlinkedPath $final
 if(-not $final.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'FinalBuild must be an owned build candidate.'}
 $algorithm=Join-Path $final 'modules\DataAnalysis\fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll';$analysis=Join-Path $final 'modules\DataAnalysis\HVX.PdReview.V1.0.dll'
 $maps=@(Get-ChildItem -LiteralPath $final -Recurse -File -Filter 'mapping.xml'|Where-Object{$_.FullName.EndsWith('\location-source-build\protection\mapping.xml',[StringComparison]::OrdinalIgnoreCase)})
 if($maps.Count -ne 1){throw 'Need exactly one final location protection mapping.'};$mapping=$maps[0].FullName
 $finalReceipt=Join-Path (Split-Path $mapping) 'receipt.json';$finalManifest=Join-Path (Split-Path (Split-Path $mapping)) 'recorded-phase-overlay-source-manifest.json'
 $protection=Get-Content -LiteralPath $finalReceipt -Raw -Encoding UTF8|ConvertFrom-Json;$overlay=Get-Content -LiteralPath $finalManifest -Raw -Encoding UTF8|ConvertFrom-Json
 if($protection.OutputSha256 -cne (Get-FileHash -LiteralPath $algorithm).Hash -or -not $protection.NumericRegressionPassed -or $protection.InputSha256 -cne $overlay.OutputSHA256){throw 'Final protected algorithm is not bound to exact build/protection evidence.'}
 if($overlay.SharedPacketContractSourceSHA256 -cne (Get-FileHash -LiteralPath (Join-Path $root 'src\CalibrationPacketIdentityContract.cs')).Hash){throw 'Final algorithm contract source is stale.'}
 foreach($entry in $overlay.Files){if($entry.OverlaySHA256 -cne (Get-FileHash -LiteralPath $entry.OverlaySource).Hash){throw 'Final CAL overlay source is stale.'}}
 $inputs+=@($algorithm,$analysis,$mapping,$finalReceipt,$finalManifest,(Join-Path $final 'build-manifest.json'),(Join-Path $final 'module-manifest.json'))
 foreach($folder in @('modules\DataAnalysis','modules\Hybrid')){$inputs+=@(Get-ChildItem -LiteralPath (Join-Path $final $folder) -Recurse -File|ForEach-Object FullName)}
}
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o');New-Item -ItemType Directory -Path $qa|Out-Null
$common=@('/nologo','/noconfig','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
if((Get-FileHash -LiteralPath $legacyInput).Hash -cne '5BE1895AA9BF2D9449A4875112C1A3D976F0119D23F624E976141FE955C6D37D'){throw 'Legacy input pin source changed.'}
if($final -eq ''){
 $algorithmOutput=Join-Path $qa 'algorithm';$algorithm=Join-Path $algorithmOutput 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'
 & (Join-Path $PSScriptRoot 'Build-LocationAlgorithm.ps1') -OutputDirectory $algorithmOutput
 $analysis=Join-Path $qa 'modules\DataAnalysis\HVX.PdReview.V1.0.dll';New-Item -ItemType Directory -Path (Split-Path $analysis)|Out-Null
 $pins=Join-Path $qa 'legacy-unit-pins.json';@((Get-Content -LiteralPath $legacyInput -Raw -Encoding UTF8|ConvertFrom-Json)|ForEach-Object{$_.sha256})|ConvertTo-Json|Set-Content -LiteralPath $pins -Encoding UTF8
 $compiled=@(& $csc @common /target:library ('/resource:'+$brand+',XIONGXIN.Brand.ico') ('/resource:'+$pins+',HVX.LegacyUnitPins') ('/out:'+$analysis) @analysisSources 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'analysis.compile.txt') -Encoding UTF8;if($code -ne 0){throw ($compiled -join "`n")}
 Copy-Item -LiteralPath $algorithm -Destination (Join-Path (Split-Path $analysis) 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll')
}
$testBuild=if($final -eq ''){$qa}else{$final}
$results=New-Object 'System.Collections.Generic.List[object]';$failed=$false
foreach($name in $names){
 $exe=Join-Path $qa ($name+'.exe');$sources=@(Join-Path $root ('tests\'+$name+'.cs'))
 if($name -eq 'AnalysisCalibrationPacketContractSmoke'){$sources+=Join-Path $root 'src\CalibrationPacketIdentityContract.cs';$sources+=Join-Path $root 'tests\LocationCyclePhaseSmoke.cs'}
 if($name -eq 'AnalysisActualCalibrationPacketContractSmoke'){$sources+=Join-Path $root 'tests\AnalysisActualCalibrationLocReadonly.cs'}
 if($name -eq 'AnalysisCalibrationPresentSidecarSmoke'){$sources+=Join-Path $root 'src\CalibrationPacketIdentityContract.cs'}
 if($name -in @('AnalysisCalibrationPacketContractSmoke','AnalysisActualCalibrationPacketContractSmoke')){$sources+=Join-Path $root 'tests\CalibrationProtectedTestReflection.cs'}
 $compiled=@(& $csc @common /target:exe ('/main:'+$name) ('/out:'+$exe) @sources 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8;if($code -ne 0){throw ($compiled -join "`n")}
 $arguments=if($name -eq 'AnalysisCalibrationPacketContractSmoke'){@($algorithm,$old,(Join-Path $qa 'strict-cases'))}elseif($name -eq 'AnalysisCalibrationInputScopeSmoke'){@($testBuild,(Join-Path $qa 'scope-cases'))}elseif($name -eq 'AnalysisCalibrationPresentSidecarSmoke'){@($testBuild,(Join-Path $qa 'present-cases'))}else{@($testBuild,(Join-Path $qa 'actual-cases'),$source)}
 try{
  if($final -ne ''){$env:HVX_CAL_TEST_MAP=$mapping;$env:HVX_CAL_TEST_MAP_SHA256=(Get-FileHash -LiteralPath $mapping).Hash;$env:HVX_CAL_TEST_ALGORITHM_SHA256=(Get-FileHash -LiteralPath $algorithm).Hash}
  $run=Invoke-RegressionExecutable -Executable $exe -Arguments $arguments -LogPrefix (Join-Path $qa $name) -TimeoutMilliseconds 120000
 }finally{Remove-Item Env:\HVX_CAL_TEST_MAP,Env:\HVX_CAL_TEST_MAP_SHA256,Env:\HVX_CAL_TEST_ALGORITHM_SHA256 -ErrorAction SilentlyContinue}
 $results.Add([ordered]@{Name=$name;Result=$run;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash});$run.Output|Write-Output
 if($run.ExitCode -ne 0){$failed=$true}
}
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$proof=Join-Path $qa 'verification-cal-packet-contract.json'
[ordered]@{Schema='HVX.CalibrationPacketContract.Focused/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Status=$(if(-not $failed -and $unchanged){'PASS'}else{'FAIL'});SourceUnchanged=$unchanged;Inputs=$before;
 HardwareAccessed=$false;SDKCalled=$false;ProductEntryExecuted=$false;UserProjectWritten=$false;PhysicalAccuracyVerified=$false;FullRegression=$false;SourceProject=$source;
 FinalBuild=$final;SameFinalProductBytes=($final -ne '');ProductModulesRecompiled=($final -eq '');AlgorithmPath=$algorithm;AnalysisPath=$analysis;ProtectionMapping=$mapping;
 AlgorithmSHA256=(Get-FileHash -LiteralPath $algorithm).Hash;AnalysisSHA256=(Get-FileHash -LiteralPath $analysis).Hash;Tests=$results.ToArray();
 Scope='Explicit local CAL packet identity overlay; original frozen AB verified unchanged; legacy no-sidecar exact regression, original B overlap rejection, full actual A6 readonly provenance.'}|ConvertTo-Json -Depth 15|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output ('CAL_PACKET_CONTRACT_PROOF='+$proof)
if($failed -or -not $unchanged){throw ('CAL packet contract focused regression failed: '+$qa)}
