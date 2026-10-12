param()
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$qa=Join-Path $root ('build\AdjacentHardwareRangePolicyQA-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
New-Item -ItemType Directory -Path $qa|Out-Null
$source=Join-Path $root 'modules-src\FactoryWorker\AdjacentHardwareRangePolicy.cs'
$wrapper=Join-Path $root 'modules-src\PressureTest\src\PressureRangeSingleStep.cs'
$sequence=Join-Path $root 'modules-src\FactoryWorker\FactoryPressureSequence.cs'
$test=Join-Path $root 'tests\AdjacentHardwareRangePolicySmoke.cs'
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Invalid bounded runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
$inputs=@($source,$wrapper,$sequence,$test,$helper,$PSCommandPath,(Join-Path $root 'tools\Build-UiModules.ps1'),(Join-Path $PSScriptRoot 'compiler\csc.exe'))
$records=@($inputs|ForEach-Object{[ordered]@{Path=$_.Substring($root.Length+1);SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$worker=Get-Content -LiteralPath $sequence -Raw -Encoding UTF8
$ui=Get-Content -LiteralPath $wrapper -Raw -Encoding UTF8
$build=Get-Content -LiteralPath (Join-Path $root 'tools\Build-UiModules.ps1') -Raw -Encoding UTF8
if($worker -notmatch 'return HVX\.Acquisition\.BoundedClippedRangePolicy\.Next\(current,q\.RecommendedIndex,BenchSettings\.Ranges\.Length,' -or
 $worker -match 'return current\s*\+\s*Math\.Sign' -or $ui -match 'Math\.Sign' -or
 $build -notmatch 'AdjacentHardwareRangePolicy\.cs'){throw 'Duplicate policy or shared UI source closure remains incorrect.'}
$exe=Join-Path $qa 'AdjacentHardwareRangePolicySmoke.exe'
$output=@(& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /langversion:7.3 /target:exe /platform:x64 /deterministic+ /r:System.dll ('/out:'+$exe) $source $wrapper $test 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$output|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($code -ne 0){throw ('Pure policy test compilation failed: '+($output -join "`n"))}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @() -LogPrefix (Join-Path $qa 'AdjacentHardwareRangePolicySmoke') -TimeoutMilliseconds 30000
$text=$run.Output;$errorText=$run.Error
[ordered]@{Schema='HVX.AdjacentHardwareRangePolicy.SourceQA/1';ExitCode=$run.ExitCode;Inputs=$records;
 HardwareAccessed=$false;SDKCalled=$false;WorkersStarted=0;ProductEntryStarted=$false;
 Scope='Original adjacent policy exhaustive tests and static shared bounded-wrapper/source-closure checks. Does not validate clipped two-step behavior; run Test-PressureClippedUpscale separately. No worker entry started.';
 Output=$text;Error=$errorText}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
Write-Output $text;Write-Output ('ADJACENT_HARDWARE_RANGE_POLICY_QA='+$qa)
if($run.ExitCode -ne 0){throw ('Pure policy test failed: '+$errorText)}
