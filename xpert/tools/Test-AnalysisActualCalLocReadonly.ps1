param([string]$BuildRoot='build\main-test-20261009-r39',[Parameter(Mandatory=$true)][string]$SourceProject)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$build=[IO.Path]::GetFullPath((Join-Path $root $BuildRoot));$source=[IO.Path]::GetFullPath($SourceProject)
$qa=Join-Path $root ('build\qa-actual-cal-loc-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing;Assert-RegressionUnlinkedPath $source
$test=Join-Path $root 'tests\AnalysisActualCalibrationLocReadonly.cs';$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=@($PSCommandPath,$helper,$test,$csc,(Join-Path $build 'modules\DataAnalysis\HVX.PdReview.V1.0.dll'),(Join-Path $build 'modules\DataAnalysis\fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'))
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o');New-Item -ItemType Directory -Path $qa|Out-Null
$exe=Join-Path $qa 'AnalysisActualCalibrationLocReadonly.exe'
$compiled=@(& $csc /nologo /langversion:7.3 /platform:x64 /target:exe /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($build,(Join-Path $qa 'readonly'),$source) -LogPrefix (Join-Path $qa 'actual-cal-loc') -TimeoutMilliseconds 120000
$run.Output|Write-Output
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$proof=Join-Path $qa 'verification-actual-cal-loc-readonly.json'
[ordered]@{Schema='HVX.Actual.CalibrationLoc.Runner/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Status=$(if($run.ExitCode -eq 0 -and $unchanged){'PASS'}else{'FAIL'});SourceUnchanged=$unchanged;Inputs=$before;Execution=$run;
HardwareAccessed=$false;SDKCalled=$false;ProductEntryExecuted=$false;UserProjectWritten=$false;PhysicalAccuracyVerified=$false;FullRegression=$false;SourceProject=$source;
Scope='Read-only selected actual nonzero LOC+DEM+CAL+metadata; byte-exact QA copy; protected published frozen A/B managed algorithm; no physical acceptance.'}|ConvertTo-Json -Depth 15|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output ('ACTUAL_CAL_LOC_PROOF='+$proof)
if($run.ExitCode -ne 0 -or -not $unchanged){throw ('Actual readonly CAL/LOC chain failed: '+$qa)}
