param([string]$CandidateLibrary='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-fir-fastpath-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$source=Join-Path $root 'src\PdLinearHighPass.cs'
$test=Join-Path $root 'tests\PdHighPassInteriorFastPathSmoke.cs'
$baseline=Join-Path $root 'build\main-test-20261009-r39\modules\DataAnalysis\HVX.PdReview.V1.0.dll'
$inputs=@($source,$test,$baseline,$PSCommandPath,$helper,$csc)
$candidate=$null
if($CandidateLibrary){
 $candidate=[IO.Path]::GetFullPath($CandidateLibrary)
 if(-not $candidate.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Candidate must be an isolated current build file.'}
 Assert-RegressionUnlinkedPath $candidate
 if(-not(Test-Path -LiteralPath $candidate -PathType Leaf)){throw 'Candidate library missing.'}
 $inputs+=$candidate
}
function Inventory{@($inputs|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
New-Item -ItemType Directory -Path $qa|Out-Null
$exe=Join-Path $qa 'FirOffline.exe'
$compiled=@(& $csc /nologo /optimize+ /langversion:7.3 /platform:x64 /target:exe /r:System.dll /r:System.Core.dll ('/out:'+$exe) $test $source 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
$runArgs=@($baseline);if($candidate){$runArgs+=$candidate}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments $runArgs -LogPrefix (Join-Path $qa 'fir') -TimeoutMilliseconds 60000
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$proof=Join-Path $qa 'verification-fir-fastpath.json'
[ordered]@{Schema='HVX.Pd.HighPassInteriorFastPath.Focused/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');
 Status=$(if($run.ExitCode -eq 0 -and $unchanged){'PASS'}else{'FAIL'});HardwareAccessed=$false;SDKCalled=$false;ProductEntryExecuted=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;
 SourceUnchanged=$unchanged;FullRegression=$false;CandidateLibrary=$candidate;Inputs=$before;Execution=$run;
 Scope='Every FIR output bit versus exact protected r39 baseline; edge reflection, unchanged identity/input/errors, cancellation and synthetic CPU timing. Worker frozen FIR not modified.'}|
 ConvertTo-Json -Depth 15|Set-Content -LiteralPath $proof -Encoding UTF8
$run.Output|Write-Output;Write-Output ('FIR_FASTPATH_PROOF='+$proof)
if($run.ExitCode -ne 0 -or -not $unchanged){throw ('FIR fast-path focused regression failed: '+$qa)}
