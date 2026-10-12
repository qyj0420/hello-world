# Mechanical inventories for the explicit r37 local changes. Frozen upstream is
# never edited. The independent hash pin is updated separately after review.
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$worker=Join-Path $project 'modules-src/FactoryWorker'
$adaptationPath=Join-Path $worker 'v30-batch-local-adaptation-20261008.json'
$provenancePath=Join-Path $worker 'v30-batch-integration-provenance.json'
$a=Get-Content -LiteralPath $adaptationPath -Raw -Encoding UTF8|ConvertFrom-Json
$p=Get-Content -LiteralPath $provenancePath -Raw -Encoding UTF8|ConvertFrom-Json
$review=[ordered]@{
 'FactoryRunner.cs'='r37 pressure context uses only selected voltage in startup/ramp/hold; IMON raw >=5 V and selected voltage no-growth must both persist 3 s. Retain time bound, current emergency, selected validity/limits, manual Stop and cleanup; no output recovery.'
 'FactoryArtFeedback.cs'='r37 validates only selected pressure voltage; other channels remain raw diagnostics. Calibration keeps its independent context. No sample clipping or invented zero.'
 'FactoryFeedbackWindow.cs'='r37 selected pressure window retains unselected NaN diagnostics without accepting a non-finite selected window.'
 'FactoryTargetHold.cs'='r37 hold uses same selected voltage, not a hidden second-source veto; selected validity and existing limits retained.'
 'FactoryFaultResidual.cs'='r37 end-only residual observation uses selected pressure voltage and configured threshold; no physical discharge certification.'
 'NativeFactoryBackend.cs'='r37 establishes sole read-only DI and reports real initial snapshot before Pico Open; exact identities/output ownership still required. No additional native reader.'
 'FactoryModels.cs'='r37 non-XML pressure runtime context distinguishes pressure from calibration and preserves it only in managed Clone; config cannot manufacture it.'
 'FactoryNativeSessionPolicy.cs'='r37 derives pressure context from real operation purpose, never from XML; manual zero and calibration boundaries retained.'
 'FactoryZeroInputRunner.cs'='r37 derives supervised zero-pressure context only; manual input-only capture and output restrictions unchanged.'
 'FactoryResidentDeviceServer.cs'='r37 accepts immutable exact request input snapshot before round; publishes initial read-only DI status during Open, while ready requires full initialization. Release proof follows actual cleanup only.'
 'FactoryResidentDevices.cs'='r37 drains real startup progress before ready and continues exact delivered cursor; does not replay old watch status or open outputs from input status.'
 'FactoryResidentDeviceServerTests.cs'='r37 fake actual resident loop verifies immutable input/SaveResult rewrite binding, cleanup failures and initial HIGH/LOW/UNKNOWN publication before ready, exact UTC and no replay; no SDK.'
 'FactoryResidualStageTests.cs'='r37 explicitly marks pressure-context residual fixtures; selected-source and end threshold semantics verified offline.'
}
$reviewedNew=@('FactoryInputStopEvidence.cs','FactoryFailedRoundReleaseEvidence.cs','FactoryArtNegativeFeedbackTests.cs','FactorySelectedFeedbackTests.cs','FactoryFeedbackConfigurationTests.cs')
foreach($entry in $p.Files){
 $hash=(Get-FileHash -LiteralPath (Join-Path $worker $entry.Path) -Algorithm SHA256).Hash
 if($hash -ceq $entry.IntegratedSHA256){continue}
 if(-not $review.Contains([string]$entry.Path)){throw ('Unlisted r37 source change: '+$entry.Path)}
 if($hash -ceq $entry.FrozenUpstreamSHA256){throw ('Unexpected return to upstream: '+$entry.Path)}
 $entry.IntegratedSHA256=$hash;$entry.ExactUpstream=$false
 $patch=@($a.Patches|Where-Object Path -CEQ $entry.Path)
 if($patch.Count -eq 0){$a.Patches+=([pscustomobject]@{Path=$entry.Path;FrozenUpstreamSHA256=$entry.FrozenUpstreamSHA256;IntegratedSHA256=$hash;Reason=$review[$entry.Path]})}
 elseif($patch.Count -eq 1){$patch[0].IntegratedSHA256=$hash;$patch[0].Reason=$patch[0].Reason+' '+$review[$entry.Path]}
 else{throw 'Duplicate patch identity.'}
}
foreach($name in $reviewedNew){
 $hash=(Get-FileHash -LiteralPath (Join-Path $worker $name) -Algorithm SHA256).Hash
 $entry=@($a.NewFiles|Where-Object Path -CEQ $name)
 if($entry.Count -eq 0){$a.NewFiles+=([pscustomobject]@{Path=$name;SHA256=$hash})}
 elseif($entry.Count -eq 1){$entry[0].SHA256=$hash}else{throw 'Duplicate local file identity.'}
}
foreach($entry in $a.NewFiles){
 if((Get-FileHash -LiteralPath (Join-Path $worker $entry.Path) -Algorithm SHA256).Hash -cne $entry.SHA256){throw ('Unreviewed local file change: '+$entry.Path)}
}
$p.LocalAdapters=@('UnifiedPressureBridge.cs','UnifiedWorkerEntry.cs')+@($a.NewFiles|ForEach-Object Path)
$a.ReviewScope='20261009 r37 quick logic: selected DIV/VMON config-to-runtime binding, explicit pressure context, joint raw IMON 5 V/voltage no-growth 3 s guard, real initial DI delivery, immutable failed/input-stop request snapshot. Retains previous reviewed changes. Named offline fake/replay tests only; no SDK/product/device launch, full regression or physical acceptance.'
$a|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $adaptationPath -Encoding UTF8
$p|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $provenancePath -Encoding UTF8
Get-FileHash -LiteralPath $adaptationPath,$provenancePath -Algorithm SHA256|Format-List
