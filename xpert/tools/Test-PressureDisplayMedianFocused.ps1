param([ValidateSet('Median','Envelope','Preparation')][string]$TestCase='Median',[string]$BuildRoot='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-display-'+$TestCase.ToLowerInvariant()+'-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
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
$source=Join-Path $root $(if($TestCase -eq 'Median'){'modules-src\PressureTest\src\PressureDisplayMedian.cs'}elseif($TestCase -eq 'Envelope'){'modules-src\PressureTest\src\PressurePdDisplayEnvelope.cs'}else{'modules-src\PressureTest\src\PressureCapturePreparation.cs'})
$test=Join-Path $root $(if($TestCase -eq 'Median'){'tests\PressureDisplayMedianPerformanceSmoke.cs'}elseif($TestCase -eq 'Envelope'){'tests\PressurePdDisplayEnvelopeSmoke.cs'}else{'tests\PressureCapturePreparationSmoke.cs'})
$inputs=@($source,$test,$PSCommandPath,$helper,$csc)
$candidateRoot=''
if($BuildRoot){
 $candidateRoot=[IO.Path]::GetFullPath($BuildRoot)
 if(-not $candidateRoot.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Candidate must be an isolated project build.'}
 $candidate=Join-Path $candidateRoot 'modules\PressureTest\Xiongxin.OWTS.PressureTest.dll'
 Assert-RegressionUnlinkedPath $candidate
 if(-not(Test-Path -LiteralPath $candidate -PathType Leaf)){throw 'Actual candidate library missing.'}
 $inputs+=$candidate
}
function Inventory{@($inputs|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
New-Item -ItemType Directory -Path $qa|Out-Null
$exe=Join-Path $qa ($TestCase+'Offline.exe')
$compiled=@(& $csc /nologo /langversion:7.3 /platform:x64 /target:exe /r:System.dll /r:System.Core.dll ('/out:'+$exe) $test $source 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
$runArguments=@();if($candidateRoot){$runArguments+=@($candidateRoot)}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments $runArguments -LogPrefix (Join-Path $qa 'median') -TimeoutMilliseconds 60000
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$proof=Join-Path $qa 'verification-display-median.json'
[ordered]@{Schema='HVX.Pressure.DisplayPreparation.Focused/1';TestCase=$TestCase;StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');
 Status=$(if($run.ExitCode -eq 0 -and $unchanged){'PASS'}else{'FAIL'});HardwareAccessed=$false;SDKCalled=$false;ProductEntryExecuted=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;
 SourceUnchanged=$unchanged;FullRegression=$false;ActualCandidateBuild=$candidateRoot;Inputs=$before;Execution=$run;
 Scope='Exact display baseline or horizontal-bucket extrema versus independent reference, immutable input, cancellation and synthetic CPU benchmark.'}|
 ConvertTo-Json -Depth 15|Set-Content -LiteralPath $proof -Encoding UTF8
$run.Output|Write-Output;Write-Output ('DISPLAY_MEDIAN_PROOF='+$proof)
if($run.ExitCode -ne 0 -or -not $unchanged){throw ('Display median focused regression failed: '+$qa)}
