# Mechanical inventory update for the explicitly reviewed afternoon patch set.
# This does not update the independent pin in Test-FactoryBatchSource.ps1.
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$worker=Join-Path $root 'modules-src/FactoryWorker'
$adaptationPath=Join-Path $worker 'v30-batch-local-adaptation-20261008.json'
$provenancePath=Join-Path $worker 'v30-batch-integration-provenance.json'
$a=Get-Content -LiteralPath $adaptationPath -Raw -Encoding UTF8|ConvertFrom-Json
$p=Get-Content -LiteralPath $provenancePath -Raw -Encoding UTF8|ConvertFrom-Json
$reasons=[ordered]@{
 'Program.cs'='Own-assembly child dispatch; nonce/PID/config-bound evidence only at pre-native cancellation guard; route manual free-running zero-input capture to the resident owner, preserving positive round sequencing and failures.'
 'FactoryEmergencyMonitor.cs'='Retain the sole DI reader and bounded fresh samples during idle/input-only rounds. LOW is read-only input, not lost connection or output permission. Active latch and 500 ms fault rule retained.'
 'FactoryModels.cs'='Previously reviewed serial-bound fine-ramp enable/threshold and finite-value validation; unchanged in afternoon follow-up.'
 'FactoryRunner.cs'='Previously reviewed bounded fine AO steps and cancellable cadence with feedback, timeouts, current/voltage checks and cleanup retained; unchanged in afternoon follow-up.'
 'NativeFactoryBackend.cs'='Integrate narrow zero-input resident adapter; reject all output entry points for input-only ownership; retain Pico and sole DI, AI cleanup only on actual configuration change or session close. Never clear failed ownership.'
 'FactoryNativeSessionPolicy.cs'='Validate resident manual zero-input queue separately from HV rounds. No optical/automatic/batch permit on this input request; existing host dispatches automatic-plan zero rows as ordinary zero requests.'
 'FactoryRangePhase.cs'='Separate independently unconfirmed oscillation-circuit configuration from missing native voltage or timebase. No inferred confirmation or relaxed eligibility.'
 'FactoryResidentDeviceServer.cs'='Publish zero-input stop evidence only after successful round cleanup, fresh input/session health and exact accepted request identity; revoke batch permission and never treat an exit code alone as release evidence.'
 'FactoryResidentDevices.cs'='Verify current owned process/session, request nonce, settings hash and explicit stop receipt before reporting normal zero-input cancellation. No clearing old blocked leases or automatic output recovery.'
 'FactoryResidentDeviceServerTests.cs'='Fake resident protocol tests for input-only zero requests and exact stop receipt binding, mismatches and unhealthy sessions; no SDK/device operation.'
 'FactoryZeroInputRunner.cs'='Use resident input-only generation-bound operations when supported, preserving DI and handles. Legacy isolated fallback retains its own fresh DI publication; no AO/DO or optical output.'
 'FactoryZeroInputTests.cs'='Pure fake assertions for retained emergency input and input-only stopping/ownership; no real hardware.'
 'Imported/HardwareCapture.cs'='Optional retained input DI task for isolated input-only fallback; Stop does not clear connection identity or fabricate samples; failure retains ownership.'
}
$new=@('FactoryEmergencyReadPolicy.cs','FactoryEmergencyTimingContractTests.cs','FactoryBufferEmergencyCurrentTests.cs','UnifiedFinePressureRamp.cs','UnifiedWorkerLaunchEvidence.cs','NativeFactoryBackend.ZeroInput.cs','FactoryResidentZeroInputTests.cs','FactoryInputStopEvidence.cs')
foreach($f in $p.Files){
 $h=(Get-FileHash -LiteralPath (Join-Path $worker $f.Path) -Algorithm SHA256).Hash
 if($reasons.Contains($f.Path)){
  if($h -ceq $f.FrozenUpstreamSHA256){throw ('Reviewed patch unexpectedly equals upstream: '+$f.Path)}
  $f.IntegratedSHA256=$h;$f.ExactUpstream=$false
 }elseif($h -cne $f.IntegratedSHA256 -or -not $f.ExactUpstream){throw ('Unlisted source change: '+$f.Path)}
}
$a.Patches=@(foreach($name in $reasons.Keys){
 $f=@($p.Files|Where-Object Path -CEQ $name)
 if($f.Count -ne 1){throw ('Missing unique upstream identity: '+$name)}
 [ordered]@{Path=$name;FrozenUpstreamSHA256=$f[0].FrozenUpstreamSHA256;IntegratedSHA256=$f[0].IntegratedSHA256;Reason=$reasons[$name]}
})
$a.NewFiles=@(foreach($name in $new){[ordered]@{Path=$name;SHA256=(Get-FileHash -LiteralPath (Join-Path $worker $name) -Algorithm SHA256).Hash}})
$a.ReviewScope='20261008 afternoon: thirteen explicit reviewed upstream differences plus eight listed local files. Pure fake resident zero-input, round-owned completed input stop, pre-native cancellation proof and DI epoch tests only; no hardware operation or physical acceptance. Source and runtime verification must both pass for this exact candidate.'
$p.LocalAdapters=@('UnifiedPressureBridge.cs','UnifiedWorkerEntry.cs')+$new
$a|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $adaptationPath -Encoding UTF8
$p|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $provenancePath -Encoding UTF8
Get-FileHash -LiteralPath $adaptationPath,$provenancePath -Algorithm SHA256|Format-List
