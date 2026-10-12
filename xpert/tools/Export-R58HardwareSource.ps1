[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$CompileRoot,
    [Parameter(Mandatory=$true)][string]$HardwareRoot,
    [Parameter(Mandatory=$true)][string]$Destination,
    [string[]]$VerificationRoots=@(),
    [switch]$ValidateOnly
)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if([IO.Path]::GetFileName($root) -cne 'r58-hardware-source-20261012'){throw 'This export belongs to the isolated r58 source tree.'}
$deliveryParent='D:\codex\HVXUnifiedDesktop\deliverables'
$destination=[IO.Path]::GetFullPath($Destination).TrimEnd('\')
if(-not $destination.StartsWith($deliveryParent+'\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $destination)){throw 'Use a new, nonexisting delivery subdirectory.'}
function Assert-Unlinked([string]$Path) {
    for($walk=[IO.Path]::GetFullPath($Path);$walk;$walk=[IO.Path]::GetDirectoryName($walk)) {
        if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw ('Linked path rejected: '+$walk)}
    }
}
function Files([string]$Directory) {
    Assert-Unlinked $Directory
    $pending=New-Object 'System.Collections.Generic.Stack[string]';$pending.Push($Directory)
    $found=New-Object 'System.Collections.Generic.List[string]'
    while($pending.Count) {
        foreach($entry in @(Get-ChildItem -LiteralPath $pending.Pop() -Force)) {
            if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){throw ('Linked entry rejected: '+$entry.FullName)}
            if($entry.PSIsContainer){$pending.Push($entry.FullName)}else{$found.Add($entry.FullName)}
        }
    }
    return @($found.ToArray()|Sort-Object)
}
function Hash([string]$Path) {(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function Contained-File([string]$Directory,[string]$RelativePath) {
    if([IO.Path]::IsPathRooted($RelativePath)){throw ('Absolute artifact path rejected: '+$RelativePath)}
    $path=[IO.Path]::GetFullPath((Join-Path $Directory $RelativePath))
    if(-not $path.StartsWith($Directory+'\',[StringComparison]::OrdinalIgnoreCase)){throw ('Artifact escaped its root: '+$RelativePath)}
    Assert-Unlinked $path
    return $path
}
function Copy-Checked([string]$Source,[string]$Target) {
    Assert-Unlinked $Source;Assert-Unlinked $Target
    if(-not (Test-Path -LiteralPath $Source -PathType Leaf)){throw ('Missing file: '+$Source)}
    $parent=[IO.Path]::GetDirectoryName($Target)
    if(-not (Test-Path -LiteralPath $parent)){$null=New-Item -ItemType Directory -Path $parent -Force}
    Copy-Item -LiteralPath $Source -Destination $Target
    if((Hash $Source) -cne (Hash $Target)){throw ('Copy verification failed: '+$Source)}
}
function Copy-Tree([string]$Source,[string]$Target) {
    foreach($file in @(Files $Source)){Copy-Checked $file (Join-Path $Target $file.Substring($Source.Length+1))}
}
function Inventory([string]$Directory) {
    return @(Files $Directory|ForEach-Object{[ordered]@{Path=$_.Substring($Directory.Length+1).Replace('\','/');Bytes=(Get-Item -LiteralPath $_).Length;SHA256=(Hash $_)}})
}
function Assert-ExpectedFiles([string]$Directory,[System.Collections.IDictionary]$Expected) {
    $actual=@(Files $Directory)
    if($actual.Count -ne $Expected.Count){throw ('Artifact file set changed: '+$Directory)}
    foreach($file in $actual) {
        $relative=$file.Substring($Directory.Length+1).Replace('\','/')
        if(-not $Expected.Contains($relative) -or (Hash $file) -cne $Expected[$relative]){throw ('Unexpected or modified artifact: '+$relative)}
    }
}
function Archive-Checked([string]$Directory,[string]$ZipPath) {
    $before=@(Inventory $Directory)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($Directory,$ZipPath,[IO.Compression.CompressionLevel]::Optimal,$true)
    $zip=[IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $expected=@{};$prefix=[IO.Path]::GetFileName($Directory)+'/'
        foreach($entry in $before){$expected.Add($prefix+$entry.Path,$entry)}
        $seen=New-Object 'System.Collections.Generic.HashSet[string]'
        foreach($entry in $zip.Entries) {
            if($entry.FullName.EndsWith('/')){continue}
            $name=$entry.FullName.Replace('\','/')
            if(-not $expected.ContainsKey($name) -or -not $seen.Add($name)){throw ('Unexpected/duplicate ZIP entry: '+$name)}
            $record=$expected[$name];if($entry.Length -ne $record.Bytes){throw ('ZIP length mismatch: '+$name)}
            $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
            try{$actual=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$stream.Dispose();$sha.Dispose()}
            if($actual -cne $record.SHA256){throw ('ZIP content mismatch: '+$name)}
        }
        if($seen.Count -ne $expected.Count){throw 'ZIP entries are missing.'}
    } finally {$zip.Dispose()}
    if(($before|ConvertTo-Json -Depth 5 -Compress) -cne (@(Inventory $Directory)|ConvertTo-Json -Depth 5 -Compress)){throw 'Staged files changed while archiving.'}
    return [ordered]@{File=[IO.Path]::GetFileName($ZipPath);SHA256=(Hash $ZipPath);Bytes=(Get-Item -LiteralPath $ZipPath).Length;VerifiedFileCount=$before.Count;AllEntryHashesVerified=$true}
}
Assert-Unlinked $root;Assert-Unlinked $destination
$compile=[IO.Path]::GetFullPath($CompileRoot).TrimEnd('\');$hardware=[IO.Path]::GetFullPath($HardwareRoot).TrimEnd('\')
foreach($inputRoot in @($compile,$hardware)) {
    if(-not $inputRoot.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Only r58 build-contained candidates can be exported.'}
    Assert-Unlinked $inputRoot
}
$compileManifest=Get-Content -LiteralPath (Join-Path $compile 'compile-only-manifest.json') -Raw|ConvertFrom-Json
if($compileManifest.Status -cne 'COMPILE_ONLY_PASS' -or -not $compileManifest.InputSetsAndHashesUnchanged -or -not $compileManifest.BaseInputsUnchanged){throw 'Compile candidate does not have a stable successful receipt.'}
foreach($record in $compileManifest.Inputs){if((Hash $record.Path) -cne $record.SHA256){throw ('Compile input changed since the candidate build: '+$record.Path)}}
foreach($record in $compileManifest.Products) {
    if((Hash (Contained-File $compile $record.File)) -cne $record.SHA256){throw ('Compiled product changed: '+$record.File)}
    if($record.PSObject.Properties.Name -contains 'NativeDLL') {
        if($null -ne $record.NativeDLL -and (Hash (Contained-File $compile $record.NativeDLL.File)) -cne $record.NativeDLL.SHA256){throw ('Copied native product changed: '+$record.NativeDLL.File)}
    }
}
foreach($name in @('CopiedDependency','RuntimeConfig')) {
    $record=$compileManifest.$name
    if((Hash (Contained-File $compile $record.File)) -cne $record.SHA256){throw ('Compiled dependency changed: '+$record.File)}
}
$hardwareManifestPath=Join-Path $hardware 'hardware-build-manifest.json'
$hardwareManifest=Get-Content -LiteralPath $hardwareManifestPath -Raw|ConvertFrom-Json
if($hardwareManifest.Schema -cne 'XIONGXIN.UnifiedDesktop.HardwareBuild.v1' -or $hardwareManifest.SourceMode -cne 'LocalUnifiedProject' -or $hardwareManifest.Architecture -cne 'x64'){throw 'Hardware modules do not have the expected build receipt.'}
foreach($record in $hardwareManifest.Inputs){if((Hash (Contained-File $root $record.Path)) -cne $record.Sha256){throw ('Hardware build input changed: '+$record.Path)}}
foreach($record in $hardwareManifest.Binaries){if((Hash (Contained-File $hardware $record.Path)) -cne $record.Sha256){throw ('Hardware product changed: '+$record.Path)}}
$native=Join-Path $root 'native-hardware/out/HvxAcquisitionNative.dll'
if((Hash $native) -cne $compileManifest.FrozenNativeDLL.SHA256){throw 'Native DLL differs from the candidate.'}
# Exact staged-runtime and candidate allowlists also reject unrelated/fake files.
$compileExpected=@{}
$compileExpected.Add('compile-only-manifest.json',(Hash (Join-Path $compile 'compile-only-manifest.json')))
$compileExpected.Add('r58-vs-r57-base-differences.json',$compileManifest.DifferenceFileSHA256)
$compileExpected.Add('README-CANDIDATE.txt',$compileManifest.ReadmeSHA256)
foreach($record in $compileManifest.Products) {
    $compileExpected.Add($record.File,$record.SHA256)
    $compileExpected.Add($record.NativeDLL.File,$record.NativeDLL.SHA256)
    $compileExpected.Add($record.Name+'/compile.rsp',$record.ResponseFileSHA256)
    $compileExpected.Add($record.Name+'/compile.log',$record.CompileLogSHA256)
}
foreach($name in @('CopiedDependency','RuntimeConfig')){$record=$compileManifest.$name;$compileExpected.Add($record.File,$record.SHA256)}
foreach($record in $compileManifest.DerivedResources){$compileExpected.Add($record.Path,$record.SHA256)}
$runtimeProof=$compileManifest.CorrectedEPlusRuntime
$pinsFile=Join-Path $compile 'research-pins.json'
$receiptFile=Join-Path $compile 'research-runtime-input-receipt.json'
if((Hash $pinsFile) -cne $runtimeProof.PinsSHA256 -or (Hash $receiptFile) -cne $runtimeProof.ReceiptSHA256){throw 'Staged research metadata differs from its compiled receipt.'}
$compileExpected.Add('research-pins.json',$runtimeProof.PinsSHA256)
$compileExpected.Add('research-runtime-input-receipt.json',$runtimeProof.ReceiptSHA256)
$pins=Get-Content -LiteralPath $pinsFile -Raw|ConvertFrom-Json
if(@($pins.PSObject.Properties).Count -ne $runtimeProof.Files){throw 'Staged research pin count changed.'}
foreach($pin in $pins.PSObject.Properties){$null=Contained-File $compile ($runtimeProof.StagedDirectory+'/'+$pin.Name);$compileExpected.Add($runtimeProof.StagedDirectory+'/'+$pin.Name,$pin.Value)}
$hardwareExpected=@{};$hardwareExpected.Add('hardware-build-manifest.json',(Hash $hardwareManifestPath))
foreach($record in $hardwareManifest.Binaries){$hardwareExpected.Add($record.Path,$record.Sha256)}
foreach($record in $hardwareManifest.Inputs) {
    if($record.Path.StartsWith('dependencies/HardwareConnector/runtime/',[StringComparison]::Ordinal)) {
        $hardwareExpected.Add('HardwareIntegration/runtime/'+$record.Path.Substring('dependencies/HardwareConnector/runtime/'.Length),$record.Sha256)
    }elseif($record.Path.StartsWith('dependencies/Task6/',[StringComparison]::Ordinal)) {
        $hardwareExpected.Add('Task38/Task6/'+$record.Path.Substring('dependencies/Task6/'.Length),$record.Sha256)
    }elseif($record.Path -cin @('assets/xiongxin-original.png','assets/xiongxin.ico')) {
        $hardwareExpected.Add('HardwareIntegration/'+$record.Path,$record.Sha256)
    }
}
Assert-ExpectedFiles $compile $compileExpected
Assert-ExpectedFiles $hardware $hardwareExpected
if($ValidateOnly) {
    Write-Output ('R58_EXPORT_VALIDATION_PASS compileFiles='+$compileExpected.Count+' hardwareFiles='+$hardwareExpected.Count+' researchFiles='+$runtimeProof.Files)
    return
}
$null=New-Item -ItemType Directory -Path $destination
$sourceStage=Join-Path $destination 'r58-hardware-source-20261012'
$candidateStage=Join-Path $destination 'r58-compile-only-candidate-20261012'
$null=New-Item -ItemType Directory -Path $sourceStage,$candidateStage
foreach($entry in @(Get-ChildItem -LiteralPath $root -File -Force)){Copy-Checked $entry.FullName (Join-Path $sourceStage $entry.Name)}
foreach($tree in @('src','modules-src','hybrid-src','tools','dependencies','assets','tests','installer','docs','provenance')) {
    Copy-Tree (Join-Path $root $tree) (Join-Path $sourceStage $tree)
}
foreach($file in @(Files (Join-Path $root 'qa'))) {
    if([IO.Path]::GetExtension($file) -notin @('.json','.txt','.log','.tsv','.csv','.md')){throw ('Non-text QA export rejected: '+$file)}
    Copy-Checked $file (Join-Path $sourceStage ('qa/'+$file.Substring((Join-Path $root 'qa').Length+1)))
}
# Include real native source/test source and the production bridge only. Never
# carry fake-only DLLs/EXEs or native object/import-library intermediates.
foreach($entry in @(Get-ChildItem -LiteralPath (Join-Path $root 'native-hardware') -File)){Copy-Checked $entry.FullName (Join-Path $sourceStage ('native-hardware/'+$entry.Name))}
Copy-Tree (Join-Path $root 'native-hardware/tests') (Join-Path $sourceStage 'native-hardware/tests')
foreach($name in @('HvxAcquisitionNative.dll','build-evidence.json','api-coverage.json','production-exports.txt','production-pe-audit.txt')) {
    Copy-Checked (Join-Path $root ('native-hardware/out/'+$name)) (Join-Path $sourceStage ('native-hardware/out/'+$name))
}
Copy-Tree $compile (Join-Path $candidateStage 'compiled-closures')
Copy-Tree $hardware (Join-Path $candidateStage 'hardware-modules')
Assert-ExpectedFiles $compile $compileExpected
Assert-ExpectedFiles $hardware $hardwareExpected
Assert-ExpectedFiles (Join-Path $candidateStage 'compiled-closures') $compileExpected
Assert-ExpectedFiles (Join-Path $candidateStage 'hardware-modules') $hardwareExpected
foreach($record in $compileManifest.Inputs) {
    if((Hash $record.Path) -cne $record.SHA256){throw ('Compiled input changed during export: '+$record.Path)}
    if($record.Path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) {
        if((Hash (Contained-File $sourceStage $record.Path.Substring($root.Length+1))) -cne $record.SHA256){throw ('Exported source differs from compilation: '+$record.Path)}
    }
}
foreach($record in $hardwareManifest.Inputs){if((Hash (Contained-File $sourceStage $record.Path)) -cne $record.Sha256){throw ('Exported hardware source differs from compilation: '+$record.Path)}}
foreach($verification in $VerificationRoots) {
    $verified=[IO.Path]::GetFullPath($verification).TrimEnd('\')
    if(-not $verified.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Verification must be r58-contained.'}
    # Export textual evidence only; executable offline fixtures are not products.
    foreach($file in @(Files $verified)|Where-Object{[IO.Path]::GetExtension($_) -in @('.json','.txt','.log','.tsv','.csv','.md')}) {
        Copy-Checked $file (Join-Path $sourceStage ('verification/'+[IO.Path]::GetFileName($verified)+'/'+$file.Substring($verified.Length+1)))
    }
}
$sourceInventory=@(Inventory $sourceStage)
$candidateInventory=@(Inventory $candidateStage)
$sourceInventory|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $destination 'source-files.json') -Encoding UTF8
$candidateInventory|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $destination 'candidate-files.json') -Encoding UTF8
$sourceZip=Join-Path $destination 'HVX_r58_Hardware_Eplus_Source_20261012.zip'
$candidateZip=Join-Path $destination 'HVX_r58_CompileOnly_Candidate_20261012.zip'
$archives=@((Archive-Checked $sourceStage $sourceZip),(Archive-Checked $candidateStage $candidateZip))
[ordered]@{Schema='HVX.R58.SourceAndCompileOnlyExport/1';Utc=[DateTime]::UtcNow.ToString('o');SourceRoot=$root;CompileManifestSHA256=(Hash (Join-Path $compile 'compile-only-manifest.json'));HardwareRoot=$hardware;HardwareManifestSHA256=(Hash $hardwareManifestPath);NativeSHA256=(Hash $native);Archives=$archives;SourceInventorySHA256=(Hash (Join-Path $destination 'source-files.json'));CandidateInventorySHA256=(Hash (Join-Path $destination 'candidate-files.json'));CustomerInstaller=$false;CompleteDesktopExecutable=$false;DriverInstallerIncluded=$false;ProductEntryExecuted=$false;HardwareAccessed=$false;PhysicalAcceptance=$false}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $destination 'export-receipt.json') -Encoding UTF8
Write-Output ('R58_EXPORT='+$destination)
$archives|ConvertTo-Json -Depth 5
