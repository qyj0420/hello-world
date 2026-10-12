param([string]$PreviousBuild='build\main-test-20261009-r39')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$previous=[IO.Path]::GetFullPath((Join-Path $root $PreviousBuild))
$qa=Join-Path $root ('build\qa-calibration-candidates-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$output=Join-Path $qa 'modules\CalibrationTest';$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$sources=@(Get-ChildItem -LiteralPath 'modules-src\CalibrationTest\src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$sources+=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','R2RoundedButton.cs','CalibrationPulseDisplayWindow.cs','PdLinearHighPass.cs','LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','NoisePulseTemplates.cs','PdLinearLowPass.cs','PicoCaptureCadence.cs','CableJointStore.cs','CableJointDialog.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$sources+=Join-Path $root 'modules-src\LocationABCD\ABRelease\AB\source\PeakAnchoredLeadingEdgeMath.cs'
$sources+=@(Get-ChildItem -LiteralPath 'modules-src\CalibrationStandalone' -File -Filter '*.cs'|Where-Object {$_.Name -notin @('Program.cs','ControlBoxTests.cs')}|Sort-Object Name|ForEach-Object FullName)
$sources+=@(Get-ChildItem -LiteralPath 'modules-src\CalibrationStandalone\PicoCore' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$resources=@('modules-src\CalibrationTest\assets\approvedUI.png,HVX.CalibrationProgram.ApprovedUi.png','modules-src\CalibrationTest\assets\brand.ico,HVX.CalibrationProgram.Xiongxin.ico','modules-src\CalibrationStandalone\assets\xiongxin.ico,Brand.ico','modules-src\CalibrationStandalone\assets\xiongxin-original.png,Brand.png','assets\xiongxin.ico,XIONGXIN.Brand.ico')
$tests=@('CalibrationCandidateReviewSmoke','CalibrationUiAccessibilitySmoke','SharedCableJointStoreSmoke')
$testPaths=@($tests|ForEach-Object{Join-Path $root ('tests\'+$_+'.cs')})
$inputs=@($sources)+$testPaths+@($PSCommandPath,$helper,$csc,(Join-Path $previous 'modules\CalibrationTest\Xiongxin.OWTS.CalibrationTest.dll'))+@($resources|ForEach-Object{Join-Path $root $_.Split(',')[0]})
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o');New-Item -ItemType Directory -Path $output|Out-Null
$common=@('/nologo','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
$executables=@{}
foreach($name in $tests){
 $extra=@();if($name -eq 'SharedCableJointStoreSmoke'){$extra=@((Join-Path $root 'src\CableJointStore.cs'),(Join-Path $root 'src\ProjectMeasurementMode.cs'))}
 $exe=Join-Path $qa ($name+'.exe');$compiled=@(& $csc @common /target:exe ('/main:'+$name) ('/out:'+$exe) (Join-Path $root ('tests\'+$name+'.cs')) @extra 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ($compiled -join "`n")};$executables[$name]=$exe
}
$negative=Invoke-RegressionExecutable -Executable $executables['CalibrationCandidateReviewSmoke'] -Arguments @($previous,(Join-Path $qa 'negative')) -LogPrefix (Join-Path $qa 'negative-previous-package') -TimeoutMilliseconds 20000
$negativeExpected=$negative.ExitCode -ne 0 -and ($negative.Output -join "`n").Contains('per-candidate modal editor was actually opened')
if(-not $negativeExpected){throw ('Previous functional negative did not reproduce missing editor: '+($negative.Output -join "`n"))}
$dll=Join-Path $output 'Xiongxin.OWTS.CalibrationTest.dll'
$resourceArgs=@($resources|ForEach-Object{'/resource:'+ (Join-Path $root $_.Split(',')[0])+','+$_.Split(',')[1]})
$compiled=@(& $csc @common /target:library ('/out:'+$dll) @resourceArgs @sources 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'module.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
$runs=New-Object 'System.Collections.Generic.List[object]'
foreach($name in $tests){
 $arguments=@($qa,(Join-Path $qa ('positive-'+$runs.Count)));if($name -eq 'SharedCableJointStoreSmoke'){$arguments=@((Join-Path $qa 'store-fakes'))}
 $run=Invoke-RegressionExecutable -Executable $executables[$name] -Arguments $arguments -LogPrefix (Join-Path $qa $name) -TimeoutMilliseconds 90000
 $runs.Add([ordered]@{Name=$name;Execution=$run});$run.Output|Write-Output
}
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$passed=@($runs|Where-Object {$_.Execution.ExitCode -ne 0}).Count -eq 0;$proof=Join-Path $qa 'verification-candidate-review.json'
[ordered]@{Schema='HVX.Calibration.CandidateReview.Focused/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Status=$(if($passed -and $unchanged){'PASS'}else{'FAIL'});
HardwareAccessed=$false;SDKCalled=$false;ProductEntryExecuted=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;FullRegression=$false;
SourceUnchanged=$unchanged;Inputs=$before;ModuleSHA256=(Get-FileHash -LiteralPath $dll).Hash;NegativeExpected=$negativeExpected;NegativePreviousPackage=$negative;PositiveRuns=$runs.ToArray();
Scope='Synthetic per-candidate modal type/insulation review; exact unedited position; actual metadata transaction, stale-context guards, no manual overwrites, visibility restoration. Old high-confidence auto-confirm test is superseded, not rewritten.'}|
ConvertTo-Json -Depth 15|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output ('CANDIDATE_REVIEW_PROOF='+$proof)
if(-not $passed -or -not $unchanged){throw ('Candidate focused regression failed: '+$qa)}
