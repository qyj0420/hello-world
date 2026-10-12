param([Parameter(Mandatory=$true)][string]$OutputRoot,[string]$DependencyBuild='build\public-beta-1.1.1-20261011-r51')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$output=[IO.Path]::GetFullPath($OutputRoot)
if(!$output.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Use a new project-local proof directory.'}
$dependency=[IO.Path]::GetFullPath((Join-Path $root $DependencyBuild))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Invalid bounded runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $output -AllowMissing
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$analysis=Join-Path $dependency 'modules\PressureTest\HVX.PdReview.V1.0.dll'
$algorithm=Join-Path $dependency 'modules\PressureTest\fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'
$shared=Join-Path $root 'modules-src\FactoryWorker\AdjacentHardwareRangePolicy.cs'
$ui=@(Get-ChildItem -LiteralPath 'modules-src\PressureTest\src' -Filter '*.cs' -File|Sort-Object Name|ForEach-Object FullName)
$ui+=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','R2RoundedButton.cs','ZeroVoltageNoiseReference.cs','LocationCycleRecord.cs','PressureRecordPublication.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$ui+=$shared
$contract=Join-Path $PSScriptRoot 'Test-PressureTask39Contract.ps1';$manifestPath=Join-Path $root 'modules-src\Task39InputWorker\provenance\source-manifest.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
foreach($name in @('BenchModels.cs','PicoCaptureConfiguration.cs','LocationRangeAdvisor.cs','LocationRangeWindow.cs','RangeDecision.cs')){
 $entry=@($manifest.Files|Where-Object {$_.Path -ceq ('src/'+$name)});if($entry.Count -ne 1){throw 'Pinned pure contract missing.'}
 $ui+=& $contract -Name $name -OriginalHash $entry[0].SHA256
}
$runtime=@(Get-ChildItem -LiteralPath 'modules-src\FactoryWorker' -Recurse -Filter '*.cs' -File|Sort-Object FullName|ForEach-Object FullName)
$trial=@('PressureAnalysisTrial.cs','PressureBackgroundAnalysisQueue.cs','ProjectMeasurementMode.cs','PressureRecordPublication.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$pure='PressureClippedUpscalePolicySmoke';$runtimeTest='PressureClippedUpscaleRuntimeSmoke'
$uiNames=@('PressureRangeSingleStepSmoke','PressureOverflowUpscaleSmoke','PressurePlanRangeFloorSmoke','PressureManualOverRangeSmoke','PressureNextRangeAutomaticSmoke','PressureOverRangeRetrySmoke')
$tests=@(@($pure,$runtimeTest)+$uiNames|ForEach-Object{Join-Path $root ('tests\'+$_+'.cs')})
$inputs=@($ui)+$runtime+$trial+$tests+@($analysis,$algorithm,$csc,$helper,$contract,$manifestPath,$PSCommandPath)
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=[IO.Path]::GetFullPath($_);SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
$module=Join-Path $output 'modules\PressureTest';New-Item -ItemType Directory -Path $module|Out-Null
$common=@('/nologo','/langversion:latest','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
function Compile([string]$name,[string[]]$arguments,[string[]]$sources){
 $lines=@(& $csc @common @arguments @sources 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE
 $lines|Set-Content -LiteralPath (Join-Path $output ($name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ($lines -join "`n")}
}
$dll=Join-Path $module 'Xiongxin.OWTS.PressureTest.dll'
Compile 'PressureTest' @('/target:library',('/r:'+$analysis),('/out:'+$dll)) $ui
Copy-Item -LiteralPath $analysis -Destination (Join-Path $module 'HVX.PdReview.V1.0.dll')
Copy-Item -LiteralPath $algorithm -Destination (Join-Path $module 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll')
$trialDll=Join-Path $output 'PressureTrialContract.dll';Compile 'Trial' @('/target:library',('/out:'+$trialDll)) $trial
$runs=New-Object 'System.Collections.Generic.List[object]'
foreach($name in @($pure,$runtimeTest)+$uiNames){
 $exe=Join-Path $output ($name+'.exe');$source=@(Join-Path $root ('tests\'+$name+'.cs'))
 if($name -eq $pure){$source+=$shared}
 if($name -eq $runtimeTest){$source+=$runtime}
 Compile $name @('/target:exe',('/main:'+$name),('/out:'+$exe)) $source
 $arguments=if($name -eq $pure -or $name -eq $runtimeTest){@()}elseif($name -eq 'PressureManualOverRangeSmoke'){@($output)}else{@($output,(Join-Path $output ($name+'-fixture')))}
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments $arguments -LogPrefix (Join-Path $output $name) -TimeoutMilliseconds 120000
 $runs.Add([ordered]@{Name=$name;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash;Execution=$run});$run.Output|Write-Output
}
$after=@(Inventory);$same=($before|ConvertTo-Json -Depth 5 -Compress) -ceq ($after|ConvertTo-Json -Depth 5 -Compress)
$passed=@($runs|Where-Object {$_.Execution.ExitCode -ne 0}).Count -eq 0
$proof=Join-Path $output 'proof.json'
[ordered]@{Schema='HVX.Pressure.ClippedUpscale.Offline/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Status=$(if($passed -and $same){'PASS'}else{'FAIL'});
 SourceUnchanged=$same;InputMismatchCount=@($before|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count;Inputs=$before;Runs=$runs.ToArray();
 ModuleSHA256=(Get-FileHash -LiteralPath $dll).Hash;HardwareAccessed=$false;SDKCalled=$false;WorkersStarted=0;ProductStarted=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;
 Scope='Shared bounded clipping execution arithmetic, current UI managed synthetic capture/release/intent/hash/manual/floor gates, runtime policy fixture ONLY. Not a released or protected build; vendor algorithms unchanged; no actual worker entry or hardware.'}|
 ConvertTo-Json -Depth 15|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output ('CLIPPED_UPSCALE_PROOF='+$proof)
if(!$passed -or !$same){throw 'Clipped upscale offline regression failed; evidence retained.'}
