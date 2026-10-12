param([string]$DependencyBuild='build\main-repair-20261010-r42',[string]$AnalysisPath='D:\codex\Task28_FilterB_20261010\qa\host\modules\DataAnalysis\HVX.PdReview.V1.0.dll')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$dependency=[IO.Path]::GetFullPath((Join-Path $root $DependencyBuild));$qa=Join-Path $root ('build\qa-location-upto-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors);if($errors.Count){throw 'Invalid runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){$node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name);if($node.Count -ne 1){throw 'Missing runner helper.'};. ([ScriptBlock]::Create($node[0].Extent.Text))}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$module=Join-Path $qa 'modules\PressureTest';$analysis=[IO.Path]::GetFullPath($AnalysisPath)
$algorithm=Join-Path $dependency 'modules\PressureTest\fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'
$sources=@(Get-ChildItem -LiteralPath 'modules-src\PressureTest\src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$sources+=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','ZeroVoltageNoiseReference.cs','LocationCycleRecord.cs','PressureRecordPublication.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$contract=Join-Path $PSScriptRoot 'Test-PressureTask39Contract.ps1';$manifestPath=Join-Path $root 'modules-src\Task39InputWorker\provenance\source-manifest.json';$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
foreach($name in @('BenchModels.cs','PicoCaptureConfiguration.cs','LocationRangeAdvisor.cs','LocationRangeWindow.cs','RangeDecision.cs')){$entry=@($manifest.Files|Where-Object {$_.Path -ceq ('src/'+$name)});if($entry.Count -ne 1){throw 'Pinned input missing.'};$sources+=& $contract -Name $name -OriginalHash $entry[0].SHA256}
$test=Join-Path $root 'tests\LocationCycleUpToSmoke.cs';$record=Join-Path $root 'src\LocationCycleRecord.cs';$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=$sources+@($analysis,$algorithm,$test,$record,$PSCommandPath,$helper,$contract,$manifestPath,$csc)
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);New-Item -ItemType Directory -Path $module|Out-Null
$common=@('/nologo','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
$dll=Join-Path $module 'Xiongxin.OWTS.PressureTest.dll';$lines=@(& $csc @common /target:library ('/r:'+$analysis) ('/out:'+$dll) @sources 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa 'module.compile.txt') -Encoding UTF8;if($code -ne 0){throw ($lines -join "`n")}
Copy-Item -LiteralPath $analysis -Destination (Join-Path $module 'HVX.PdReview.V1.0.dll');Copy-Item -LiteralPath $algorithm -Destination (Join-Path $module 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll')
$exe=Join-Path $qa 'LocationCycleUpToSmoke.exe';$lines=@(& $csc @common /target:exe /main:LocationCycleUpToSmoke ('/out:'+$exe) $test $record 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE;$lines|Set-Content -LiteralPath ($exe+'.compile.txt') -Encoding UTF8;if($code -ne 0){throw ($lines -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa,(Join-Path $qa 'fixture')) -LogPrefix (Join-Path $qa 'LocationCycleUpToSmoke') -TimeoutMilliseconds 55000
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
[ordered]@{Schema='HVX.LocationCycle.UpTo.Focused/1';Status=$(if($run.ExitCode -eq 0 -and $unchanged){'PASS'}else{'FAIL'});Inputs=$before;SourceUnchanged=$unchanged;Run=$run;ModuleSHA256=(Get-FileHash -LiteralPath $dll).Hash;HardwareAccessed=$false;SDKCalled=$false;WorkerEntryExecuted=$false;ProductMainExecuted=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;Scope='Strict Find unchanged; independent shared acquired clocks choose actual complete1..5; formal LOC persistence then ACK; no complete cycle remains invalid/no ACK/no plan continuation; partial tails not implemented.'}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'verification-location-upto.json') -Encoding UTF8
$run.Output|Write-Output;Write-Output ('LOCATION_UPTO_PROOF='+$qa)
if($run.ExitCode -ne 0 -or -not $unchanged){throw ('Up-to regression failed; retained evidence '+$qa)}
