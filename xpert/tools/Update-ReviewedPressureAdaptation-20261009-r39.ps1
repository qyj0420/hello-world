# Narrow field-failure corrections; no frozen upstream source is rewritten.
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$worker=Join-Path $root 'modules-src\FactoryWorker'
$adaptation=Join-Path $worker 'v30-batch-local-adaptation-20261008.json'
$provenance=Join-Path $worker 'v30-batch-integration-provenance.json'
$a=Get-Content -LiteralPath $adaptation -Raw -Encoding UTF8|ConvertFrom-Json
$p=Get-Content -LiteralPath $provenance -Raw -Encoding UTF8|ConvertFrom-Json
$review=@(
 [pscustomobject]@{Path='FactoryEmergencyMonitor.cs';Before='A957411C488CE21BAE33A250CF1B5BFFA40571B855D1C3B293F8948A066B7908';After='6A518F68ABC23F78468655D2A66D26E2BC0FE0C74D4830B4164151F4264FBDC6';Reason='r39 same healthy DI task keeps its fresh physical display sample and original sample time across logical idle/active policy changes. Output ReadReleased still requires a genuinely new-mode sample; error, age, retry, stopped and rebuilt-task gaps remain fail-closed. Field handoff UNKNOWN negative reproduction and six managed fake suites retained.'},
 [pscustomobject]@{Path='FactoryResidentDevices.cs';Before='07435DC7562CAA838493EA3C4A0D1FDDB7E93F784457899416AFFF10A79C8F44';After='EABF9C0F6C54EFA9FEAA173B11ABA0F71A95BA69D7C826CEC397D2DFBD81177B';Reason='r39 nonzero/malformed round result is not itself process-exit evidence. Precisely report living resident incomplete outcome without granting cleanup/reuse permission. Actual exit/native fault/cleanup fault attribution and all original bound stop/release proof gates retained; 42 pure managed outcome checks.'},
 [pscustomobject]@{Path='FactoryRunner.cs';Before='92ECEB759F9E97367BD7F9EBEB4FF81AE46C08AFE038AAC39E3429F59283D444';After='B10E1E0225470CFD397B8EBDA56779DE9C721C38D7776E05EAAFFDBB1D4911D3';Reason='r39 flush the already-observed last real feedback after target hold and before ARM, not four seconds later at final cleanup. No new feedback read, pause, AO command, threshold or speed policy. Managed original negative and 30 targeted corrected checks retained.'}
)
# Verify the complete changed set first; never bless an unrelated edit.
foreach($e in $p.Files){
 $hash=(Get-FileHash -LiteralPath (Join-Path $worker $e.Path) -Algorithm SHA256).Hash
 $r=@($review|Where-Object Path -CEQ $e.Path)
 if($r.Count -eq 1){
  if($e.IntegratedSHA256 -cne $r[0].Before -and $e.IntegratedSHA256 -cne $r[0].After){throw ('Unexpected prior pin: '+$e.Path)}
  if($hash -cne $r[0].After){throw ('Reviewed r39 file changed: '+$e.Path)}
 }elseif($hash -cne $e.IntegratedSHA256){throw ('Unreviewed source difference: '+$e.Path)}
}
foreach($e in $a.NewFiles){
 $hash=(Get-FileHash -LiteralPath (Join-Path $worker $e.Path) -Algorithm SHA256).Hash
 if($e.Path -ceq 'FactoryEmergencyDisplayTests.cs'){
  if($hash -cne 'A696AC91F045319B528BA492731BA5A41CCC55EBD16EB7A3861598ED750EB383'){throw 'Changed reviewed display fake test.'}
 }elseif($hash -cne $e.SHA256){throw ('Unreviewed local source difference: '+$e.Path)}
}
foreach($r in $review){
 $e=@($p.Files|Where-Object Path -CEQ $r.Path);$patch=@($a.Patches|Where-Object Path -CEQ $r.Path)
 if($e.Count -ne 1 -or $patch.Count -ne 1){throw 'Missing exact reviewed source/patch identity.'}
 $e[0].IntegratedSHA256=$r.After;$e[0].ExactUpstream=$false
 $patch[0].IntegratedSHA256=$r.After
 if(-not $patch[0].Reason.Contains($r.Reason)){$patch[0].Reason+=' '+$r.Reason}
}
($a.NewFiles|Where-Object Path -CEQ 'FactoryEmergencyDisplayTests.cs').SHA256='A696AC91F045319B528BA492731BA5A41CCC55EBD16EB7A3861598ED750EB383'
$a.ReviewScope+=' r39 narrow field-failure corrections: manual0 host display/Stop separation is outside this worker closure; no logical DI policy-change synthetic UNKNOWN, precise unconfirmed-round diagnostics, and final real reached feedback published before ARM. No ramp speed-policy changes, no weakened output-generation or native cleanup proof, no product/SDK/device execution.'
$a|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $adaptation -Encoding UTF8
$p|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $provenance -Encoding UTF8
Get-FileHash -LiteralPath $adaptation,$provenance -Algorithm SHA256|Format-List
