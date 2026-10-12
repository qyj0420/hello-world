param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Use a fresh isolated test directory under project/build.'}
for($walk=$output;$walk;$walk=[IO.Path]::GetDirectoryName($walk)){
 if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked test paths are not allowed.'}
}
$null=New-Item -ItemType Directory -Path (Join-Path $output 'qa')
$gate=Join-Path $PSScriptRoot 'AlgorithmConfidentialityGate.ps1'
$gateHash=(Get-FileHash -LiteralPath $gate).Hash
. $gate
$checks=0
function Check([bool]$Passed,[string]$Message){$script:checks++;if(-not $Passed){throw $Message}}
function Reject([scriptblock]$Action,[string]$Message){$failed=$false;try{& $Action|Out-Null}catch{$failed=$true};Check $failed $Message}
function Clone($Value){return ($Value|ConvertTo-Json -Depth 15|ConvertFrom-Json)}
$proofFile=Join-Path $output 'qa/synthetic-only-review.json'
$files=@();foreach($kind in @('ScopeReport','StaticInspection','Compatibility')){
 $file=Join-Path $output ('qa/'+$kind+'.txt')
 [IO.File]::WriteAllText($file,'SYNTHETIC GATE FIXTURE ONLY. NOT A REAL REVIEW OR AUTHORIZATION.')
 $files+=([pscustomobject]@{Kind=$kind;Path=$kind+'.txt';SHA256=(Get-FileHash -LiteralPath $file).Hash})
}
$runtime=@([pscustomobject]@{Path='Xiongxin.OWTS.Desktop.exe';SHA256=('1'*64)},[pscustomobject]@{Path='modules/analysis.dll';SHA256=('2'*64)},[pscustomobject]@{Path='model-payload/model.hxm';SHA256=('3'*64)})
$valid=[pscustomobject]@{Schema='HVX.AlgorithmConfidentialityReview/1';Status='PASS';BuildRoot=$output;HardwareAccessed=$false;DirectReadableProprietaryImplementationFound=$false;UnresolvedBlockingFindings=$false;AbsoluteProtectionClaimed=$false;FullProprietaryScopeReviewed=$true;RuntimeCacheCovered=$true;ModelLoadingCovered=$true;CompatibilityPassed=$true;Reviewer='SYNTHETIC TEST';Limitations='Fixture only; no executable or algorithm was examined.';Runtime=$runtime;Evidence=$files}
function Put($Value){$Value|ConvertTo-Json -Depth 15|Set-Content -LiteralPath $proofFile -Encoding UTF8}
Put $valid
$accepted=Assert-AlgorithmConfidentialityReview $output $proofFile $runtime
Check ($accepted.SHA256 -ceq (Get-FileHash -LiteralPath $proofFile).Hash) 'Exact review bytes are recorded'
Check ($accepted.Evidence.Count -eq 3) 'All three independent evidence kinds required'
Reject {Assert-AlgorithmConfidentialityReview $output '' $runtime} 'Missing review rejects publishing'
Reject {Assert-AlgorithmConfidentialityReview $output (Join-Path $output 'qa/missing.json') $runtime} 'Missing file rejects publishing'
Reject {Assert-AlgorithmConfidentialityReview $output (Join-Path $project 'handoff.md') $runtime} 'Evidence outside this build/qa rejected'
foreach($field in @('HardwareAccessed','DirectReadableProprietaryImplementationFound','UnresolvedBlockingFindings','AbsoluteProtectionClaimed')){
 $bad=Clone $valid;$bad.$field=$true;Put $bad;Reject {Assert-AlgorithmConfidentialityReview $output $proofFile $runtime} ('Blocking condition '+$field)
 $bad.$field='false';Put $bad;Reject {Assert-AlgorithmConfidentialityReview $output $proofFile $runtime} ('String Boolean '+$field)
}
foreach($field in @('FullProprietaryScopeReviewed','RuntimeCacheCovered','ModelLoadingCovered','CompatibilityPassed')){
 $bad=Clone $valid;$bad.$field=$false;Put $bad;Reject {Assert-AlgorithmConfidentialityReview $output $proofFile $runtime} ('Incomplete coverage '+$field)
 $bad.$field='true';Put $bad;Reject {Assert-AlgorithmConfidentialityReview $output $proofFile $runtime} ('String coverage '+$field)
}
foreach($case in @(@('Schema','Wrong'),@('Status','FAIL'),@('BuildRoot',(Join-Path $project 'build/other')), @('Reviewer',''), @('Limitations',''))){
 $bad=Clone $valid;$bad.($case[0])=$case[1];Put $bad;Reject {Assert-AlgorithmConfidentialityReview $output $proofFile $runtime} ('Invalid '+$case[0])
}
$bad=Clone $valid;$bad.Runtime=@($runtime[0],$runtime[1]);Put $bad;Reject {Assert-AlgorithmConfidentialityReview $output $proofFile $runtime} 'Omitted model or runtime file rejected'
$bad=Clone $valid;$bad.Runtime[0].SHA256='4'*64;Put $bad;Reject {Assert-AlgorithmConfidentialityReview $output $proofFile $runtime} 'Changed artifact bytes rejected'
$bad=Clone $valid;$bad.Runtime+=@($bad.Runtime[0]);Put $bad;Reject {Assert-AlgorithmConfidentialityReview $output $proofFile $runtime} 'Duplicate runtime entry rejected'
foreach($path in @('../outside','C:/outside','modules/a:ads','modules/../outside','modules//bad')){
 $bad=Clone $valid;$bad.Runtime[0].Path=$path;Put $bad;Reject {Assert-AlgorithmConfidentialityReview $output $proofFile $runtime} ('Malformed runtime path '+$path)
}
foreach($kind in @('ScopeReport','StaticInspection','Compatibility')){
 $bad=Clone $valid;$bad.Evidence=@($bad.Evidence|Where-Object Kind -cne $kind);Put $bad;Reject {Assert-AlgorithmConfidentialityReview $output $proofFile $runtime} ('Missing evidence category '+$kind)
}
$bad=Clone $valid;$bad.Evidence[1].Path=$bad.Evidence[0].Path;Put $bad;Reject {Assert-AlgorithmConfidentialityReview $output $proofFile $runtime} 'Reusing one file cannot count as two evidence categories'
$bad=Clone $valid;$bad.Evidence[0].SHA256='0'*64;Put $bad;Reject {Assert-AlgorithmConfidentialityReview $output $proofFile $runtime} 'Evidence changed after review rejected'
$bad=Clone $valid;$bad.Evidence[0].Path='../../../../handoff.md';Put $bad;Reject {Assert-AlgorithmConfidentialityReview $output $proofFile $runtime} 'Evidence escaping qa rejected'
Put $valid
[IO.File]::AppendAllText((Join-Path $output 'qa/StaticInspection.txt'),'CHANGED')
Reject {Assert-AlgorithmConfidentialityReview $output $proofFile $runtime} 'Actual inspection bytes drift rejected'
foreach($name in @('Publish-R27Validation.ps1','Publish-V30Validation.ps1')){
 $path=Join-Path $PSScriptRoot $name;$tokens=$null;$errors=$null
 $ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
 Check ($errors.Count -eq 0) ($name+' parses')
 $text=Get-Content -LiteralPath $path -Raw
 $first=$text.IndexOf('$confidentiality=Assert-AlgorithmConfidentialityReview')
 Check ($first -gt 0 -and $first -lt $text.IndexOf('$candidate=Join-Path')) ($name+' reviews before creating delivery candidate')
 Check ($text.Contains('$confidentialityAfter=Assert-AlgorithmConfidentialityReview')) ($name+' checks unchanged evidence again after copy')
 Check ($text.Contains("Kind='Confidentiality';SHA256=")) ($name+' records review fingerprint in release receipt')
 Check ($text.Contains('CONFIDENTIALITY_NOT_APPROVED_BY_THIS_COMMAND')) ($name+' does not label functional validation as confidentiality approval')
}
Check ((Get-FileHash -LiteralPath $gate).Hash -ceq $gateHash) 'Gate implementation unchanged during tests'
# Leave the fake PASS deliberately invalid, so it cannot be mistaken for a real audit.
$valid.Status='SYNTHETIC_TEST_NOT_APPROVED';Put $valid
[ordered]@{Schema='HVX.ConfidentialityGateTests/1';Status='PASS';Checks=$checks;GateSHA256=$gateHash;HardwareAccessed=$false;ProductExecuted=$false;InstallerBuilt=$false;ActualConfidentialityApproved=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
Write-Output ('CONFIDENTIALITY_GATE_TESTS_PASS='+$checks+'; NO_ACTUAL_REVIEW_APPROVED')
