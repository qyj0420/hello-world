param([ValidateSet('CalibrationCustomerCompatibilitySmoke','PressureCalibrationSelectionSmoke','LegacyCalibrationShortChoiceSmoke')][string]$TestClass='CalibrationCustomerCompatibilitySmoke')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-calibration-customer-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')) {
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Bounded runner helper missing.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing;New-Item -ItemType Directory -Path $qa|Out-Null
$sources=@('src/LiveCalibrationRecord.cs','src/LegacyCalibrationBinding.cs','src/PressureCalibrationSelection.cs',
'src/MainForm.LegacyCalibration.cs','src/LegacyCalibrationImportDialog.cs','src/R2RoundedButton.cs',
'src/ProjectMeasurementMode.cs','src/CableJointStore.cs','src/PdLinearHighPass.cs','src/PdLinearLowPass.cs','tests/CalibrationCustomerCompatibilitySmoke.cs')
if($TestClass -ne 'CalibrationCustomerCompatibilitySmoke'){$sources+=('tests/'+$TestClass+'.cs')}
if($TestClass -eq 'LegacyCalibrationShortChoiceSmoke'){$sources+=@('src/UiLanguage.cs','src/InternationalText.cs','src/InternationalText.Generated.cs')}
$paths=@($sources)+@('src/MainForm.CalibrationNavigation.cs',$PSCommandPath,$helper,'tools/compiler/csc.exe')
if($TestClass -eq 'LegacyCalibrationShortChoiceSmoke'){$paths+='assets/i18n/calibration-safety.packs.txt'}
if($TestClass -eq 'PressureCalibrationSelectionSmoke'){$paths+='src/ModuleHost.FactoryPressure.cs'}
$inputs=@($paths|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=[IO.Path]::GetFullPath($_);SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$exe=Join-Path $qa 'CalibrationCustomerCompatibilityFakeOnly.exe'
$compiled=@(& (Join-Path $PSScriptRoot 'compiler/csc.exe') /nologo /target:exe /platform:x64 /langversion:7.3 ('/main:'+$TestClass) /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:'+$exe) @sources 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @((Join-Path $qa 'synthetic'),$root) -LogPrefix (Join-Path $qa 'compatibility') -TimeoutMilliseconds 90000
$changed=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count
$proof=Join-Path $qa 'proof.json'
[ordered]@{Schema='HVX.Calibration.CustomerCompatibility.Offline/1';UTC=[DateTime]::UtcNow.ToString('o');TestClass=$TestClass;
ProductStarted=$false;WorkerStarted=$false;SDKCalled=$false;HardwareAccessed=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;FullRegression=$false;
SourceUnchanged=($changed -eq 0);InputMismatchCount=$changed;Inputs=$inputs;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash;Result=$run}|
ConvertTo-Json -Depth 10|Set-Content -LiteralPath $proof -Encoding UTF8
$run.Output|Write-Output;Write-Output ('CALIBRATION_CUSTOMER_PROOF='+$proof)
if($run.ExitCode -ne 0 -or $changed -ne 0){throw 'Calibration customer compatibility regression failed.'}
