param([string]$DependencyBuild='build\main-repair-20261010-r42',[string]$AnalysisPath='')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$dependency=[IO.Path]::GetFullPath((Join-Path $root $DependencyBuild))
if(-not $dependency.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an isolated project build dependency.'}
$qa=Join-Path $root ('build\qa-healthy-stop-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$module=Join-Path $qa 'modules\PressureTest';$workerDir=Join-Path $qa 'modules\PressureRuntime'
$analysis=Join-Path $dependency 'modules\PressureTest\HVX.PdReview.V1.0.dll'
if(-not [String]::IsNullOrWhiteSpace($AnalysisPath)){$analysis=[IO.Path]::GetFullPath($AnalysisPath);Assert-RegressionUnlinkedPath $analysis}
$algorithm=Join-Path $dependency 'modules\PressureTest\fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'
$workerSources=@(Get-ChildItem -LiteralPath (Join-Path $root 'modules-src\FactoryWorker') -Recurse -File -Filter '*.cs'|Sort-Object FullName|ForEach-Object FullName)
$hostSources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -File -Filter '*.cs'|Sort-Object FullName|ForEach-Object FullName)
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'modules-src\PressureTest\src') -File -Filter '*.cs'|Sort-Object FullName|ForEach-Object FullName)
$sources+=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','ZeroVoltageNoiseReference.cs','LocationCycleRecord.cs','PressureRecordPublication.cs','R2RoundedButton.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$sources+=Join-Path $root 'modules-src\FactoryWorker\AdjacentHardwareRangePolicy.cs'
$contract=Join-Path $PSScriptRoot 'Test-PressureTask39Contract.ps1';$manifestPath=Join-Path $root 'modules-src\Task39InputWorker\provenance\source-manifest.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
foreach($name in @('BenchModels.cs','PicoCaptureConfiguration.cs','LocationRangeAdvisor.cs','LocationRangeWindow.cs','RangeDecision.cs')){
 $entry=@($manifest.Files|Where-Object {$_.Path -ceq ('src/'+$name)});if($entry.Count -ne 1){throw 'Pinned pure input contract missing.'}
 $sources+=& $contract -Name $name -OriginalHash $entry[0].SHA256
}
$testNames=@('PressurePreparationOutcomeSmoke','PressureFailureCleanupHostSmoke')
$tests=@($testNames|ForEach-Object{Join-Path $root ('tests\'+$_+'.cs')});$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=@($sources)+@($workerSources)+@($hostSources)+@($analysis,$algorithm)+$tests+@($PSCommandPath,$helper,$contract,$manifestPath,$csc)
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o');New-Item -ItemType Directory -Path $module,$workerDir|Out-Null
$common=@('/nologo','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
function Compile([string]$name,[string[]]$arguments){
 $lines=@(& $csc @common @arguments 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE
 $lines|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ($lines -join "`n")}
}
$dll=Join-Path $module 'Xiongxin.OWTS.PressureTest.dll';$worker=Join-Path $workerDir 'Xiongxin.Pressure.Runtime.exe'
Compile 'worker' (@('/langversion:latest','/target:library',('/out:'+$worker))+@($workerSources))
Compile 'module' (@('/target:library',('/r:'+$analysis),('/out:'+$dll))+@($sources))
Copy-Item -LiteralPath $analysis -Destination (Join-Path $module 'HVX.PdReview.V1.0.dll');Copy-Item -LiteralPath $algorithm -Destination (Join-Path $module 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll')
$runs=New-Object 'System.Collections.Generic.List[object]'
foreach($name in $testNames){
 $exe=Join-Path $qa ($name+'.exe');$extra=@();if($name -eq 'PressureFailureCleanupHostSmoke'){$extra=@($hostSources)}
 Compile $name (@('/target:exe',('/main:'+$name),('/out:'+$exe),(Join-Path $root ('tests\'+$name+'.cs')))+$extra)
 $runArgs=@($qa,(Join-Path $qa ('case-'+$runs.Count)))
 if($name -eq 'PressurePreparationOutcomeSmoke'){$runArgs+=@('round-stopped,round-stopped-unverified,round-stopped-not-operator,round-stopped-di-error,verified-stop,unverified-stop,committed,outcome-session-ended,outcome-release-unconfirmed,outcome-worker-exited')}
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments $runArgs -LogPrefix (Join-Path $qa $name) -TimeoutMilliseconds 120000
 $runs.Add([ordered]@{Name=$name;Execution=$run;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash});$run.Output|Write-Output
}
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$passed=@($runs|Where-Object {$_.Execution.ExitCode -ne 0}).Count -eq 0;$proof=Join-Path $qa 'verification-healthy-stop.json'
[ordered]@{Schema='HVX.Pressure.HealthyStop.Focused/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Status=$(if($passed -and $unchanged){'PASS'}else{'FAIL'});
HardwareAccessed=$false;SDKCalled=$false;ProductEntryExecuted=$false;WorkerEntryExecuted=$false;NativeSessionCreated=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;FullRegression=$false;FinalExeIntegration=$false;
SourceUnchanged=$unchanged;DependencyBuild=$dependency;Inputs=$before;ModuleSHA256=(Get-FileHash -LiteralPath $dll).Hash;WorkerLibrarySHA256=(Get-FileHash -LiteralPath $worker).Hash;Runs=$runs.ToArray();
Scope='Actual pressure control owned-action Stop classification and exact worker-type Host outcome gates/notices; synthetic DI, injected delegate, source compilation, no Main/worker/native entry.'}|
ConvertTo-Json -Depth 15|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output ('HEALTHY_STOP_PROOF='+$proof)
if(-not $passed -or -not $unchanged){throw ('Healthy-stop focused regression failed: '+$qa)}
