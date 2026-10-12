param([string]$BuildRoot='build\diag-segment-20261007')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
if(-not [IO.Path]::IsPathRooted($BuildRoot)){$BuildRoot=Join-Path $root $BuildRoot}
$BuildRoot=[IO.Path]::GetFullPath($BuildRoot)
if(-not $BuildRoot.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an isolated QA directory under build.'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded test runner.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
$qa=Join-Path $BuildRoot ('qa\segment-save-'+[guid]::NewGuid().ToString('N').Substring(0,8))
Assert-RegressionUnlinkedPath $qa -AllowMissing
$moduleFolder=Join-Path $qa 'modules\CalibrationTest'
New-Item -ItemType Directory -Path $moduleFolder | Out-Null
$sources=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','NoisePulseTemplates.cs','PicoCaptureCadence.cs','CableJointStore.cs','CableJointDialog.cs','R2RoundedButton.cs','CalibrationPulseDisplayWindow.cs')|ForEach-Object{Join-Path $root ('src\'+$_)}
$sources+=Join-Path $root 'modules-src\LocationABCD\ABRelease\AB\source\PeakAnchoredLeadingEdgeMath.cs'
$sources+=@(Get-ChildItem -LiteralPath 'modules-src\CalibrationTest\src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$sources+=@(Get-ChildItem -LiteralPath 'modules-src\CalibrationStandalone' -File -Filter '*.cs'|Where-Object Name -notin @('Program.cs','ControlBoxTests.cs')|Sort-Object Name|ForEach-Object FullName)
$sources+=@(Get-ChildItem -LiteralPath 'modules-src\CalibrationStandalone\PicoCore' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$resources=@('modules-src\CalibrationTest\assets\approvedUI.png,HVX.CalibrationProgram.ApprovedUi.png','modules-src\CalibrationTest\assets\brand.ico,HVX.CalibrationProgram.Xiongxin.ico','modules-src\CalibrationStandalone\assets\xiongxin.ico,Brand.ico','modules-src\CalibrationStandalone\assets\xiongxin-original.png,Brand.png','assets\xiongxin.ico,XIONGXIN.Brand.ico')
$names=@('CalibrationIncidentOnlySaveSmoke','CalibrationMissingReflectionSelectionSmoke','CalibrationQualityContractSmoke','CalibrationStopLatchSmoke','CalibrationUiAccessibilitySmoke','CalibrationAcCouplingSmoke')
$tests=@($names|ForEach-Object{Join-Path $root ('tests\'+$_+'.cs')})
$inputs=@(($sources+$tests+@($resources|ForEach-Object{($_ -split ',')[0]}))|ForEach-Object{[ordered]@{Path=[IO.Path]::GetFullPath($_);SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Accessibility.dll')
$dll=Join-Path $moduleFolder 'Xiongxin.OWTS.CalibrationTest.dll'
$resourceArgs=@($resources|ForEach-Object{'/resource:'+$_})
$lines=@(& $csc /nologo /noconfig /langversion:7.3 /optimize+ /deterministic+ /target:library /platform:x64 @refs ('/out:'+$dll) @resourceArgs @sources 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa 'module.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($lines -join "`n")}
$moduleHash=(Get-FileHash -LiteralPath $dll).Hash
$results=New-Object 'System.Collections.Generic.List[object]'
foreach($name in $names){
 $exe=Join-Path $qa ($name+'.exe')
 $lines=@(& $csc /nologo /langversion:7.3 /target:exe /platform:x64 @refs ('/out:'+$exe) (Join-Path $root ('tests\'+$name+'.cs')) 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ($lines -join "`n")}
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa,(Join-Path $qa ('case-'+$results.Count))) -LogPrefix (Join-Path $qa $name)
 $results.Add([ordered]@{Name=$name;Result=$run});$run.Output|Write-Output
 $unchanged=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count -eq 0
 [ordered]@{Schema='HVX.Calibration.SegmentSaving/1';CompletedUtc=[DateTime]::UtcNow.ToString('o');HardwareAccessed=$false;UserSettingsWritten=$false;UserProjectsWritten=$false;Inputs=$inputs;SourceUnchanged=$unchanged;Module=$dll;ModuleSHA256=$moduleHash;ModuleUnchanged=((Get-FileHash -LiteralPath $dll).Hash -ceq $moduleHash);Tests=$results.ToArray()}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
 if($run.ExitCode -ne 0 -or -not $unchanged){throw ('Segment save regression failed: '+$qa)}
}
Write-Output ('CALIBRATION_SEGMENT_SAVE_PROOF='+$qa)
