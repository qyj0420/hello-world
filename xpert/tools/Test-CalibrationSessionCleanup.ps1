param([Parameter(Mandatory=$true)][string]$OutputRoot,[ValidateSet('CalibrationSessionCleanupSmoke','CalibrationSessionReuseSmoke','CalibrationEmergencyPollingSmoke','CalibrationEntrySafetySmoke','CalibrationDeleteResaveSmoke','CalibrationRecordLifecycleR50Smoke','PressureCalibrationSelectionSmoke','CalibrationFormatRoutingSmoke','CalibrationEntryRecordFeedbackSmoke','CalibrationLocalizationFocusedSmoke','CalibrationPacketTimingSmoke','CalibrationSelectedPacketRecoverySmoke')][string]$TestClass='CalibrationSessionCleanupSmoke')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root=$repo # Existing bounded runner uses this name for its local runtime policy.
Set-Location -LiteralPath $repo
$output=[IO.Path]::GetFullPath($OutputRoot)
if (!$output.StartsWith($repo+'\build\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Use a new project-local build proof directory.' }
if (Test-Path -LiteralPath $output) { throw 'Proof directory already exists.' }
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'Bounded runner helper cannot be parsed.' }
foreach ($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')) {
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if ($node.Count -ne 1) { throw 'Bounded runner helper missing.' }
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $output -AllowMissing
New-Item -ItemType Directory -Path $output | Out-Null
$sources=@(('tests/'+$TestClass+'.cs'),'src/UiLanguage.cs','src/InternationalText.cs','src/InternationalText.Generated.cs',
'src/ProjectMeasurementMode.cs','src/OemUiBranding.cs','src/PdLinearHighPass.cs','src/PdLinearLowPass.cs','src/LiveCalibrationRecord.cs',
'src/LegacyCalibrationBinding.cs','src/NoisePulseTemplates.cs','src/PicoCaptureCadence.cs','src/CableJointStore.cs','src/CableJointDialog.cs','src/R2RoundedButton.cs','src/CalibrationPulseDisplayWindow.cs',
'modules-src/LocationABCD/ABRelease/AB/source/PeakAnchoredLeadingEdgeMath.cs')
if($TestClass -in @('CalibrationRecordLifecycleR50Smoke','PressureCalibrationSelectionSmoke')){$sources+='src/PressureCalibrationSelection.cs'}
$sources+=@(Get-ChildItem -LiteralPath 'modules-src/CalibrationTest/src' -File -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
$sources+=@(Get-ChildItem -LiteralPath 'modules-src/CalibrationStandalone' -File -Filter '*.cs' | Where-Object Name -notin @('Program.cs','ControlBoxTests.cs') | Sort-Object Name | ForEach-Object FullName)
$sources+=@(Get-ChildItem -LiteralPath 'modules-src/CalibrationStandalone/PicoCore' -File -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
$sources=@($sources|ForEach-Object{if([IO.Path]::IsPathRooted($_)){$_}else{Join-Path $repo $_}})
$inputPaths=@($sources)+@($PSCommandPath,(Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),
    'tools/Generate-InternationalCatalog.ps1','assets/i18n/calibration.packs.txt','assets/i18n/calibration-safety.packs.txt','assets/i18n/calibration-flow.packs.txt',
    'modules-src/CalibrationTest/assets/approvedUI.png','modules-src/CalibrationTest/assets/brand.ico',
    'modules-src/CalibrationStandalone/assets/xiongxin.ico','modules-src/CalibrationStandalone/assets/xiongxin-original.png','assets/xiongxin.ico')+
    @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'compiler') -File|Where-Object Extension -in @('.exe','.dll','.config','.rsp')|ForEach-Object FullName)
if($TestClass -eq 'PressureCalibrationSelectionSmoke'){$inputPaths+='src/ModuleHost.FactoryPressure.cs'}
$inputPaths=@($inputPaths|ForEach-Object{if([IO.Path]::IsPathRooted($_)){$_}else{Join-Path $repo $_}})
$inputs=@($inputPaths|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=[IO.Path]::GetFullPath($_);SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$exe=Join-Path $output 'CalibrationSessionCleanupFakeOnly.exe'
$arguments=@('/nologo','/target:exe','/platform:x64','/langversion:7.3',('/main:'+$TestClass),'/r:System.dll','/r:System.Core.dll',
'/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll','/r:Accessibility.dll',
('/out:'+$exe),'/resource:modules-src/CalibrationTest/assets/approvedUI.png,HVX.CalibrationProgram.ApprovedUi.png',
'/resource:modules-src/CalibrationTest/assets/brand.ico,HVX.CalibrationProgram.Xiongxin.ico',
'/resource:modules-src/CalibrationStandalone/assets/xiongxin.ico,Brand.ico','/resource:modules-src/CalibrationStandalone/assets/xiongxin-original.png,Brand.png',
'/resource:assets/xiongxin.ico,XIONGXIN.Brand.ico')
$lines=@(& (Join-Path $PSScriptRoot 'compiler/csc.exe') @arguments @sources 2>&1|ForEach-Object{$_.ToString()})
$compileCode=$LASTEXITCODE
$lines|Set-Content -LiteralPath (Join-Path $output 'compile.txt') -Encoding UTF8
if ($compileCode -ne 0) { throw ($lines -join "`n") }
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @((Join-Path $output 'synthetic')) -LogPrefix (Join-Path $output 'session')
$mismatches=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count
$unchanged=$mismatches -eq 0
[ordered]@{Schema='HVX.Calibration.SessionCleanup.Offline/1';UTC=[DateTime]::UtcNow.ToString('o');HardwareAccessed=$false;ActualWorkerStarted=$false;
ProductStarted=$false;SourceUnchanged=$unchanged;InputMismatchCount=$mismatches;Inputs=$inputs;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash;Result=$run}|
ConvertTo-Json -Depth 9|Set-Content -LiteralPath (Join-Path $output 'proof.json') -Encoding UTF8
$run.Output|Write-Output
Write-Output ('SESSION_CLEANUP_PROOF='+(Join-Path $output 'proof.json'))
if ($run.ExitCode -ne 0 -or !$unchanged) { throw ('Session fake regression failed: '+$output) }
