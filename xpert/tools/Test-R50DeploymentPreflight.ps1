param([Parameter(Mandatory=$true)][string]$BuildRoot)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$finalRoot=[IO.Path]::GetFullPath($BuildRoot)
if(-not $finalRoot.StartsWith($projectRoot+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use the isolated project build.'}
$preflight=Join-Path $PSScriptRoot 'Invoke-R50DeploymentPreflight.ps1'
. $preflight
$qaRoot=Join-Path $projectRoot ('build\r50-deployment-preflight-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
New-Item -ItemType Directory -Path $qaRoot|Out-Null
$program=Join-Path $finalRoot 'Xiongxin.OWTS.Desktop.exe'
$bound=@($program,$preflight,$PSCommandPath,(Join-Path $projectRoot 'src\FirstHardwareProvisioning.cs'),(Join-Path $projectRoot 'src\EmbeddedModulePayload.cs'))
$inputs=@($bound|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$assembly=[Reflection.Assembly]::LoadFile($program)
$checkCount=0
function Check([bool]$Condition,[string]$Name){$script:checkCount++;if(-not $Condition){throw $Name}}
$app=Join-Path $qaRoot 'app';$private=Join-Path $app 'private';New-Item -ItemType Directory -Path $private|Out-Null
$state=Join-Path $qaRoot 'state.xml';$manifest=Join-Path $private 'parent-manifest.xml'
Check ((Get-R50ParentGate $assembly $app $state).Passed) 'Standalone with no parent footprint is not falsely marked partially installed.'
$row='fixture|'+('A'*64)+'|core'
$sha=[Security.Cryptography.SHA256]::Create()
try{$package=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($row))).Replace('-','')}finally{$sha.Dispose()}
$manifestText='<ParentManifest Schema="1" PackageId="'+$package+'"><File Path="fixture" Sha256="'+('A'*64)+'" Scope="core" /></ParentManifest>'
$complete='<ParentInstallState Schema="1" PackageId="'+$package+'" Stage="Complete" Hardware="True" />'
[IO.File]::WriteAllText($state,$complete)
Check (-not(Get-R50ParentGate $assembly $app $state).Passed) 'Global state alone cannot authorize a fresh independent folder.'
[IO.File]::WriteAllText($manifest,$manifestText)
Check ((Get-R50ParentGate $assembly $app $state).Passed) 'Matching completed installation documents permit the pure startup predicate.'
foreach($invalid in @($complete.Replace($package,('B'*64)),$complete.Replace('Complete','Pico'),$complete.Replace('True','False'),$complete.Replace('True','invalid'),'<invalid/>','<!DOCTYPE x [<!ENTITY z SYSTEM "file:///x">]><ParentInstallState>&z;</ParentInstallState>')){
 [IO.File]::WriteAllText($state,$invalid)
 Check (-not(Get-R50ParentGate $assembly $app $state).Passed) 'Mismatch, incomplete, offline, malformed and DTD states remain blocked.'
}
[IO.File]::WriteAllText($state,$complete)
[IO.File]::WriteAllText($manifest,$manifestText.Replace($package,('B'*64)))
Check (-not(Get-R50ParentGate $assembly $app $state).Passed) 'Manifest content identity must match its declared package.'
Remove-Item -LiteralPath $manifest
Check (-not(Get-R50ParentGate $assembly $app $state).Passed) 'Missing manifest remains blocked.'
$asset=Join-Path $app 'asset.txt';[IO.File]::WriteAllText($asset,'fixture')
$files=@([pscustomobject]@{Path='asset.txt';SHA256=(Get-FileHash -LiteralPath $asset).Hash})
Test-R50RuntimeFiles $app $files;Check $true 'Exact runtime bytes accepted.'
[IO.File]::WriteAllText($asset,'changed')
$rejected=$false;try{Test-R50RuntimeFiles $app $files}catch{$rejected=$true};Check $rejected 'Existing different assets cannot be silently accepted or overwritten.'
$rejected=$false;try{Test-R50RuntimeFiles $app @([pscustomobject]@{Path='..\state.xml';SHA256='A'*64})}catch{$rejected=$true};Check $rejected 'Escaping delivery paths rejected.'
Check ($assembly.GetManifestResourceNames() -contains 'HVX.Modules.Index.v1') 'Actual final EXE contains embedded payload; no external module fallback needed.'
$programName=([char]0x516c).ToString()+[char]0x6d4b+[char]0x7248+'V1.1.1.exe'
Copy-Item -LiteralPath $program -Destination (Join-Path $app $programName)
$deliveryFile=Join-Path $app 'delivery-manifest.json'
$programHash=(Get-FileHash -LiteralPath $program).Hash
$fixtureDelivery=[ordered]@{Schema='HVX.R50.TestDelivery/1';ProgramFileName=$programName;ProgramSHA256=$programHash;RuntimeFiles=@([ordered]@{Path=$programName;SHA256=$programHash})}
$fixtureDelivery|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $deliveryFile -Encoding UTF8
[IO.File]::WriteAllText($manifest,$manifestText)
Check ((Invoke-R50Preflight $app $app $state).InstallationDocumentGatePassed) 'Whole preflight accepts the actual renamed final EXE and a matching synthetic gate.'
Check (-not (Invoke-R50Preflight $app $app (Join-Path $qaRoot 'absent-state.xml')).InstallationDocumentGatePassed) 'Whole preflight still blocks an incomplete installation.'
$fixtureDelivery.ProgramSHA256='B'*64
$fixtureDelivery|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $deliveryFile -Encoding UTF8
$rejected=$false;try{Invoke-R50Preflight $app $app $state|Out-Null}catch{$rejected=$true};Check $rejected 'Whole preflight rejects a program hash missing from runtime entries.'
$fixtureDelivery.ProgramSHA256=$programHash;$fixtureDelivery.RuntimeFiles=@()
$fixtureDelivery|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $deliveryFile -Encoding UTF8
$rejected=$false;try{Invoke-R50Preflight $app $app $state|Out-Null}catch{$rejected=$true};Check $rejected 'Empty runtime inventory cannot load an unverified executable.'
Check (-not([IO.File]::ReadAllText($preflight).ToCharArray()|Where-Object{[int]$_ -gt 127})) 'Public preflight source is ASCII-safe for Windows PowerShell 5.1 regional code pages.'
$mismatches=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count
$proof=Join-Path $qaRoot 'verification.json'
[ordered]@{Schema='HVX.R50.DeploymentPreflight.SourceQA/1';Checks=$checkCount;ExitCode=0;Inputs=$inputs;InputMismatchCount=$mismatches;SourceUnchanged=($mismatches -eq 0);
 ActualFinalAssembly=$program;PureDeliveredParentValidator=$true;ProductStarted=$false;SDKCalled=$false;HardwareAccessed=$false;ActualInstallStateReadOrWritten=$false;PhysicalAcceptance=$false}|
 ConvertTo-Json -Depth 5|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output ('DEPLOYMENT_PREFLIGHT_PASS='+$checkCount+';PROOF='+$proof)
if($mismatches){throw 'Preflight input changed.'}
