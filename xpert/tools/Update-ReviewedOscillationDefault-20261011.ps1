# Exact reviewed feature-default adaptation, never a generic repinning tool.
param()
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$worker=Join-Path $root 'modules-src\FactoryWorker'
$adaptationPath=Join-Path $worker 'v30-batch-local-adaptation-20261008.json'
$provenancePath=Join-Path $worker 'v30-batch-integration-provenance.json'
$a=Get-Content -LiteralPath $adaptationPath -Raw -Encoding UTF8|ConvertFrom-Json
$p=Get-Content -LiteralPath $provenancePath -Raw -Encoding UTF8|ConvertFrom-Json
$review=@(
 [pscustomobject]@{Path='FactoryAnalysis.cs';Before='A037CFD3938BE78F3CDA490DC8CC98B1E779FC82D426BC14FE6F70EC44C7F5D9';After='03864A480DB3E33BC224973B9C15B6A32FA04A11C211C3B269BACDA9003CAB63';Reason='Latest user authorization removes the historical diagnostic-enable checkbox: always evaluate captured oscillation metrics, retaining all raw waveform, SNR, reliable crossing, fit and parameter validity checks. The old false metadata remains unchanged and is never evidence of physical wiring, safe discharge or an HV permit.'},
 [pscustomobject]@{Path='FactoryAnalysisTests.cs';Before='04F1B65D624A33127650468F74593F366466DD3D63D34EF4813A20C238F0779A';After='6827936653E4F67BFF868BE4A76687AEE34E028943AD2EBF3D4475E898041A40';Reason='Update only the obsolete feature-false expectation: measured valid damped oscillation computes metrics without rewriting the legacy metadata or site attestations. All independent ratios, clipping, invalid data, noise and fit regression assertions retained.'},
 [pscustomobject]@{Path='FactoryMainForm.IntegratedSmoke.cs';Before='425148A049EAD10CC66312BC15397482B1FFCC19E2FA887601F97AA4FF7C48A0';After='3FCC22B397B6455318D6AEC6763673E843B86E3A10F58190ECF034D0B5552E66';Reason='Synthetic-only queue-stop fixture now uses an actual flat raw voltage instead of the removed feature checkbox. This source compiles in the focused closure; this visual smoke itself was not executed in this targeted run.'},
 [pscustomobject]@{Path='FactoryPressureSequence.cs';Before='280C64430AB92E4629E7DE762D7F48EEAF98C26E19588E80D7623F864416E98C';After='47340767A1023C1CBC04449B4D9F946D59889C4E0B3D9874B028EAF6FE305878';Reason='Remove only obsolete checkbox-specific replacement of a real range-window failure. Preserve the original measured invalid-window reason, raw record, queue cancellation and authorization checks.'},
 [pscustomobject]@{Path='FactoryPressureSequenceTests.cs';Before='CD271145A495145659478649762ACF73EEE591637EEC8201D29D5EFDB4786C88';After='CA76E9FFB202CA32154A4828773D29D92F99E4B789F103528AB9FCCCAD64F498';Reason='Legacy feature-false simulation now proves valid windows can continue without fabricating a confirmation. A genuine flat-voltage fixture still stops after the captured round, retains honest result/residual flags and reports the measured SNR failure.'},
 [pscustomobject]@{Path='FactoryPressureStartPrompt.cs';Before='CD2246CDA39D301EA0FA93F5371EF5A7C92A5F8770946D1C6E20448F268102DA';After='B80FF541548797DACD06807FA4C92B53110BABCC15667D31CA8D2AA4E9CAF2CD';Reason='Remove obsolete diagnostic-enable preference rejection. Retain all-DC capture placement preflight and the independent physical safety/operator start confirmation text and checks.'},
 [pscustomobject]@{Path='FactoryR29Tests.cs';Before='6FC50BB19D94DB8125FCCC8875052FDA6492A5380FF7117F0082912F7C1F19F9';After='8DA8CB68BAEA7CD8456C911211CD5E3AB9DB48D98D72DE3BBD401AED6B30BCE6';Reason='Update only obsolete false-feature preflight expectation; all-DC repeat rejection remains. This source compiles in the focused closure; the full FactoryR29 UI suite was not executed in this targeted run.'},
 [pscustomobject]@{Path='FactoryRangePhase.cs';Before='F25279F734E5D675C87414AC6FA28C5CCB8C155A430B141A554125AB7C6896BA';After='56F9F04D79941FE230CB6A5417EE66A6A625C964AB663EE1C18D2CFBD0565E15';Reason='Remove only historical diagnostic-enable false early return. Actual timebase, full scale, raw finite/rail, SNR, first reliable bipolar crossing and consistent-cycle checks remain unchanged; no inferred wiring/HV/discharge attestation.'}
)
$evidence=@('build\qa-oscillation-default-20261011-r55-02\proof.json','build\qa-oscillation-range-ui-20261011-r55-01\proof.json')
$receipts=@()
foreach($relative in $evidence){
 $path=Join-Path $root $relative;$proof=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
 if($proof.Status -cne 'PASS' -or $proof.SourceUnchanged -ne $true -or $proof.HardwareAccessed -ne $false -or $proof.PhysicalAcceptance -ne $false){throw 'Focused evidence boundary is not PASS.'}
 foreach($r in $review){$matches=@($proof.Inputs|Where-Object {$_.Path -ieq (Join-Path $worker $r.Path)});if($matches.Count -ne 1 -or $matches[0].SHA256 -cne $r.After){throw ('Focused proof does not bind reviewed input: '+$r.Path)}}
 $receipts+=[pscustomobject]@{Path=$relative.Replace('\','/');SHA256=(Get-FileHash -LiteralPath $path).Hash}
}
# Check every existing source, including adapters. Never absorb other work.
foreach($entry in $p.Files){
 $hash=(Get-FileHash -LiteralPath (Join-Path $worker $entry.Path)).Hash
 $r=@($review|Where-Object Path -CEQ $entry.Path)
 if($r.Count -eq 1){if($entry.IntegratedSHA256 -cne $r[0].Before -and $entry.IntegratedSHA256 -cne $r[0].After){throw ('Unexpected previous identity: '+$entry.Path)};if($hash -cne $r[0].After){throw ('Reviewed source changed: '+$entry.Path)}}
 elseif($hash -cne $entry.IntegratedSHA256){throw ('Unreviewed source difference: '+$entry.Path)}
}
foreach($entry in @($a.NewFiles)+@($a.ReviewedEntryAdapters)){
 if((Get-FileHash -LiteralPath (Join-Path $worker $entry.Path)).Hash -cne $entry.SHA256){throw ('Unreviewed local adapter difference: '+$entry.Path)}
}
foreach($r in $review){
 $entry=@($p.Files|Where-Object Path -CEQ $r.Path);if($entry.Count -ne 1){throw 'Missing reviewed source.'}
 $patch=@($a.Patches|Where-Object Path -CEQ $r.Path)
 if($patch.Count -eq 0){$a.Patches+=@([pscustomobject]@{Path=$r.Path;FrozenUpstreamSHA256=$entry[0].FrozenUpstreamSHA256;IntegratedSHA256=$r.After;Reason=$r.Reason})}
 elseif($patch.Count -eq 1){$patch[0].IntegratedSHA256=$r.After;if(!$patch[0].Reason.Contains($r.Reason)){$patch[0].Reason+=' '+$r.Reason}}
 else{throw 'Duplicate reviewed source patch.'}
 $entry[0].IntegratedSHA256=$r.After;$entry[0].ExactUpstream=$false
}
$scope='20261011 latest explicit user authorization removes the customer oscillation feature checkbox and enables waveform-derived diagnostics by default, independent of historical false. This is not a site attestation or HV authorization. All raw finite/rail/timebase/SNR/reliable-zero/fixed-cycle and physical safety checks remain. Genuine invalid-window UI state is separate from actual overflow, never adopted as valid or auto-retried. No hardware/product/worker/SDK execution.'
if(!$a.ReviewScope.Contains($scope)){$a.ReviewScope+=' '+$scope}
$reviewReceipt=[pscustomobject]@{Scope=$scope;Evidence=$receipts;Checks='113 focused factory assertions (12 default/window,10 phase boundary,57 metrics,34 sequence) and 3381 existing/current managed UI/policy/runtime assertions; runtime also invokes 116 existing batch assertions.';HardwareAccessed=$false;PhysicalAcceptance=$false;FinalExeIntegration=$false;Uncovered='Actual SDK/worker/device, field ADC and voltage waveforms, physical wiring/attestations, final protected EXE. Full standalone R29/visual smokes compiled but not executed.'}
$a|Add-Member -NotePropertyName OscillationDefaultReview20261011 -NotePropertyValue $reviewReceipt -Force
# Mechanical serialization of the exact reviewed fields above. Upstream pins
# and all unrelated adaptation entries are retained.
$a|ConvertTo-Json -Depth 15|Set-Content -LiteralPath $adaptationPath -Encoding UTF8
$p|ConvertTo-Json -Depth 15|Set-Content -LiteralPath $provenancePath -Encoding UTF8
Get-FileHash -LiteralPath $adaptationPath,$provenancePath -Algorithm SHA256|ForEach-Object{Write-Output ($_.Path+'='+$_.Hash)}
