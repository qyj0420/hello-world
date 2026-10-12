[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$BuildRoot,
 [Parameter(Mandatory=$true)][string]$CurrentEvidence,
 [Parameter(Mandatory=$true)][string]$ReleaseNotes,
 [Parameter(Mandatory=$true)][string]$DeploymentReadme,
 [string[]]$AdditionalDocuments=@(),
 [string[]]$AdditionalRemaining=@()
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($PSVersionTable.PSVersion.Major -lt 7){throw 'Run this export with PowerShell 7; the delivered read-only preflight uses Windows PowerShell 5.1.'}
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$build=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
if(-not $build.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use the isolated verified r52 build.'}
if([IO.Path]::GetFileName($build) -cne 'public-beta-1.1.1-20261011-r52'){throw 'Only the new isolated r52 build may be exported; do not rename an old build.'}
$snapshots=New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
$referenceSnapshots=New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
function Assert-R52ProjectPath([string]$Path) {
 $full=[IO.Path]::GetFullPath($Path)
 if(-not $full.StartsWith($project+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Export input/output escaped the project.'}
 for($walk=$full;$walk.Length -ge $project.Length;$walk=[IO.Path]::GetDirectoryName($walk)){
  if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked export paths are not accepted.'}
 }
 return $full
}
function Read-R52ExportJson([string]$Path,[long]$Limit) {
 $full=Assert-R52ProjectPath $Path;$info=Get-Item -LiteralPath $full
 if($info.PSIsContainer -or $info.Length -gt $Limit){throw 'Export manifest is oversized or not a file.'}
 $hash=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
 $value=([IO.File]::ReadAllText($full)|ConvertFrom-Json)
 if((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -cne $hash){throw 'Manifest changed while it was validated.'}
 if($snapshots.ContainsKey($full) -and $snapshots[$full] -cne $hash){throw 'Validated manifest changed.'}
 $snapshots[$full]=$hash
 return $value
}
function Assert-R52True($Value,[string]$Name) {
 if($Value -isnot [bool] -or -not $Value){throw ($Name+' must explicitly be true.')}
}
function Assert-R52BoundFile([string]$Root,[string]$Relative,[string]$Expected) {
 if([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':') -or $Relative.Replace('/','\').Split('\') -contains '..' -or $Expected -cnotmatch '^[A-F0-9]{64}$'){throw 'Invalid bound file entry.'}
 $full=Assert-R52ProjectPath (Join-Path $Root $Relative)
 if(-not $full.StartsWith($Root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Bound file escaped its declared root.'}
 if(-not(Test-Path -LiteralPath $full -PathType Leaf) -or (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -cne $Expected){throw ('Bound payload is missing or changed: '+$Relative)}
 if($snapshots.ContainsKey($full) -and $snapshots[$full] -cne $Expected){throw 'A bound payload changed during export validation.'}
 $snapshots[$full]=$Expected
 return $full
}
function Assert-R52ReferencePath([string]$Path) {
 # References are explicit local-drive files only: no UNC/network, device path,
 # relative traversal, alternate data stream, link or recursive enumeration.
 if([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathFullyQualified($Path) -or $Path -notmatch '^[A-Za-z]:[\\/]' -or $Path.Substring(2).Contains(':') -or $Path.Replace('/','\').Split('\') -contains '..'){throw 'A reference input must be an explicit fully qualified local file path.'}
 $full=[IO.Path]::GetFullPath($Path)
 if(-not(Test-Path -LiteralPath $full -PathType Leaf)){throw 'A reference input is missing or is not a file.'}
 for($walk=$full;-not [string]::IsNullOrWhiteSpace($walk);$walk=[IO.Path]::GetDirectoryName($walk)){
  if((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked reference input paths are not accepted.'}
 }
 return $full
}
function Assert-R52ReferenceInput([string]$Path,[string]$Expected) {
 if($Expected -cnotmatch '^[A-F0-9]{64}$'){throw 'A reference input needs an exact uppercase SHA256.'}
 $full=Assert-R52ReferencePath $Path
 if(($referenceSnapshots.ContainsKey($full) -and $referenceSnapshots[$full] -cne $Expected) -or ($snapshots.ContainsKey($full) -and $snapshots[$full] -cne $Expected)){throw 'Conflicting hashes were recorded for one reference input.'}
 if((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -cne $Expected){throw 'A read-only reference input no longer matches its recorded hash.'}
 $referenceSnapshots[$full]=$Expected
 $snapshots[$full]=$Expected
}
$snapshots[$PSCommandPath]=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
$sourceExe=Assert-R52ProjectPath (Join-Path $build 'Xiongxin.OWTS.Desktop.exe')
$exeHash=(Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash
$buildManifest=Read-R52ExportJson (Join-Path $build 'build-manifest.json') (16MB)
if($buildManifest.ExecutableSha256 -cne $exeHash -or (Get-Item -LiteralPath $sourceExe).VersionInfo.FileVersion -ne '1.1.1.0'){throw 'Final EXE identity/version mismatch.'}
Assert-R52True $buildManifest.ProtectedRelease 'ProtectedRelease'
Assert-R52True $buildManifest.MaintenancePasswordEnabled 'MaintenancePasswordEnabled'
$moduleManifest=Read-R52ExportJson (Join-Path $build 'module-manifest.json') (16MB)
Assert-R52True $moduleManifest.ProtectedRelease 'Module ProtectedRelease'
Assert-R52True $moduleManifest.MaintenancePasswordEnabled 'Module MaintenancePasswordEnabled'
$evidenceFile=Assert-R52ProjectPath $CurrentEvidence
$evidence=Read-R52ExportJson $evidenceFile (16MB)
if($evidence.Schema -cne 'HVX.ExternalStaticAudit.CurrentEvidence/1'){throw 'Use the current-source audit collector output.'}
Assert-R52True $evidence.AllCurrentHashBindingsPassed 'AllCurrentHashBindingsPassed'
$rows=@($evidence.Results)
if($rows.Count -lt 8){throw 'At least eight distinct named current directed proofs are required for r52.'}
$proofNames=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$proofPaths=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$snapshots[$sourceExe]=$exeHash
$proofInputs=New-Object System.Collections.Generic.List[object]
foreach($row in $rows){
 $relative=[string]$row.Proof
 if([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative.Replace('/','\').Split('\') -contains '..' -or -not $proofNames.Add($relative.Replace('/','\'))){throw 'Proof paths must be nonempty, distinct, project-relative names.'}
 Assert-R52True $row.RecordedTestPassed 'RecordedTestPassed'
 Assert-R52True $row.CurrentHashBindingPassed 'CurrentHashBindingPassed'
 if([long]$row.InputCount -le 0 -or [long]$row.CurrentInputMismatchCount -ne 0){throw 'Every proof needs current nonempty inputs with zero mismatches.'}
 $path=Assert-R52ProjectPath (Join-Path $project $relative)
 if(-not $proofPaths.Add($path)){throw 'Proof aliases cannot count as independent directed evidence.'}
 $hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
 if([string]$row.ProofSHA256 -cnotmatch '^[A-F0-9]{64}$' -or $hash -cne [string]$row.ProofSHA256){throw ('A verified proof changed: '+$relative)}
 $proofDocument=Read-R52ExportJson $path (64MB)
 $entries=@();$entryBase=$project
 if($proofDocument.PSObject.Properties['Inputs']){$entries=@($proofDocument.Inputs)}
 elseif($proofDocument.PSObject.Properties['CurrentSourceHashes']){$entries=@($proofDocument.CurrentSourceHashes)}
 elseif($proofDocument.PSObject.Properties['Sources']){$entries=@($proofDocument.Sources)}
 elseif($proofDocument.PSObject.Properties['SourceInventory']){$entries=@($proofDocument.SourceInventory);$entryBase=Join-Path $project 'modules-src\FactoryWorker'}
 else{throw 'Every current directed proof must expose its source hash inventory.'}
 if($entries.Count -ne [long]$row.InputCount -or $entries.Count -eq 0){throw 'Current evidence input count does not match its bound proof.'}
 foreach($entry in $entries){
  $entryPath=if([IO.Path]::IsPathRooted($entry.Path)){[IO.Path]::GetFullPath($entry.Path)}else{[IO.Path]::GetFullPath((Join-Path $entryBase $entry.Path))}
  $entryPath=Assert-R52ProjectPath $entryPath
  $null=Assert-R52BoundFile $project $entryPath.Substring($project.Length+1) ([string]$entry.SHA256)
 }
 if($proofDocument.PSObject.Properties['ReferenceInputs']){foreach($entry in @($proofDocument.ReferenceInputs)){
  Assert-R52ReferenceInput ([string]$entry.Path) ([string]$entry.SHA256)
 }}
 foreach($name in @('Runtime','ProofHashes','Children')){if($proofDocument.PSObject.Properties[$name]){foreach($entry in @($proofDocument.$name)){
  $entryPath=if([IO.Path]::IsPathRooted($entry.Path)){[IO.Path]::GetFullPath($entry.Path)}else{[IO.Path]::GetFullPath((Join-Path $project $entry.Path))}
  $entryPath=Assert-R52ProjectPath $entryPath
  $null=Assert-R52BoundFile $project $entryPath.Substring($project.Length+1) ([string]$entry.SHA256)
 }}}
 if($proofDocument.PSObject.Properties['PreviewPath']){
  $preview=Assert-R52ProjectPath ([string]$proofDocument.PreviewPath)
  $null=Assert-R52BoundFile $project $preview.Substring($project.Length+1) ([string]$proofDocument.PreviewSHA256)
 }
 $proofRuns=New-Object System.Collections.Generic.List[object]
 foreach($name in @('Execution','Result','Run','GestureExecution')){if($proofDocument.PSObject.Properties[$name]){$proofRuns.Add($proofDocument.$name)}}
 foreach($name in @('Runs','ExistingRegression')){if($proofDocument.PSObject.Properties[$name]){foreach($run in @($proofDocument.$name)){$proofRuns.Add($run.Execution)}}}
 foreach($run in $proofRuns){foreach($pair in @(@('StdoutPath','StdoutSHA256'),@('StderrPath','StderrSHA256'))){
  if($run.PSObject.Properties[$pair[0]]){
   $log=Assert-R52ProjectPath ([string]$run.($pair[0]))
   $null=Assert-R52BoundFile $project $log.Substring($project.Length+1) ([string]$run.($pair[1]))
  }
 }}
 $snapshots[$path]=$hash
 $proofInputs.Add([ordered]@{Source=$path;OriginalProof=$relative;SHA256=$hash;InputCount=[long]$row.InputCount})
}
$deploymentPath=Join-Path $build 'qa\deployment-payload.json'
$deploymentProof=Read-R52ExportJson $deploymentPath (16MB)
if($deploymentProof.Status -cne 'PASS'){throw 'Final build deployment proof did not pass.'}
$deploymentExe=@($deploymentProof.Files|Where-Object Path -ceq 'Xiongxin.OWTS.Desktop.exe')
# The payload verifier checks the host once as a distribution file and again as
# a protected conversion output. Repeated identical checks are valid, but no
# missing or conflicting host hash may be accepted.
if($deploymentExe.Count -lt 1 -or @($deploymentExe|Where-Object {$_.SHA256 -cne $exeHash}).Count -ne 0){throw 'Deployment proof does not bind the final EXE.'}
# Every deployment inventory entry must still match this final build.
foreach($entry in @($deploymentProof.Files)){
 $null=Assert-R52BoundFile $build ([string]$entry.Path) ([string]$entry.SHA256)
}
$nativePath=Join-Path $build 'qa\native-model-static\verification.json'
$nativeProof=Read-R52ExportJson $nativePath (16MB)
if($nativeProof.Status -cne 'PASS_SCOPED_STATIC_CHECKS' -or $nativeProof.ExecutableSHA256 -cne $exeHash){throw 'Native/model static proof does not bind this final EXE.'}
foreach($flag in @('HardwareAccessed','ProductExecuted','ModelDecrypted','InferenceExecuted','ActualUserCacheWritten')){
 if($nativeProof.$flag -isnot [bool] -or $nativeProof.$flag){throw 'The final native/model audit must remain explicitly static and offline.'}
}
Assert-R52True $nativeProof.CachePayloadComparedFromFinalPe 'Final-PE embedded payload comparison'
$embedded=$buildManifest.EmbeddedModules
if($embedded.BundleHash -cnotmatch '^[A-F0-9]{64}$' -or [long]$embedded.FileCount -le 0 -or [long]$embedded.FileCount -gt 4096 -or [long]$embedded.ExpandedBytes -le 0 -or [long]$embedded.ExpandedBytes -gt 1GB -or $embedded.ResourceName -cne 'HVX.Modules.Index.v1'){throw 'Invalid bounded embedded module contract.'}
if($nativeProof.EmbeddedIndexSHA256 -cne $embedded.BundleHash -or [long]$nativeProof.CacheEntries -ne [long]$embedded.FileCount -or [long]$nativeProof.UniqueCompressedResources -ne [long]$embedded.UniqueResources -or [long]$nativeProof.ExpandedBytes -ne [long]$embedded.ExpandedBytes){throw 'Embedded bundle and final-PE static proof disagree.'}
$moduleRows=@($moduleManifest.Modules);$cacheRows=@($nativeProof.CacheInventory)
if($moduleRows.Count -ne [long]$embedded.FileCount -or $cacheRows.Count -ne $moduleRows.Count){throw 'Embedded and staged module inventories are incomplete.'}
$moduleMap=New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
foreach($entry in $moduleRows){
 $name=([string]$entry.Path).Replace('\','/')
 if(-not $name.StartsWith('modules/',[StringComparison]::Ordinal) -or $moduleMap.ContainsKey($name)){throw 'Invalid or duplicate staged module path.'}
 $moduleMap.Add($name,$entry)
 $null=Assert-R52BoundFile $build $name ([string]$entry.Sha256)
}
$cacheNames=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach($entry in $cacheRows){
 $name='modules/'+([string]$entry.Path).Replace('\','/')
 if(-not $cacheNames.Add($name) -or -not $moduleMap.ContainsKey($name) -or $moduleMap[$name].Sha256 -cne $entry.SHA256 -or [long]$moduleMap[$name].Bytes -ne [long]$entry.Bytes){throw 'Final-PE cache inventory does not match protected staged modules.'}
}
$catalog=$buildManifest.DeviceAssetCatalog
if($catalog.Path -cne 'assets/device-asset-catalog.v1.json'){throw 'The declared device catalog is missing.'}
$null=Assert-R52BoundFile $build ([string]$catalog.Path) ([string]$catalog.Sha256)
$modelContractPath=Join-Path $project 'dependencies\Algorithms\models-manifest.json'
$modelContract=Read-R52ExportJson $modelContractPath (64KB)
$approvedModels=@('5581BF7A60798CB8F349A3EE3A59C2F8C121D44AB98D23172453EC856D360D16','A32A19B489CD7FB0CF51F9D027C214E7636A5621D7663384D88925F2A4CEB32B')
if($modelContract.Schema -cne 'XIONGXIN.EncryptedModels.v1' -or @($modelContract.Files).Count -ne 2 -or @($nativeProof.Models).Count -ne 2){throw 'The separate two-model contract is incomplete.'}
$modelRoot=Assert-R52ProjectPath (Join-Path $build 'model-payload')
if(@(Get-ChildItem -LiteralPath $modelRoot -Recurse -File).Count -ne 2){throw 'Unexpected separate model payload files.'}
foreach($hash in $approvedModels){
 $declared=@($modelContract.Files|Where-Object Sha256 -ceq $hash)
 $relative='model-payload/'+$hash.ToLowerInvariant()+'.hxm'
 $staticRows=@($nativeProof.Models|Where-Object {([string]$_.Path).Replace('\','/') -ceq $relative})
 $deploymentRows=@($deploymentProof.Files|Where-Object {([string]$_.Path).Replace('\','/') -ceq $relative})
 if($declared.Count -ne 1 -or $staticRows.Count -ne 1 -or $staticRows[0].SHA256 -cne $hash -or $staticRows[0].Header -cne 'PDM2' -or $deploymentRows.Count -lt 1 -or @($deploymentRows|Where-Object SHA256 -cne $hash).Count){throw 'Approved encrypted model is not bound by both final static proofs.'}
 $null=Assert-R52BoundFile $build $relative $hash
}
$programName='公测版V1.1.1_r52.exe'
$delivery=Assert-R52ProjectPath (Join-Path $project 'deliverables\PublicBeta_V1.1.1_20261011_r52')
$zip=Assert-R52ProjectPath (Join-Path $project 'deliverables\公测版V1.1.1_20261011_r52.zip')
$receipt=Assert-R52ProjectPath (Join-Path $project 'deliverables\公测版V1.1.1_r52_交付校验.json')
foreach($target in @($delivery,$zip,$receipt)){if(Test-Path -LiteralPath $target){throw 'Existing r52 output is preserved; do not overwrite or automatically remove it.'}}
$copyPlan=New-Object System.Collections.Generic.List[object]
function Add-R52Copy([string]$Source,[string]$Relative) {
 $full=Assert-R52ProjectPath $Source;$info=Get-Item -LiteralPath $full
 if($info.PSIsContainer){throw 'Copy inputs must be individual files.'}
 if([IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':') -or $Relative.Replace('/','\').Split('\') -contains '..'){throw 'Invalid export destination.'}
 $hash=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
 if($snapshots.ContainsKey($full) -and $snapshots[$full] -cne $hash){throw 'Export input changed during validation.'}
 $snapshots[$full]=$hash;$copyPlan.Add([ordered]@{Source=$full;Relative=$Relative;SHA256=$hash})
}
Add-R52Copy $sourceExe $programName
Add-R52Copy (Join-Path $build 'Xiongxin.OWTS.Desktop.exe.config') ($programName+'.config')
$assetRoot=Assert-R52ProjectPath (Join-Path $build 'assets')
if(-not(Test-Path -LiteralPath $assetRoot -PathType Container)){throw 'Final assets are missing.'}
foreach($asset in @(Get-ChildItem -LiteralPath $assetRoot -Recurse -File)){
 Add-R52Copy $asset.FullName ('assets\'+$asset.FullName.Substring($assetRoot.Length+1))
}
Add-R52Copy (Join-Path $project 'tools\Invoke-R52DeploymentPreflight.ps1') 'Invoke-R52DeploymentPreflight.ps1'
foreach($document in @($ReleaseNotes,$DeploymentReadme)){
 $full=Assert-R52ProjectPath $document
 if(-not $full.StartsWith($project+'\docs\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '(?i)r52'){throw 'Explicit new r52 release and deployment documents are required.'}
}
Add-R52Copy $DeploymentReadme 'README_先读.md'
Add-R52Copy $ReleaseNotes ([IO.Path]::GetFileName($ReleaseNotes))
foreach($document in $AdditionalDocuments){
 if([IO.Path]::GetExtension($document).ToLowerInvariant() -notin @('.md','.txt','.png','.jpg','.jpeg','.pdf','.json','.csv')){throw 'Additional attachments must be explicit documents, timing data or GUI images, not source or executable code.'}
 Add-R52Copy $document ('附件\'+[IO.Path]::GetFileName($document))
}
foreach($hash in $approvedModels){$relative='model-payload\'+$hash.ToLowerInvariant()+'.hxm';Add-R52Copy (Join-Path $build $relative) $relative}
Add-R52Copy $evidenceFile 'verification\current-source-evidence.json'
$proofIndex=New-Object System.Collections.Generic.List[object]
$index=0
foreach($proof in $proofInputs){
 $index++;$relative='verification\proof-'+$index.ToString('D2')+'.json'
 Add-R52Copy $proof.Source $relative
 $proofIndex.Add([ordered]@{OriginalProof=$proof.OriginalProof;DeliveryPath=$relative.Replace('\','/');ProofSHA256=$proof.SHA256;InputCount=$proof.InputCount})
}
foreach($extra in @(
 @{Source=(Join-Path $build 'build-manifest.json');Name='build-manifest.json'},
 @{Source=(Join-Path $build 'module-manifest.json');Name='module-manifest.json'},
 @{Source=(Join-Path $build 'research-pins.json');Name='research-pins.json'},
 @{Source=$deploymentPath;Name='deployment-payload.json'},
 @{Source=$nativePath;Name='native-model-static.json'},
 @{Source=$modelContractPath;Name='models-manifest.json'}
)){Add-R52Copy $extra.Source ('verification\'+$extra.Name)}
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
$runtime=@($files|Where-Object {$_.Path -ceq $programName -or $_.Path -ceq ($programName+'.config') -or $_.Path.StartsWith('assets/',[StringComparison]::Ordinal) -or $_.Path.StartsWith('model-payload/',[StringComparison]::Ordinal)})
[ordered]@{Schema='HVX.R52.TestDelivery/1';CreatedUtc=[DateTime]::UtcNow.ToString('o');DeliveryRevision='r52';ProgramFileName=$programName;ProgramSHA256=$exeHash;ProgramBytes=(Get-Item -LiteralPath $sourceExe).Length;
 FileVersion='1.1.1.0';BuildRoot=$build;ProtectedRelease=$true;MaintenancePasswordEnabled=$true;TestOnly=$true;CustomerInstaller=$false;ProductStarted=$false;HardwareAccessed=$false;PhysicalAcceptance=$false;SignedRelease=$false;
 EmbeddedModules=$embedded;ResearchPinsSHA256=(Get-FileHash -LiteralPath (Join-Path $build 'research-pins.json')).Hash;ModelFiles=@($runtime|Where-Object {$_.Path.StartsWith('model-payload/',[StringComparison]::Ordinal)});RuntimeFiles=$runtime;Files=$files;FilesInventoryExcludes=@('delivery-manifest.json','SHA256SUMS.txt');ProofIndex=$proofIndex.ToArray();CurrentEvidenceSHA256=$snapshots[$evidenceFile];ReferenceInputCount=$referenceSnapshots.Count;ExternalReferenceFilesCopied=$false;SharedProfile='XIONGXIN/HVXUnifiedDesktop/Validation-V30-Batch-20261008';
 Remaining=(@('Test delivery only; target-machine installation, drivers, device identity and physical electrical safety are not validated.','Real E+ location accuracy, effective CAL applicability and field performance need same-parameter target-machine verification.','Selectable languages are preserved, but full professional translation and every-page GUI/DPI review are incomplete.','Old incident records and missing cleanup receipts remain authoritative; this package never automatically unlocks them.','First local AI store read and complete dependency/security/signing/formal-release audits remain incomplete.','Static native/model protection review is scoped; packaging, obfuscation and hashes are not a claim of irreversible protection.','Legacy Factory residual/AI-policy fixtures remain recorded FAIL and were not relabeled as full-suite success.')+@($AdditionalRemaining))}|
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
# Recheck explicit reference paths and all bound bytes after ZIP verification,
# immediately before the success receipt. External inputs are never copied.
foreach($inputPath in @($referenceSnapshots.Keys)){$null=Assert-R52ReferencePath $inputPath}
foreach($inputPath in @($snapshots.Keys)){if((Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash -cne $snapshots[$inputPath]){throw 'A bound input changed before export completion; no success receipt will be created.'}}
$receiptJson=[ordered]@{Schema='HVX.R52.ExportedDelivery/1';CreatedUtc=[DateTime]::UtcNow.ToString('o');DeliveryDirectory=$delivery;Zip=$zip;ZipSHA256=(Get-FileHash -LiteralPath $zip).Hash;VerifiedZipFiles=$verified;ProgramSHA256=$exeHash;
 DeliveryManifestSHA256=(Get-FileHash -LiteralPath (Join-Path $delivery 'delivery-manifest.json')).Hash;ExporterSHA256=(Get-FileHash -LiteralPath $PSCommandPath).Hash;
 NoExistingDeliveryOverwritten=$true;NoInstalledProductChanged=$true;ProductStarted=$false;HardwareAccessed=$false;PhysicalAcceptance=$false}|
 ConvertTo-Json -Depth 5
$receiptStream=[IO.File]::Open($receipt,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try{
 $writer=New-Object IO.StreamWriter($receiptStream,(New-Object Text.UTF8Encoding($false)))
 try{$writer.WriteLine($receiptJson);$writer.Flush()}finally{$writer.Dispose()}
}finally{$receiptStream.Dispose()}
Write-Output ('DELIVERY_DIRECTORY='+$delivery)
Write-Output ('DELIVERY_ZIP='+$zip)
Write-Output ('DELIVERY_BYTE_VERIFICATION='+$verified+';EXE_SHA256='+$exeHash)
