param([string]$BuildRoot='build\Internal12-fix2',[switch]$SkipBuild,[string]$StartAtTest='',[switch]$Hybrid)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
if(-not [IO.Path]::IsPathRooted($BuildRoot)){$BuildRoot=Join-Path $root $BuildRoot}
$BuildRoot=[IO.Path]::GetFullPath($BuildRoot)
if(-not $BuildRoot.StartsWith(($root+'\build\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Test output must be beneath project build.'}
function Assert-RegressionUnlinkedPath([string]$Path,[switch]$AllowMissing) {
 $candidate=[IO.Path]::GetFullPath($Path)
 while($true) {
  try {$item=Get-Item -LiteralPath $candidate -Force -ErrorAction Stop;break}
  catch {
   if(-not $AllowMissing -or $_.CategoryInfo.Category -ne [Management.Automation.ErrorCategory]::ObjectNotFound){throw}
   $parent=[IO.Path]::GetDirectoryName($candidate.TrimEnd('\'))
   if([String]::IsNullOrWhiteSpace($parent) -or $parent -eq $candidate){throw}
   $candidate=$parent
  }
 }
 while($null -ne $item) {
  if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw ('Linked regression path is prohibited: '+$item.FullName)}
  $item=if($item -is [IO.DirectoryInfo]){$item.Parent}else{$item.Directory}
 }
}
function Get-RegressionTreeFiles([string]$Directory) {
 Assert-RegressionUnlinkedPath $Directory
 $directoryItem=Get-Item -LiteralPath $Directory -Force -ErrorAction Stop
 if(-not $directoryItem.PSIsContainer){throw ('Regression input must be a directory: '+$Directory)}
 $pending=New-Object 'System.Collections.Generic.Stack[System.IO.DirectoryInfo]'
 $pending.Push($directoryItem)
 while($pending.Count -ne 0) {
  $current=$pending.Pop()
  # Reject each entry before descending, including hidden/system junctions.
  foreach($entry in @(Get-ChildItem -LiteralPath $current.FullName -Force -ErrorAction Stop)) {
   if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){throw ('Linked regression tree entry is prohibited: '+$entry.FullName)}
   if($entry.PSIsContainer){$pending.Push($entry)}else{$entry}
  }
 }
}
Assert-RegressionUnlinkedPath $root
Assert-RegressionUnlinkedPath $BuildRoot -AllowMissing
$qa=Join-Path $BuildRoot ('qa\verified-'+[DateTime]::Now.ToString('yyyyMMdd_HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,6))
Assert-RegressionUnlinkedPath $qa -AllowMissing
New-Item -ItemType Directory -Path $qa -Force | Out-Null
Assert-RegressionUnlinkedPath $qa
$csc=Join-Path $root 'tools\compiler\csc.exe'
function Get-VerificationInputs {
 $inputs=@(Get-Item -LiteralPath (Join-Path $root 'Build.ps1'),(Join-Path $root 'Stage-Modules.ps1'),(Join-Path $root 'App.config'),(Join-Path $root 'app.manifest') -Force)
 foreach($directory in @('src','modules-src','assets','tools','tests','hybrid-src')) {
  $inputs+=@(Get-RegressionTreeFiles (Join-Path $root $directory))
 }
 @($inputs | Sort-Object FullName | ForEach-Object {Assert-RegressionUnlinkedPath $_.FullName;[ordered]@{Path=$_.FullName.Substring($root.Length+1);SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
}
function Save-RegressionProgress([string]$Status,[string]$Failure='') {
 Assert-RegressionUnlinkedPath $qa
 $progressPath=Join-Path $qa 'verification-progress.json'
 Assert-RegressionUnlinkedPath $progressPath -AllowMissing
 [ordered]@{Schema='HVX.Regression.Progress/1';Status=$Status;ReleaseEvidence=$false;BuildRoot=$BuildRoot;StartAtTest=$StartAtTest;StartedUtc=$regressionStartedUtc;UpdatedUtc=[DateTime]::UtcNow.ToString('o');HardwareAccessed=$false;PhysicalAcceptance=$false;CurrentTest=$script:currentRegressionTest;ProcessTimeoutMilliseconds=180000;FinishedGroupCount=$results.Count;Tests=$results.ToArray();BuildInputCount=$verificationInputs.Count;RuntimeFileCount=$distributionHashes.Count;Failure=$Failure;Limitations='Incremental diagnostic receipt only; never a complete PASS or a substitute for verification.json. The watchdog covers Test-Program executables, not external build/PowerShell/compiler stages.'} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $progressPath -Encoding UTF8
}
function Invoke-RegressionExecutable([string]$Executable,[string[]]$Arguments,[string]$LogPrefix,[ValidateRange(100,180000)][int]$TimeoutMilliseconds=180000) {
 Assert-RegressionUnlinkedPath $Executable
 Assert-RegressionUnlinkedPath ([IO.Path]::GetDirectoryName($LogPrefix))
 $argumentText=(@($Arguments | ForEach-Object {
  if($_ -match '[\x00\r\n]'){throw 'Regression argument contains an unsafe control character.'}
  '"'+(($_ -replace '(\\*)"','$1$1\"') -replace '(\\+)$','$1$1')+'"'
 }) -join ' ')
 $stdoutPath=$LogPrefix+'.stdout.txt';$stderrPath=$LogPrefix+'.stderr.txt'
 foreach($path in @($stdoutPath,$stderrPath)){Assert-RegressionUnlinkedPath $path -AllowMissing}
 $process=$null;$stdoutFile=$null;$stderrFile=$null;$stdoutTask=$null;$stderrTask=$null
 $pidValue=$null;$timedOut=$false;$terminated=$false;$code=-1;$failure='';$clock=[Diagnostics.Stopwatch]::StartNew()
 $stdoutEncoding=[Text.Encoding]::UTF8;$stderrEncoding=[Text.Encoding]::UTF8
 try {
  $stdoutFile=New-Object IO.FileStream($stdoutPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
  $stderrFile=New-Object IO.FileStream($stderrPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
  $start=New-Object Diagnostics.ProcessStartInfo
  $start.FileName=$Executable;$start.Arguments=$argumentText;$start.WorkingDirectory=$root
  $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
  $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
  $process=[Diagnostics.Process]::Start($start)
  if($null -eq $process){throw 'Regression executable did not start.'}
  $pidValue=$process.Id
  $stdoutEncoding=$process.StandardOutput.CurrentEncoding;$stderrEncoding=$process.StandardError.CurrentEncoding
  $stdoutTask=$process.StandardOutput.BaseStream.CopyToAsync($stdoutFile)
  $stderrTask=$process.StandardError.BaseStream.CopyToAsync($stderrFile)
  while(-not $process.WaitForExit(250)) {
   if($clock.ElapsedMilliseconds -ge $TimeoutMilliseconds){$timedOut=$true;throw ('Regression executable timed out after '+$TimeoutMilliseconds+'ms.')}
  }
  $terminated=$true;$code=$process.ExitCode
 } catch {$failure=$_.Exception.Message;$code=-1}
 finally {
  # Only the Process object started above is ever terminated. No PID/name search
  # and no process-tree termination can affect another task or a user process.
  if($null -ne $process -and -not $terminated) {
   try {if(-not $process.HasExited){$process.Kill()};$terminated=$process.WaitForExit(5000)}catch{$failure+=' Stop confirmation failed: '+$_.Exception.Message}
  }
  foreach($task in @($stdoutTask,$stderrTask)) {
   if($null -ne $task){try {if(-not $task.Wait(5000)){$failure+=' Output drain exceeded its bounded wait.';$code=-1}}catch{$failure+=' Output drain failed: '+$_.Exception.Message;$code=-1}}
  }
  if($null -ne $process){try{$process.StandardOutput.Close();$process.StandardError.Close()}catch{};$process.Dispose()}
  if($null -ne $stdoutFile){$stdoutFile.Dispose()};if($null -ne $stderrFile){$stderrFile.Dispose()}
 }
 $outText=if(Test-Path -LiteralPath $stdoutPath){[IO.File]::ReadAllText($stdoutPath,$stdoutEncoding)}else{''}
 $errText=if(Test-Path -LiteralPath $stderrPath){[IO.File]::ReadAllText($stderrPath,$stderrEncoding)}else{''}
 $lines=@(($outText+[Environment]::NewLine+$errText) -split '\r?\n' | Where-Object {$_.Length -gt 0})
 if($failure){$lines+=('EXECUTION_ERROR='+$failure)}
 [pscustomobject]@{ExitCode=$code;Output=$lines;ProcessId=$pidValue;TimedOut=$timedOut;ProcessExitConfirmed=$terminated;ElapsedMilliseconds=$clock.ElapsedMilliseconds;TimeoutMilliseconds=$TimeoutMilliseconds;StdoutPath=$stdoutPath;StdoutSHA256=$(if(Test-Path -LiteralPath $stdoutPath){(Get-FileHash -LiteralPath $stdoutPath -Algorithm SHA256).Hash}else{$null});StderrPath=$stderrPath;StderrSHA256=$(if(Test-Path -LiteralPath $stderrPath){(Get-FileHash -LiteralPath $stderrPath -Algorithm SHA256).Hash}else{$null});ExecutionFailure=$failure}
}
$regressionStartedUtc=[DateTime]::UtcNow.ToString('o')
$verificationInputs=@();$distributionHashes=@();$results=New-Object System.Collections.Generic.List[object]
$script:currentRegressionTest='Pin build inputs'
Save-RegressionProgress 'RUNNING'
try {
# Concurrent tasks may share this checkout. Pin compiler, source, assets and
# fixtures before building, so later source changes cannot masquerade as a
# regression of the delivered binary. Documentation is not a runtime input.
$verificationInputs=@(Get-VerificationInputs)
if(-not $SkipBuild){
 # Exercise the same complete distribution path as a normal build. UI-only
 # compilation omits runtime algorithms, report engine and default template.
 & .\Build.ps1 -OutputDirectory $BuildRoot -Hybrid:$Hybrid
}
$actualBuildManifest=Get-Content -LiteralPath (Join-Path $BuildRoot 'build-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$actualHybrid=$false
if ($null -ne $actualBuildManifest.HybridArchitecture) {
 if ($actualBuildManifest.HybridArchitecture.Included -isnot [bool]) { throw 'Hybrid build identity must be a typed Boolean.' }
 $actualHybrid=$actualBuildManifest.HybridArchitecture.Included
}
if ($Hybrid -and -not $actualHybrid) { throw 'Requested hybrid verification cannot verify a standard build.' }
$Hybrid=[bool]$actualHybrid # A forgotten CLI switch must never omit native validation.
$results=New-Object System.Collections.Generic.List[object]
$testStarted=[String]::IsNullOrWhiteSpace($StartAtTest)
$payloadLines=@(& .\tools\Test-DeploymentPayload.ps1 -BuildRoot $BuildRoot -EvidencePath (Join-Path $qa 'deployment-payload.json'))
$results.Add([ordered]@{Name='DeploymentPayload';ExitCode=0;Output=$payloadLines})
$payloadLines | Write-Output
# Pin the complete runtime, not only executables: templates, PDM, settings,
# assets and both manifests must remain the same throughout this regression.
function Get-DistributionFiles {
 Assert-RegressionUnlinkedPath $BuildRoot
 $files=@(Get-Item -LiteralPath (Join-Path $BuildRoot 'Xiongxin.OWTS.Desktop.exe'),(Join-Path $BuildRoot 'Xiongxin.OWTS.Desktop.exe.config'),(Join-Path $BuildRoot 'build-manifest.json'),(Join-Path $BuildRoot 'module-manifest.json') -Force)+
 @(Get-RegressionTreeFiles (Join-Path $BuildRoot 'assets'))+
 @(Get-RegressionTreeFiles (Join-Path $BuildRoot 'modules'))
 @($files | ForEach-Object {Assert-RegressionUnlinkedPath $_.FullName;$_})
}
$distributionHashes=@(Get-DistributionFiles | ForEach-Object {[ordered]@{Path=$_.FullName.Substring($BuildRoot.Length+1);SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
$publisherLines=@(& .\tests\PublisherSafetySmoke.ps1 -BuildRoot $BuildRoot -QaRoot (Join-Path $qa 'publisher-rejections'))
$results.Add([ordered]@{Name='PublisherSafetySmoke';ExitCode=0;Output=$publisherLines})
$publisherLines | Write-Output
$inventoryLines=@(& .\tests\PublisherInventorySmoke.ps1 -QaRoot (Join-Path $qa 'publisher-inventory'))
$results.Add([ordered]@{Name='PublisherInventorySmoke';ExitCode=0;Output=$inventoryLines})
$inventoryLines | Write-Output
$guardLines=@(& .\tests\HybridPublishingGuardSmoke.ps1 -QaRoot (Join-Path $qa 'private-payload-guards'))
$results.Add([ordered]@{Name='HybridPublishingGuardSmoke';ExitCode=0;Output=$guardLines})
$guardLines | Write-Output
function Test-Program([string]$Name,[string[]]$Sources,[string[]]$Arguments,[string[]]$Extra=@(),[string]$ExecutionDirectory='') {
 $oemSource=Join-Path $root 'src\OemUiBranding.cs'
 if(-not @($Sources | Where-Object {[IO.Path]::GetFullPath($_) -eq $oemSource}).Count){$Sources+= $oemSource}
 if(-not $script:testStarted) {
  if($Name -ne $StartAtTest){return}
  $script:testStarted=$true
 }
 $runtimeRoot=$BuildRoot
 if(-not [String]::IsNullOrWhiteSpace($ExecutionDirectory)) {
  $runtimeRoot=[IO.Path]::GetFullPath($ExecutionDirectory)
  if(-not $runtimeRoot.StartsWith($qa+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'An isolated smoke process must remain in this verification QA directory.'}
  Assert-RegressionUnlinkedPath $runtimeRoot -AllowMissing
  New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
 }
 $exe=Join-Path $runtimeRoot ($Name+'.exe')
 Assert-RegressionUnlinkedPath $exe -AllowMissing
 $script:currentRegressionTest=$Name
 Save-RegressionProgress 'RUNNING'
 $compile=@(& $csc /nologo /target:exe /platform:x64 /langversion:7.3 /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:'+$exe) @Extra @Sources 2>&1 | ForEach-Object {$_.ToString()})
 $compileExit=$LASTEXITCODE
 $compile | Set-Content -LiteralPath (Join-Path $qa ($Name+'.compile.txt')) -Encoding UTF8
 if($compileExit -ne 0){Save-RegressionProgress 'FAIL' ($Name+' compilation failed: '+($compile -join [Environment]::NewLine));throw ($Name+' compilation failed: '+($compile -join [Environment]::NewLine))}
 $execution=Invoke-RegressionExecutable -Executable $exe -Arguments $Arguments -LogPrefix (Join-Path $qa $Name)
 $lines=@($execution.Output)
 $testExit=$execution.ExitCode
 $lines | Set-Content -LiteralPath (Join-Path $qa ($Name+'.result.txt')) -Encoding UTF8
 $results.Add([ordered]@{Name=$Name;ExitCode=$testExit;SHA256=(Get-FileHash -LiteralPath $exe).Hash;Output=$lines;Arguments=@($Arguments);ProcessId=$execution.ProcessId;TimedOut=$execution.TimedOut;ProcessExitConfirmed=$execution.ProcessExitConfirmed;ElapsedMilliseconds=$execution.ElapsedMilliseconds;TimeoutMilliseconds=$execution.TimeoutMilliseconds;StdoutPath=$execution.StdoutPath;StdoutSHA256=$execution.StdoutSHA256;StderrPath=$execution.StderrPath;StderrSHA256=$execution.StderrSHA256})
 Save-RegressionProgress $(if($testExit -eq 0){'RUNNING'}else{'FAIL'}) $execution.ExecutionFailure
 $lines | Write-Output
 if($testExit -ne 0){throw ($Name+' failed')}
}
$filters=@('src\PdLinearHighPass.cs','src\PdLinearLowPass.cs','src\ProjectMeasurementMode.cs','src\PicoCaptureCadence.cs','src\OemUiBranding.cs')
Test-Program 'CalibrationPipelineRegression' (@('tests\CalibrationPipelineRegression.cs','src\CableJointStore.cs','src\NoisePulseTemplates.cs','src\LiveCalibrationRecord.cs','modules-src\CalibrationTest\src\CalibrationProjectStore.cs','modules-src\CalibrationStandalone\AcquisitionPlanning.cs','modules-src\CalibrationStandalone\CalibrationAcquisitionFlow.cs','modules-src\CalibrationStandalone\CalibrationWindowing.cs','modules-src\CalibrationStandalone\PicoCore\Models.cs','modules-src\CalibrationStandalone\ProjectDefaults.cs','modules-src\CalibrationStandalone\TdrCorrelation.cs')+$filters) @($qa)
$standalone=@(Get-ChildItem -LiteralPath 'modules-src\CalibrationStandalone' -Filter '*.cs' -File | Where-Object {$_.Name -notin @('Program.cs','ControlBoxTests.cs')} | ForEach-Object FullName)
$standalone+=@(Get-ChildItem -LiteralPath 'modules-src\CalibrationStandalone\PicoCore' -Filter '*.cs' -File | ForEach-Object FullName)
Test-Program 'CalibrationAcquisitionFlowSmoke' (@('tests\CalibrationAcquisitionFlowSmoke.cs','src\NoisePulseTemplates.cs')+$standalone+$filters) @((Join-Path $qa 'acquisition')) @('/resource:modules-src\CalibrationStandalone\assets\xiongxin.ico,Brand.ico','/resource:modules-src\CalibrationStandalone\assets\xiongxin-original.png,Brand.png')
Test-Program 'ProjectMeasurementModeSmoke' @('tests\ProjectMeasurementModeSmoke.cs','src\ProjectMeasurementMode.cs','modules-src\CalibrationStandalone\ProjectDefaults.cs','modules-src\CalibrationStandalone\CalibrationWindowing.cs') @($qa)
Test-Program 'ModuleTileBadgeSmoke' @('tests\ModuleTileBadgeSmoke.cs','src\ModuleTile.cs','src\Theme.cs') @((Join-Path $qa 'tile-badges'))
Test-Program 'CalibrationMeasurementFeedbackSmoke' @('tests\CalibrationMeasurementFeedbackSmoke.cs') @($BuildRoot,(Join-Path $qa 'measurement-feedback'))
Test-Program 'CalibrationCandidateQualitySmoke' @('tests\CalibrationCandidateQualitySmoke.cs') @($BuildRoot,(Join-Path $qa 'candidate-quality'))
Test-Program 'CalibrationAssessmentActionSmoke' @('tests\CalibrationAssessmentActionSmoke.cs') @($BuildRoot,(Join-Path $qa 'assessment-action'))
Test-Program 'CalibrationQualityContractSmoke' @('tests\CalibrationQualityContractSmoke.cs') @($BuildRoot,(Join-Path $qa 'quality-contract'))
Test-Program 'CalibrationStopLatchSmoke' @('tests\CalibrationStopLatchSmoke.cs') @($BuildRoot)
Test-Program 'CapacitorWorkflowSmoke' @('tests\CapacitorWorkflowSmoke.cs') @($BuildRoot,(Join-Path $qa 'capacitor-workflow'))
Test-Program 'DialogPlacementSmoke' @('tests\DialogPlacementSmoke.cs','src\DialogPlacement.cs') @()
Test-Program 'HostDialogSafetySmoke' @('tests\HostDialogSafetySmoke.cs') @($BuildRoot,(Join-Path $qa 'host-dialog-safety'))
Test-Program 'CalibrationUiAccessibilitySmoke' @('tests\CalibrationUiAccessibilitySmoke.cs') @($BuildRoot,(Join-Path $qa 'calibration-ui-accessibility')) @('/r:Accessibility.dll')
Test-Program 'CalibrationJointReviewSafetySmoke' @('tests\CalibrationJointReviewSafetySmoke.cs') @($BuildRoot,(Join-Path $qa 'calibration-joint-review-safety'))
Test-Program 'ProjectCreationDialogBoundarySmoke' @('tests\ProjectCreationDialogBoundarySmoke.cs') @($BuildRoot,(Join-Path $qa 'creation-dialog-boundary'))
Test-Program 'OwnedModuleDialogLayoutSmoke' @('tests\OwnedModuleDialogLayoutSmoke.cs') @((Join-Path $BuildRoot 'modules'),(Join-Path $qa 'owned-module-dialogs'))
Test-Program 'ReportSettingsHeaderLayoutSmoke' @('tests\ReportSettingsHeaderLayoutSmoke.cs') @($BuildRoot,(Join-Path $qa 'report-settings-header'))
Test-Program 'SharedCableJointStoreSmoke' @('tests\SharedCableJointStoreSmoke.cs','src\CableJointStore.cs','src\ProjectMeasurementMode.cs') @($qa)
Test-Program 'SharedCableJointMutationSmoke' @('tests\SharedCableJointMutationSmoke.cs','src\CableJointStore.cs','src\ProjectMeasurementMode.cs') @($qa)
Test-Program 'SharedCableJointWritableTransactionSmoke' @('tests\SharedCableJointWritableTransactionSmoke.cs','src\CableJointStore.cs','src\ProjectMeasurementMode.cs') @($qa)
Test-Program 'CableJointCommitWarningSmoke' @('tests\CableJointCommitWarningSmoke.cs') @($BuildRoot,(Join-Path $qa 'joint-commit-warning'))
Test-Program 'CableJointDialogSmoke' @('tests\CableJointDialogSmoke.cs','src\CableJointStore.cs','src\CableJointDialog.cs','src\ProjectMeasurementMode.cs','src\UiLanguage.cs','src\InternationalText.cs','src\InternationalText.Generated.cs') @($qa,$BuildRoot) @('/resource:assets\xiongxin.ico,XIONGXIN.Brand.ico')
Test-Program 'CableJointEditingSmoke' @('tests\CableJointEditingSmoke.cs') @($BuildRoot,(Join-Path $qa 'analysis-joints'))
Test-Program 'AnalysisPositionSettingsSmoke' @('tests\AnalysisPositionSettingsSmoke.cs') @($BuildRoot,(Join-Path $qa 'analysis-position-settings'))
Test-Program 'BrowserSharedJointSmoke' @('tests\BrowserSharedJointSmoke.cs') @($BuildRoot,(Join-Path $qa 'browser-shared-joints'))
Test-Program 'CalibrationSharedJointSmoke' @('tests\CalibrationSharedJointSmoke.cs') @($BuildRoot,(Join-Path $qa 'calibration-shared-joints'))
Test-Program 'CalibrationArchiveBrowserSmoke' (@('tests\CalibrationArchiveBrowserSmoke.cs','modules-src\DataBrowser\src\BrowserData.cs','modules-src\ReportGenerator\src\AnalysisRecovery.cs','src\LiveCalibrationRecord.cs','src\LegacyCalibrationBinding.cs','src\PressureRecordPublication.cs','src\CableJointStore.cs')+$filters) @($qa)
foreach($name in @('CalibrationPersistenceTimingSmoke','CalibrationViewportFeedbackSmoke','CalibrationDecayFeedbackSmoke','BrowserCalibrationActionsSmoke')) {
 Test-Program $name @('tests\'+$name+'.cs') @($BuildRoot,(Join-Path $qa $name))
}
Test-Program 'CalibrationComparisonAverageSmoke' @('tests\CalibrationComparisonAverageSmoke.cs','modules-src\DataBrowser\src\CalibrationComparisonMath.cs') @()
Test-Program 'CalibrationJointCandidatesSmoke' @('tests\CalibrationJointCandidatesSmoke.cs','modules-src\DataBrowser\src\CalibrationComparisonMath.cs','modules-src\DataBrowser\src\CalibrationJointCandidates.cs') @()
Test-Program 'BrowserAverageGeometrySmoke' @('tests\BrowserAverageGeometrySmoke.cs') @($BuildRoot,(Join-Path $qa 'BrowserAverageGeometrySmoke'))
Test-Program 'BrowserPlotLayoutSmoke' @('tests\BrowserPlotLayoutSmoke.cs','modules-src\DataBrowser\src\BrowserPlot.cs','modules-src\DataBrowser\src\BrowserTheme.cs') @($qa)
Test-Program 'BrowserHoverPerformanceSmoke' @('tests\BrowserHoverPerformanceSmoke.cs','modules-src\DataBrowser\src\BrowserPlot.cs','modules-src\DataBrowser\src\BrowserTheme.cs') @((Join-Path $qa 'browser-hover'))
Test-Program 'BrowserDemResponsivenessSmoke' @('tests\BrowserDemResponsivenessSmoke.cs') @($BuildRoot)
foreach($name in @('CalibrationAverageSelectionSmoke','BrowserJointSearchFeedbackSmoke','BrowserMeanSourceIntegritySmoke')) {
 Test-Program $name @('tests\'+$name+'.cs') @($BuildRoot,(Join-Path $qa $name))
}
Test-Program 'BrowserJointAsyncResponsiveSmoke' @('tests\BrowserJointAsyncResponsiveSmoke.cs','tests\BrowserMeanSourceIntegritySmoke.cs') @($BuildRoot,(Join-Path $qa 'browser-joint-async')) @('/main:BrowserJointAsyncResponsiveSmoke')
foreach($name in @('BrowserComparisonInteractionSmoke','BrowserJointRefreshSmoke','BrowserLengthDialogLayoutSmoke','CalibrationActionStyleSmoke','CreationMeasuredJointSmoke','CreationMeasuredJointEditSmoke','CreationJointInsulationSmoke','CreationSaveValidationSmoke','CreationFormLayoutSmoke','CreationTopologyJointSmoke','AnalysisStartAndSettingsSmoke','AnalysisReadableLayoutSmoke','PressureStatisticsUiSmoke','ReportFeedbackSmoke','ThreeModuleLanguageSmoke')) {
 Test-Program $name @('tests\'+$name+'.cs') @($BuildRoot,(Join-Path $qa $name))
}
Test-Program 'CreationMaterialCatalogSmoke' @('tests\CreationMaterialCatalogSmoke.cs') @($BuildRoot,(Join-Path $qa 'CreationMaterialCatalogSmoke')) @('/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll')
Test-Program 'CreationPrecisionTimestampSmoke' @('tests\CreationPrecisionTimestampSmoke.cs') @($BuildRoot,(Join-Path $qa 'CreationPrecisionTimestampSmoke'))
Test-Program 'ProjectSaveTransactionSmoke' @('tests\ProjectSaveTransactionSmoke.cs') @($BuildRoot,(Join-Path $qa 'ProjectSaveTransactionSmoke'))
Test-Program 'ProjectLoadIntegritySmoke' @('tests\ProjectLoadIntegritySmoke.cs') @($BuildRoot,(Join-Path $qa 'ProjectLoadIntegritySmoke'))
Test-Program 'CreationTopologyViewportSmoke' @('tests\CreationTopologyViewportSmoke.cs') @($BuildRoot,(Join-Path $qa 'CreationTopologyViewportSmoke'))
Test-Program 'CreationDraftJointIntegritySmoke' @('tests\CreationDraftJointIntegritySmoke.cs') @($BuildRoot,(Join-Path $qa 'CreationDraftJointIntegritySmoke'))
Test-Program 'ProjectFileTransactionsSmoke' @('tests\ProjectFileTransactionsSmoke.cs') @($BuildRoot,(Join-Path $qa 'ProjectFileTransactionsSmoke')) @('/r:System.Web.Extensions.dll')
Test-Program 'SecureProjectStoreSmoke' @('tests\SecureProjectStoreSmoke.cs','src\SecureProjectStore.cs','src\SecureProjectStoreIndex.cs') @((Join-Path $qa 'secure-project-store')) @('/r:System.Security.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
Test-Program 'SecureProjectSessionSmoke' @('tests\SecureProjectSessionSmoke.cs','src\SecureProjectSession.cs','src\SecureProjectStore.cs','src\SecureProjectStoreIndex.cs') @((Join-Path $qa 'secure-project-session')) @('/r:System.Security.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$shellSources=@(Get-ChildItem -LiteralPath 'src' -Filter '*.cs' -File | ForEach-Object FullName)
Test-Program 'ProjectLoadTypeChoicesSmoke' @('tests\ProjectLoadTypeChoicesSmoke.cs','src\ProjectMeasurementMode.cs') @($BuildRoot,(Join-Path $qa 'project-load-types'))
Test-Program 'PressureProjectPlanCorrespondenceSmoke' (@('tests\PressureProjectPlanCorrespondenceSmoke.cs')+$shellSources) @((Join-Path $BuildRoot 'modules'),(Join-Path $qa 'pressure-project-plan')) @('/main:PressureProjectPlanCorrespondenceSmoke','/resource:assets\xiongxin.ico,Brand.ico','/resource:assets\xiongxin-original.png,Brand.png','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:Microsoft.CSharp.dll')
Test-Program 'PressurePlanLayoutSmoke' @('tests\PressurePlanLayoutSmoke.cs') @($BuildRoot,(Join-Path $qa 'pressure-plan-layout'))
Test-Program 'PressureNavigationLockSmoke' (@('tests\PressureNavigationLockSmoke.cs')+$shellSources) @((Join-Path $qa 'pressure-navigation-lock')) @('/main:PressureNavigationLockSmoke','/win32manifest:app.manifest','/resource:assets\xiongxin.ico,Brand.ico','/resource:assets\xiongxin-original.png,Brand.png','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:Microsoft.CSharp.dll')
Test-Program 'HardwareIdentityDefaultsSmoke' (@('tests\HardwareIdentityDefaultsSmoke.cs')+$shellSources) @((Join-Path $qa 'hardware-identity-defaults')) @('/main:HardwareIdentityDefaultsSmoke','/win32manifest:app.manifest','/resource:assets\xiongxin.ico,Brand.ico','/resource:assets\xiongxin-original.png,Brand.png','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:Microsoft.CSharp.dll')
Test-Program 'ProvisioningImportSmoke' @('tests\ProvisioningImportSmoke.cs','src\ProvisioningImportAdapter.cs') @() @('/main:Xiongxin.OWTS.Desktop.ProvisioningImportSmoke')
# This provider-only test must not see the distribution's live connector at its
# AppBase. Compile its process into an isolated QA subdirectory, as in the
# upstream Temp-based contract, rather than accidentally starting real inspect.
Test-Program 'HardwareRawIdentitySnapshotSmoke' (@('tests\HardwareRawIdentitySnapshotSmoke.cs')+$shellSources) @() @('/main:HardwareRawIdentitySnapshotSmoke','/resource:assets\xiongxin.ico,Brand.ico','/resource:assets\xiongxin-original.png,Brand.png','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:Microsoft.CSharp.dll') (Join-Path $qa 'identity-provider-runtime')
Test-Program 'SettingsCompareSaveSmoke' (@('tests\SettingsCompareSaveSmoke.cs')+$shellSources) @() @('/main:SettingsCompareSaveSmoke','/resource:assets\xiongxin.ico,Brand.ico','/resource:assets\xiongxin-original.png,Brand.png','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:Microsoft.CSharp.dll') (Join-Path $qa 'settings-cas-runtime')
Test-Program 'FirstRunEntryCasSmoke' (@('tests\FirstRunEntryCasSmoke.cs')+$shellSources) @() @('/main:FirstRunEntryCasSmoke','/define:FIRST_RUN_TESTS','/resource:assets\xiongxin.ico,Brand.ico','/resource:assets\xiongxin-original.png,Brand.png','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:Microsoft.CSharp.dll') (Join-Path $qa 'first-run-cas-runtime')
foreach($name in @('NoiseToleranceSettingsSmoke','StateGridReportSettingsSmoke','PressurePreferencesSettingsSmoke','AiFeatureSettingsSmoke','CompetitionModuleSettingsSmoke','PlusDataSettingsSmoke','SettingsIntegritySmoke','ModuleCableSyncSmoke','ProjectReleaseSmoke','ProjectEditorNavigationSmoke','ProjectHeaderIdentitySmoke','HomeDeviceActionsSmoke','HomeDeviceConnectionSmoke','HomeConnectionProjectActionsSmoke','ModuleNavigationPerformanceSmoke','ModulePresentationSmoke','HomeStatusVlfSmoke')) {
 $testArguments = if($name -in @('PlusDataSettingsSmoke','ProjectReleaseSmoke','ModuleNavigationPerformanceSmoke')) { @($BuildRoot,(Join-Path $qa $name)) } else { @((Join-Path $qa $name)) }
 $testExtras = @(('/main:'+$name),'/resource:assets\xiongxin.ico,Brand.ico','/resource:assets\xiongxin-original.png,Brand.png','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:Microsoft.CSharp.dll')
 if($name -in @('ModuleNavigationPerformanceSmoke','ProjectHeaderIdentitySmoke','HomeDeviceActionsSmoke','HomeDeviceConnectionSmoke','HomeConnectionProjectActionsSmoke')) { $testExtras += '/win32manifest:app.manifest' }
 Test-Program $name (@('tests\'+$name+'.cs')+$shellSources) $testArguments $testExtras
}
Test-Program 'DeviceSetupNavigationSmoke' @('tests\DeviceSetupNavigationSmoke.cs') @($BuildRoot,(Join-Path $qa 'DeviceSetupNavigationSmoke'))
Test-Program 'ModuleAuditSmoke' (@('tests\ModuleAuditSmoke.cs')+$shellSources) @($BuildRoot,(Join-Path $qa 'ModuleAuditSmoke')) @('/main:ModuleAuditSmoke','/resource:assets\xiongxin.ico,Brand.ico','/resource:assets\xiongxin-original.png,Brand.png','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:Microsoft.CSharp.dll')
Test-Program 'DeviceSetupStatusSmoke' @('tests\DeviceSetupStatusSmoke.cs') @($BuildRoot,(Join-Path $qa 'DeviceSetupStatusSmoke'))
Test-Program 'HardwareDisconnectSmoke' @('tests\HardwareDisconnectSmoke.cs','src\HardwareStatusAdapter.cs','modules-src\HardwareConnector\Integration.cs','modules-src\HardwareConnector\Integration.Discovery.cs') @((Join-Path $qa 'HardwareDisconnectSmoke')) @('/main:HardwareDisconnectSmoke','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll')
Test-Program 'HardwareArchitectureDiscoverySmoke' @('tests\HardwareArchitectureDiscoverySmoke.cs','modules-src\HardwareConnector\Integration.cs','modules-src\HardwareConnector\Integration.Discovery.cs') @((Join-Path $qa 'HardwareArchitectureDiscoverySmoke')) @('/main:HardwareArchitectureDiscoverySmoke','/r:System.Xml.Linq.dll')
Test-Program 'DetectionDeviceSelectionSmoke' (@('tests\DetectionDeviceSelectionSmoke.cs')+$shellSources) @((Join-Path $qa 'detector-selection')) @('/main:DetectionDeviceSelectionSmoke','/win32manifest:app.manifest','/resource:assets\xiongxin.ico,Brand.ico','/resource:assets\xiongxin-original.png,Brand.png','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:Microsoft.CSharp.dll') (Join-Path $qa 'detector-runtime')
Test-Program 'DpiLayoutSmoke' (@('tests\DpiLayoutSmoke.cs')+$shellSources) @((Join-Path $qa 'shell-layout')) @('/main:DpiLayoutSmoke','/win32manifest:app.manifest','/resource:assets\xiongxin.ico,Brand.ico','/resource:assets\xiongxin-original.png,Brand.png','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:Microsoft.CSharp.dll')
Test-Program 'AnalysisDeploymentSmoke' @('tests\AnalysisDeploymentSmoke.cs') @($BuildRoot,(Join-Path $qa 'analysis-deployment'))
Test-Program 'Task28SourceIntegrationSmoke' @('tests\Task28SourceIntegrationSmoke.cs') @($BuildRoot) @('/r:System.Web.Extensions.dll')
# Task17 neural candidate is deliberately outside this Task41 release.
# Its incomplete host regression must not be counted as a passing release group.
Test-Program 'DeferredTask17BoundarySmoke' @('tests\DeferredTask17BoundarySmoke.cs') @($BuildRoot) @('/r:System.Web.Extensions.dll')
Test-Program 'LocationCyclePhaseSmoke' @('tests\LocationCyclePhaseSmoke.cs') @($BuildRoot,(Join-Path $qa 'location-cycle-phase'))
Test-Program 'PublishedLocationCyclePhaseSmoke' @('tests\PublishedLocationCyclePhaseSmoke.cs') @($BuildRoot,(Join-Path $qa 'published-location-cycle-phase'))
Test-Program 'PressurePreferencesSmoke' @('tests\PressurePreferencesSmoke.cs') @($BuildRoot,(Join-Path $qa 'pressure-preferences'))
Test-Program 'PressureFinishCurrentRoundSmoke' @('tests\PressureFinishCurrentRoundSmoke.cs') @($BuildRoot,(Join-Path $qa 'pressure-finish-current'))
Test-Program 'PressureSidebarLayoutSmoke' @('tests\PressureSidebarLayoutSmoke.cs') @($BuildRoot,(Join-Path $qa 'pressure-sidebar-layout'))
Test-Program 'PressureProgressSmoke' @('tests\PressureProgressSmoke.cs') @($BuildRoot,(Join-Path $qa 'pressure-progress'))
Test-Program 'PressureWaveformMetricsSmoke' @('tests\PressureWaveformMetricsSmoke.cs','src\LocationCycleRecord.cs','modules-src\PressureTest\src\PressureMetricWindow.cs') @($BuildRoot,(Join-Path $qa 'pressure-waveform-metrics')) @('/r:System.Web.Extensions.dll')
Test-Program 'PressureActionFeedbackSmoke' @('tests\PressureActionFeedbackSmoke.cs','src\PressureFeedbackDisplay.cs') @($BuildRoot,(Join-Path $qa 'pressure-action-feedback')) @('/r:System.Xml.Linq.dll')
Test-Program 'PressureCompletedPlanSelectionSmoke' @('tests\PressureCompletedPlanSelectionSmoke.cs') @($BuildRoot,(Join-Path $qa 'pressure-completed-selection'))
Test-Program 'PressurePhasePlanResetSmoke' @('tests\PressurePhasePlanResetSmoke.cs') @($BuildRoot,(Join-Path $qa 'pressure-phase-reset'))
Test-Program 'PressureOverRangeRetrySmoke' @('tests\PressureOverRangeRetrySmoke.cs') @($BuildRoot,(Join-Path $qa 'pressure-range-retry')) @('/r:System.Xml.Linq.dll')
Test-Program 'LocationCycleWindowSmoke' @('tests\LocationCycleWindowSmoke.cs','src\LocationCycleRecord.cs') @() @('/r:System.Web.Extensions.dll')
Test-Program 'PressureAcquisitionCycleSmoke' @('tests\PressureAcquisitionCycleSmoke.cs','src\LocationCycleRecord.cs') @($BuildRoot,(Join-Path $qa 'pressure-acquisition-cycle')) @('/r:System.Web.Extensions.dll')
Test-Program 'PressureP04FeedbackSmoke' @('tests\PressureP04FeedbackSmoke.cs') @($BuildRoot,(Join-Path $qa 'pressure-p04-feedback'))
Test-Program 'PressureZeroVoltageBackendSmoke' @('tests\PressureZeroVoltageBackendSmoke.cs') @($BuildRoot,(Join-Path $qa 'pressure-zero-backend'))
Test-Program 'Task39HostContractSmoke' (@('tests\Task39HostContractSmoke.cs')+$shellSources) @($BuildRoot,(Join-Path $qa 'task39-host')) @('/main:Task39HostContractSmoke','/resource:assets\xiongxin.ico,Brand.ico','/resource:assets\xiongxin-original.png,Brand.png','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:Microsoft.CSharp.dll')
Test-Program 'Task15EmbeddedIntegrationSmoke' @('tests\Task15EmbeddedIntegrationSmoke.cs') @($BuildRoot,(Join-Path $qa 'Task15EmbeddedIntegrationSmoke')) @('/r:System.Web.Extensions.dll')
Test-Program 'AnalysisSaveDirtySmoke' @('tests\AnalysisSaveDirtySmoke.cs') @($BuildRoot,(Join-Path $qa 'analysis-save-dirty'))
Test-Program 'AnalysisReviewedStateSmoke' @('tests\AnalysisReviewedStateSmoke.cs') @($BuildRoot,(Join-Path $qa 'analysis-reviewed-state'))
Test-Program 'ChargeCountOverlaySmoke' @('tests\ChargeCountOverlaySmoke.cs') @($BuildRoot,(Join-Path $qa 'charge-count-overlay'))
Test-Program 'AnalysisOverwriteExitSmoke' @('tests\AnalysisOverwriteExitSmoke.cs') @($BuildRoot,(Join-Path $qa 'analysis-overwrite-exit'))
Test-Program 'UnsavedAnalysisNavigationSmoke' (@('tests\UnsavedAnalysisNavigationSmoke.cs')+$shellSources) @() @('/main:UnsavedAnalysisNavigationSmoke','/resource:assets\xiongxin.ico,Brand.ico','/resource:assets\xiongxin-original.png,Brand.png','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:Microsoft.CSharp.dll')
Test-Program 'ProjectActivationSmoke' (@('tests\ProjectActivationSmoke.cs')+$shellSources) @($BuildRoot,(Join-Path $qa 'project-activation')) @('/main:ProjectActivationSmoke','/resource:assets\xiongxin.ico,Brand.ico','/resource:assets\xiongxin-original.png,Brand.png','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:Microsoft.CSharp.dll')
$managementSources=@(Get-ChildItem -LiteralPath 'modules-src\ProjectManagement\src' -Filter '*.cs' -File | ForEach-Object FullName)+@('src\UiLanguage.cs','src\InternationalText.cs','src\InternationalText.Generated.cs','src\ProjectMeasurementMode.cs')
Test-Program 'ProjectInsulationColumnSmoke' (@('tests\ProjectInsulationColumnSmoke.cs')+$managementSources) @((Join-Path $qa 'project-insulation-column')) @('/r:System.Web.Extensions.dll','/r:System.Xml.Linq.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Microsoft.CSharp.dll')
Test-Program 'ProjectCurrentPinSmoke' (@('tests\ProjectCurrentPinSmoke.cs')+$managementSources) @((Join-Path $qa 'project-current-pin')) @('/r:System.Web.Extensions.dll','/r:System.Xml.Linq.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Microsoft.CSharp.dll')
Test-Program 'ProjectInsulationStatusSmoke' @('tests\ProjectInsulationStatusSmoke.cs') @($BuildRoot,(Join-Path $qa 'project-insulation-status')) @('/r:System.Web.Extensions.dll')
Test-Program 'ReportSavedConclusionSmoke' @('tests\ReportSavedConclusionSmoke.cs') @($BuildRoot,(Join-Path $qa 'report-saved-conclusion')) @('/r:System.Web.Extensions.dll')
Test-Program 'ReportActionsStructureSmoke' @('tests\ReportActionsStructureSmoke.cs') @($BuildRoot,(Join-Path $qa 'report-actions-structure'))
Test-Program 'ReportPresentationSmoke' @('tests\ReportPresentationSmoke.cs') @($BuildRoot,(Join-Path $qa 'report-presentation')) @('/r:System.Web.Extensions.dll')
Test-Program 'ReportCatalogSnapshotSmoke' @('tests\ReportCatalogSnapshotSmoke.cs') @($BuildRoot,(Join-Path $qa 'report-catalog-snapshot'))
Test-Program 'ReportProjectIdentitySmoke' @('tests\ReportProjectIdentitySmoke.cs') @($BuildRoot,(Join-Path $qa 'report-project-identity'))
# Keep filename fixtures shallow so exact multilingual names can be exercised;
# a separate assertion covers deliberately overlong output directories.
Test-Program 'ReportFileNamingSmoke' @('tests\ReportFileNamingSmoke.cs') @($BuildRoot,(Join-Path $BuildRoot 'qa'))
Test-Program 'BrowserWheelAxesSmoke' @('tests\BrowserWheelAxesSmoke.cs') @($BuildRoot)
Test-Program 'BrowserSharedMeanJointSmoke' @('tests\BrowserSharedMeanJointSmoke.cs','tests\BrowserAverageGeometrySmoke.cs') @($BuildRoot,(Join-Path $qa 'browser-shared-mean-joint')) @('/main:BrowserSharedMeanJointSmoke')
Test-Program 'CalibrationGeometryFeedbackSmoke' @('tests\CalibrationGeometryFeedbackSmoke.cs') @($BuildRoot,(Join-Path $qa 'calibration-geometry-feedback'))
Test-Program 'CompetitionDefectHostSmoke' @('tests\CompetitionDefectHostSmoke.cs') @($BuildRoot,(Join-Path $qa 'competition-host')) @('/r:System.Security.dll')
Test-Program 'CompetitionDefectRecognitionSmoke' @('tests\CompetitionDefectRecognitionSmoke.cs') @($BuildRoot,(Join-Path $qa 'competition-recognition')) @('/r:System.Web.Extensions.dll')
Test-Program 'CompetitionLearningPathBudgetSmoke' @('tests\CompetitionLearningPathBudgetSmoke.cs','modules-src\DataAnalysis\src\ReviewLearningTransactions.cs') @($BuildRoot) @('/r:System.Web.Extensions.dll')
Test-Program 'CompetitionScientificLayoutSmoke' @('tests\CompetitionScientificLayoutSmoke.cs') @($BuildRoot,(Join-Path $qa 'competition-scientific-layout')) @('/r:System.Web.Extensions.dll','/win32manifest:app.manifest')
Test-Program 'CompetitionSpectrumViewsSmoke' @('tests\CompetitionSpectrumViewsSmoke.cs') @($BuildRoot,(Join-Path $qa 'competition-spectrum-views'))
Test-Program 'OrdinarySpectrumBridgeSmoke' @('tests\OrdinarySpectrumBridgeSmoke.cs','modules-src\DataAnalysis\vendor\Spectrum\tests\ReviewBridgeRegression.cs') @($BuildRoot) @('/main:OrdinarySpectrumBridgeSmoke',('/r:'+(Join-Path $BuildRoot 'modules\DataAnalysis\HVX.PdReview.V1.0.dll')))
Test-Program 'AnalysisUiStabilitySmoke' @('tests\AnalysisUiStabilitySmoke.cs') @($BuildRoot,(Join-Path $qa 'analysis-ui-stability'))
Test-Program 'AnalysisLanguageNoOpSmoke' @('tests\AnalysisLanguageNoOpSmoke.cs') @($BuildRoot)
Test-Program 'CalibrationInteractionRegressionSmoke' @('tests\CalibrationInteractionRegressionSmoke.cs') @($BuildRoot,(Join-Path $qa 'calibration-interaction'))
Test-Program 'AnalysisAiAutomaticEvaluationSmoke' @('tests\AnalysisAiAutomaticEvaluationSmoke.cs') @($BuildRoot,(Join-Path $qa 'ai-auto-evaluation'))
Test-Program 'AnalysisInvariantDateSmoke' @('tests\AnalysisInvariantDateSmoke.cs') @($BuildRoot,(Join-Path $qa 'analysis-dates')) @('/r:System.Web.Extensions.dll')
Test-Program 'EnvironmentAutoRefreshSmoke' @('tests\EnvironmentAutoRefreshSmoke.cs','src\ModuleHost.Environment.cs','src\EnvironmentalReadings.cs') @()
Test-Program 'EnvironmentUiBridgeSmoke' @('tests\EnvironmentUiBridgeSmoke.cs','src\ModuleHost.Environment.cs','src\EnvironmentalReadings.cs') @($BuildRoot)
Test-Program 'EnvironmentalAcquisitionSmoke' @('tests\EnvironmentalAcquisitionSmoke.cs','src\ArtEnvironmentReader.cs','src\GnssUdpReader.cs','src\EnvironmentalReadings.cs') @()
$creationSources=@(Get-ChildItem -LiteralPath 'modules-src\ProjectCreation\src' -Filter '*.cs' -File | ForEach-Object FullName)+@('src\UiLanguage.cs','src\InternationalText.cs','src\InternationalText.Generated.cs','src\ProjectMeasurementMode.cs','src\CableJointStore.cs','src\CableJointDialog.cs')
Test-Program 'CableJointManagerSmoke' (@('tests\CableJointManagerSmoke.cs')+$creationSources) @((Join-Path $qa 'joint-manager')) @('/main:CableJointManagerSmoke','/resource:assets\xiongxin.ico,XIONGXIN.Brand.ico','/resource:assets\xiongxin-capacitor-hd.png,XIONGXIN.Capacitor.png','/r:System.Web.Extensions.dll','/r:System.Xml.Linq.dll')
Test-Program 'JointDefaultsFeedbackSmoke' (@('tests\JointDefaultsFeedbackSmoke.cs')+$creationSources) @((Join-Path $qa 'joint-defaults')) @('/main:JointDefaultsFeedbackSmoke','/resource:assets\xiongxin.ico,XIONGXIN.Brand.ico','/r:System.Web.Extensions.dll','/r:System.Xml.Linq.dll')
$projectDateSources=@($creationSources+$managementSources | Where-Object { [IO.Path]::GetFileName($_) -ne 'AssemblyInfo.cs' } | Select-Object -Unique)
Test-Program 'ProjectInvariantTimeSmoke' (@('tests\ProjectInvariantTimeSmoke.cs')+$projectDateSources) @((Join-Path $qa 'project-invariant-time')) @('/main:ProjectInvariantTimeSmoke','/resource:assets\xiongxin.ico,XIONGXIN.Brand.ico','/resource:assets\xiongxin-capacitor-hd.png,XIONGXIN.Capacitor.png','/r:System.Web.Extensions.dll','/r:System.Xml.Linq.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Microsoft.CSharp.dll')
Test-Program 'ReportBundledTemplateSmoke' @('tests\ReportBundledTemplateSmoke.cs') @($BuildRoot,(Join-Path $qa 'ReportBundledTemplateSmoke')) @('/r:System.Xml.Linq.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
Test-Program 'ReportAppendixLayoutSmoke' @('tests\ReportAppendixLayoutSmoke.cs') @($BuildRoot,(Join-Path $qa 'report-appendix')) @('/r:System.Xml.Linq.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
Test-Program 'StateGridReportSmoke' @('tests\StateGridReportSmoke.cs') @($BuildRoot,(Join-Path $qa 'StateGridReportSmoke')) @('/r:System.Xml.Linq.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
Test-Program 'LocationCountAxisSmoke' @('tests\LocationCountAxisSmoke.cs','modules-src\ReportGenerator\src\LocationChartRenderer.cs','modules-src\ReportGenerator\src\ReportModels.cs','modules-src\ReportGenerator\src\ProjectReportData.cs','modules-src\ReportGenerator\src\ReportTheme.cs','modules-src\ReportGenerator\src\ReportLanguages.cs','modules-src\ReportGenerator\src\ReportTraditionalResources.cs','src\UiLanguage.cs','src\InternationalText.cs','src\InternationalText.Generated.cs','src\CableJointStore.cs','src\ProjectMeasurementMode.cs') @((Join-Path $qa 'LocationCountAxisSmoke'))
Test-Program 'EmbeddedModulePayloadSmoke' @('tests\EmbeddedModulePayloadSmoke.cs') @($BuildRoot,(Join-Path $qa 'embedded'))
if ($Hybrid) { Test-Program 'HybridRuntimeSmoke' @('tests\HybridRuntimeSmoke.cs') @($BuildRoot,(Join-Path $qa 'hybrid-runtime')) }
if ($Hybrid) { Test-Program 'HybridPhasePerformanceSmoke' @('tests\HybridPhasePerformanceSmoke.cs','src\LocationCycleRecord.cs') @((Join-Path $BuildRoot 'modules\Hybrid\HvxHybridCore.dll'),$actualBuildManifest.HybridArchitecture.NativeSha256,(Join-Path $qa 'hybrid-performance')) @('/r:System.Web.Extensions.dll') }
# This group invokes only the upstream --self-test. Its simulated records and
# owned job probes stay in a fresh QA directory, never inside staged modules.
if($testStarted -or $StartAtTest -eq 'Task39WorkerOfflineSmoke') {
 $testStarted=$true
 $workerBuild=Join-Path $qa 'task39-worker\modules'
 $workerLines=@(& .\tools\Build-Task39InputWorker.ps1 -OutputRoot $workerBuild -RunOfflineTests 6>&1 | ForEach-Object {$_.ToString()})
 foreach($file in @('Xiongxin.Task39.InputWorker.exe','Xiongxin.Task39.InputWorker.exe.config','source-manifest.json')) {
  $rebuilt=Join-Path $workerBuild ('Task39\'+$file)
  $shipped=Join-Path $BuildRoot ('modules\Task39\'+$file)
  if((Get-FileHash -LiteralPath $rebuilt -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $shipped -Algorithm SHA256).Hash){throw ('Task39 source-verified offline worker differs from distribution: '+$file)}
 }
 $workerHash=(Get-FileHash -LiteralPath (Join-Path $workerBuild 'Task39\Xiongxin.Task39.InputWorker.exe') -Algorithm SHA256).Hash
 $workerLines+=('TASK39_WORKER_SOURCE_AND_OFFLINE_PASS='+$workerHash)
 $workerLines | Set-Content -LiteralPath (Join-Path $qa 'Task39WorkerOfflineSmoke.result.txt') -Encoding UTF8
 $results.Add([ordered]@{Name='Task39WorkerOfflineSmoke';ExitCode=0;SHA256=$workerHash;HardwareAccessed=$false;Output=$workerLines})
 $workerLines | Write-Output
}
$productFiles=@(Get-DistributionFiles | Where-Object {$_.FullName -eq (Join-Path $BuildRoot 'Xiongxin.OWTS.Desktop.exe') -or ($_.FullName.StartsWith((Join-Path $BuildRoot 'modules')+'\',[StringComparison]::OrdinalIgnoreCase) -and $_.Extension -in @('.dll','.exe'))})
$productHashes=@($productFiles | ForEach-Object {[ordered]@{Path=$_.FullName.Substring($BuildRoot.Length+1);SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
if(@(Get-DistributionFiles).Count -ne $distributionHashes.Count){throw 'The runtime file inventory changed during regression.'}
foreach($entry in $distributionHashes) {
 if((Get-FileHash -LiteralPath (Join-Path $BuildRoot $entry.Path) -Algorithm SHA256).Hash -ne $entry.SHA256){throw ('Runtime file changed during regression: '+$entry.Path)}
}
$currentInputs=@(Get-VerificationInputs)
if($currentInputs.Count -ne $verificationInputs.Count){throw 'Build/test input inventory changed during regression; rebuild from a frozen source snapshot.'}
for($inputIndex=0;$inputIndex -lt $verificationInputs.Count;$inputIndex++) {
 if($currentInputs[$inputIndex].Path -ne $verificationInputs[$inputIndex].Path -or $currentInputs[$inputIndex].SHA256 -ne $verificationInputs[$inputIndex].SHA256){throw ('Build/test input changed during regression: '+$verificationInputs[$inputIndex].Path)}
}
if(-not $testStarted){throw ('Unknown diagnostic start test: '+$StartAtTest)}
# Diagnostic continuation never produces the complete-release PASS artifact.
$completeRun=[String]::IsNullOrWhiteSpace($StartAtTest)
$reportName=if($completeRun){'verification.json'}else{'verification-partial.json'}
$status=if($completeRun){'PASS'}else{'PARTIAL_PASS'}
[ordered]@{Status=$status;StartAtTest=$StartAtTest;BuiltUtc=[DateTime]::UtcNow.ToString('o');HardwareAccessed=$false;PhysicalAcceptance=$false;Tests=$results.ToArray();ProductBinaries=$productHashes;DistributionFiles=$distributionHashes;BuildTestInputs=$verificationInputs;BuildTestInputsUnchanged=$true} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $qa $reportName) -Encoding UTF8
Write-Output ('VERIFICATION_REPORT='+$qa)
$script:currentRegressionTest=$null
Save-RegressionProgress 'COMPLETED_NONRELEASE'
} catch {
 try {Save-RegressionProgress 'FAIL' $_.Exception.Message;Write-Output ('INCOMPLETE_REGRESSION_RECEIPT='+(Join-Path $qa 'verification-progress.json'))}catch{Write-Warning ('Could not preserve incremental regression receipt: '+$_.Exception.Message)}
 throw
}
