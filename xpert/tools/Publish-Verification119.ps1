param(
    [Parameter(Mandatory=$true)][string]$BuildRoot,
    [Parameter(Mandatory=$true)][string]$VerificationPath,
    [string]$OutputName='HV-DAC_Xpert_1.1.9_验证版_20260930',
    [switch]$IncludeModules
)
$ErrorActionPreference='Stop'
throw 'Historical 1.1.9 publishing is retired; no new unguarded delivery can be generated from this entry.'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'VerifiedRuntimeInventory.ps1')
$buildBase=Join-Path $root 'build'
if(-not [IO.Path]::IsPathRooted($BuildRoot)){$BuildRoot=Join-Path $root $BuildRoot}
if(-not [IO.Path]::IsPathRooted($VerificationPath)){$VerificationPath=Join-Path $root $VerificationPath}
$source=[IO.Path]::GetFullPath($BuildRoot)
$proof=[IO.Path]::GetFullPath($VerificationPath)
if(-not $source.StartsWith($buildBase+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Source must be under this project build directory.'}
if(-not $proof.StartsWith($source+'\qa\',[StringComparison]::OrdinalIgnoreCase)){throw 'Verification must belong to the source build.'}
if([IO.Path]::GetFileName($OutputName) -ne $OutputName -or $OutputName -match '[\\/:]'){throw 'OutputName must be a single directory name.'}
$destination=Join-Path $buildBase $OutputName
if(Test-Path -LiteralPath $destination){throw 'Refusing to overwrite an existing delivery.'}
$proofSnapshot=Read-VerificationProofSnapshot -Path $proof
$proofBytes=$proofSnapshot.Bytes
$proofSha=$proofSnapshot.SHA256
$verification=$proofSnapshot.Value
if($verification.HardwareAccessed -isnot [bool] -or $verification.HardwareAccessed -ne $false -or
   $verification.PhysicalAcceptance -isnot [bool] -or $verification.PhysicalAcceptance -ne $false){throw 'This preview requires an explicit hardware-free, non-physical regression record.'}
if($verification.BuildTestInputsUnchanged -isnot [bool] -or $verification.BuildTestInputsUnchanged -ne $true -or -not $verification.BuildTestInputs){throw 'The complete frozen build/test input record is required.'}
# The 117 passing 1.1.8 groups remain required individually, not merely by count.
# Additions for 1.1.9 cannot substitute for a missing baseline test.
$requiredTests=@(
    'DeploymentPayload',
    'PublisherSafetySmoke',
    'PublisherInventorySmoke',
    'CalibrationPipelineRegression',
    'CalibrationAcquisitionFlowSmoke',
    'ProjectMeasurementModeSmoke',
    'ModuleTileBadgeSmoke',
    'CalibrationMeasurementFeedbackSmoke',
    'CalibrationCandidateQualitySmoke',
    'CalibrationAssessmentActionSmoke',
    'CalibrationQualityContractSmoke',
    'CalibrationStopLatchSmoke',
    'CapacitorWorkflowSmoke',
    'DialogPlacementSmoke',
    'SharedCableJointStoreSmoke',
    'SharedCableJointMutationSmoke',
    'SharedCableJointWritableTransactionSmoke',
    'CableJointCommitWarningSmoke',
    'CableJointDialogSmoke',
    'CableJointEditingSmoke',
    'AnalysisPositionSettingsSmoke',
    'BrowserSharedJointSmoke',
    'CalibrationSharedJointSmoke',
    'CalibrationArchiveBrowserSmoke',
    'CalibrationPersistenceTimingSmoke',
    'CalibrationViewportFeedbackSmoke',
    'CalibrationDecayFeedbackSmoke',
    'BrowserCalibrationActionsSmoke',
    'CalibrationComparisonAverageSmoke',
    'CalibrationJointCandidatesSmoke',
    'BrowserAverageGeometrySmoke',
    'BrowserPlotLayoutSmoke',
    'BrowserComparisonInteractionSmoke',
    'BrowserJointRefreshSmoke',
    'BrowserLengthDialogLayoutSmoke',
    'CalibrationActionStyleSmoke',
    'CreationMeasuredJointSmoke',
    'CreationMeasuredJointEditSmoke',
    'CreationJointInsulationSmoke',
    'CreationSaveValidationSmoke',
    'CreationFormLayoutSmoke',
    'CreationTopologyJointSmoke',
    'AnalysisStartAndSettingsSmoke',
    'AnalysisReadableLayoutSmoke',
    'PressureStatisticsUiSmoke',
    'ReportFeedbackSmoke',
    'ThreeModuleLanguageSmoke',
    'CreationMaterialCatalogSmoke',
    'CreationPrecisionTimestampSmoke',
    'ProjectSaveTransactionSmoke',
    'ProjectLoadIntegritySmoke',
    'CreationTopologyViewportSmoke',
    'CreationDraftJointIntegritySmoke',
    'ProjectFileTransactionsSmoke',
    'NoiseToleranceSettingsSmoke',
    'StateGridReportSettingsSmoke',
    'PressurePreferencesSettingsSmoke',
    'AiFeatureSettingsSmoke',
    'CompetitionModuleSettingsSmoke',
    'PlusDataSettingsSmoke',
    'SettingsIntegritySmoke',
    'ModuleCableSyncSmoke',
    'ProjectReleaseSmoke',
    'ProjectEditorNavigationSmoke',
    'ProjectHeaderIdentitySmoke',
    'HomeDeviceActionsSmoke',
    'HomeDeviceConnectionSmoke',
    'ModuleNavigationPerformanceSmoke',
    'ModulePresentationSmoke',
    'HomeStatusVlfSmoke',
    'DeviceSetupNavigationSmoke',
    'ModuleAuditSmoke',
    'DeviceSetupStatusSmoke',
    'HardwareDisconnectSmoke',
    'HardwareArchitectureDiscoverySmoke',
    'DetectionDeviceSelectionSmoke',
    'HybridPublishingGuardSmoke',
    'SecureProjectStoreSmoke',
    'DpiLayoutSmoke',
    'AnalysisDeploymentSmoke',
    'Task28SourceIntegrationSmoke',
    'DeferredTask17BoundarySmoke',
    'LocationCyclePhaseSmoke',
    'PressurePreferencesSmoke',
    'PressureSidebarLayoutSmoke',
    'PressureProgressSmoke',
    'LocationCycleWindowSmoke',
    'PressureAcquisitionCycleSmoke',
    'PressureP04FeedbackSmoke',
    'Task15EmbeddedIntegrationSmoke',
    'AnalysisSaveDirtySmoke',
    'AnalysisReviewedStateSmoke',
    'AnalysisOverwriteExitSmoke',
    'UnsavedAnalysisNavigationSmoke',
    'ProjectActivationSmoke',
    'ProjectInsulationColumnSmoke',
    'ProjectInsulationStatusSmoke',
    'ReportSavedConclusionSmoke',
    'ReportActionsStructureSmoke',
    'ReportPresentationSmoke',
    'ReportCatalogSnapshotSmoke',
    'ReportProjectIdentitySmoke',
    'ReportFileNamingSmoke',
    'BrowserWheelAxesSmoke',
    'BrowserSharedMeanJointSmoke',
    'CalibrationGeometryFeedbackSmoke',
    'CompetitionDefectHostSmoke',
    'CompetitionDefectRecognitionSmoke',
    'CompetitionLearningPathBudgetSmoke',
    'CompetitionScientificLayoutSmoke',
    'CompetitionSpectrumViewsSmoke',
    'OrdinarySpectrumBridgeSmoke',
    'AnalysisUiStabilitySmoke',
    'AnalysisLanguageNoOpSmoke',
    'CalibrationInteractionRegressionSmoke',
    'AnalysisAiAutomaticEvaluationSmoke',
    'AnalysisInvariantDateSmoke',
    'EnvironmentAutoRefreshSmoke',
    'EnvironmentalAcquisitionSmoke',
    'CableJointManagerSmoke',
    'JointDefaultsFeedbackSmoke',
    'ProjectInvariantTimeSmoke',
    'ReportBundledTemplateSmoke',
    'StateGridReportSmoke',
    'LocationCountAxisSmoke',
    'EmbeddedModulePayloadSmoke',
    'BrowserHoverPerformanceSmoke',
    'BrowserDemResponsivenessSmoke',
    'CalibrationAverageSelectionSmoke',
    'BrowserJointSearchFeedbackSmoke',
    'BrowserMeanSourceIntegritySmoke',
    'ReportAppendixLayoutSmoke',
    'HomeConnectionProjectActionsSmoke',
    'ProjectCurrentPinSmoke',
    'BrowserJointAsyncResponsiveSmoke',
    'PressureZeroVoltageBackendSmoke',
    'Task39HostContractSmoke',
    'Task39WorkerOfflineSmoke',
    'ProjectLoadTypeChoicesSmoke',
    'PressureProjectPlanCorrespondenceSmoke',
    'PressurePlanLayoutSmoke',
    'PressureNavigationLockSmoke',
    'PressureFinishCurrentRoundSmoke',
    'HardwareIdentityDefaultsSmoke',
    'ProvisioningImportSmoke',
    'HardwareRawIdentitySnapshotSmoke',
    'SettingsCompareSaveSmoke',
    'FirstRunEntryCasSmoke',
    'EnvironmentUiBridgeSmoke'
)
if($verification.Status -ne 'PASS' -or $verification.StartAtTest -or @($verification.Tests).Count -lt $requiredTests.Count){throw 'A complete passing 1.1.9 regression record is required.'}
$buildManifest=Get-Content -LiteralPath (Join-Path $source 'build-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($null -ne $buildManifest.HybridArchitecture) {
    if ($buildManifest.HybridArchitecture.Included -isnot [bool]) { throw 'Hybrid build identity must be a typed Boolean.' }
    if ($buildManifest.HybridArchitecture.Included) { $requiredTests+=@('HybridRuntimeSmoke','HybridPhasePerformanceSmoke') }
}
if(@($verification.Tests | Group-Object Name | Where-Object {$_.Count -ne 1}).Count -ne 0){throw 'Duplicate verification test names are not permitted.'}
foreach($test in @($verification.Tests)) {
    if($null -eq $test.ExitCode -or $test.ExitCode -ne 0){throw ('Non-passing verification entry: '+$test.Name)}
}
foreach($required in $requiredTests) {
    if(@($verification.Tests | Where-Object {$_.Name -eq $required}).Count -ne 1){throw ('Missing or duplicate required verification entry: '+$required)}
}
if(-not $verification.DistributionFiles){throw 'The complete verified runtime inventory is required.'}
$runtimeFiles=@(Get-Item -LiteralPath (Join-Path $source 'Xiongxin.OWTS.Desktop.exe'),(Join-Path $source 'Xiongxin.OWTS.Desktop.exe.config'),(Join-Path $source 'build-manifest.json'),(Join-Path $source 'module-manifest.json') -Force)+
    @(Get-ChildItem -LiteralPath (Join-Path $source 'assets') -Recurse -File -Force)+@(Get-ChildItem -LiteralPath (Join-Path $source 'modules') -Recurse -File -Force)
$runtimeMap=@{}
foreach($file in $runtimeFiles){$runtimeMap[$file.FullName.Substring($source.Length+1).Replace('/','\')]=$file.FullName}
if($runtimeFiles.Count -ne @($verification.DistributionFiles).Count){throw 'The verified runtime inventory is not exhaustive.'}
$verifiedRuntimePaths=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach($entry in $verification.DistributionFiles) {
    $relative=([string]$entry.Path).Replace('/','\')
    if(-not $verifiedRuntimePaths.Add($relative) -or -not $runtimeMap.ContainsKey($relative) -or $entry.SHA256 -notmatch '^[A-Fa-f0-9]{64}$'){throw ('Invalid verified runtime path: '+$relative)}
    if((Get-FileHash -LiteralPath $runtimeMap[$relative] -Algorithm SHA256).Hash -ne $entry.SHA256){throw ('Verified runtime file changed: '+$relative)}
}
if(-not $verification.ProductBinaries -or @($verification.ProductBinaries).Count -lt 8){throw 'Verified product binary manifest is missing.'}
foreach($entry in $verification.ProductBinaries){
    $binary=[IO.Path]::GetFullPath((Join-Path $source $entry.Path))
    if(-not $binary.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid product manifest path.'}
    if((Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ne $entry.SHA256){throw ('Verified binary changed: '+$entry.Path)}
}
$exe=Join-Path $source 'Xiongxin.OWTS.Desktop.exe'
if([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -ne '1.1.9.0'){throw 'Expected version 1.1.9.0.'}
$browser=Join-Path $source 'modules\DataBrowser\Xiongxin.OWTS.DataBrowser.dll'
if([Diagnostics.FileVersionInfo]::GetVersionInfo($browser).FileVersion -ne '1.1.12.0'){throw 'Expected DataBrowser version 1.1.12.0.'}
foreach($binaryPath in @('Xiongxin.OWTS.Desktop.exe','modules\DataBrowser\Xiongxin.OWTS.DataBrowser.dll','modules\Task39\Xiongxin.Task39.InputWorker.exe')) {
    if(@($verification.ProductBinaries | Where-Object {$_.Path.Replace('/','\') -eq $binaryPath}).Count -ne 1){throw ('Missing or duplicate verified product binary: '+$binaryPath)}
}
$verifiedExeHash=($verification.ProductBinaries | Where-Object {$_.Path -eq 'Xiongxin.OWTS.Desktop.exe'}).SHA256
if($IncludeModules) {
    $moduleManifestPath=Join-Path $source 'module-manifest.json'
    $moduleManifest=Get-Content -LiteralPath $moduleManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if($moduleManifest.Schema -ne 'XIONGXIN.UnifiedDesktop.Modules.v2' -or @($moduleManifest.Modules).Count -eq 0){throw 'A complete module manifest is required for an integration payload.'}
    $seenModules=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $moduleManifest.Modules) {
        $relative=[string]$entry.Path
        if(-not $relative.StartsWith('modules/',[StringComparison]::Ordinal) -or $relative.Contains('..') -or -not $seenModules.Add($relative)){throw ('Invalid or duplicate module path: '+$relative)}
        $moduleFile=[IO.Path]::GetFullPath((Join-Path $source $relative.Replace('/','\')))
        if(-not $moduleFile.StartsWith($source+'\modules\',[StringComparison]::OrdinalIgnoreCase)){throw 'Module path escapes the verified payload.'}
        if((Get-FileHash -LiteralPath $moduleFile -Algorithm SHA256).Hash -ne $entry.Sha256){throw ('Module manifest mismatch: '+$relative)}
    }
    Assert-PublicModuleFiles -ModulesRoot (Join-Path $source 'modules')
    foreach($moduleFile in @(Get-ChildItem -LiteralPath (Join-Path $source 'modules') -Recurse -File -Force)) {
        $relative=$moduleFile.FullName.Substring($source.Length+1).Replace('\','/')
        if(-not $seenModules.Contains($relative)){throw ('Unmanifested module file: '+$relative)}
    }
}
$documents=@{'1.1.9_使用说明.md'='使用说明.md';'1.1.9_修正说明.md'='修正汇总.md';'1.1.9_审核汇总.md'='审核汇总.md';'1.1.9_验证记录.md'='验证记录.md'}
if($IncludeModules){$documents.Add('1.1.9_一体化接入说明.md','一体化接入说明.md')}
foreach($name in $documents.Keys){if(-not(Test-Path -LiteralPath (Join-Path $root ('docs\'+$name)))){throw ('Missing delivery document: '+$name)}}
$finalDestination=$destination
$destination=Join-Path $buildBase ($OutputName+'.candidate-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $destination | Out-Null
foreach($name in @('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config','build-manifest.json')){
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $destination $name)
}
Copy-Item -LiteralPath (Join-Path $source 'assets') -Destination (Join-Path $destination 'assets') -Recurse
if($IncludeModules) {
    Copy-Item -LiteralPath (Join-Path $source 'module-manifest.json') -Destination (Join-Path $destination 'module-manifest.json')
    Copy-Item -LiteralPath (Join-Path $source 'modules') -Destination (Join-Path $destination 'modules') -Recurse
    foreach($entry in $moduleManifest.Modules) {
        if((Get-FileHash -LiteralPath (Join-Path $destination $entry.Path.Replace('/','\')) -Algorithm SHA256).Hash -ne $entry.Sha256){throw ('Delivered module mismatch: '+$entry.Path)}
    }
}
foreach($item in $documents.GetEnumerator()){Copy-Item -LiteralPath (Join-Path $root ('docs\'+$item.Key)) -Destination (Join-Path $destination $item.Value)}
$auditDirectory=Join-Path $destination '审核证据'
New-Item -ItemType Directory -Path $auditDirectory | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'docs') -File | Where-Object { $_.Name -like 'audit-1.1.9-*.md' -or $_.Name -like 'audit-1.1.8-*.md' -or $_.Name -like 'audit-1.1.7-*.md' -or $_.Name -in @('1.1.7_审核汇总.md','task15-v3-integration-20260930.md','Internal12_原界面修正进度与验证_20260929.md','原界面修正审核表_20260929.md','1.1.8_修正说明.md','1.1.8_使用说明.md') } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $auditDirectory $_.Name)
}
$evidenceDirectory=Join-Path $destination '验证证据'
New-Item -ItemType Directory -Path $evidenceDirectory | Out-Null
[IO.File]::WriteAllBytes((Join-Path $evidenceDirectory 'verification.json'),$proofBytes)
Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($proof)) -File | Where-Object {$_.Name -like '*.result.txt' -or $_.Name -eq 'deployment-payload.json'} | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $evidenceDirectory $_.Name)
}
$originalHash=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
$deliveredHash=(Get-FileHash -LiteralPath (Join-Path $destination 'Xiongxin.OWTS.Desktop.exe') -Algorithm SHA256).Hash
if($originalHash -ne $verifiedExeHash -or $deliveredHash -ne $verifiedExeHash){throw 'Delivery no longer matches the verified executable.'}
$deliveredRuntimeCount=Assert-VerifiedRuntimeInventory -PayloadRoot $destination -Entries @($verification.DistributionFiles) -IncludeModules ([bool]$IncludeModules) -StrictCandidateRoot $true
if((Get-FileHash -LiteralPath (Join-Path $evidenceDirectory 'verification.json') -Algorithm SHA256).Hash -ne $proofSha){throw 'The copied verification record differs from the validated immutable evidence.'}
# An incomplete candidate is retained for diagnosis, never announced as a
# verified delivery. Directory.Move refuses a concurrently created target.
[IO.Directory]::Move($destination,$finalDestination)
$destination=$finalDestination
Write-Output ('DELIVERY='+$destination)
Write-Output ('EXE_SHA256='+$deliveredHash)
Write-Output ('FULL_REGRESSION_PROGRAMS='+@($verification.Tests).Count)
Write-Output ('DELIVERED_RUNTIME_FILES_VERIFIED='+$deliveredRuntimeCount)
Write-Output ('VERIFICATION_SHA256='+$proofSha)
