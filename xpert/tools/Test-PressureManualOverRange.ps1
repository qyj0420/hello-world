param([string]$DependencyBuild='build\public-beta-1.1.1-20261011-r51',[switch]$IncludeExistingRegression)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$dependency=[IO.Path]::GetFullPath((Join-Path $root $DependencyBuild))
$qa=Join-Path $root ('build\qa-manual-overrange-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
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
$sources=@(Get-ChildItem -LiteralPath 'modules-src\PressureTest\src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$sources+=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','R2RoundedButton.cs','ZeroVoltageNoiseReference.cs','LocationCycleRecord.cs','PressureRecordPublication.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$sources+=Join-Path $root 'modules-src\FactoryWorker\AdjacentHardwareRangePolicy.cs'
$contract=Join-Path $PSScriptRoot 'Test-PressureTask39Contract.ps1';$manifestPath=Join-Path $root 'modules-src\Task39InputWorker\provenance\source-manifest.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
foreach($name in @('BenchModels.cs','PicoCaptureConfiguration.cs','LocationRangeAdvisor.cs','LocationRangeWindow.cs','RangeDecision.cs')){
 $entry=@($manifest.Files|Where-Object {$_.Path -ceq ('src/'+$name)});if($entry.Count -ne 1){throw 'Pinned pure input contract missing.'}
 $sources+=& $contract -Name $name -OriginalHash $entry[0].SHA256
}
$trial=@('PressureAnalysisTrial.cs','PressureBackgroundAnalysisQueue.cs','ProjectMeasurementMode.cs','PressureRecordPublication.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$test=Join-Path $root 'tests\PressureManualOverRangeSmoke.cs';$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$extraNames=if($IncludeExistingRegression){@('PressureCapacitorSavingSmoke','PressureOverRangeRetrySmoke','PressureOverflowUpscaleSmoke')}else{@()}
$inputs=@($sources)+$trial+@($test,$csc,$analysis,$algorithm,$contract,$manifestPath,$helper,$PSCommandPath)
foreach($name in $extraNames){$inputs+=Join-Path $root ('tests\'+$name+'.cs')}
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o');New-Item -ItemType Directory -Path $module|Out-Null
$common=@('/nologo','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
$dll=Join-Path $module 'Xiongxin.OWTS.PressureTest.dll'
$compiled=@(& $csc @common /target:library ('/r:'+$analysis) ('/out:'+$dll) @sources 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'module.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
Copy-Item -LiteralPath $analysis -Destination (Join-Path $module 'HVX.PdReview.V1.0.dll');Copy-Item -LiteralPath $algorithm -Destination (Join-Path $module 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll')
$trialDll=Join-Path $qa 'PressureTrialContract.dll'
$compiled=@(& $csc @common /target:library ('/out:'+$trialDll) @trial 2>&1|ForEach-Object{$_.ToString()});if($LASTEXITCODE -ne 0){throw ($compiled -join "`n")}
$exe=Join-Path $qa 'PressureManualOverRangeSmoke.exe'
$compiled=@(& $csc @common /target:exe /main:PressureManualOverRangeSmoke ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()});if($LASTEXITCODE -ne 0){throw ($compiled -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa) -LogPrefix (Join-Path $qa 'PressureManualOverRangeSmoke') -TimeoutMilliseconds 120000
$extraRuns=New-Object 'System.Collections.Generic.List[object]'
foreach($name in $extraNames){
 $extra=Join-Path $qa ($name+'.exe');$compiled=@(& $csc @common /target:exe ('/main:'+$name) ('/out:'+$extra) (Join-Path $root ('tests\'+$name+'.cs')) 2>&1|ForEach-Object{$_.ToString()})
 if($LASTEXITCODE -ne 0){throw ($compiled -join "`n")}
 $arguments=if($name -eq 'PressureCapacitorSavingSmoke'){@($qa)}else{@($qa,(Join-Path $qa ('existing-'+$name)))}
 $result=Invoke-RegressionExecutable -Executable $extra -Arguments $arguments -LogPrefix (Join-Path $qa $name) -TimeoutMilliseconds 120000
 $extraRuns.Add([ordered]@{Name=$name;Execution=$result});$result.Output|Write-Output
}
$extraPass=@($extraRuns|Where-Object {$_.Execution.ExitCode -ne 0}).Count -eq 0
$same=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress));$proof=Join-Path $qa 'proof.json'
[ordered]@{Schema='HVX.Pressure.ManualOverRange/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Status=$(if($run.ExitCode -eq 0 -and $same -and $extraPass){'PASS'}else{'FAIL'});
SourceUnchanged=$same;Inputs=$before;Run=$run;ExistingRegression=$extraRuns.ToArray();HardwareAccessed=$false;SDKCalled=$false;ProductStarted=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;
ModuleSHA256=(Get-FileHash -LiteralPath $dll).Hash;TrialSHA256=(Get-FileHash -LiteralPath $trialDll).Hash;
Scope='Actual managed manual overrange decision and save/recovery state; synthetic callback/files only. Host continuation wiring requires full build and independent source review.'}|
ConvertTo-Json -Depth 14|Set-Content -LiteralPath $proof -Encoding UTF8
$run.Output|Write-Output;Write-Output ('MANUAL_OVERRANGE_PROOF='+$proof)
if($run.ExitCode -ne 0 -or -not $same -or -not $extraPass){throw ('Manual overrange regression failed: '+$qa)}
