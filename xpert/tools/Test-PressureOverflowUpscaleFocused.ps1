param([string]$DependencyBuild='build\main-test-20261009-r39')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$dependency=[IO.Path]::GetFullPath((Join-Path $root $DependencyBuild))
$qa=Join-Path $root ('build\qa-pressure-overflow-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$module=Join-Path $qa 'modules\PressureTest';$analysis=Join-Path $dependency 'modules\PressureTest\HVX.PdReview.V1.0.dll'
$algorithm=Join-Path $dependency 'modules\PressureTest\fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'
$previousModule=Join-Path $dependency 'modules\PressureTest\Xiongxin.OWTS.PressureTest.dll'
$sources=@(Get-ChildItem -LiteralPath 'modules-src\PressureTest\src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$sources+=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','ZeroVoltageNoiseReference.cs','LocationCycleRecord.cs','PressureRecordPublication.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$contract=Join-Path $PSScriptRoot 'Test-PressureTask39Contract.ps1';$manifestPath=Join-Path $root 'modules-src\Task39InputWorker\provenance\source-manifest.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
foreach($name in @('BenchModels.cs','PicoCaptureConfiguration.cs','LocationRangeAdvisor.cs','LocationRangeWindow.cs','RangeDecision.cs')){
 $entry=@($manifest.Files|Where-Object {$_.Path -ceq ('src/'+$name)});if($entry.Count -ne 1){throw 'Pinned pure input contract missing.'}
 $sources+=& $contract -Name $name -OriginalHash $entry[0].SHA256
}
$testNames=@('PressureOverflowUpscaleSmoke','PressureOverRangeRetrySmoke')
$tests=@($testNames|ForEach-Object{Join-Path $root ('tests\'+$_+'.cs')});$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=@($sources)+@($analysis,$algorithm,$previousModule)+$tests+@($PSCommandPath,$helper,$contract,$manifestPath,$csc)
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o');New-Item -ItemType Directory -Path $module|Out-Null
$common=@('/nologo','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
$testExecutables=@{}
foreach($name in $testNames){
 $exe=Join-Path $qa ($name+'.exe');$compiled=@(& $csc @common /target:exe ('/main:'+$name) ('/out:'+$exe) (Join-Path $root ('tests\'+$name+'.cs')) 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ($compiled -join "`n")};$testExecutables[$name]=$exe
}
# An unchanged previous package must fail the first actual requested behavior,
# not merely because a new helper class is absent.
$negative=Invoke-RegressionExecutable -Executable $testExecutables['PressureOverflowUpscaleSmoke'] -Arguments @($dependency,(Join-Path $qa 'negative')) -LogPrefix (Join-Path $qa 'negative-previous-package') -TimeoutMilliseconds 30000
$negativeExpected=$negative.ExitCode -ne 0 -and ($negative.Output -join "`n").Contains('real uncertain overflow automatically retries without reminder suppression')
if(-not $negativeExpected){throw ('Previous package functional negative did not reproduce expected hold: '+($negative.Output -join "`n"))}
$dll=Join-Path $module 'Xiongxin.OWTS.PressureTest.dll'
$compiled=@(& $csc @common /target:library ('/r:'+$analysis) ('/out:'+$dll) @sources 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'module.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
Copy-Item -LiteralPath $analysis -Destination (Join-Path $module 'HVX.PdReview.V1.0.dll');Copy-Item -LiteralPath $algorithm -Destination (Join-Path $module 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll')
$runs=New-Object 'System.Collections.Generic.List[object]'
foreach($name in $testNames){
 $run=Invoke-RegressionExecutable -Executable $testExecutables[$name] -Arguments @($qa,(Join-Path $qa ('positive-'+$runs.Count))) -LogPrefix (Join-Path $qa $name) -TimeoutMilliseconds 120000
 $runs.Add([ordered]@{Name=$name;Execution=$run});$run.Output|Write-Output
}
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$passed=@($runs|Where-Object {$_.Execution.ExitCode -ne 0}).Count -eq 0;$proof=Join-Path $qa 'verification-overflow-upscale.json'
[ordered]@{Schema='HVX.Pressure.OverflowUpscale.Focused/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Status=$(if($passed -and $unchanged){'PASS'}else{'FAIL'});
HardwareAccessed=$false;SDKCalled=$false;ProductEntryExecuted=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;FullRegression=$false;
SourceUnchanged=$unchanged;DependencyBuild=$dependency;Inputs=$before;ModuleSHA256=(Get-FileHash -LiteralPath $dll).Hash;NegativeExpected=$negativeExpected;NegativePreviousPackage=$negative;PositiveRuns=$runs.ToArray();
Scope='Actual managed capture/release/ACK; narrow one-use automatic PLAN overflow upscale, immutable quality/raw evidence, maximum hold; no worker/Host or original range policy changes.'}|
ConvertTo-Json -Depth 15|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output ('OVERFLOW_UPSCALE_PROOF='+$proof)
if(-not $passed -or -not $unchanged){throw ('Overflow focused regression failed: '+$qa)}
