[CmdletBinding()]
param([string]$DestinationRoot='D:\codex\HVXR27Release')
# Copy only the explicitly selected release inputs into a new isolated project.
# This never modifies the working project, starts hardware, builds or publishes.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$source=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$destination=[IO.Path]::GetFullPath($DestinationRoot).TrimEnd('\')
if($source -ine 'D:\codex\HVXUnifiedDesktop' -or $destination -ine 'D:\codex\HVXR27Release'){
 throw 'This approved snapshot is restricted to HVXUnifiedDesktop -> HVXR27Release.'
}
if(Test-Path -LiteralPath $destination){throw 'The isolated release destination already exists; it will not be overwritten.'}
function Assert-SnapshotPath([string]$Path,[string]$Boundary){
 $full=[IO.Path]::GetFullPath($Path);$limit=[IO.Path]::GetFullPath($Boundary).TrimEnd('\')
 if($full -ine $limit -and -not $full.StartsWith($limit+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Snapshot path escaped its explicit root.'}
 for($walk=$full;-not[String]::IsNullOrEmpty($walk);$walk=[IO.Path]::GetDirectoryName($walk)){
  if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw ('Linked snapshot path is prohibited: '+$walk)}
 }
 return $full
}
$null=Assert-SnapshotPath $source $source
$null=Assert-SnapshotPath $destination 'D:\codex'
$modules=@('CalibrationStandalone','CalibrationTest','ConclusionAlgorithm','DataAnalysis','DataBrowser','FactoryWorker',
 'HardwareConnector','HardwareControl','LocationABC','LocationABCD','LocationAlgorithm','PressureTest',
 'ProjectCreation','ProjectManagement','ReportEngine','ReportGenerator','ResearchAdapter','Task39InputWorker')
$trees=@('src','dependencies','assets','tools','hybrid-src','installer','tests','docs','build/protection-tools-env')+
 @($modules|ForEach-Object{'modules-src/'+$_})
$files=@('Build.ps1','Stage-Modules.ps1','App.config','app.manifest','AGENTS.md',
 'build/prerequisites/NDP48-x86-x64-AllOS-ENU.exe')
foreach($optional in @('handoff.md','README.txt','FIRST_USE_HV_SAFETY_CONFIRMATION_PENDING.md')){
 if(Test-Path -LiteralPath (Join-Path $source $optional) -PathType Leaf){$files+=$optional}
}
function Get-SnapshotInventory([string]$Root){
 $paths=New-Object 'System.Collections.Generic.List[string]'
 foreach($relative in $files){
  $path=Assert-SnapshotPath (Join-Path $Root $relative) $Root
  if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw ('Required snapshot file is missing: '+$relative)}
  $paths.Add($path)
 }
 foreach($relative in $trees){
  $path=Assert-SnapshotPath (Join-Path $Root $relative) $Root
  if(-not(Test-Path -LiteralPath $path -PathType Container)){throw ('Required snapshot tree is missing: '+$relative)}
  $pending=New-Object 'System.Collections.Generic.Stack[string]';$pending.Push($path)
  while($pending.Count){
   foreach($entry in Get-ChildItem -LiteralPath $pending.Pop() -Force){
    $null=Assert-SnapshotPath $entry.FullName $Root
    if($entry.PSIsContainer){$pending.Push($entry.FullName)}else{$paths.Add($entry.FullName)}
   }
  }
 }
 @($paths|Sort-Object -Unique|ForEach-Object{
  $item=Get-Item -LiteralPath $_ -Force
  [ordered]@{Path=$_.Substring($Root.Length+1).Replace('\','/');Bytes=$item.Length;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}
 })
}
function Canonical($Value){ConvertTo-Json -InputObject @($Value) -Depth 5 -Compress}
$started=[DateTime]::UtcNow.ToString('o')
$prerequisite=Assert-SnapshotPath (Join-Path $source 'build/prerequisites/NDP48-x86-x64-AllOS-ENU.exe') $source
$prerequisiteHash=(Get-FileHash -LiteralPath $prerequisite -Algorithm SHA256).Hash
$prerequisiteSignature=Get-AuthenticodeSignature -LiteralPath $prerequisite
if($prerequisiteHash -cne '0A3A390C47E639D0F7FC65B21195FEE6B7F65B066F80F70C60FAB191D14B7E40' -or
 $prerequisiteSignature.Status -ne 'Valid' -or $prerequisiteSignature.SignerCertificate.Subject -notmatch 'Microsoft Corporation'){
 throw 'The offline .NET prerequisite did not match the approved hash and Microsoft signature.'
}
$before=@(Get-SnapshotInventory $source)
if(Test-Path -LiteralPath $destination){throw 'Snapshot destination appeared during inspection; stopped without overwriting.'}
$null=New-Item -ItemType Directory -Path $destination
# Empty required trees are retained so the unchanged build/publisher contracts work.
foreach($relative in $trees){$null=New-Item -ItemType Directory -Path (Join-Path $destination $relative) -Force}
foreach($entry in $before){
 $from=Assert-SnapshotPath (Join-Path $source $entry.Path) $source
 $to=Assert-SnapshotPath (Join-Path $destination $entry.Path) $destination
 $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($to)) -Force
 $null=Assert-SnapshotPath $to $destination
 # Opening the source with read-only sharing also excludes writes during copy.
 $inputFile=[IO.File]::Open($from,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 try{
  $outputFile=[IO.File]::Open($to,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
  try{$inputFile.CopyTo($outputFile);$outputFile.Flush($true)}finally{$outputFile.Dispose()}
 }finally{$inputFile.Dispose()}
 if((Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash -cne $entry.SHA256){throw ('Snapshot copy mismatch; incomplete directory retained: '+$entry.Path)}
}
$copied=@(Get-SnapshotInventory $destination)
$after=@(Get-SnapshotInventory $source)
if((Canonical $before) -cne (Canonical $copied) -or (Canonical $before) -cne (Canonical $after)){
 throw 'Selected release inputs changed during snapshot. The incomplete snapshot is retained; do not build it.'
}
$receipt=[ordered]@{
 Schema='HVX.R27.ReleaseSourceSnapshot/1';Status='PASS';SourceRoot=$source;DestinationRoot=$destination
 StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Files=$before;IncludedTrees=$trees;IncludedRootFiles=$files
 ExcludedTrees=@('modules-src/HardwareLinkStress','build (except protection-tools-env and the explicit .NET prerequisite)','deliverables')
 ExclusionReason='HardwareLinkStress is an independently owned diagnostic task and is not part of this R27 release. No files in that task were changed, moved or deleted.'
 ExternalReadOnlyPrerequisites=@('Installed Visual Studio C++ toolchain and Windows SDK',
  'Existing CPython 3.12.14 base runtime referenced by pyvenv.cfg',
  'Existing Obfuscar/Confuser and strong-name key referenced by Protect-LocationAlgorithm.ps1; the external key is not copied')
 SourceBeforeAfterMatched=$true;DestinationHashesMatched=$true;OldBuildReceiptsCopied=$false
 Prerequisite=[ordered]@{Path='build/prerequisites/NDP48-x86-x64-AllOS-ENU.exe';SHA256=$prerequisiteHash;SignatureStatus=$prerequisiteSignature.Status.ToString();Signer=$prerequisiteSignature.SignerCertificate.Subject;Executed=$false}
 HardwareAccessed=$false;ProductLaunched=$false;InstallerLaunched=$false;SourceModified=$false
}
$receiptPath=Join-Path $destination 'snapshot-receipt.json'
$receipt|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $receiptPath -Encoding UTF8
Write-Output ('R27_SOURCE_SNAPSHOT_PASS; FILES='+$before.Count+'; RECEIPT='+$receiptPath)
