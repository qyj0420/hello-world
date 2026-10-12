# Exact reviewed STOP-edge adaptation. Never absorb arbitrary source changes.
param()
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$worker=Join-Path $root 'modules-src\FactoryWorker'
$adaptationPath=Join-Path $worker 'v30-batch-local-adaptation-20261008.json'
$provenancePath=Join-Path $worker 'v30-batch-integration-provenance.json'
if((Get-FileHash -LiteralPath $adaptationPath).Hash -cne 'EE3F4BDF20C17F415D2480AD70B00A8EA07726E9B42C735A054B267528CE7547' -or
 (Get-FileHash -LiteralPath $provenancePath).Hash -cne '68198276CC11D7DCF2A2472C53E00C01A322BA0CE11615903C63D8723AD5F1BE'){throw 'Reviewed pre-STOP metadata changed; do not repin it.'}
$a=Get-Content -LiteralPath $adaptationPath -Raw -Encoding UTF8|ConvertFrom-Json
$p=Get-Content -LiteralPath $provenancePath -Raw -Encoding UTF8|ConvertFrom-Json
$review=@(
 [pscustomobject]@{Path='FactoryRunner.cs';Before='5A3611AA62780562B8468497BA341E41F3FBE022092E2566F95CA31BB9F177D3';After='5C34A0A8D28990D9E2978EA7DC7BA16792F0FC3E3F7F7F415025AFC461DCD61F';Reason='After actual runner cleanup, detect late cancellation before/after terminal reporting using the existing partial-result FactoryRunException contract. Retain terminal publication errors as real failures, not a generic successful Stop; no native cleanup, ramp, capture or output logic changed.'},
 [pscustomobject]@{Path='Program.cs';Before='18853FB3045F583069600119DA66B47AC5E74233495D6B33895D6F1DFEB96EBB';After='F5261A7963F7F95EA87E636DE300E551ADC069E37AE70592009E2BBBA846DE8A';Reason='Synchronously observe owned cancel.flag at post-run and post-RECORD edges in addition to the existing watcher. Preserve actual partial raw capture, incomplete status and original save/journal errors; healthy callback still requires pure cancellation plus actual complete EndRound facts and healthy held session.'},
 [pscustomobject]@{Path='FactoryResidentDeviceServer.cs';Before='266D91BA2D94EC0B2DEAEF54CA75F10029AC5351F654718A113BA007B7A2A0FB';After='2C600562865F99EDB2D5B134F2C26E331DA06557A2CE46E282510D2ECF3368BA';Reason='A successful Worker return with a nonnull archived result may participate in a later exact operator Stop receipt, including the edge after batch-cancel snapshot. Both paths still require all five actual EndRound facts, healthy original session, exact accepted immutable request/nonce/hash/PIDs/ready and new round STOP markers with no terminal fault. Failures never become healthy; revoke batch and continue read-only idle, never auto-output/retry.'},
 [pscustomobject]@{Path='FactoryResidentDeviceServerTests.cs';Before='D6B1E332F5D438577F4978E5D76A3DA2A6ACE7656327F7405DF2383061021D68';After='1F82FE51D82E4D83020FB74EFABD5C118008EA365F579F2AB1F11721274D7713';Reason='Thirty-one managed fake production-loop checks cover returned-zero late Stop before/after snapshot, missing archive, each of five missing EndRound facts, wrong nonce, stale Stop ID, failed terminal drain and unusable session. Existing receipt failure/replay checks retained; no Program.Worker or native/SDK/product entry executed.'},
 [pscustomobject]@{Path='FactoryHealthyRoundStopEvidence.cs';Before='7B0EEFF53F45B1597DB0AE9035F400CD3A8A074D94D394A81B4F5D17FD5661C5';After='C408299153745680388DD7DC6C061BD8099FBF39B7D3DEA70AC5F2118FC992F3';Reason='Only a post-run canceled token is wrapped together with that exact partial result; the existing bare-OCE rejection, exact new Stop binding and all five release facts remain unchanged.'},
 [pscustomobject]@{Path='FactoryProgressJournal.cs';Before='24B8D558D813F6671C79DB6C62409B2A65879E9033E5F21B41115ACBF14BE98A';After='0A6A8EF15CF61EE3BA34E9364CF233F3692D2E416952189082F3B2666A35ECA5';Reason='Only STEP_COMPLETE_PASSED, STEP_COMPLETE_UNVERIFIED and RECORD use an independent bounded drain token after cleanup/save. Enabling/ARM barriers remain promptly operator-cancelable. Original five-second deadline, queue cap, actual write/flush/fallback and final-dispose failure propagation remain.'}
)
$proofs=@(
 [pscustomobject]@{Path='build/qa-late-stop-journal-20261011-r55-03/verification-progress-journal.json';SHA256='E2A480FE8F9C7A37B688942D9E944E4D196496868F56A393C4F6BE990DF959E3'},
 [pscustomobject]@{Path='build/qa-late-stop-receipt-20261011-r55-01/verification-failed-release-focused.json';SHA256='634180E7957433A4130B808A436624935730860E3DFD128CD68EDB0CF7887080'}
)
foreach($receipt in $proofs){
 $path=Join-Path $root $receipt.Path
 if((Get-FileHash -LiteralPath $path).Hash -cne $receipt.SHA256){throw 'Focused receipt changed.'}
 $proof=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
 if($proof.HardwareAccessed -ne $false -or $proof.SDKCalled -ne $false -or $proof.PhysicalAcceptance -ne $false){throw 'Focused proof boundary invalid.'}
 if($receipt.Path.Contains('journal') -and ($proof.TestPassed -ne $true -or $proof.InputsUnchanged -ne $true)){throw 'Journal proof is not PASS with unchanged inputs.'}
 if($receipt.Path.Contains('receipt') -and (@($proof.Tests).Count -ne 4 -or @($proof.Tests|Where-Object ExitCode -NE 0).Count -ne 0)){throw 'Receipt proof not complete.'}
 foreach($r in $review){$matches=@($proof.SourceInputs|Where-Object {$_.Path -ieq (Join-Path $worker $r.Path)});if($matches.Count -ne 1 -or $matches[0].SHA256 -cne $r.After){throw ('Proof does not bind exact reviewed source: '+$r.Path)}}
}
foreach($entry in $p.Files){
 $hash=(Get-FileHash -LiteralPath (Join-Path $worker $entry.Path)).Hash
 $r=@($review|Where-Object Path -CEQ $entry.Path)
 if($r.Count -eq 1){if($entry.IntegratedSHA256 -cne $r[0].Before -or $hash -cne $r[0].After){throw ('Unexpected reviewed delta: '+$entry.Path)}}
 elseif($hash -cne $entry.IntegratedSHA256){throw ('Unreviewed source difference: '+$entry.Path)}
}
foreach($entry in @($a.NewFiles)+@($a.ReviewedEntryAdapters)){
 $hash=(Get-FileHash -LiteralPath (Join-Path $worker $entry.Path)).Hash;$r=@($review|Where-Object Path -CEQ $entry.Path)
 if($r.Count -eq 1){if($entry.SHA256 -cne $r[0].Before -or $hash -cne $r[0].After){throw ('Unexpected adapter delta: '+$entry.Path)}}
 elseif($hash -cne $entry.SHA256){throw ('Unreviewed adapter difference: '+$entry.Path)}
}
foreach($r in $review){
 $entry=@($p.Files|Where-Object Path -CEQ $r.Path)
 if($entry.Count -eq 1){
  $patch=@($a.Patches|Where-Object Path -CEQ $r.Path);if($patch.Count -ne 1){throw 'Missing reviewed patch.'}
  $patch[0].IntegratedSHA256=$r.After;$patch[0].Reason+=' '+$r.Reason;$entry[0].IntegratedSHA256=$r.After;$entry[0].ExactUpstream=$false
 }else{
  $adapter=@($a.NewFiles|Where-Object Path -CEQ $r.Path);if($adapter.Count -ne 1){throw 'Missing reviewed local helper.'};$adapter[0].SHA256=$r.After
 }
}
$scope='20261011 authorized late operator Stop correction: post-cleanup journal drain remains independently bounded, completion-edge cancellation retains original partial capture and exact cleanup contract, and a fresh exact STOP after successful Worker return can retain only the already-healthy original session with all five EndRound facts. Save/journal/native cleanup failures, stale/wrong identities and arbitrary OCE remain fail-closed. Batch is revoked; no automatic output, retry or completed-data fabrication. No SDK/device/product/worker entry execution.'
$a.ReviewScope+=' '+$scope
$a|Add-Member -NotePropertyName LateRoundStopReview20261011 -NotePropertyValue ([pscustomobject]@{Scope=$scope;Evidence=$proofs;Checks='60 journal/late-runner +30 before-ARM trend +127 failed-release +49 input-stop +33 existing healthy-stop +31 new late-stop managed checks; exact source inventory unchanged at each proof.';HardwareAccessed=$false;PhysicalAcceptance=$false;FinalExeIntegration=$false;Uncovered='Real hardware STOP/HV OFF timing, ART/Pico release, field callbacks, final protected EXE, installation and whole-suite physical acceptance. Earlier failed test fixtures retained.'})
# Mechanical serialization of only the six exact reviewed entries above.
$a|ConvertTo-Json -Depth 15|Set-Content -LiteralPath $adaptationPath -Encoding UTF8
$p|ConvertTo-Json -Depth 15|Set-Content -LiteralPath $provenancePath -Encoding UTF8
Get-FileHash -LiteralPath $adaptationPath,$provenancePath -Algorithm SHA256|ForEach-Object{Write-Output ($_.Path+'='+$_.Hash)}
