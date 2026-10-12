param([string]$BuildRoot='build\main-test-20261009-r39',[Parameter(Mandatory=$true)][string]$ExactInputClone)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$build=[IO.Path]::GetFullPath((Join-Path $root $BuildRoot));$clone=[IO.Path]::GetFullPath($ExactInputClone)
if(-not $clone.StartsWith($root+'\build\qa-actual-cal-loc-',[StringComparison]::OrdinalIgnoreCase)){throw 'Only an owned readonly diagnosis input clone is allowed.'}
$qa=Join-Path $root ('build\qa-actual-cal-packets-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing;Assert-RegressionUnlinkedPath $clone
$intermediates=@(Get-ChildItem -LiteralPath $build -Recurse -File -Filter 'unprotected-input.dll'|Where-Object{$_.FullName.EndsWith('\location-source-build\protection\unprotected-input.dll',[StringComparison]::OrdinalIgnoreCase)})
if($intermediates.Count -ne 1){throw 'Need precisely one same-build algorithm intermediate.'}
$intermediate=$intermediates[0].FullName
$manifest=Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($intermediate))) 'recorded-phase-overlay-source-manifest.json'
$sourceProof=Get-Content -LiteralPath $manifest -Raw|ConvertFrom-Json
if($sourceProof.OutputSHA256 -cne (Get-FileHash -LiteralPath $intermediate).Hash){throw 'Unprotected intermediate is not the algorithm build-manifest output.'}
$analysis=Join-Path $build 'modules\DataAnalysis\HVX.PdReview.V1.0.dll';$test=Join-Path $root 'tests\AnalysisActualCalibrationPacketsReadonly.cs';$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=@($PSCommandPath,$helper,$test,$csc,$analysis,$intermediate,$manifest)+@(Get-ChildItem -LiteralPath $clone -File|ForEach-Object FullName)
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o');New-Item -ItemType Directory -Path $qa|Out-Null
$exe=Join-Path $qa 'AnalysisActualCalibrationPacketsReadonly.exe'
$compiled=@(& $csc /nologo /langversion:7.3 /platform:x64 /target:exe /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($analysis,$intermediate,$clone,(Join-Path $qa 'actual-cal-packets-readonly.json')) -LogPrefix (Join-Path $qa 'actual-cal-packets') -TimeoutMilliseconds 60000
$run.Output|Write-Output
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$proof=Join-Path $qa 'verification-actual-cal-packets-readonly.json'
[ordered]@{Schema='HVX.Actual.CalibrationPacket.Runner/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Status=$(if($run.ExitCode -eq 0 -and $unchanged){'PASS_READONLY_DIAGNOSIS'}else{'FAIL'});SourceUnchanged=$unchanged;Inputs=$before;Execution=$run;
HardwareAccessed=$false;SDKCalled=$false;ProductEntryExecuted=$false;UserProjectWritten=$false;PhysicalAccuracyVerified=$false;FullRegression=$false;ExactInputClone=$clone;
Scope='Same-build source-manifest-bound unprotected intermediate; exact original packet guard invoked read-only; diagnosis only, not acceptance or algorithm fix.'}|ConvertTo-Json -Depth 15|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output ('ACTUAL_CAL_PACKETS_PROOF='+$proof)
if($run.ExitCode -ne 0 -or -not $unchanged){throw ('Actual readonly CAL packet diagnosis failed: '+$qa)}
