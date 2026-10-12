param([string]$OutputRoot='build/qa-r58-calibration-navigation-handoff')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not [IO.Path]::IsPathRooted($OutputRoot)){$OutputRoot=Join-Path $root $OutputRoot}
$qa=[IO.Path]::GetFullPath($OutputRoot)
if(-not $qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $qa)){throw 'Use a fresh build-contained output directory.'}
$null=New-Item -ItemType Directory -Path $qa
$compiler=Join-Path $PSScriptRoot 'compiler/csc.exe'
$runner=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')) {
 $nodes=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($nodes.Count -ne 1){throw 'Missing bounded offline runner helper.'}
 . ([ScriptBlock]::Create($nodes[0].Extent.Text))
}
$desktop=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File|Sort-Object Name|ForEach-Object FullName)
$calibration=@(Get-ChildItem -LiteralPath (Join-Path $root 'modules-src/CalibrationTest/src') -Filter '*.cs' -File|Sort-Object Name|ForEach-Object FullName)
$calibration+=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','R2RoundedButton.cs','CalibrationPulseDisplayWindow.cs','PdLinearHighPass.cs','LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','NoisePulseTemplates.cs','PdLinearLowPass.cs','PicoCaptureCadence.cs','CableJointStore.cs','CableJointDialog.cs','PressureRecordPublication.cs'|ForEach-Object{Join-Path $root ('src/'+$_)})
$calibration+=Join-Path $root 'modules-src/LocationABCD/ABRelease/AB/source/PeakAnchoredLeadingEdgeMath.cs'
$calibration+=@(Get-ChildItem -LiteralPath (Join-Path $root 'modules-src/CalibrationStandalone') -Filter '*.cs' -File|Where-Object Name -notin @('Program.cs','ControlBoxTests.cs')|Sort-Object Name|ForEach-Object FullName)
$calibration+=@(Get-ChildItem -LiteralPath (Join-Path $root 'modules-src/CalibrationStandalone/PicoCore') -Filter '*.cs' -File|Sort-Object Name|ForEach-Object FullName)
$helper=Join-Path $root 'src/MainForm.CalibrationHandoff.cs';$test=Join-Path $root 'tests/CalibrationNavigationHandoffOffline.cs'
$inputs=@($desktop+$calibration+@($helper,$test,$compiler,$runner,$PSCommandPath))|Sort-Object -Unique
function Inventory {@($inputs|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
$common=@('/nologo','/langversion:7.3','/deterministic+','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
function Compile([string]$name,[string]$kind,[string[]]$sources) {
 $dest=Join-Path $qa $name
 $log=@(& $compiler @common ('/target:'+$kind) ('/out:'+$dest) @sources 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$log|Set-Content -LiteralPath ($dest+'.compile.log') -Encoding UTF8
 if($code -ne 0){throw ($log -join "`n")};return $dest
}
$hostDll=Compile 'Host.CompileOnly.dll' 'library' $desktop
$calibrationDll=Compile 'Calibration.CompileOnly.dll' 'library' $calibration
$exe=Compile 'CalibrationNavigationHandoffOffline.exe' 'exe' @($helper,$test)
# This executable contains only the small managed handoff helper plus synthetic
# delegates. Neither compiled product assembly is loaded or executed.
$execution=Invoke-RegressionExecutable -Executable $exe -Arguments @() -LogPrefix (Join-Path $qa 'offline') -TimeoutMilliseconds 20000
$output=$execution.Output -join "`n"
if($execution.ExitCode -ne 0 -or $output -notmatch 'CALIBRATION_HANDOFF_OFFLINE_PASS checks=(\d+)'){throw $output}
$checks=[int]$Matches[1]
$main=[IO.File]::ReadAllText((Join-Path $root 'src/MainForm.cs'))
$navigation=[IO.File]::ReadAllText((Join-Path $root 'src/MainForm.CalibrationNavigation.cs'))
$entry=[IO.File]::ReadAllText((Join-Path $root 'modules-src/CalibrationTest/src/CalibrationProgramControl.Hardware.cs'))
$static=[ordered]@{
 LiveCalibrationRoutesToAwaitedHandoff=$main.Contains('OpenLiveCalibrationControlAsync(navigationRequest);')
 DrainBeforeStartupDelegate=$navigation.IndexOf('owner.DrainEnvironmentForHardwareAsync') -lt $navigation.IndexOf('Control page=OpenTestModuleControlCore')
 ActualEntryAwaited=$navigation.Contains('await (Task)RequireCalibrationEntryAsyncMethod(page).Invoke(page,null);')
 ActualEntryCancellationIsNotSilenced=$navigation.Contains('when(!entryAdmitted && (cancellation.IsCancellationRequested')
 NavigationOwnerProjectRechecked=$navigation.Contains('Object.ReferenceEquals(owner,moduleHost)') -and $navigation.Contains('String.Equals(project,state.ProjectPath')
 OldQueuedEntryRemoved= -not $main.Contains('GetMethod("NotifyCalibrationPageEntered")')
 PublicTaskApi=$entry.Contains('public Task NotifyCalibrationPageEnteredAsync()')
 ApiUiOwnerRequired=$entry.Contains('Environment.CurrentManagedThreadId!=ownerThreadId')
 DuplicateEntryJoinsSameTask=$entry.Contains('if(calibrationEntryCheckActive)return calibrationEntryCheckTask ?? Task.CompletedTask;')
 LegacyWrapperObservesTask=$entry.Contains('ObserveCalibrationEntryTask(NotifyCalibrationPageEnteredAsync());')
 SimulationAndVerificationSkipNative=$main.Contains('if(!verificationMode && !simulationMode && String.Equals(id,ModuleHost.CalibrationTest')
}
$staticPassed=@($static.Values|Where-Object{$_ -ne $true}).Count -eq 0
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$proof=Join-Path $qa 'verification-calibration-navigation-handoff.json'
[ordered]@{Schema='HVX.Calibration.NavigationHandoff.Offline/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Status=$(if($staticPassed -and $unchanged){'PASS'}else{'FAIL'});Checks=$checks+$static.Count;
 HardwareAccessed=$false;SDKCalled=$false;ProductEntryExecuted=$false;ProductAssembliesCompileOnly=$true;UserProjectsWritten=$false;PhysicalAcceptance=$false;
 SourceUnchanged=$unchanged;Inputs=$before;StaticChecks=$static;ManagedTestOutput=$output;Execution=$execution;HostDLLSHA256=(Get-FileHash -LiteralPath $hostDll).Hash;CalibrationDLLSHA256=(Get-FileHash -LiteralPath $calibrationDll).Hash;OfflineTestSHA256=(Get-FileHash -LiteralPath $exe).Hash;
 Scope='Synthetic drain/start completion, stale navigation, cancellation, Stop/Clear failure propagation, null-lease fail-closed, initialization failure and independent handoff ownership; host and calibration are compiled only.'}|
 ConvertTo-Json -Depth 10|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output $output;Write-Output ('CALIBRATION_NAVIGATION_HANDOFF_PROOF='+$proof)
if(-not $staticPassed -or -not $unchanged){throw 'Static/source stability check failed.'}
