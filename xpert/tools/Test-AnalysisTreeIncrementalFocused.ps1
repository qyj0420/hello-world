param([string]$BaselineBuild='D:\codex\HVXUnifiedDesktop\build\main-repair-20261010-r42')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$baseline=[IO.Path]::GetFullPath($BaselineBuild)
$qa=Join-Path $root ('build\qa-analysis-tree-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing;Assert-RegressionUnlinkedPath $baseline
if(-not $qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'QA must remain in owned build directory.'}
$sources=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','PressureRecordPublication.cs','ZeroVoltageNoiseReference.cs','DefectModelCatalog.cs','AnalysisDateText.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','NoisePulseTemplates.cs','LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','CableJointStore.cs','CableJointDialog.cs','CalibrationPacketIdentityContract.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$sources+=Join-Path $root 'src\LocationScorePolicy.cs'
foreach($part in @('src','vendor\PdMap','vendor\PdMap\Common','vendor\Spectrum','vendor\Task7','vendor\Task28','vendor\LocationTree','shared')){
 $sources+=@(Get-ChildItem -LiteralPath (Join-Path $root ('modules-src\DataAnalysis\'+$part)) -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
}
$sources+=@('modules-src\LocationABCD\ABRelease\AB\source\PeakAnchoredLeadingEdgeMath.cs','modules-src\ConclusionAlgorithm\EvidenceV3\HVX.Task15.Embedded.cs'|ForEach-Object{Join-Path $root $_})
$names=@('AnalysisTreeIncrementalSmoke','AnalysisReviewedStateSmoke','AnalysisCameraDisplayOnlySmoke')
$tests=@($names|ForEach-Object{Join-Path $root ('tests\'+$_+'.cs')})
$pinSource=Join-Path $root 'modules-src\LocationABCD\ABRelease\confirmed-legacy-inputs.json'
$brand=Join-Path $root 'assets\xiongxin.ico';$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$baselineModule=Join-Path $baseline 'modules\DataAnalysis\HVX.PdReview.V1.0.dll'
$dependencies=@(Get-ChildItem -LiteralPath (Join-Path $baseline 'modules\DataAnalysis') -Recurse -File|ForEach-Object FullName)
$inputs=$sources+$tests+$dependencies+@($PSCommandPath,$helper,$pinSource,$brand,$csc)
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o');New-Item -ItemType Directory -Path $qa|Out-Null
$common=@('/nologo','/noconfig','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
foreach($name in $names){
 $exe=Join-Path $qa ($name+'.exe')
 $compiled=@(& $csc @common /target:exe ('/main:'+$name) ('/out:'+$exe) (Join-Path $root ('tests\'+$name+'.cs')) 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ($compiled -join "`n")}
}
$negative=Invoke-RegressionExecutable -Executable (Join-Path $qa 'AnalysisTreeIncrementalSmoke.exe') -Arguments @($baseline,(Join-Path $qa 'baseline-cases')) -LogPrefix (Join-Path $qa 'baseline') -TimeoutMilliseconds 60000
$negative.Output|Write-Output
if($negative.ExitCode -eq 0 -or -not ($negative.Output -match 'Equal matrix causes zero notifications and zero new commit')){throw 'Baseline did not reproduce the exact equal-matrix notification defect.'}
if((Get-FileHash -LiteralPath $pinSource).Hash -cne '5BE1895AA9BF2D9449A4875112C1A3D976F0119D23F624E976141FE955C6D37D'){throw 'Legacy input pin source changed.'}
$moduleDirectory=Join-Path $qa 'modules\DataAnalysis';New-Item -ItemType Directory -Path $moduleDirectory|Out-Null
foreach($input in $dependencies){
 if($input -ceq $baselineModule){continue}
 $relative=$input.Substring((Join-Path $baseline 'modules\DataAnalysis').Length+1);$target=Join-Path $moduleDirectory $relative
 New-Item -ItemType Directory -Path (Split-Path $target) -Force|Out-Null;Copy-Item -LiteralPath $input -Destination $target
}
$pins=Join-Path $qa 'legacy-unit-pins.json';@((Get-Content -LiteralPath $pinSource -Raw -Encoding UTF8|ConvertFrom-Json)|ForEach-Object{$_.sha256})|ConvertTo-Json|Set-Content -LiteralPath $pins -Encoding UTF8
$module=Join-Path $moduleDirectory 'HVX.PdReview.V1.0.dll'
$compiled=@(& $csc @common /target:library ('/resource:'+$brand+',XIONGXIN.Brand.ico') ('/resource:'+$pins+',HVX.LegacyUnitPins') ('/out:'+$module) @sources 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'analysis.compile.txt') -Encoding UTF8;if($code -ne 0){throw ($compiled -join "`n")}
$results=New-Object 'System.Collections.Generic.List[object]';$failed=$false
foreach($name in $names){
 $exe=Join-Path $qa ($name+'.exe');$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa,(Join-Path $qa ($name+'-cases'))) -LogPrefix (Join-Path $qa $name) -TimeoutMilliseconds 60000
 $run.Output|Write-Output;$results.Add([ordered]@{Name=$name;Result=$run;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash});if($run.ExitCode -ne 0){$failed=$true}
}
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$proof=Join-Path $qa 'verification-analysis-tree.json'
[ordered]@{Schema='HVX.AnalysisTreeIncremental.Focused/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Status=$(if(-not $failed -and $unchanged){'PASS'}else{'FAIL'});Inputs=$before;SourceUnchanged=$unchanged;
 BaselineBuild=$baseline;BaselineModuleSHA256=(Get-FileHash -LiteralPath $baselineModule).Hash;ExpectedNegative=$negative;AnalysisSHA256=(Get-FileHash -LiteralPath $module).Hash;
 HardwareAccessed=$false;SDKCalled=$false;ProductEntryExecuted=$false;UserProjectWritten=$false;SameFinalProductBytes=$false;SourceIsolatedModule=$true;AlgorithmExecuted=$false;FullRegression=$false;Tests=$results.ToArray();
 Scope='Equal-value commits; exact row identity; local MAP/parent refresh; atomic review flags; structure rebuild state restoration; source generation reentry; review serialization; camera display-only compatibility.'}|ConvertTo-Json -Depth 15|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output ('ANALYSIS_TREE_PROOF='+$proof)
if($failed -or -not $unchanged){throw ('Analysis tree focused regression failed: '+$qa)}
