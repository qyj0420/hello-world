[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$BuildRoot,[Parameter(Mandatory=$true)][string]$CurrentEvidence)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($PSVersionTable.PSVersion.Major -lt 7){throw 'Run this export with PowerShell 7; the delivered read-only preflight uses Windows PowerShell 5.1.'}
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$build=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
if(-not $build.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use the isolated verified r51 build.'}
$snapshots=New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
function Assert-R51ProjectPath([string]$Path) {
 $full=[IO.Path]::GetFullPath($Path)
 if(-not $full.StartsWith($project+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Export input/output escaped the project.'}
 for($walk=$full;$walk.Length -ge $project.Length;$walk=[IO.Path]::GetDirectoryName($walk)){
  if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked export paths are not accepted.'}
 }
 return $full
}
function Read-R51ExportJson([string]$Path,[long]$Limit) {
 $full=Assert-R51ProjectPath $Path;$info=Get-Item -LiteralPath $full
 if($info.PSIsContainer -or $info.Length -gt $Limit){throw 'Export manifest is oversized or not a file.'}
 $hash=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
 $value=([IO.File]::ReadAllText($full)|ConvertFrom-Json)
 if((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -cne $hash){throw 'Manifest changed while it was validated.'}
 if($snapshots.ContainsKey($full) -and $snapshots[$full] -cne $hash){throw 'Validated manifest changed.'}
 $snapshots[$full]=$hash
 return $value
}
function Assert-R51True($Value,[string]$Name) {
 if($Value -isnot [bool] -or -not $Value){throw ($Name+' must explicitly be true.')}
}
$sourceExe=Assert-R51ProjectPath (Join-Path $build 'Xiongxin.OWTS.Desktop.exe')
$exeHash=(Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash
$buildManifest=Read-R51ExportJson (Join-Path $build 'build-manifest.json') (16MB)
if($buildManifest.ExecutableSha256 -cne $exeHash -or (Get-Item -LiteralPath $sourceExe).VersionInfo.FileVersion -ne '1.1.1.0'){throw 'Final EXE identity/version mismatch.'}
Assert-R51True $buildManifest.ProtectedRelease 'ProtectedRelease'
Assert-R51True $buildManifest.MaintenancePasswordEnabled 'MaintenancePasswordEnabled'
$moduleManifest=Read-R51ExportJson (Join-Path $build 'module-manifest.json') (16MB)
Assert-R51True $moduleManifest.ProtectedRelease 'Module ProtectedRelease'
Assert-R51True $moduleManifest.MaintenancePasswordEnabled 'Module MaintenancePasswordEnabled'
$evidenceFile=Assert-R51ProjectPath $CurrentEvidence
$evidence=Read-R51ExportJson $evidenceFile (16MB)
Assert-R51True $evidence.AllCurrentHashBindingsPassed 'AllCurrentHashBindingsPassed'
$rows=@($evidence.Results)
if($rows.Count -lt 8){throw 'At least eight distinct named current directed proofs are required for r51.'}
$proofNames=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$proofPaths=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$snapshots[$sourceExe]=$exeHash
$proofInputs=New-Object System.Collections.Generic.List[object]
foreach($row in $rows){
 $relative=[string]$row.Proof
 if([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative.Replace('/','\').Split('\') -contains '..' -or -not $proofNames.Add($relative.Replace('/','\'))){throw 'Proof paths must be nonempty, distinct, project-relative names.'}
 Assert-R51True $row.RecordedTestPassed 'RecordedTestPassed'
 Assert-R51True $row.CurrentHashBindingPassed 'CurrentHashBindingPassed'
 if([long]$row.InputCount -le 0 -or [long]$row.CurrentInputMismatchCount -ne 0){throw 'Every proof needs current nonempty inputs with zero mismatches.'}
 $path=Assert-R51ProjectPath (Join-Path $project $relative)
 if(-not $proofPaths.Add($path)){throw 'Proof aliases cannot count as independent directed evidence.'}
 $hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
 if([string]$row.ProofSHA256 -cnotmatch '^[A-F0-9]{64}$' -or $hash -cne [string]$row.ProofSHA256){throw ('A verified proof changed: '+$relative)}
 $snapshots[$path]=$hash
 $proofInputs.Add([ordered]@{Source=$path;OriginalProof=$relative;SHA256=$hash;InputCount=[long]$row.InputCount})
}
$deploymentPath=Join-Path $build 'qa\deployment-payload.json'
$deploymentProof=Read-R51ExportJson $deploymentPath (16MB)
if($deploymentProof.Status -cne 'PASS'){throw 'Final build deployment proof did not pass.'}
$deploymentExe=@($deploymentProof.Files|Where-Object Path -ceq 'Xiongxin.OWTS.Desktop.exe')
# The payload verifier checks the host once as a distribution file and again as
# a protected conversion output. Repeated identical checks are valid, but no
# missing or conflicting host hash may be accepted.
if($deploymentExe.Count -lt 1 -or @($deploymentExe|Where-Object {$_.SHA256 -cne $exeHash}).Count -ne 0){throw 'Deployment proof does not bind the final EXE.'}
$programName='公测版V1.1.1_r51.exe'
$delivery=Assert-R51ProjectPath (Join-Path $project 'deliverables\PublicBeta_V1.1.1_20261011_r51')
$zip=Assert-R51ProjectPath (Join-Path $project 'deliverables\公测版V1.1.1_20261011_r51.zip')
$receipt=Assert-R51ProjectPath (Join-Path $project 'deliverables\公测版V1.1.1_r51_交付校验.json')
foreach($target in @($delivery,$zip,$receipt)){if(Test-Path -LiteralPath $target){throw 'Existing r51 output is preserved; do not overwrite or automatically remove it.'}}
$copyPlan=New-Object System.Collections.Generic.List[object]
function Add-R51Copy([string]$Source,[string]$Relative) {
 $full=Assert-R51ProjectPath $Source;$info=Get-Item -LiteralPath $full
 if($info.PSIsContainer){throw 'Copy inputs must be individual files.'}
 if([IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':') -or $Relative.Replace('/','\').Split('\') -contains '..'){throw 'Invalid export destination.'}
 $hash=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
 if($snapshots.ContainsKey($full) -and $snapshots[$full] -cne $hash){throw 'Export input changed during validation.'}
 $snapshots[$full]=$hash;$copyPlan.Add([ordered]@{Source=$full;Relative=$Relative;SHA256=$hash})
}
Add-R51Copy $sourceExe $programName
Add-R51Copy (Join-Path $build 'Xiongxin.OWTS.Desktop.exe.config') ($programName+'.config')
$assetRoot=Assert-R51ProjectPath (Join-Path $build 'assets')
if(-not(Test-Path -LiteralPath $assetRoot -PathType Container)){throw 'Final assets are missing.'}
foreach($asset in @(Get-ChildItem -LiteralPath $assetRoot -Recurse -File)){
 Add-R51Copy $asset.FullName ('assets\'+$asset.FullName.Substring($assetRoot.Length+1))
}
Add-R51Copy (Join-Path $project 'tools\Invoke-R51DeploymentPreflight.ps1') 'Invoke-R51DeploymentPreflight.ps1'
Add-R51Copy (Join-Path $project 'docs\20261011_r51_部署先读.md') 'README_先读.md'
Add-R51Copy (Join-Path $project 'docs\20261011_r51_测试修正与未完成项.md') '20261011_r51_测试修正与未完成项.md'
foreach($document in @('20261011_r51_部署先读.md','20261011_r51_测试修正与未完成项.md','20261011_r51_退出会话离线核验.md','校准格式与入页提示修正_20261011.md','20261011_r50_依赖核查边界.md')){
 Add-R51Copy (Join-Path $project ('docs\'+$document)) ('说明\'+$document)
}
Add-R51Copy $evidenceFile 'verification\current-source-evidence.json'
$proofIndex=New-Object System.Collections.Generic.List[object]
$index=0
foreach($proof in $proofInputs){
 $index++;$relative='verification\proof-'+$index.ToString('D2')+'.json'
 Add-R51Copy $proof.Source $relative
 $proofIndex.Add([ordered]@{OriginalProof=$proof.OriginalProof;DeliveryPath=$relative.Replace('\','/');ProofSHA256=$proof.SHA256;InputCount=$proof.InputCount})
}
foreach($extra in @(
 @{Source=(Join-Path $build 'build-manifest.json');Name='build-manifest.json'},
 @{Source=(Join-Path $build 'module-manifest.json');Name='module-manifest.json'},
 @{Source=(Join-Path $build 'research-pins.json');Name='research-pins.json'},
 @{Source=$deploymentPath;Name='deployment-payload.json'},
 @{Source=(Join-Path $build 'qa\native-model-static\verification.json');Name='native-model-static.json'},
 @{Source=(Join-Path $project 'build\qa-r51-feedback-languages-bff8899866\language-catalog-audit.json');Name='language-catalog-audit.json'}
)){Add-R51Copy $extra.Source ('verification\'+$extra.Name)}
$destinations=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach($copy in $copyPlan){if(-not $destinations.Add($copy.Relative.Replace('/','\'))){throw 'Duplicate delivery destination.'}}
# Only after every required source and receipt has been checked may output start.
New-Item -ItemType Directory -Path $delivery|Out-Null
foreach($copy in $copyPlan){
 if((Get-FileHash -LiteralPath $copy.Source).Hash -cne $copy.SHA256){throw 'Source changed before copying; partial output is retained for review.'}
 $target=Join-Path $delivery $copy.Relative
 New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force|Out-Null
 Copy-Item -LiteralPath $copy.Source -Destination $target -ErrorAction Stop
 if((Get-FileHash -LiteralPath $target).Hash -cne $copy.SHA256){throw 'Copied file changed.'}
}
$files=@(Get-ChildItem -LiteralPath $delivery -Recurse -File|Sort-Object FullName|ForEach-Object{[ordered]@{Path=$_.FullName.Substring($delivery.Length+1).Replace('\','/');Bytes=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$runtime=@($files|Where-Object {$_.Path -ceq $programName -or $_.Path -ceq ($programName+'.config') -or $_.Path.StartsWith('assets/',[StringComparison]::Ordinal)})
[ordered]@{Schema='HVX.R51.TestDelivery/1';CreatedUtc=[DateTime]::UtcNow.ToString('o');DeliveryRevision='r51';ProgramFileName=$programName;ProgramSHA256=$exeHash;ProgramBytes=(Get-Item -LiteralPath $sourceExe).Length;
 FileVersion='1.1.1.0';BuildRoot=$build;ProtectedRelease=$true;MaintenancePasswordEnabled=$true;TestOnly=$true;CustomerInstaller=$false;ProductStarted=$false;HardwareAccessed=$false;PhysicalAcceptance=$false;SignedRelease=$false;
 RuntimeFiles=$runtime;Files=$files;FilesInventoryExcludes=@('delivery-manifest.json','SHA256SUMS.txt');ProofIndex=$proofIndex.ToArray();CurrentEvidenceSHA256=$snapshots[$evidenceFile];SharedProfile='XIONGXIN/HVXUnifiedDesktop/Validation-V30-Batch-20261008';
 Remaining=@('Real 31 LOC E+ accuracy / approximately 80 m cluster and effective CAL need same-parameter verification.','Full performance baseline and target-PC entry/old incident receipt diagnosis remain incomplete; missing receipts never auto-unlock.','40 selectable languages are not complete translations; static catalog audit is limited.','First local AI store read remains synchronous.','Physical HV OFF/DI/cleanup, actual UI/DPI and target machine installation are not validated.','Dependency maintenance, full security review, signing and formal customer release remain incomplete.')}|
 ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $delivery 'delivery-manifest.json') -Encoding UTF8
Get-ChildItem -LiteralPath $delivery -Recurse -File|Sort-Object FullName|ForEach-Object{(Get-FileHash -LiteralPath $_.FullName).Hash+'  '+$_.FullName.Substring($delivery.Length+1)}|
 Set-Content -LiteralPath (Join-Path $delivery 'SHA256SUMS.txt') -Encoding UTF8
foreach($inputPath in $snapshots.Keys){if((Get-FileHash -LiteralPath $inputPath).Hash -cne $snapshots[$inputPath]){throw 'An export input changed; partial output retained, no delivery success receipt.'}}
Compress-Archive -LiteralPath $delivery -DestinationPath $zip -CompressionLevel Optimal
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead($zip);$verified=0
$seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
try{
 foreach($entry in $archive.Entries){
  if($entry.FullName.EndsWith('/')){continue}
  $prefix=[IO.Path]::GetFileName($delivery)+'/'
  if(-not $entry.FullName.StartsWith($prefix,[StringComparison]::Ordinal)){throw 'Unexpected ZIP root.'}
  $relative=$entry.FullName.Substring($prefix.Length)
  if($relative.Split('/') -contains '..' -or $relative.Contains(':') -or -not $seen.Add($relative)){throw 'Invalid or duplicate ZIP path.'}
  $expectedFile=Get-Item -LiteralPath (Join-Path $delivery $relative)
  if($entry.Length -ne $expectedFile.Length){throw 'ZIP length mismatch.'}
  $expected=(Get-FileHash -LiteralPath $expectedFile.FullName).Hash
  $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
  try{$actual=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$sha.Dispose();$stream.Dispose()}
  if($expected -cne $actual){throw 'ZIP byte verification failed.'};$verified++
 }
 if($verified -ne @(Get-ChildItem -LiteralPath $delivery -Recurse -File).Count){throw 'ZIP file inventory mismatch.'}
}finally{$archive.Dispose()}
[ordered]@{Schema='HVX.R51.ExportedDelivery/1';CreatedUtc=[DateTime]::UtcNow.ToString('o');DeliveryDirectory=$delivery;Zip=$zip;ZipSHA256=(Get-FileHash -LiteralPath $zip).Hash;VerifiedZipFiles=$verified;ProgramSHA256=$exeHash;
 DeliveryManifestSHA256=(Get-FileHash -LiteralPath (Join-Path $delivery 'delivery-manifest.json')).Hash;ExporterSHA256=(Get-FileHash -LiteralPath $PSCommandPath).Hash;
 NoExistingDeliveryOverwritten=$true;NoInstalledProductChanged=$true;ProductStarted=$false;HardwareAccessed=$false;PhysicalAcceptance=$false}|
 ConvertTo-Json -Depth 5|Set-Content -LiteralPath $receipt -Encoding UTF8
Write-Output ('DELIVERY_DIRECTORY='+$delivery)
Write-Output ('DELIVERY_ZIP='+$zip)
Write-Output ('DELIVERY_BYTE_VERIFICATION='+$verified+';EXE_SHA256='+$exeHash)
