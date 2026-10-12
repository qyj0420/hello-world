# Explicit reviewed local differences only. No frozen upstream is changed.
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
 'FactoryAiTrace.cs'='r38 pressure forbids before/after residual sampling including diagnostic shutdown tail; preserve real writes/results and calibration trace.'
 'FactoryBatchAuthorization.cs'='r38 pressure acquisition-only receipt requires matching native-ended evidence; historical residual-safe fallback cannot manufacture current acceptance.'
 'FactoryFaultResidual.cs'='r38 pressure faults retain unknown residual and original error; no new AI observation after off, no output recovery.'
 'FactoryManualEngine.cs'='r38 independent pressure engine obeys no before/after residual rule; actual active ramp feedback, DI and time limit retained.'
 'FactoryModels.cs'='r38 derives no-residual pressure execution policy; XML cannot re-enable old residual tests; explicit precision opt-in separates +/-5% precision, selected-source raw protection and AO ceiling; no synthetic ResidualSafe/Completed result.'
 'FactoryEmergencyMonitor.cs'='r38 sole serial DI reader continues actual physical HIGH/LOW display sampling after an active LOW; permanent output-fault latch and native errors remain separate and fail-closed.'
 'FactoryResidentDeviceServer.cs'='r38 serial managed journal publisher drains queued input snapshots during slow device opening; no additional DI/SDK reader, output restart, or failed-session keepalive.'
 'FactoryResidentDeviceServerTests.cs'='r38 controlled managed fake open/journal test verifies continuously published DI during blocked initialization and serialized publication lifecycle; no SDK or device execution.'
 'FactorySessionFault.cs'='r38 explicitly classifies new fault-latched LOW stage as EmergencyInputLow, preserving original fault attribution and cleanup policy.'
 'FactoryNativeSessionTests.cs'='r38 managed calibration confirmation fixture explicitly selects zero target, matching calibration execution purpose; native pre-SDK guards and identity checks retained.'
 'FactoryRunner.cs'='r38 nonzero pressure skips startup/end/final AI4 residual stages, keeps active selected-source ramp/hold, DI, manual cancellation and actual shutdown results; opted-in precision deviation freezes AO and rechecks three genuinely new feedback batches before an explicit warning, without weakening raw protection.'
 'FactoryTargetHold.cs'='r38 explicit pressure precision opt-in preserves a three-new-read out-of-band warning without additional AO or false precision-pass; raw protection and old APIs remain unchanged.'
 'FactoryStartConfirmation.cs'='r38 pressure start confirmation describes independent site handling without promising software residual measurement.'
 'NativeFactoryBackend.cs'='r38 manual zero establishes display-only sole DI without output permit; pressure AI starts only in actual ramp and stops after shutdown without clearing failed ownership.'
 'NativeFactoryBackend.FinalAi4.cs'='r38 pressure no-residual execution cannot start a post-run diagnostic AI4 task.'
 'Program.cs'='r38 validates exact matched acquisition-only pressure receipt under current no-residual policy; no synthetic complete or physical-discharge assertion.'
}
foreach($entry in $p.Files){
 $hash=(Get-FileHash -LiteralPath (Join-Path $worker $entry.Path) -Algorithm SHA256).Hash
 if($hash -ceq $entry.IntegratedSHA256){continue}
 if(-not $review.Contains([string]$entry.Path)){throw ('Unlisted r38 source change: '+$entry.Path)}
 if($hash -ceq $entry.FrozenUpstreamSHA256){throw ('Unexpected upstream return: '+$entry.Path)}
 $entry.IntegratedSHA256=$hash;$entry.ExactUpstream=$false
 $patch=@($a.Patches|Where-Object Path -CEQ $entry.Path)
 if($patch.Count -eq 0){$a.Patches+=([pscustomobject]@{Path=$entry.Path;FrozenUpstreamSHA256=$entry.FrozenUpstreamSHA256;IntegratedSHA256=$hash;Reason=$review[$entry.Path]})}
 elseif($patch.Count -eq 1){$patch[0].IntegratedSHA256=$hash;$patch[0].Reason=$patch[0].Reason+' '+$review[$entry.Path]}
 else{throw 'Duplicate patch identity.'}
}
foreach($name in @('FactoryResidentZeroInputTests.cs','FactorySelectedFeedbackTests.cs','FactoryFeedbackConfigurationTests.cs','FactoryNoResidualPressureTests.cs','FactoryEmergencyTimingContractTests.cs','FactoryEmergencySnapshotPublisher.cs','FactoryEmergencyDisplayTests.cs','FactoryPressurePrecisionTests.cs')){
 $hash=(Get-FileHash -LiteralPath (Join-Path $worker $name) -Algorithm SHA256).Hash
 $entry=@($a.NewFiles|Where-Object Path -CEQ $name)
 if($entry.Count -eq 0){$a.NewFiles+=([pscustomobject]@{Path=$name;SHA256=$hash})}
 elseif($entry.Count -eq 1){$entry[0].SHA256=$hash}else{throw 'Duplicate local file identity.'}
}
foreach($entry in $a.NewFiles){if((Get-FileHash -LiteralPath (Join-Path $worker $entry.Path)).Hash -cne $entry.SHA256){throw ('Unreviewed local file change: '+$entry.Path)}}
$p.LocalAdapters=@('UnifiedPressureBridge.cs','UnifiedWorkerEntry.cs')+@($a.NewFiles|ForEach-Object Path)
$a.ReviewScope='20261009 r38 latest explicit pressure rule supersedes previous startup/end residual measurements and 4% acceptance. Explicit new pressure opt-in separates +/-5% precision and three fresh rechecks from raw selected-source protection; old APIs unchanged. Sole serial DI reader plus managed snapshot/journal publication continues physical display without releasing the emergency fault latch. IMON joint 3-second condition, DI/manual Stop/limits/time/cleanup retained. Manual zero gets display-only DI. Named managed fake tests, no SDK/product/device execution or physical acceptance.'
$a|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $adaptationPath -Encoding UTF8
$p|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $provenancePath -Encoding UTF8
Get-FileHash -LiteralPath $adaptationPath,$provenancePath -Algorithm SHA256|Format-List
