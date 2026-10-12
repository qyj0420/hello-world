param(
 [ValidateSet('Capture','Validate','Publish')][string]$Mode='Validate',
 [Parameter(Mandatory=$true)][string]$BuildRoot,
 [Parameter(Mandatory=$true)][string]$FreezeEvidence,
 [string]$DeploymentProof='',
 [string[]]$UiProofs=@(),
 [string[]]$HostProofs=@(),
 [string]$ConfidentialityProof='',
 [string]$OutputName='HV-DAC_Xpert_Validation_V30_1.1.9.9_20261008_01'
)
# Explicitly limited operator validation lane. This never invokes an installer,
# product program or device, and never edits the formal Publish-Verification119 gate.
$ErrorActionPreference='Stop'
if($Mode -eq 'Publish'){throw 'New historical V30 deliveries are disabled; use the protected maintenance V30 batch publisher. Capture/Validate remain available for historical audit.'}
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$build=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
$evidenceSnapshots=@{}
function Assert-V30Unlinked([string]$Path,[string]$Boundary) {
 $full=[IO.Path]::GetFullPath($Path);$limit=[IO.Path]::GetFullPath($Boundary).TrimEnd('\')
 if($full -ine $limit -and -not $full.StartsWith($limit+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Path escapes the approved root.'}
 for($walk=$full;$walk.Length -ge $limit.Length;$walk=[IO.Path]::GetDirectoryName($walk)){
  if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw ('Linked path is prohibited: '+$walk)}
  if($walk -ieq $limit){break}
 }
 return $full
}
function Get-V30File([string]$Base,[string]$Relative) {
 if([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')){throw 'Invalid relative file name.'}
 $file=Assert-V30Unlinked (Join-Path $Base $Relative) $Base
 if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw ('Required file missing: '+$Relative)}
 return $file
}
function Get-V30TreeFiles([string]$Base,[string]$Relative) {
 $directory=Assert-V30Unlinked (Join-Path $Base $Relative) $Base
 if(-not(Test-Path -LiteralPath $directory -PathType Container)){throw ('Required directory missing: '+$Relative)}
 $pending=New-Object 'System.Collections.Generic.Stack[string]';$pending.Push($directory)
 while($pending.Count){
  foreach($entry in Get-ChildItem -LiteralPath $pending.Pop() -Force){
   $null=Assert-V30Unlinked $entry.FullName $Base
   if($entry.PSIsContainer){$pending.Push($entry.FullName)}else{$entry.FullName}
  }
 }
}
function Get-V30Inventory([string]$Base,[string[]]$Trees,[string[]]$Files) {
 $paths=@($Files|ForEach-Object{Get-V30File $Base $_})
 foreach($tree in $Trees){$paths+=@(Get-V30TreeFiles $Base $tree)}
 @($paths|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_.Substring($Base.TrimEnd('\').Length+1).Replace('\','/');SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})
}
function Assert-V30Hash([string]$Base,[string]$Relative,[string]$Expected) {
 if($Expected -notmatch '^[A-Fa-f0-9]{64}$'){throw ('Invalid hash for '+$Relative)}
 $file=Get-V30File $Base $Relative
 if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $Expected){throw ('File changed: '+$Relative)}
 return $file
}
function Convert-V30Canonical($Value){ConvertTo-Json -InputObject @($Value) -Depth 20 -Compress}
function Assert-V30SameInventory($Actual,$Expected,[string]$Description) {
 $a=@($Actual|ForEach-Object{[ordered]@{Path=([string]$_.Path).Replace('\','/');SHA256=([string]$_.SHA256).ToUpperInvariant()}}|Sort-Object Path)
 $e=@($Expected|ForEach-Object{[ordered]@{Path=([string]$_.Path).Replace('\','/');SHA256=([string]$_.SHA256).ToUpperInvariant()}}|Sort-Object Path)
 if((Convert-V30Canonical $a) -cne (Convert-V30Canonical $e)){throw ($Description+' changed or incomplete.')}
}
function Read-V30Json([string]$Path) {
 if((Get-Item -LiteralPath $Path).Length -gt 32MB){throw 'Evidence exceeds the size limit.'}
 Get-Content -LiteralPath $Path -Raw -Encoding UTF8|ConvertFrom-Json
}
function Get-V30ProofFile([string]$Path) {
 $full=Assert-V30Unlinked ([IO.Path]::GetFullPath($Path)) (Join-Path $build 'qa')
 if(-not(Test-Path -LiteralPath $full -PathType Leaf)){throw 'Evidence file is missing.'}
 return $full
}
function Read-V30Evidence([string]$Path) {
 $file=Get-V30ProofFile $Path
 if((Get-Item -LiteralPath $file).Length -gt 32MB){throw 'Evidence exceeds the size limit.'}
 $bytes=[IO.File]::ReadAllBytes($file);$algorithm=[Security.Cryptography.SHA256]::Create()
 try{$sha=[BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-','')}finally{$algorithm.Dispose()}
 $script:evidenceSnapshots[$file]=$sha
 [Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF)|ConvertFrom-Json
}
function Assert-V30Boolean($Value,[bool]$Expected,[string]$Description) {
 if($Value -isnot [bool] -or $Value -ne $Expected){throw ('Invalid '+$Description)}
}
function Assert-V30TestResult($Test) {
 if($Test.Name -notmatch '^[A-Za-z0-9]+$' -or $null -eq $Test.CompileExit -or $Test.CompileExit -ne 0 -or $null -eq $Test.ExitCode -or $Test.ExitCode -ne 0){throw 'Failed or malformed test result.'}
 Assert-V30Boolean $Test.TimedOut $false 'test timeout'
 Assert-V30Boolean $Test.ProcessExitConfirmed $true 'test process completion'
 $null=Assert-V30Hash $root ('tests/'+$Test.Name+'.cs') $Test.SourceSHA256
}
function Assert-V30Proof($Proof,[string]$ExpectedSchema,[datetime]$FrozenUtc) {
 if($Proof.Schema -cne $ExpectedSchema -or [IO.Path]::GetFullPath([string]$Proof.BuildRoot).TrimEnd('\') -ine $build){throw 'Evidence is from a different build or schema.'}
 Assert-V30Boolean $Proof.HardwareAccessed $false 'hardware-free evidence'
 Assert-V30Boolean $Proof.RuntimeUnchanged $true 'unchanged tested runtime'
 if([datetime]::Parse($Proof.StartedUtc).ToUniversalTime() -lt $FrozenUtc -or [datetime]::Parse($Proof.CompletedUtc).ToUniversalTime() -lt [datetime]::Parse($Proof.StartedUtc).ToUniversalTime()){throw 'Tests must run after this build freeze.'}
 Assert-V30SameInventory $runtimeTestFiles $Proof.Runtime 'Test runtime inventory'
 if(@($Proof.Tests).Count -eq 0 -or @($Proof.Tests|Group-Object Name|Where-Object Count -ne 1).Count){throw 'Missing or duplicate tests in one proof.'}
 foreach($test in $Proof.Tests){Assert-V30TestResult $test}
}

$null=Assert-V30Unlinked $build (Join-Path $root 'build')
if($build -ieq (Join-Path $root 'build')){throw 'Select a dedicated build directory.'}
$freezePath=Assert-V30Unlinked ([IO.Path]::GetFullPath($FreezeEvidence)) (Join-Path $build 'qa')
$bm=Read-V30Json (Get-V30File $build 'build-manifest.json')
$mm=Read-V30Json (Get-V30File $build 'module-manifest.json')
Assert-V30Boolean $bm.V30ValidationProfile $true 'V30 validation profile'
Assert-V30Boolean $bm.R27ValidationProfile $false 'R27 profile exclusion'
Assert-V30Boolean $bm.FrozenInternalProfile $false 'legacy profile exclusion'
Assert-V30Boolean $bm.HybridArchitecture.Included $true 'hybrid runtime'
if($bm.ConfigurationProfile -cne 'Validation-V30-20261007' -or $bm.FileVersion -cne '1.1.9.9'){throw 'Unexpected validation version/profile.'}
$exe=Assert-V30Hash $build 'Xiongxin.OWTS.Desktop.exe' $bm.ExecutableSha256
if([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -ne '1.1.9.9' -or $null -eq $bm.EmbeddedModules -or $bm.EmbeddedModules.ResourceName -cne 'HVX.Modules.Index.v1'){throw 'Wrong executable version or missing embedded modules.'}
if($mm.Schema -cne 'XIONGXIN.UnifiedDesktop.Modules.v2' -or $mm.SourceLocationAlgorithm.Candidate -cne 'AB_INTERNAL_20261006_R02' -or $mm.SourceLocationAlgorithm.FileVersion -cne '3.0.22.0' -or $mm.SourceLocationAlgorithm.CandidateManifestSha256 -cne '953203C6268C4B55F874E86C1D4D6019648722F406F98CABA6DA17BB88DD0562'){throw 'Unexpected location algorithm contract.'}
$actualModules=Get-V30Inventory $build @('modules') @()
Assert-V30SameInventory $actualModules $mm.Modules 'Module manifest'
$worker=Get-V30File $build 'modules/PressureRuntime/Xiongxin.Pressure.Runtime.exe'
if([Diagnostics.FileVersionInfo]::GetVersionInfo($worker).FileVersion -ne '1.0.30.0'){throw 'Expected frozen V30 pressure runtime.'}
$upstream=Read-V30Json (Get-V30File $root 'modules-src/FactoryWorker/v30-integration-provenance.json')
if($upstream.Schema -cne 'HVX.Pressure.V30.Import/1' -or $upstream.SourceVersion -cne '1.0.30.0' -or $upstream.SourceExecutableSHA256 -cne 'DD1481226E15AA47611D6DB798B14A7908483E13718406A387B4A90041880114'){throw 'V30 source provenance mismatch.'}
Assert-V30Boolean $upstream.HardwareAccessed $false 'upstream import hardware boundary'
Assert-V30Boolean $upstream.PhysicalAcceptance $false 'integration physical acceptance not inferred'
$workerRoot=Join-Path $root 'modules-src/FactoryWorker'
$upstreamManifestPath=Assert-V30Hash $workerRoot 'v30-upstream-manifest.json' 'B0E86C0C3B87127511E95B6AAC05CC7C898B90D937FEE0BF93056E49A9D0BE3A'
$upstreamManifest=Read-V30Json $upstreamManifestPath
if($upstreamManifest.FileVersion -cne '1.0.30.0' -or $upstreamManifest.ProductVersion -cne 'V1.0.30'){throw 'Wrong upstream release manifest.'}
$correspondence=Read-V30Json (Get-V30File $workerRoot 'v30-source-correspondence.json')
if($correspondence.Schema -cne 'HVX.Factory.SourceCorrespondence/1' -or $correspondence.Version -cne '1.0.30.0' -or
 $correspondence.ReleasedBinarySHA256 -cne $upstream.SourceExecutableSHA256 -or $correspondence.ManifestSHA256 -cne (Get-FileHash -LiteralPath $upstreamManifestPath).Hash){throw 'Source-to-release correspondence mismatch.'}
Assert-V30Boolean $correspondence.SourceUnchanged $true 'upstream source stability during correspondence check'
Assert-V30Boolean $correspondence.CodeAndResourcesByteIdentical $true 'upstream code and resource equivalence'
Assert-V30Boolean $correspondence.HardwareAccessed $false 'upstream correspondence hardware boundary'
Assert-V30Boolean $correspondence.PhysicalAcceptance $false 'upstream correspondence not physical acceptance'
if(@($correspondence.OnlyAllowedDifferences).Count -ne 2 -or
 @($correspondence.OnlyAllowedDifferences|Where-Object { $_ -cnotin @('PE TimeDateStamp (4 bytes)','CLR ModuleVersionId (16 bytes)') }).Count){throw 'Unapproved source correspondence exclusions.'}
$vendorFiles=@($upstream.Files)
if($vendorFiles.Count -eq 0 -or @($vendorFiles|Group-Object Path|Where-Object Count -ne 1).Count){throw 'Missing or duplicate imported source entries.'}
foreach($entry in $vendorFiles){
 $null=Assert-V30Hash $workerRoot $entry.Path $entry.IntegratedSHA256
 $match=@($correspondence.SourceFiles|Where-Object { ([string]$_.Path).Replace('\','/') -ceq ([string]$entry.Path).Replace('\','/') -and $_.SHA256 -ceq $entry.FrozenUpstreamSHA256 })
 if($match.Count -ne 1){throw ('Imported source has no frozen upstream evidence: '+$entry.Path)}
 if($entry.ExactUpstream -isnot [bool]){throw 'Malformed upstream adaptation flag.'}
 if($entry.ExactUpstream -and $entry.FrozenUpstreamSHA256 -cne $entry.IntegratedSHA256){throw 'An adapted source was labelled exact upstream.'}
}
$compiledNames=@($upstream.CompiledFiles|ForEach-Object{([string]$_).Replace('\','/')}|Sort-Object)
if($compiledNames.Count -eq 0 -or @($compiledNames|Group-Object|Where-Object Count -ne 1).Count){throw 'Missing or duplicate explicit pressure compilation set.'}
$workerManifest=Read-V30Json (Get-V30File $build 'modules/PressureRuntime/source-manifest.json')
if($workerManifest.UpstreamBinarySHA256 -cne $upstream.SourceExecutableSHA256 -or $workerManifest.BinarySHA256 -cne (Get-FileHash -LiteralPath $worker).Hash){throw 'Pressure runtime was not built from the selected upstream contract.'}
$builtNames=@($workerManifest.Files|ForEach-Object{([string]$_.Path).Replace('\','/')}|Sort-Object)
if((Convert-V30Canonical $compiledNames) -cne (Convert-V30Canonical $builtNames)){throw 'Pressure runtime compilation closure differs from the selected import.'}
foreach($entry in $workerManifest.Files){$null=Assert-V30Hash $workerRoot $entry.Path $entry.SHA256}
$models=@('5581BF7A60798CB8F349A3EE3A59C2F8C121D44AB98D23172453EC856D360D16','A32A19B489CD7FB0CF51F9D027C214E7636A5621D7663384D88925F2A4CEB32B')
foreach($hash in $models){$null=Assert-V30Hash $build ('model-payload/'+$hash.ToLowerInvariant()+'.hxm') $hash}
if(@(Get-V30TreeFiles $build 'model-payload').Count -ne 2){throw 'Unexpected model payload.'}
$null=Assert-V30Hash $build 'assets/shortcuts/256.ico' '3B9F43CC1860C2C38F444A84D0AEED77A696A5C293D6DD9843A04A735A0F9285'
$null=Assert-V30Hash $build 'assets/shortcuts/512.ico' 'BAF373C7BEEF9EEFFFF87109BA903BDD560DC6D1DC8C334EC98348A5A7AB3995'
$sourceFiles=Get-V30Inventory $root @('src') @()
# Build.Sources is limited to C# files; unrelated source assets are independently frozen.
Assert-V30SameInventory @($sourceFiles|Where-Object Path -like '*.cs') $bm.Sources 'Compiled shell sources'
$buildInventory=Get-V30Inventory $build @('assets','modules','model-payload') @('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config','build-manifest.json','module-manifest.json','research-pins.json')
$runtimeTestFiles=@($buildInventory|Where-Object { $_.Path.StartsWith('modules/') -or $_.Path -in @('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config') })
$inputInventory=Get-V30Inventory $root @('src','modules-src','dependencies','assets','tools','hybrid-src','installer','tests') @('Build.ps1','Stage-Modules.ps1','App.config','app.manifest','docs/20261007_V30验证版使用说明.txt')
if($Mode -eq 'Capture'){
 if(Test-Path -LiteralPath $freezePath){throw 'Never overwrite a freeze receipt.'}
 $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($freezePath)) -Force
 [ordered]@{Schema='HVX.V30.ValidationFreeze/1';BuildRoot=$build;FrozenUtc=[DateTime]::UtcNow.ToString('o');BuildFiles=$buildInventory;InputFiles=$inputInventory;HardwareAccessed=$false}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $freezePath -Encoding UTF8
 Write-Output ('V30_FREEZE='+$freezePath);return
}
$freeze=Read-V30Evidence $freezePath
if($freeze.Schema -cne 'HVX.V30.ValidationFreeze/1' -or $freeze.BuildRoot -ine $build){throw 'Wrong frozen build.'}
Assert-V30Boolean $freeze.HardwareAccessed $false 'freeze hardware boundary'
Assert-V30SameInventory $buildInventory $freeze.BuildFiles 'Frozen runtime'
Assert-V30SameInventory $inputInventory $freeze.InputFiles 'Frozen build/test/installer inputs'
$frozenUtc=[datetime]::Parse($freeze.FrozenUtc).ToUniversalTime()
$deploymentPath=Get-V30ProofFile $DeploymentProof
$deployment=Read-V30Evidence $deploymentPath
if($deployment.Status -cne 'PASS' -or $deployment.BuildRoot -ine $build -or [datetime]::Parse($deployment.CheckedUtc).ToUniversalTime() -lt $frozenUtc){throw 'Deployment proof is missing, stale or from a different build.'}
Assert-V30Boolean $deployment.HardwareAccessed $false 'deployment hardware boundary'
foreach($entry in $deployment.Files){$null=Assert-V30Hash $build $entry.Path $entry.SHA256}
foreach($entry in @($actualModules)+@($buildInventory|Where-Object { $_.Path.StartsWith('model-payload/') -or $_.Path -eq 'Xiongxin.OWTS.Desktop.exe' })){
 if(@($deployment.Files|Where-Object {([string]$_.Path).Replace('\','/') -ceq $entry.Path -and $_.SHA256 -ieq $entry.SHA256}).Count -eq 0){throw ('Deployment proof omits '+$entry.Path)}
}
$requiredUi=@('AutomaticDisplayRecommendationSmoke','AnalysisReviewedStateSmoke','PressureP04FeedbackSmoke','PressureStatisticsUiSmoke','PressureInlineStartConfirmationSmoke','V30PressureBridgeSmoke','PressureSidebarRefreshSmoke','DeviceAvailabilityDisplaySmoke','PressureDischargeRangeSmoke','CalibrationPhaseSharingSmoke','PressureTrialIsolationSmoke','PressureCumulativeAnalysisSmoke','CompetitionDefectRecognitionSmoke','CalibrationAcCouplingSmoke','CalibrationPeakFeedbackSmoke','CalibrationOperatorFeedbackSmoke','CalibrationQualityContractSmoke','CalibrationProjectAuditSmoke','V30ValidationIsolationSmoke','HostDialogSafetySmoke','ReportSettingsHeaderLayoutSmoke','PressurePlanLayoutSmoke','PicoHighPassConsistencySmoke','PressureHighPassMetadataSmoke','ReportOperatorLanguageSmoke','ReportSavedConclusionSmoke','PressureFixtureDecisionSmoke','CalibrationNoiseHighPassSmoke','PressureTerminationEvidenceSmoke','PressurePlanOperatorFeedbackSmoke','PressureSidebarLayoutSmoke','PressureMainDpiLayoutSmoke','PressureZeroVoltageAnalysisSmoke','PressureZeroVoltageBackendSmoke','AnalysisZeroVoltageOptInSmoke','PressureRoundReleaseUiSmoke','PressureNextRangeAutomaticSmoke','PressurePlanClickSmoke','LegacyCalibrationBindingSmoke','LegacyCalibrationImportDialogSmoke','CalibrationIncidentOnlySaveSmoke','CalibrationMissingReflectionSelectionSmoke','ManualCursorStableSelectionSmoke','AnalysisOperatorLanguageSmoke','OperatorScreenshotLayoutSmoke','DeviceSetupStatusSmoke','DeviceSetupNavigationSmoke')
$requiredHost=@('DeviceParameterProfilesSmoke','SettingsNoRecheckSmoke','ModuleNavigationPerformanceSmoke','ModuleAuditSmoke','Task12ModelRoutingSmoke','AnalysisZeroVoltageSettingsSmoke','PressurePreferencesSettingsSmoke','PressureCalibrationSelectionSmoke','DeviceSetupConnectionTimingSmoke')
$testedUi=@{};$testedHost=@{};$proofHashes=New-Object 'System.Collections.Generic.List[object]'
foreach($path in $UiProofs){
 $file=Get-V30ProofFile $path;$proof=Read-V30Evidence $file
 Assert-V30Proof $proof 'HVX.UiRepair.Focused/1' $frozenUtc
 Assert-V30Boolean $proof.UserProjectsWritten $false 'UI project isolation'
 foreach($test in $proof.Tests){$testedUi[$test.Name]=$true;$null=Assert-V30Hash ([IO.Path]::GetDirectoryName($file)) ($test.Name+'.exe') $test.TestSHA256}
 $proofHashes.Add([ordered]@{Kind='UI';SHA256=$evidenceSnapshots[$file];Tests=@($proof.Tests.Name)})
}
foreach($path in $HostProofs){
 $file=Get-V30ProofFile $path;$proof=Read-V30Evidence $file
 Assert-V30Proof $proof 'HVX.PasswordFree.Source/2' $frozenUtc
 Assert-V30Boolean $proof.UserSettingsWritten $false 'host settings isolation'
 Assert-V30Boolean $proof.SourceUnchanged $true 'host source stability'
 Assert-V30SameInventory $bm.Sources $proof.SourceInventory 'Host tested sources'
 foreach($test in $proof.Tests){$testedHost[$test.Name]=$true;$null=Assert-V30Hash ([IO.Path]::GetDirectoryName($file)) ($test.Name+'.exe') $test.TestSHA256}
 $proofHashes.Add([ordered]@{Kind='Host';SHA256=$evidenceSnapshots[$file];Tests=@($proof.Tests.Name)})
}
foreach($name in $requiredUi){if(-not $testedUi.ContainsKey($name)){throw ('Required UI test missing: '+$name)}}
foreach($name in $requiredHost){if(-not $testedHost.ContainsKey($name)){throw ('Required host test missing: '+$name)}}
foreach($file in $evidenceSnapshots.Keys){if((Get-FileHash -LiteralPath $file).Hash -cne $evidenceSnapshots[$file]){throw 'Validated proof changed during checking.'}}
if($Mode -eq 'Validate'){Write-Output ('V30_VALIDATION_READY; CONFIDENTIALITY_NOT_APPROVED_BY_THIS_COMMAND; UI='+$testedUi.Count+'; HOST='+$testedHost.Count+'; EXE='+$bm.ExecutableSha256);return}
. (Join-Path $PSScriptRoot 'AlgorithmConfidentialityGate.ps1')
$confidentialityInventory=@($buildInventory|Where-Object { $_.Path.StartsWith('modules/') -or $_.Path.StartsWith('model-payload/') -or $_.Path -eq 'Xiongxin.OWTS.Desktop.exe' })
$confidentiality=Assert-AlgorithmConfidentialityReview -BuildRoot $build -ReviewPath $ConfidentialityProof -RuntimeInventory $confidentialityInventory
$proofHashes.Add([ordered]@{Kind='Confidentiality';SHA256=$confidentiality.SHA256})
if($OutputName -notmatch '^HV-DAC_Xpert_Validation_V30_1\.1\.9\.9_[0-9]{8}_[0-9]{2}$'){throw 'Use an explicit neutral V30 validation delivery name.'}
$delivery=Assert-V30Unlinked (Join-Path $root ('deliverables/'+$OutputName)) $root
if(Test-Path -LiteralPath $delivery){throw 'Never overwrite an existing delivery.'}
$runtime=Get-V30File $root 'build/prerequisites/NDP48-x86-x64-AllOS-ENU.exe'
$signature=Get-AuthenticodeSignature -LiteralPath $runtime
if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation' -or (Get-FileHash -LiteralPath $runtime).Hash -cne '0A3A390C47E639D0F7FC65B21195FEE6B7F65B066F80F70C60FAB191D14B7E40'){throw 'Invalid offline .NET prerequisite.'}
$candidate=Join-Path $root ('build/v30-delivery-candidate-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $candidate
foreach($name in @('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config','module-manifest.json')){Copy-Item -LiteralPath (Get-V30File $build $name) -Destination (Join-Path $candidate $name)}
foreach($name in @('assets','model-payload')){Copy-Item -LiteralPath (Join-Path $build $name) -Destination (Join-Path $candidate $name) -Recurse}
Copy-Item -LiteralPath (Get-V30File $root 'docs/20261007_V30验证版使用说明.txt') -Destination (Join-Path $candidate '使用说明.txt')
$null=New-Item -ItemType Directory -Path (Join-Path $candidate 'licenses')
foreach($entry in @(@('modules/DataAnalysis/Task12/ONNX_RUNTIME_LICENSE.txt','ONNX_RUNTIME_LICENSE.txt'),@('modules/r71/LICENSE.txt','PYTHON_LICENSE.txt'),@('modules/r71/licenses/numpy-METADATA.txt','NUMPY_LICENSE_METADATA.txt'))){Copy-Item -LiteralPath (Get-V30File $build $entry[0]) -Destination (Join-Path $candidate ('licenses/'+$entry[1]))}
$null=New-Item -ItemType Directory -Path (Join-Path $candidate 'prerequisites')
Copy-Item -LiteralPath $runtime -Destination (Join-Path $candidate 'prerequisites/NDP48-x86-x64-AllOS-ENU.exe')
foreach($entry in $buildInventory|Where-Object { $_.Path.StartsWith('assets/') -or $_.Path.StartsWith('model-payload/') -or $_.Path -in @('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config','module-manifest.json') }){$null=Assert-V30Hash $candidate $entry.Path $entry.SHA256}
# Recheck after copying. A concurrent edit cannot be masked by old passing receipts.
$confidentialityAfter=Assert-AlgorithmConfidentialityReview -BuildRoot $build -ReviewPath $ConfidentialityProof -RuntimeInventory $confidentialityInventory
if($confidentialityAfter.SHA256 -cne $confidentiality.SHA256){throw 'Confidentiality approval changed during copying.'}
Assert-V30SameInventory (Get-V30Inventory $build @('assets','modules','model-payload') @('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config','build-manifest.json','module-manifest.json','research-pins.json')) $freeze.BuildFiles 'Frozen runtime after copy'
Assert-V30SameInventory (Get-V30Inventory $root @('src','modules-src','dependencies','assets','tools','hybrid-src','installer','tests') @('Build.ps1','Stage-Modules.ps1','App.config','app.manifest','docs/20261007_V30验证版使用说明.txt')) $freeze.InputFiles 'Frozen inputs after copy'
foreach($file in $evidenceSnapshots.Keys){if((Get-FileHash -LiteralPath $file).Hash -cne $evidenceSnapshots[$file]){throw 'Validated proof changed during copying.'}}
[ordered]@{Schema='HVX.V30.ValidationRelease/1';Version='1.1.9.9';Profile='Validation-V30-20261007';GeneratedUtc=[DateTime]::UtcNow.ToString('o');ExecutableSHA256=$bm.ExecutableSha256;FreezeSHA256=$evidenceSnapshots[$freezePath];DeploymentProofSHA256=$evidenceSnapshots[$deploymentPath];Proofs=$proofHashes.ToArray();HardwareAccessed=$false;PhysicalAcceptance=$false;FullRegression=$false;SafetyMaterialsApproved=$false;MaintenancePasswordIncluded=$false;ApplicationSignature=[string](Get-AuthenticodeSignature -LiteralPath $exe).Status;Algorithms='AB 3.0.22; C 0.3.0; D 0.1.1';Pressure='V30 control and range logic; per-round operator confirmation';ModelsOutsideApplication=$true;DriversInstalled=$false}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $candidate 'release-validation.json') -Encoding UTF8
$sums=@(Get-ChildItem -LiteralPath $candidate -Recurse -File|Sort-Object FullName|ForEach-Object{(Get-FileHash -LiteralPath $_.FullName).Hash+'  '+$_.FullName.Substring($candidate.Length+1).Replace('\','/')})
[IO.File]::WriteAllLines((Join-Path $candidate 'SHA256SUMS.txt'),$sums,(New-Object Text.UTF8Encoding($false)))
$null=New-Item -ItemType Directory -Path (Join-Path $root 'deliverables') -Force
[IO.Directory]::Move($candidate,$delivery)
Write-Output ('V30_VALIDATION_PAYLOAD='+$delivery)
