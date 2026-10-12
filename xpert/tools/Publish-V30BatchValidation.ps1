param(
 [ValidateSet('Capture','Validate','Publish')][string]$Mode='Validate',
 [Parameter(Mandatory=$true)][string]$BuildRoot,
 [Parameter(Mandatory=$true)][string]$FreezeEvidence,
 [string]$DeploymentProof='',
 [string[]]$UiProofs=@(),
 [string[]]$HostProofs=@(),
 [string]$MaintenancePasswordProof='',
 [string]$ConfidentialityProof='',
 [string]$OutputName='HV-DAC_Xpert_Validation_V30Batch_1.0.30.9_20261008_01'
)
# Explicitly limited operator validation lane. This never invokes an installer,
# product program or device, and never edits the formal Publish-Verification119 gate.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$build=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
$evidenceSnapshots=@{}
function Assert-V30BatchUnlinked([string]$Path,[string]$Boundary) {
 $full=[IO.Path]::GetFullPath($Path);$limit=[IO.Path]::GetFullPath($Boundary).TrimEnd('\')
 if($full -ine $limit -and -not $full.StartsWith($limit+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Path escapes the approved root.'}
 for($walk=$full;$walk.Length -ge $limit.Length;$walk=[IO.Path]::GetDirectoryName($walk)){
  if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw ('Linked path is prohibited: '+$walk)}
  if($walk -ieq $limit){break}
 }
 return $full
}
function Get-V30BatchFile([string]$Base,[string]$Relative) {
 if([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')){throw 'Invalid relative file name.'}
 $file=Assert-V30BatchUnlinked (Join-Path $Base $Relative) $Base
 if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw ('Required file missing: '+$Relative)}
 return $file
}
function Get-V30BatchTreeFiles([string]$Base,[string]$Relative) {
 $directory=Assert-V30BatchUnlinked (Join-Path $Base $Relative) $Base
 if(-not(Test-Path -LiteralPath $directory -PathType Container)){throw ('Required directory missing: '+$Relative)}
 $pending=New-Object 'System.Collections.Generic.Stack[string]';$pending.Push($directory)
 while($pending.Count){
  foreach($entry in Get-ChildItem -LiteralPath $pending.Pop() -Force){
   $null=Assert-V30BatchUnlinked $entry.FullName $Base
   if($entry.PSIsContainer){$pending.Push($entry.FullName)}else{$entry.FullName}
  }
 }
}
function Get-V30BatchInventory([string]$Base,[string[]]$Trees,[string[]]$Files) {
 $paths=@($Files|ForEach-Object{Get-V30BatchFile $Base $_})
 foreach($tree in $Trees){$paths+=@(Get-V30BatchTreeFiles $Base $tree)}
 @($paths|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_.Substring($Base.TrimEnd('\').Length+1).Replace('\','/');SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})
}
function Assert-V30BatchHash([string]$Base,[string]$Relative,[string]$Expected) {
 if($Expected -notmatch '^[A-Fa-f0-9]{64}$'){throw ('Invalid hash for '+$Relative)}
 $file=Get-V30BatchFile $Base $Relative
 if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $Expected){throw ('File changed: '+$Relative)}
 return $file
}
function Convert-V30BatchCanonical($Value){ConvertTo-Json -InputObject @($Value) -Depth 20 -Compress}
function Assert-V30BatchSameInventory($Actual,$Expected,[string]$Description) {
 # Explicit member expressions also sort OrderedDictionary rows in PowerShell 7.
 $a=@($Actual|ForEach-Object{[ordered]@{Path=([string]$_.Path).Replace('\','/');SHA256=([string]$_.SHA256).ToUpperInvariant()}}|Sort-Object { $_.Path })
 $e=@($Expected|ForEach-Object{[ordered]@{Path=([string]$_.Path).Replace('\','/');SHA256=([string]$_.SHA256).ToUpperInvariant()}}|Sort-Object { $_.Path })
 if((Convert-V30BatchCanonical $a) -cne (Convert-V30BatchCanonical $e)){throw ($Description+' changed or incomplete.')}
}
function Read-V30BatchJson([string]$Path) {
 if((Get-Item -LiteralPath $Path).Length -gt 32MB){throw 'Evidence exceeds the size limit.'}
 Get-Content -LiteralPath $Path -Raw -Encoding UTF8|ConvertFrom-Json
}
function Get-V30BatchProofFile([string]$Path) {
 $full=Assert-V30BatchUnlinked ([IO.Path]::GetFullPath($Path)) (Join-Path $build 'qa')
 if(-not(Test-Path -LiteralPath $full -PathType Leaf)){throw 'Evidence file is missing.'}
 return $full
}
function Read-V30BatchEvidence([string]$Path) {
 $file=Get-V30BatchProofFile $Path
 if((Get-Item -LiteralPath $file).Length -gt 32MB){throw 'Evidence exceeds the size limit.'}
 $bytes=[IO.File]::ReadAllBytes($file);$algorithm=[Security.Cryptography.SHA256]::Create()
 try{$sha=[BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-','')}finally{$algorithm.Dispose()}
 $script:evidenceSnapshots[$file]=$sha
 [Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF)|ConvertFrom-Json
}
function Assert-V30BatchBoolean($Value,[bool]$Expected,[string]$Description) {
 if($Value -isnot [bool] -or $Value -ne $Expected){throw ('Invalid '+$Description)}
}
function Assert-V30BatchTestResult($Test) {
 if($Test.Name -notmatch '^[A-Za-z0-9]+$' -or $null -eq $Test.CompileExit -or $Test.CompileExit -ne 0 -or $null -eq $Test.ExitCode -or $Test.ExitCode -ne 0){throw 'Failed or malformed test result.'}
 Assert-V30BatchBoolean $Test.TimedOut $false 'test timeout'
 Assert-V30BatchBoolean $Test.ProcessExitConfirmed $true 'test process completion'
 $null=Assert-V30BatchHash $root ('tests/'+$Test.Name+'.cs') $Test.SourceSHA256
}
function Assert-V30BatchProof($Proof,[string]$ExpectedSchema,[datetime]$FrozenUtc) {
 if($Proof.Schema -cne $ExpectedSchema -or [IO.Path]::GetFullPath([string]$Proof.BuildRoot).TrimEnd('\') -ine $build){throw 'Evidence is from a different build or schema.'}
 Assert-V30BatchBoolean $Proof.HardwareAccessed $false 'hardware-free evidence'
 Assert-V30BatchBoolean $Proof.RuntimeUnchanged $true 'unchanged tested runtime'
 if([datetime]::Parse($Proof.StartedUtc).ToUniversalTime() -lt $FrozenUtc -or [datetime]::Parse($Proof.CompletedUtc).ToUniversalTime() -lt [datetime]::Parse($Proof.StartedUtc).ToUniversalTime()){throw 'Tests must run after this build freeze.'}
 Assert-V30BatchSameInventory $runtimeTestFiles $Proof.Runtime 'Test runtime inventory'
 if(@($Proof.Tests).Count -eq 0 -or @($Proof.Tests|Group-Object Name|Where-Object Count -ne 1).Count){throw 'Missing or duplicate tests in one proof.'}
 foreach($test in $Proof.Tests){Assert-V30BatchTestResult $test}
}

$null=Assert-V30BatchUnlinked $build (Join-Path $root 'build')
if($build -ieq (Join-Path $root 'build')){throw 'Select a dedicated build directory.'}
$freezePath=Assert-V30BatchUnlinked ([IO.Path]::GetFullPath($FreezeEvidence)) (Join-Path $build 'qa')
$bm=Read-V30BatchJson (Get-V30BatchFile $build 'build-manifest.json')
$mm=Read-V30BatchJson (Get-V30BatchFile $build 'module-manifest.json')
$uiCompilation=$null;$uiCompilationPath=$null
if($mm.PSObject.Properties['SourceUiRuntime']){
 $uiCompilationPath=Assert-V30BatchHash $build ([string]$mm.SourceUiRuntime.ManifestPath) ([string]$mm.SourceUiRuntime.ManifestSHA256)
 $uiCompilation=Read-V30BatchJson $uiCompilationPath
}
if($Mode -eq 'Publish'){
 # Historical Capture/Validate remain available, but cannot issue a new release.
 . (Join-Path $PSScriptRoot 'ReleaseMaintenanceEvidence.ps1')
 Assert-ReleaseMaintenanceCompilation $bm $mm $uiCompilation
}
Assert-V30BatchBoolean $bm.V30BatchValidationProfile $true 'V30Batch validation profile'
Assert-V30BatchBoolean $bm.V30ValidationProfile $false 'old V30 profile exclusion'
Assert-V30BatchBoolean $bm.R27ValidationProfile $false 'R27 profile exclusion'
Assert-V30BatchBoolean $bm.FrozenInternalProfile $false 'legacy profile exclusion'
Assert-V30BatchBoolean $bm.HybridArchitecture.Included $true 'hybrid runtime'
Assert-V30BatchBoolean $bm.ProtectedRelease $true 'protected candidate build'
$maintenancePassword=$false
if($bm.PSObject.Properties['MaintenancePasswordEnabled']){
 Assert-V30BatchBoolean $bm.MaintenancePasswordEnabled ([bool]$bm.MaintenancePasswordEnabled) 'maintenance build marker'
 $maintenancePassword=$bm.MaintenancePasswordEnabled
}
if($maintenancePassword){Assert-V30BatchBoolean $mm.MaintenancePasswordEnabled $true 'maintenance module marker'}
$pressureStandalone=$bm.PSObject.Properties['PressureStandalone'] -and $bm.PressureStandalone
$expectedProfile=if($pressureStandalone){'Pressure-Standalone-20261008'}else{'Validation-V30-Batch-20261008'}
if($bm.ConfigurationProfile -cne $expectedProfile -or $bm.FileVersion -cne '1.0.30.9'){throw 'Unexpected validation version/profile.'}
$exe=Assert-V30BatchHash $build 'Xiongxin.OWTS.Desktop.exe' $bm.ExecutableSha256
if([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -ne '1.0.30.9' -or $null -eq $bm.EmbeddedModules -or $bm.EmbeddedModules.ResourceName -cne 'HVX.Modules.Index.v1'){throw 'Wrong executable version or missing embedded modules.'}
if($mm.Schema -cne 'XIONGXIN.UnifiedDesktop.Modules.v2' -or $mm.SourceLocationAlgorithm.Candidate -cne 'AB_INTERNAL_20261006_R02' -or $mm.SourceLocationAlgorithm.FileVersion -cne '3.0.22.0' -or $mm.SourceLocationAlgorithm.CandidateManifestSha256 -cne '953203C6268C4B55F874E86C1D4D6019648722F406F98CABA6DA17BB88DD0562'){throw 'Unexpected location algorithm contract.'}
$actualModules=Get-V30BatchInventory $build @('modules') @()
Assert-V30BatchSameInventory $actualModules $mm.Modules 'Module manifest'
$worker=Get-V30BatchFile $build 'modules/PressureRuntime/Xiongxin.Pressure.Runtime.exe'
if([Diagnostics.FileVersionInfo]::GetVersionInfo($worker).FileVersion -ne '1.0.30.9'){throw 'Expected frozen V30Batch pressure runtime.'}
# Bind all 198 original source identities plus the hash-pinned local differences
# and exact named local helpers. New behavior is not upstream physical acceptance.
$batchSource=& (Join-Path $PSScriptRoot 'Test-FactoryBatchSource.ps1')
if((ConvertTo-Json -InputObject $batchSource -Depth 8 -Compress) -cne
   (ConvertTo-Json -InputObject $bm.FactoryBatchSource -Depth 8 -Compress)){throw 'Batch build source evidence differs from current verified inputs.'}
$workerRoot=Join-Path $root 'modules-src/FactoryWorker'
$upstream=Read-V30BatchJson (Get-V30BatchFile $workerRoot 'v30-batch-integration-provenance.json')
$compiledNames=@(@($upstream.Files|ForEach-Object{([string]$_.Path).Replace('\','/')})+@($upstream.LocalAdapters)|Sort-Object)
if($compiledNames.Count -ne ($batchSource.SourceCount+@($batchSource.LocalAdapters).Count) -or
   @($compiledNames|Group-Object|Where-Object Count -ne 1).Count){throw 'Missing or duplicate batch source closure.'}
$workerManifestPath=Assert-V30BatchHash $build $mm.SourcePressureRuntime.ManifestPath $mm.SourcePressureRuntime.ManifestSHA256
if($workerManifestPath.StartsWith((Join-Path $build 'modules')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Pressure build evidence must not be inside the payload.'}
$workerManifest=Read-V30BatchJson $workerManifestPath
if($workerManifest.SourceVersion -cne '1.0.30.9' -or
 $workerManifest.UpstreamBinarySHA256 -cne $batchSource.SourceExecutableSHA256 -or
 $workerManifest.UpstreamManifestSHA256 -cne $batchSource.SourceManifestSHA256 -or
 $workerManifest.LocalAdaptationSHA256 -cne $batchSource.LocalAdaptationSHA256 -or
 $workerManifest.ExactUpstreamSourceCount -ne $batchSource.ExactUpstreamSourceCount){throw 'Pressure runtime was not built from the verified .9 base and reviewed adaptations.'}
$workerHash=(Get-FileHash -LiteralPath $worker).Hash
if($workerManifest.BinarySHA256 -cne $workerHash){
 if(-not $mm.PSObject.Properties['OwnedAlgorithmProtection']){throw 'Changed pressure runtime lacks a protection transformation receipt.'}
 $workerProtection=@($mm.OwnedAlgorithmProtection|Where-Object {([string]$_.Path).Replace('\','/') -ceq 'modules/PressureRuntime/Xiongxin.Pressure.Runtime.exe'})
 if($workerProtection.Count -ne 1 -or $workerProtection[0].InputSHA256 -cne $workerManifest.BinarySHA256 -or
    $workerProtection[0].OutputSHA256 -cne $workerHash){throw 'Pressure runtime protection does not preserve the source-to-output chain.'}
 $protectionFile=Assert-V30BatchHash $build $workerProtection[0].ReceiptPath $workerProtection[0].ReceiptSHA256
 $protectionReceipt=Read-V30BatchJson $protectionFile
 if($protectionReceipt.Schema -cne 'HVX.OwnedAlgorithmProtection/1' -or
    $protectionReceipt.InputSHA256 -cne $workerManifest.BinarySHA256 -or
    $protectionReceipt.OutputSHA256 -cne $workerHash){throw 'Pressure protection receipt has a different transformation.'}
}
Assert-V30BatchBoolean $workerManifest.HardwareAccessed $false 'worker build hardware boundary'
Assert-V30BatchBoolean $workerManifest.PhysicalAcceptance $false 'worker build not physical acceptance'
$builtNames=@($workerManifest.Files|ForEach-Object{([string]$_.Path).Replace('\','/')}|Sort-Object)
if((Convert-V30BatchCanonical $compiledNames) -cne (Convert-V30BatchCanonical $builtNames)){throw 'Pressure runtime compilation closure differs from the accepted import.'}
foreach($entry in $workerManifest.Files){$null=Assert-V30BatchHash $workerRoot $entry.Path $entry.SHA256}
$models=@('5581BF7A60798CB8F349A3EE3A59C2F8C121D44AB98D23172453EC856D360D16','A32A19B489CD7FB0CF51F9D027C214E7636A5621D7663384D88925F2A4CEB32B')
foreach($hash in $models){$null=Assert-V30BatchHash $build ('model-payload/'+$hash.ToLowerInvariant()+'.hxm') $hash}
if(@(Get-V30BatchTreeFiles $build 'model-payload').Count -ne 2){throw 'Unexpected model payload.'}
$null=Assert-V30BatchHash $build 'assets/shortcuts/256.ico' '3B9F43CC1860C2C38F444A84D0AEED77A696A5C293D6DD9843A04A735A0F9285'
$null=Assert-V30BatchHash $build 'assets/shortcuts/512.ico' 'BAF373C7BEEF9EEFFFF87109BA903BDD560DC6D1DC8C334EC98348A5A7AB3995'
$sourceFiles=Get-V30BatchInventory $root @('src') @()
# Build.Sources is limited to C# files; unrelated source assets are independently frozen.
Assert-V30BatchSameInventory @($sourceFiles|Where-Object Path -like '*.cs') $bm.Sources 'Compiled shell sources'
$buildEvidenceFiles=@('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config','build-manifest.json','module-manifest.json','research-pins.json',([string]$mm.SourcePressureRuntime.ManifestPath),([string]$mm.SourceResearchRuntime.ManifestPath))
if($null -ne $uiCompilationPath){$buildEvidenceFiles+=[string]$mm.SourceUiRuntime.ManifestPath}
$expectedProtection=@{
 'modules/DataAnalysis/HVX.PdReview.V1.0.dll'='PdReview';'modules/PressureTest/HVX.PdReview.V1.0.dll'='PdReview';
 'modules/DataBrowser/Xiongxin.OWTS.DataBrowser.dll'='DataBrowser';'modules/ReportGenerator/Xiongxin.OWTS.ReportGenerator.dll'='ReportGenerator';
 'modules/CalibrationTest/Xiongxin.OWTS.CalibrationTest.dll'='CalibrationTest';'modules/CalibrationAcquisition/XIONGXIN.Calibration.Verification.exe'='CalibrationAcquisition';
 'modules/PressureTest/Xiongxin.OWTS.PressureTest.dll'='PressureTest';'modules/PressureRuntime/Xiongxin.Pressure.Runtime.exe'='PressureRuntimePure';
 'modules/Task39/Xiongxin.Task39.InputWorker.exe'='Task39InputPure';'Xiongxin.OWTS.Desktop.exe'='HostPure';
 'modules/Task38/HVX.OWTS.Task38.V1.0.38.dll'='Task38LibraryPure'
}
$conversions=@($mm.OwnedAlgorithmProtection)+@($bm.OwnedHostAlgorithmProtection)
if($conversions.Count -ne $expectedProtection.Count){throw 'Required algorithm protection mappings are incomplete.'}
foreach($relative in $expectedProtection.Keys){
  $matching=@($conversions|Where-Object {$_.Path -ceq $relative})
  if($matching.Count -ne 1 -or $matching[0].Profile -cne $expectedProtection[$relative]){throw ('Missing or mismatched protection mapping: '+$relative)}
  $transform=$matching[0]
  $receiptFile=Assert-V30BatchHash $build $transform.ReceiptPath $transform.ReceiptSHA256
  if($receiptFile.StartsWith((Join-Path $build 'modules')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Protection evidence must not be inside the payload.'}
  $receipt=Read-V30BatchJson $receiptFile
  if($receipt.Schema -cne 'HVX.OwnedAlgorithmProtection/1' -or $receipt.Profile -cne $transform.Profile -or $receipt.InputSHA256 -cne $transform.InputSHA256 -or
     $receipt.OutputSHA256 -cne $transform.OutputSHA256){throw 'Algorithm protection receipt identity mismatch.'}
  Assert-V30BatchBoolean $receipt.ExistingMetadataPreserved $true 'protected metadata contract'
  Assert-V30BatchBoolean $receipt.InputUnchanged $true 'original compiled input preserved'
  Assert-V30BatchBoolean $receipt.HardwareAccessed $false 'protection hardware boundary'
  $originalRelative=(Join-Path ([IO.Path]::GetDirectoryName($transform.ReceiptPath)) ('input/'+[IO.Path]::GetFileName($relative))).Replace('\','/')
  $null=Assert-V30BatchHash $build $originalRelative $transform.InputSHA256
  $null=Assert-V30BatchHash $build $transform.Path $transform.OutputSHA256
  $buildEvidenceFiles+=@(([string]$transform.ReceiptPath),$originalRelative)
}
# Freeze the reviewed 439-file research runtime lineage without distributing
# source paths or original build copies. Only the three current native modules
# may differ; every reused third-party byte must still match the pinned source.
$researchReceiptPath=Assert-V30BatchHash $build $mm.SourceResearchRuntime.ManifestPath $mm.SourceResearchRuntime.ManifestSHA256
if($researchReceiptPath.StartsWith((Join-Path $build 'modules')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Research input evidence must not be inside the payload.'}
$researchReceipt=Read-V30BatchJson $researchReceiptPath
$researchInputPath=Get-V30BatchFile $root 'dependencies/ResearchRuntime/frozen-runtime-inputs.json'
$researchInput=Read-V30BatchJson $researchInputPath
$freezeHash='79EC1832D17F15D2FB4698AF950D94ED39B94CE7DB939D2FEB8E5AB8703B68CF'
$pinsHash='E107F81077FEB010AD9E1D66AA4F6BB5511FA28D9BC3B7144F62CC24448ECDE7'
if($researchReceipt.schema -cne 'HVX.ResearchRuntime.FrozenReuse/1' -or $researchReceipt.thirdPartyFilesReused -ne 436 -or
 $researchReceipt.sourceFilesVerified -ne 439 -or $researchReceipt.sourceFreezeSha256 -cne $freezeHash -or $researchReceipt.sourcePinsSha256 -cne $pinsHash -or
 $researchInput.schema -cne 'HVX.ResearchRuntime.FrozenInputs/1' -or $researchInput.approvalStatus -cne 'reviewed-frozen-reuse' -or
 $researchReceipt.inputManifestSha256 -cne (Get-FileHash -LiteralPath $researchInputPath).Hash){throw 'Research runtime reuse evidence is not the reviewed fixed input.'}
Assert-V30BatchBoolean $researchReceipt.currentMachineRuntimeCopied $false 'no mutable machine runtime'
Assert-V30BatchBoolean $researchReceipt.hardwareAccessed $false 'research staging hardware boundary'
Assert-V30BatchBoolean $researchReceipt.confidentialityApproved $false 'input reuse not confidentiality approval'
Assert-V30BatchBoolean $researchReceipt.dependencySecurityCertification $false 'input reuse not security certification'
$researchSource=[IO.Path]::GetFullPath([string]$researchInput.sourceBuildRoot).TrimEnd('\')
if([IO.Path]::GetFullPath([string]$researchReceipt.sourceBuildRoot).TrimEnd('\') -ine $researchSource){throw 'Research runtime source differs from its approved input.'}
$sourceFreeze=Read-V30BatchJson (Assert-V30BatchHash $researchSource 'qa/r27-freeze.json' $freezeHash)
$sourcePinsPath=Assert-V30BatchHash $researchSource 'research-pins.json' $pinsHash
if(@($sourceFreeze.BuildFiles|Where-Object {$_.Path -ceq 'research-pins.json' -and $_.SHA256 -ceq $pinsHash}).Count -ne 1){throw 'Frozen runtime pin file lacks its source freeze binding.'}
$sourcePins=Read-V30BatchJson $sourcePinsPath
$nativeNames=@('q71.cp312-win_amd64.pyd','q72.cp312-win_amd64.pyd','q73.cp312-win_amd64.pyd')
if(@($sourcePins.PSObject.Properties).Count -ne 439 -or @($researchReceipt.newNativeModules.PSObject.Properties).Count -ne 3){throw 'Unexpected source/runtime module count.'}
$sourceRuntimeFiles=@($sourcePins.PSObject.Properties|ForEach-Object {[ordered]@{Path=('modules/r71/'+$_.Name);SHA256=$_.Value}})
Assert-V30BatchSameInventory (Get-V30BatchInventory $researchSource @('modules/r71') @()) $sourceRuntimeFiles 'Original 439-file research runtime'
$expectedRuntimeFiles=@($sourcePins.PSObject.Properties|ForEach-Object {
 $digest=$_.Value
 if($_.Name -cin $nativeNames){$digest=$researchReceipt.newNativeModules.PSObject.Properties[$_.Name].Value}
 [ordered]@{Path=('modules/r71/'+$_.Name);SHA256=$digest}
})
foreach($name in $nativeNames){if($null -eq $sourcePins.PSObject.Properties[$name] -or $null -eq $researchReceipt.newNativeModules.PSObject.Properties[$name]){throw 'A required native research slot is missing.'}}
Assert-V30BatchSameInventory (Get-V30BatchInventory $build @('modules/r71') @()) $expectedRuntimeFiles 'Current frozen/rebuilt research runtime'
$buildInventory=Get-V30BatchInventory $build @('assets','modules','model-payload') $buildEvidenceFiles
$runtimeTestFiles=@($buildInventory|Where-Object { $_.Path.StartsWith('modules/') -or $_.Path -in @('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config') })
$inputEvidenceFiles=@('Build.ps1','Stage-Modules.ps1','App.config','app.manifest','docs/20261008_V30Batch验证版使用说明.txt','docs/20261008_独立加压内测说明.txt','docs/20261010_修正版06使用说明.txt','docs/20261010修正版06安装提示.txt')
if($maintenancePassword){
 $inputEvidenceFiles+=@('docs/20261010_修正版08使用说明.txt','docs/20261010修正版08安装提示.txt','docs/international/electrical-glossary.html','docs/international/electrical-glossary.pdf')
 $null=Assert-V30BatchHash $root 'dependencies/Task41/HV-DAC-Xpert-Setup_1.4.8_LiveStress_Neutral.exe' '45AD475BEA28DAACDA7DFF44CB9C3D2DE9D70DAB06879AA23F85CB2112B91F02'
 $null=Assert-V30BatchHash $root 'docs/international/electrical-glossary.html' 'EF675BE55C03AF6DF6A03290BE98CD2105F9BD9F9CE624053A72CE6C46621DD5'
 $null=Assert-V30BatchHash $root 'docs/international/electrical-glossary.pdf' 'CA7106DB9E9AE69DC1F41780EA757902A7817C3927444A86C7BF69ECAED6EA50'
 $manualManifestPath=Assert-V30BatchHash $root 'dependencies/Documentation/manual4.1-files.json' 'E34629581AF8529BE97AC70B42C4BEDDA5C8B8CF5666DF32CFEA04741D1AD4A4'
 $manualManifest=Read-V30BatchJson $manualManifestPath
 if($manualManifest.Schema -cne 'HVX.BilingualManual.OriginalFiles/1' -or $manualManifest.DocumentVersion -cne '4.1' -or $manualManifest.ReferenceDelivery -cne '20261010_07' -or $manualManifest.FileCount -ne 83 -or @($manualManifest.Files).Count -ne 83){throw 'Unexpected original bilingual manual manifest.'}
 $manualRoot=Assert-V30BatchUnlinked (Join-Path $root 'dependencies/Documentation/manual4.1') $root
 Assert-V30BatchSameInventory (Get-V30BatchInventory $manualRoot @('docs/manual') @('说明书接入说明.txt')) $manualManifest.Files 'Original bilingual manual files'
}
$inputInventory=Get-V30BatchInventory $root @('src','modules-src','dependencies','assets','tools','hybrid-src','installer','tests') $inputEvidenceFiles
if($Mode -eq 'Capture'){
 if(Test-Path -LiteralPath $freezePath){throw 'Never overwrite a freeze receipt.'}
 $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($freezePath)) -Force
 [ordered]@{Schema='HVX.V30Batch.ValidationFreeze/1';BuildRoot=$build;FrozenUtc=[DateTime]::UtcNow.ToString('o');BuildFiles=$buildInventory;InputFiles=$inputInventory;HardwareAccessed=$false}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $freezePath -Encoding UTF8
 Write-Output ('V30Batch_FREEZE='+$freezePath);return
}
$freeze=Read-V30BatchEvidence $freezePath
if($freeze.Schema -cne 'HVX.V30Batch.ValidationFreeze/1' -or $freeze.BuildRoot -ine $build){throw 'Wrong frozen build.'}
Assert-V30BatchBoolean $freeze.HardwareAccessed $false 'freeze hardware boundary'
Assert-V30BatchSameInventory $buildInventory $freeze.BuildFiles 'Frozen runtime'
Assert-V30BatchSameInventory $inputInventory $freeze.InputFiles 'Frozen build/test/installer inputs'
$frozenUtc=[datetime]::Parse($freeze.FrozenUtc).ToUniversalTime()
$deploymentPath=Get-V30BatchProofFile $DeploymentProof
$deployment=Read-V30BatchEvidence $deploymentPath
if($deployment.Status -cne 'PASS' -or $deployment.BuildRoot -ine $build -or [datetime]::Parse($deployment.CheckedUtc).ToUniversalTime() -lt $frozenUtc){throw 'Deployment proof is missing, stale or from a different build.'}
Assert-V30BatchBoolean $deployment.HardwareAccessed $false 'deployment hardware boundary'
foreach($entry in $deployment.Files){$null=Assert-V30BatchHash $build $entry.Path $entry.SHA256}
foreach($entry in @($actualModules)+@($buildInventory|Where-Object { $_.Path.StartsWith('model-payload/') -or $_.Path -eq 'Xiongxin.OWTS.Desktop.exe' })){
 if(@($deployment.Files|Where-Object {([string]$_.Path).Replace('\','/') -ceq $entry.Path -and $_.SHA256 -ieq $entry.SHA256}).Count -eq 0){throw ('Deployment proof omits '+$entry.Path)}
}
$requiredUi=@('AutomaticDisplayRecommendationSmoke','AnalysisReviewedStateSmoke','PressureP04FeedbackSmoke','PressureStatisticsUiSmoke','PressureInlineStartConfirmationSmoke','PressureSidebarRefreshSmoke','DeviceAvailabilityDisplaySmoke','PressureDischargeRangeSmoke','CalibrationPhaseSharingSmoke','PressureTrialIsolationSmoke','PressureCumulativeAnalysisSmoke','CompetitionDefectRecognitionSmoke','CalibrationAcCouplingSmoke','CalibrationPeakFeedbackSmoke','CalibrationOperatorFeedbackSmoke','CalibrationQualityContractSmoke','CalibrationProjectAuditSmoke','V30BatchValidationIsolationSmoke','StandalonePressureShellSmoke','PressureBatchRuntimeSmoke','PressureBatchUiSmoke','HostDialogSafetySmoke','ReportSettingsHeaderLayoutSmoke','PressurePlanLayoutSmoke','PicoHighPassConsistencySmoke','PressureHighPassMetadataSmoke','ReportOperatorLanguageSmoke','ReportSavedConclusionSmoke','PressureFixtureDecisionSmoke','CalibrationNoiseHighPassSmoke','PressureTerminationEvidenceSmoke','PressurePlanOperatorFeedbackSmoke','PressureSidebarLayoutSmoke','PressureMainDpiLayoutSmoke','PressureZeroVoltageAnalysisSmoke','PressureZeroVoltageBackendSmoke','AnalysisZeroVoltageOptInSmoke','PressureRoundReleaseUiSmoke','PressureNextRangeAutomaticSmoke','PressurePlanClickSmoke','LegacyCalibrationBindingSmoke','LegacyCalibrationImportDialogSmoke','CalibrationIncidentOnlySaveSmoke','CalibrationMissingReflectionSelectionSmoke','ManualCursorStableSelectionSmoke','AnalysisOperatorLanguageSmoke','OperatorScreenshotLayoutSmoke','DeviceSetupStatusSmoke','DeviceSetupNavigationSmoke','BrowserLayoutReuseSmoke','BrowserPreparedProjectSmoke','FinePressurePublishedSmoke','CalibrationEmergencyPublishedSmoke')
$requiredHost=@('PressureBatchHostContextSmoke','DeviceParameterProfilesSmoke','SettingsNoRecheckSmoke','ModuleNavigationPerformanceSmoke','ModuleAuditSmoke','Task12ModelRoutingSmoke','AnalysisZeroVoltageSettingsSmoke','PressurePreferencesSettingsSmoke','PressureCalibrationSelectionSmoke','DeviceSetupConnectionTimingSmoke','WindowLayoutMemorySmoke','HomeConfiguredAutoConnectSmoke','HomeDeviceConnectionSmoke','DetectionDeviceSelectionSmoke','DeviceFinePressureSmoke')
if($maintenancePassword){$requiredUi+='HelpPackagedManualSmoke'}
$testedUi=@{};$testedHost=@{};$proofHashes=New-Object 'System.Collections.Generic.List[object]'
foreach($path in $UiProofs){
 $file=Get-V30BatchProofFile $path;$proof=Read-V30BatchEvidence $file
 Assert-V30BatchProof $proof 'HVX.UiRepair.Focused/1' $frozenUtc
 Assert-V30BatchBoolean $proof.UserProjectsWritten $false 'UI project isolation'
 foreach($test in $proof.Tests){$testedUi[$test.Name]=$true;$null=Assert-V30BatchHash ([IO.Path]::GetDirectoryName($file)) ($test.Name+'.exe') $test.TestSHA256}
 $proofHashes.Add([ordered]@{Kind='UI';SHA256=$evidenceSnapshots[$file];Tests=@($proof.Tests.Name)})
}
foreach($path in $HostProofs){
 $file=Get-V30BatchProofFile $path;$proof=Read-V30BatchEvidence $file
 Assert-V30BatchProof $proof 'HVX.PasswordFree.Source/2' $frozenUtc
 Assert-V30BatchBoolean $proof.UserSettingsWritten $false 'host settings isolation'
 Assert-V30BatchBoolean $proof.SourceUnchanged $true 'host source stability'
 Assert-V30BatchSameInventory $bm.Sources $proof.SourceInventory 'Host tested sources'
 foreach($test in $proof.Tests){$testedHost[$test.Name]=$true;$null=Assert-V30BatchHash ([IO.Path]::GetDirectoryName($file)) ($test.Name+'.exe') $test.TestSHA256}
 $proofHashes.Add([ordered]@{Kind='Host';SHA256=$evidenceSnapshots[$file];Tests=@($proof.Tests.Name)})
}
foreach($name in $requiredUi){if(-not $testedUi.ContainsKey($name)){throw ('Required UI test missing: '+$name)}}
foreach($name in $requiredHost){if(-not $testedHost.ContainsKey($name)){throw ('Required host test missing: '+$name)}}
$maintenanceProofHash=$null
if($Mode -eq 'Publish' -and (-not $maintenancePassword -or [string]::IsNullOrWhiteSpace($MaintenancePasswordProof))){throw 'Every new publish requires actual guarded-program maintenance evidence.'}
if($maintenancePassword){
 if([string]::IsNullOrWhiteSpace($MaintenancePasswordProof)){throw 'Maintenance build requires actual protected-program access tests.'}
 $file=Get-V30BatchProofFile $MaintenancePasswordProof;$proof=Read-V30BatchEvidence $file
 Assert-V30BatchProof $proof 'HVX.MaintenanceAccess.Published/1' $frozenUtc
 if($proof.Status -cne 'PASS' -or $proof.ExecutableSHA256 -cne $bm.ExecutableSha256 -or $proof.FreezeSHA256 -cne $evidenceSnapshots[$freezePath]){throw 'Maintenance proof is not bound to the current program/freeze.'}
 foreach($field in @('SourceUnchanged','CancellationDenied','InvalidPasswordDenied','DirectEntryGuarded','RoutineEntryPasswordFree')){Assert-V30BatchBoolean $proof.$field $true ('maintenance '+$field)}
 foreach($field in @('ProductExecuted','UserSettingsWritten','CredentialMaterialIncluded')){Assert-V30BatchBoolean $proof.$field $false ('maintenance '+$field)}
 Assert-V30BatchSameInventory $bm.Sources $proof.SourceInventory 'Maintenance tested host sources'
 if($null -eq $proof.CompileExit -or $proof.CompileExit -ne 0 -or $null -eq $proof.ExitCode -or $proof.ExitCode -ne 0 -or $null -eq $proof.Checks -or $proof.Checks -le 0){throw 'Maintenance test result is incomplete or failed.'}
 if(@($proof.Tests).Count -ne 1 -or $proof.Tests[0].Name -cne 'MaintenanceAdvancedAccessPublishedSmoke'){throw 'Unexpected maintenance test closure.'}
 foreach($test in $proof.Tests){$null=Assert-V30BatchHash ([IO.Path]::GetDirectoryName($file)) ($test.Name+'.exe') $test.TestSHA256}
 $maintenanceProofHash=$evidenceSnapshots[$file]
 $proofHashes.Add([ordered]@{Kind='Maintenance';SHA256=$maintenanceProofHash;Tests=@($proof.Tests.Name)})
}elseif(-not[string]::IsNullOrWhiteSpace($MaintenancePasswordProof)){throw 'A maintenance proof cannot relabel a password-free build.'}
foreach($file in $evidenceSnapshots.Keys){if((Get-FileHash -LiteralPath $file).Hash -cne $evidenceSnapshots[$file]){throw 'Validated proof changed during checking.'}}
if($Mode -eq 'Validate'){Write-Output ('V30Batch_VALIDATION_READY; CONFIDENTIALITY_NOT_APPROVED_BY_THIS_COMMAND; UI='+$testedUi.Count+'; HOST='+$testedHost.Count+'; EXE='+$bm.ExecutableSha256);return}
. (Join-Path $PSScriptRoot 'AlgorithmConfidentialityGate.ps1')
$confidentialityInventory=@($buildInventory|Where-Object { $_.Path.StartsWith('modules/') -or $_.Path.StartsWith('model-payload/') -or $_.Path -eq 'Xiongxin.OWTS.Desktop.exe' })
$confidentiality=Assert-AlgorithmConfidentialityReview -BuildRoot $build -ReviewPath $ConfidentialityProof -RuntimeInventory $confidentialityInventory
$proofHashes.Add([ordered]@{Kind='Confidentiality';SHA256=$confidentiality.SHA256})
$outputPattern=if($pressureStandalone){'^HV-DAC_PressureTest_1\.0\.30\.9_[0-9]{8}_[0-9]{2}$'}else{'^HV-DAC_Xpert_Validation_V30Batch_1\.0\.30\.9_[0-9]{8}_[0-9]{2}$'}
if($OutputName -notmatch $outputPattern){throw 'Use the explicit delivery name for this exact profile.'}
$delivery=Assert-V30BatchUnlinked (Join-Path $root ('deliverables/'+$OutputName)) $root
if(Test-Path -LiteralPath $delivery){throw 'Never overwrite an existing delivery.'}
$runtime=Get-V30BatchFile $root 'build/prerequisites/NDP48-x86-x64-AllOS-ENU.exe'
$signature=Get-AuthenticodeSignature -LiteralPath $runtime
if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation' -or (Get-FileHash -LiteralPath $runtime).Hash -cne '0A3A390C47E639D0F7FC65B21195FEE6B7F65B066F80F70C60FAB191D14B7E40'){throw 'Invalid offline .NET prerequisite.'}
$candidate=Join-Path $root ('build/v30batch-delivery-candidate-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $candidate
foreach($name in @('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config','module-manifest.json')){Copy-Item -LiteralPath (Get-V30BatchFile $build $name) -Destination (Join-Path $candidate $name)}
foreach($name in @('assets','model-payload')){Copy-Item -LiteralPath (Join-Path $build $name) -Destination (Join-Path $candidate $name) -Recurse}
Copy-Item -LiteralPath (Get-V30BatchFile $root $(if($pressureStandalone){'docs/20261008_独立加压内测说明.txt'}elseif($maintenancePassword){'docs/20261010_修正版08使用说明.txt'}else{'docs/20261010_修正版06使用说明.txt'})) -Destination (Join-Path $candidate '使用说明.txt')
$null=New-Item -ItemType Directory -Path (Join-Path $candidate 'licenses')
foreach($entry in @(@('modules/DataAnalysis/Task12/ONNX_RUNTIME_LICENSE.txt','ONNX_RUNTIME_LICENSE.txt'),@('modules/r71/LICENSE.txt','PYTHON_LICENSE.txt'),@('modules/r71/licenses/numpy-METADATA.txt','NUMPY_LICENSE_METADATA.txt'))){Copy-Item -LiteralPath (Get-V30BatchFile $build $entry[0]) -Destination (Join-Path $candidate ('licenses/'+$entry[1]))}
Copy-Item -LiteralPath (Get-V30BatchFile $root 'dependencies/Protection/ConfuserEx-LICENSE.txt') -Destination (Join-Path $candidate 'licenses/ConfuserEx-LICENSE.txt')
$null=New-Item -ItemType Directory -Path (Join-Path $candidate 'prerequisites')
Copy-Item -LiteralPath $runtime -Destination (Join-Path $candidate 'prerequisites/NDP48-x86-x64-AllOS-ENU.exe')
if($maintenancePassword){
 $null=New-Item -ItemType Directory -Path (Join-Path $candidate 'docs/international') -Force
 foreach($name in @('electrical-glossary.html','electrical-glossary.pdf')){Copy-Item -LiteralPath (Get-V30BatchFile $root ('docs/international/'+$name)) -Destination (Join-Path $candidate ('docs/international/'+$name))}
 foreach($entry in $manualManifest.Files){
  $target=if($entry.Path -ceq '说明书接入说明.txt'){'docs/说明书接入说明_4.1.txt'}else{[string]$entry.Path}
  $destination=Assert-V30BatchUnlinked (Join-Path $candidate $target) $candidate
  $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force
  Copy-Item -LiteralPath (Assert-V30BatchHash $manualRoot $entry.Path $entry.SHA256) -Destination $destination
  $null=Assert-V30BatchHash $candidate $target $entry.SHA256
 }
 Copy-Item -LiteralPath (Get-V30BatchFile $root 'dependencies/Task41/HV-DAC-Xpert-Setup_1.4.8_LiveStress_Neutral.exe') -Destination (Join-Path $candidate 'prerequisites/HV-DAC-Xpert-Setup_1.4.8_LiveStress_Neutral.exe')
 $null=Assert-V30BatchHash $candidate 'prerequisites/HV-DAC-Xpert-Setup_1.4.8_LiveStress_Neutral.exe' '45AD475BEA28DAACDA7DFF44CB9C3D2DE9D70DAB06879AA23F85CB2112B91F02'
 foreach($name in @('electrical-glossary.html','electrical-glossary.pdf')){$null=Assert-V30BatchHash $candidate ('docs/international/'+$name) ((Get-FileHash -LiteralPath (Get-V30BatchFile $root ('docs/international/'+$name))).Hash)}
}
foreach($entry in $buildInventory|Where-Object { $_.Path.StartsWith('assets/') -or $_.Path.StartsWith('model-payload/') -or $_.Path -in @('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config','module-manifest.json') }){$null=Assert-V30BatchHash $candidate $entry.Path $entry.SHA256}
# Recheck after copying. A concurrent edit cannot be masked by old passing receipts.
$confidentialityAfter=Assert-AlgorithmConfidentialityReview -BuildRoot $build -ReviewPath $ConfidentialityProof -RuntimeInventory $confidentialityInventory
if($confidentialityAfter.SHA256 -cne $confidentiality.SHA256){throw 'Confidentiality approval changed during copying.'}
Assert-V30BatchSameInventory (Get-V30BatchInventory $build @('assets','modules','model-payload') $buildEvidenceFiles) $freeze.BuildFiles 'Frozen runtime after copy'
Assert-V30BatchSameInventory (Get-V30BatchInventory $root @('src','modules-src','dependencies','assets','tools','hybrid-src','installer','tests') $inputEvidenceFiles) $freeze.InputFiles 'Frozen inputs after copy'
foreach($file in $evidenceSnapshots.Keys){if((Get-FileHash -LiteralPath $file).Hash -cne $evidenceSnapshots[$file]){throw 'Validated proof changed during copying.'}}
[ordered]@{Schema='HVX.V30Batch.ValidationRelease/1';Version='1.0.30.9';Profile=$expectedProfile;PressureStandalone=[bool]$pressureStandalone;GeneratedUtc=[DateTime]::UtcNow.ToString('o');ExecutableSHA256=$bm.ExecutableSha256;FreezeSHA256=$evidenceSnapshots[$freezePath];DeploymentProofSHA256=$evidenceSnapshots[$deploymentPath];Proofs=$proofHashes.ToArray();HardwareAccessed=$false;PhysicalAcceptance=$false;FullRegression=$false;SafetyMaterialsApproved=$false;MaintenancePasswordIncluded=$false;ApplicationSignature=[string](Get-AuthenticodeSignature -LiteralPath $exe).Status;Algorithms='AB 3.0.22 pinned originals with reviewed local measured-phase and validated calibration-packet identity overlays; C 0.3.0; D 0.1.1';Pressure='V1.0.30.9 local reviewed full-plan continuation and bounded same-voltage overflow upscale; explicit start, real release/save guards, no automatic fault recovery';UpstreamUserReportedAcceptedVersion='1.0.30.9';FineRampReferenceUserReportedAcceptedVersion='1.0.30.13';LocalAdaptationSHA256=$batchSource.LocalAdaptationSHA256;ExactUpstreamSourceCount=$batchSource.ExactUpstreamSourceCount;LocalChanges='Local bounded progress journal; true reached progress; CPU display preparation and FIR interior fast path; validated CAL packet identity overlays, operator-confirmed joints, display-only smoothing/ranges, 3D event isolation, wheel/language/DPI/minimize fixes and read-only built-in device manager. Native protections, timing and cooldown retained. Local adaptations are not upstream or physical acceptance; see exact source and compatibility receipts';WholeSuitePhysicalAcceptance=$false;Task41CompleteSuiteIncluded=$false;ModelsOutsideApplication=$true;DriversInstalled=$false}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $candidate 'release-validation.json') -Encoding UTF8
if($maintenancePassword){
 # Only a verified marker AND the negative/direct-entry tests above may turn on
 # this release declaration. Old password-free validation deliveries stay false.
 $releasePath=Join-Path $candidate 'release-validation.json'
 $release=Read-V30BatchJson $releasePath
 $release.MaintenancePasswordIncluded=$true
 if($Mode -eq 'Publish'){
  $release|Add-Member -MemberType NoteProperty -Name MaintenanceCompilation -Value ([ordered]@{Schema='HVX.ReleaseMaintenanceCompilation/1';Guard='MAINTENANCE_PASSWORD_GUARD';UiCompilationSHA256=[string]$mm.SourceUiRuntime.ManifestSHA256;HostCompilationSHA256=[string]$bm.HostCompilation.OutputSHA256;ProtectedHostSHA256=[string]$bm.ExecutableSha256;UiModuleCount=@($uiCompilation.Modules).Count})
 }
 $release.Task41CompleteSuiteIncluded=$true
 $release|Add-Member -MemberType NoteProperty -Name ManualVersion -Value '4.1'
 $release|Add-Member -MemberType NoteProperty -Name ManualReferenceDelivery -Value '20261010_07'
 $release|Add-Member -MemberType NoteProperty -Name ManualManifestSHA256 -Value 'E34629581AF8529BE97AC70B42C4BEDDA5C8B8CF5666DF32CFEA04741D1AD4A4'
 $release|Add-Member -MemberType NoteProperty -Name Task41InstallerSHA256 -Value '45AD475BEA28DAACDA7DFF44CB9C3D2DE9D70DAB06879AA23F85CB2112B91F02'
 $release|Add-Member -MemberType NoteProperty -Name MaintenancePasswordProofSHA256 -Value $maintenanceProofHash
 $release|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $releasePath -Encoding UTF8
}
$sums=@(Get-ChildItem -LiteralPath $candidate -Recurse -File|Sort-Object { $_.FullName }|ForEach-Object{(Get-FileHash -LiteralPath $_.FullName).Hash+'  '+$_.FullName.Substring($candidate.Length+1).Replace('\','/')})
[IO.File]::WriteAllLines((Join-Path $candidate 'SHA256SUMS.txt'),$sums,(New-Object Text.UTF8Encoding($false)))
$null=New-Item -ItemType Directory -Path (Join-Path $root 'deliverables') -Force
[IO.Directory]::Move($candidate,$delivery)
Write-Output ('V30Batch_VALIDATION_PAYLOAD='+$delivery)
