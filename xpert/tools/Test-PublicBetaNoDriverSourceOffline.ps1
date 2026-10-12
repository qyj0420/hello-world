[CmdletBinding()]
param([string]$OutputRoot='',[string]$BuildRoot='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$script=Join-Path $PSScriptRoot 'Export-PublicBetaNoDriverSource.ps1'
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($script,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Source exporter does not parse.'}
foreach($name in @('SafeRelative','IsSourceEligible','AssertCompiledSourceRows','AssertConfidentialityRelease')){
    $nodes=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -ceq $name)
    if($nodes.Count -ne 1){throw 'Ambiguous source helper.'};. ([ScriptBlock]::Create($nodes[0].Extent.Text))
}
$checks=1
foreach($path in @('build/host.cs','modules-src/X/build/leak.cs','src/.env','src/client.pfx','src/password-material.cs','src/maintenance-material.cs',
  'dependencies/Task41/setup.exe','tools/installer-toolchain/ISCC.exe','assets/device-asset-catalog.v1.json','assets/OEMpic/customer.png','src/user-config.json',
  'modules-src/X/data/sample.loc','modules-src/X/evidence/source.cs','deliverables/a.zip','modules-src/X/bin/a.dll','dependencies/Algorithms/model.pdm',
  'dependencies/HardwareConnector/runtime/driver.dll','docs/evidence/trace.txt','assets/branding/KEHUI/kehui-original.png',
  'src/UserSettings.json','src/UserSettings.config','src/UserConfig.xml','modules-src/X/src/CustomerConfig.json','tools/user-settings.txt',
  'src/UserSettings.cs.config','src/UserSettings.private.key')){
    if(IsSourceEligible $path){throw ('Disallowed source input accepted: '+$path)};$checks++
}
foreach($path in @('Build.ps1','Stage-Modules.ps1','app.manifest','README.txt','src/ProjectCreationStorage.cs','hybrid-src/native/hvx_hybrid_core.cpp','modules-src/ResearchAdapter/research_entry.py',
  'modules-src/CalibrationStandalone/assets/xiongxin.ico','modules-src/LocationE/source-manifest.json','tools/compiler/Microsoft.CSharp.Core.targets',
  'tools/Build-PublicBetaNoDriverSetup.ps1','installer/XIONGXIN-PublicBeta-NoDriver.iss','assets/i18n/core.packs.txt','assets/shortcuts/256.ico',
  'docs/PublicBeta_NoDriver_Packaging_Audit_20261011.md','dependencies/ResearchRuntime/frozen-runtime-inputs.json','dependencies/Protection/ConfuserEx-LICENSE.txt',
  'src/UserSettings.cs','src/UserConfig.cs','modules-src/X/src/CustomerConfig.cs','modules-src/X/src/UserSettings.resx')){
    if(-not(IsSourceEligible $path)){throw ('Required source input rejected: '+$path)};$checks++
}
foreach($path in @('../src/x.cs','src/../secret.cs','C:/private.cs','src/a:b.cs','src/CON.cs','src/a.')){
    $rejected=$false;try{$null=IsSourceEligible $path}catch{$rejected=$true};if(-not $rejected){throw 'Unsafe relative path accepted.'};$checks++
}
$sampleSources=@([pscustomobject]@{Path='src/UserSettings.cs';SHA256=('A'*64)},[pscustomobject]@{Path='modules-src/X/src/UserConfig.cs';SHA256=('B'*64)})
$coverage=AssertCompiledSourceRows $sampleSources $sampleSources
if($coverage.Status -cne 'PASS' -or $coverage.DistinctCompiledSources -ne 2 -or $coverage.MissingSources -ne 0 -or $coverage.HashMismatches -ne 0){throw 'Complete compiled-source rows were rejected.'};$checks++
foreach($badInputs in @(@([pscustomobject]@{Path='src/UserSettings.cs';SHA256=('C'*64)}),@([pscustomobject]@{Path='src/OmittedClass.cs';SHA256=('D'*64)}),@([pscustomobject]@{Path='src/maintenance-material.cs';SHA256=('E'*64)}),@())){
    $rejected=$false;try{$null=AssertCompiledSourceRows $sampleSources $badInputs}catch{$rejected=$true}
    if(-not $rejected){throw 'Missing/changed/blocked/empty compiled-source coverage was accepted.'};$checks++
}
$fullReview=[pscustomobject]@{ConfidentialityStatus='PASS';FullConfidentialityAccepted=$true;ConfidentialityReviewSHA256=('A'*64);ConfidentialityDeferralSHA256=''}
$deferred=[pscustomobject]@{ConfidentialityStatus='DEFERRED_BY_USER';FullConfidentialityAccepted=$false;ConfidentialityReviewSHA256='';ConfidentialityDeferralSHA256=('B'*64)}
AssertConfidentialityRelease $fullReview;AssertConfidentialityRelease $deferred;$checks+=2
foreach($pair in @(@($deferred,'FullConfidentialityAccepted',$true),@($deferred,'ConfidentialityReviewSHA256',('C'*64)),@($deferred,'ConfidentialityDeferralSHA256',''),
    @($deferred,'ConfidentialityStatus','PASS'),@($fullReview,'FullConfidentialityAccepted',$false),@($fullReview,'ConfidentialityDeferralSHA256',('C'*64)),
    @($fullReview,'ConfidentialityReviewSHA256','bad'),@($deferred,'FullConfidentialityAccepted','false'),@($deferred,'ConfidentialityStatus','UNKNOWN'))){
    $bad=$pair[0]|ConvertTo-Json|ConvertFrom-Json;$bad.($pair[1])=$pair[2]
    $rejected=$false;try{AssertConfidentialityRelease $bad}catch{$rejected=$true}
    if(-not $rejected){throw 'An untruthful confidentiality source-release state was accepted.'};$checks++
}
$bad=$deferred|ConvertTo-Json|ConvertFrom-Json;$bad.PSObject.Properties.Remove('FullConfidentialityAccepted')
$rejected=$false;try{AssertConfidentialityRelease $bad}catch{$rejected=$true}
if(-not $rejected){throw 'Missing confidentiality acceptance state was accepted.'};$checks++
$audit=@(& $script -Mode Audit -BuildRoot $BuildRoot)|Out-String|ConvertFrom-Json
if($audit.Status -cne 'WHITELIST_AUDIT_PASS' -or $audit.Files -le 100 -or $audit.ArchiveCreated -or $audit.CredentialContentRead -or $audit.ProductExecuted -or $audit.HardwareAccessed){throw 'Source whitelist audit failed.'};$checks++
if(-not [string]::IsNullOrWhiteSpace($BuildRoot)){
    if($audit.CompiledSourceCoverage.Status -cne 'PASS' -or $audit.CompiledSourceCoverage.DistinctCompiledSources -le 0 -or
       $audit.CompiledSourceCoverage.MissingSources -ne 0 -or $audit.CompiledSourceCoverage.HashMismatches -ne 0){throw 'Actual compiled-source coverage was incomplete.'};$checks++
}
if([string]::IsNullOrWhiteSpace($OutputRoot)){$OutputRoot=Join-Path $project ('build/qa-source-whitelist-'+[Guid]::NewGuid().ToString('N').Substring(0,10))}
$output=[IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Use a new isolated QA directory.'}
for($walk=$output;$walk.Length -ge $project.Length;$walk=[IO.Path]::GetDirectoryName($walk)){
    if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked QA output prohibited.'}
    if($walk -ieq $project){break}
}
$null=New-Item -ItemType Directory -Path $output
[ordered]@{Schema='HVX.PublicBetaNoDriver.SourceOffline/1';Status='PASS';CheckedUtc=[datetime]::UtcNow.ToString('o');Checks=$checks;Audit=$audit;
  Inputs=@([ordered]@{Path=$script;SHA256=(Get-FileHash -LiteralPath $script -Algorithm SHA256).Hash},[ordered]@{Path=$PSCommandPath;SHA256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash});
  ArchiveCreated=$false;CredentialContentRead=$false;ProductExecuted=$false;WorkerStarted=$false;SDKCalled=$false;HardwareAccessed=$false;
  Remaining='Actual export is allowed only after the parent confirms final package freeze. No archive was created; no installer/product execution or content-level secret scanning is claimed.'}|
  ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $output 'proof.json') -Encoding UTF8
Write-Output ('SOURCE_WHITELIST_OFFLINE_PROOF='+$output)
Write-Output ('PASS checks='+$checks)
