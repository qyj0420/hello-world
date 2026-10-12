param(
 [string]$DependencyBuild='build\main-repair-20261010-r42',
 [string]$AnalysisPath='D:\codex\Task28_FilterB_20261010\qa\host\modules\DataAnalysis\HVX.PdReview.V1.0.dll'
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-pressure-countdown-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$runner=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$dependency=[IO.Path]::GetFullPath((Join-Path $root $DependencyBuild))
$analysis=[IO.Path]::GetFullPath($AnalysisPath)
$algorithm=Join-Path $dependency 'modules\PressureTest\fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'
$pressureSources=@(Get-ChildItem -LiteralPath 'modules-src\PressureTest\src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$pressureSources+=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','ZeroVoltageNoiseReference.cs','LocationCycleRecord.cs','PressureRecordPublication.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$contract=Join-Path $PSScriptRoot 'Test-PressureTask39Contract.ps1'
$manifestPath=Join-Path $root 'modules-src\Task39InputWorker\provenance\source-manifest.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
foreach($name in @('BenchModels.cs','PicoCaptureConfiguration.cs','LocationRangeAdvisor.cs','LocationRangeWindow.cs','RangeDecision.cs')){
 $entry=@($manifest.Files|Where-Object {$_.Path -ceq ('src/'+$name)})
 if($entry.Count -ne 1){throw 'Pinned pure input contract missing.'}
 $pressureSources+=& $contract -Name $name -OriginalHash $entry[0].SHA256
}
$hostSources=@(Get-ChildItem -LiteralPath 'src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$names=@('PressureCountdownOneSecondSmoke','PressurePreferencesSmoke','PressurePreferencesSettingsSmoke','SettingsUiLanguageTreeSmoke')
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe';$brand=Join-Path $root 'assets\xiongxin.ico'
$inputs=$pressureSources+$hostSources+@($names|ForEach-Object{Join-Path $root ('tests\'+$_+'.cs')})+@($PSCommandPath,$runner,$contract,$manifestPath,$csc,$brand,$analysis,$algorithm)
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
$pressureDir=Join-Path $qa 'modules\PressureTest';$analysisDir=Join-Path $qa 'modules\DataAnalysis'
New-Item -ItemType Directory -Path $pressureDir,$analysisDir|Out-Null
Copy-Item -LiteralPath $analysis -Destination (Join-Path $pressureDir 'HVX.PdReview.V1.0.dll');Copy-Item -LiteralPath $analysis -Destination (Join-Path $analysisDir 'HVX.PdReview.V1.0.dll')
Copy-Item -LiteralPath $algorithm -Destination (Join-Path $pressureDir 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll')
$common=@('/nologo','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
function Compile([string]$output,[string]$target,[string[]]$extra,[string[]]$files){
 $rsp=$output+'.rsp';@($common+@(('/target:'+$target),('/out:'+$output))+$extra+$files|ForEach-Object{'"'+$_+'"'})|Set-Content -LiteralPath $rsp -Encoding UTF8
 $result=@(& $csc ('@'+$rsp) 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE;$result|Set-Content -LiteralPath ($output+'.compile.txt') -Encoding UTF8
 if($code -ne 0){throw ($result -join "`n")}
}
$pressure=Join-Path $pressureDir 'Xiongxin.OWTS.PressureTest.dll';$hostDll=Join-Path $qa 'CurrentUiHost.dll'
Compile $pressure 'library' @(('/r:'+$analysis)) $pressureSources
Compile $hostDll 'library' @(('/resource:'+$brand+',Brand.ico')) $hostSources
$runs=@()
foreach($name in $names){
 $exe=Join-Path $qa ($name+'.exe');$files=@((Join-Path $root ('tests\'+$name+'.cs')));$extra=@('/main:'+$name)
 if($name -eq 'PressurePreferencesSettingsSmoke'){$files+=$hostSources;$extra+=('/resource:'+$brand+',Brand.ico')}
 Compile $exe 'exe' $extra $files
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa,(Join-Path $qa ($name+'-fixture'))) -LogPrefix (Join-Path $qa $name) -TimeoutMilliseconds 60000
 $runs+=,[ordered]@{Name=$name;Result=$run};$run.Output|Write-Output
}
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$passed=@($runs|Where-Object{$_.Result.ExitCode -ne 0}).Count -eq 0
$proof=Join-Path $qa 'verification-pressure-countdown.json'
[ordered]@{Schema='HVX.Pressure.Countdown.Focused/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Status=$(if($passed -and $unchanged){'PASS'}else{'FAIL'});
 HardwareAccessed=$false;SDKCalled=$false;ProductEntryStarted=$false;UserProjectsWritten=$false;DeliveryBuilt=$false;PrivateQaLibrariesOnly=$true;FullRegression=$false;SourceUnchanged=$unchanged;
 Inputs=$before;HostSHA256=(Get-FileHash -LiteralPath $hostDll).Hash;PressureSHA256=(Get-FileHash -LiteralPath $pressure).Hash;Runs=$runs;
 Scope='Automatic pre-plan default/minimum 1 s, explicit 2..10 s retained, historical zero normalized to 1, canceled/hidden countdown waits; removed explanation with EN/TR labels; Pico finite capture cooldown remains independently 2 s. No device/product entry.'}|
 ConvertTo-Json -Depth 14|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output ('PRESSURE_COUNTDOWN_PROOF='+$proof)
if(-not $passed -or -not $unchanged){throw ('Focused countdown regression failed: '+$qa)}
