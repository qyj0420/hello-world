param([string]$DependencyBuild='build\pressure-firsttest-20261010-r43',
 [string]$AnalysisPath='D:\codex\Task28_FilterB_20261010\qa\host\modules\DataAnalysis\HVX.PdReview.V1.0.dll',
 [string]$PublishedBuildRoot='')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-locref-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors);if($errors.Count){throw 'Invalid bounded runner helper'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){$node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name);if($node.Count -ne 1){throw 'Missing bounded helper'};. ([ScriptBlock]::Create($node[0].Extent.Text))}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$test=Join-Path $root 'tests\PressureLocationReferenceRetentionSmoke.cs';$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$sourceMode=[String]::IsNullOrWhiteSpace($PublishedBuildRoot);$sources=@();$support=@()
if($sourceMode){
 $moduleDir=Join-Path $qa 'modules\PressureTest';$build=$qa
 $sources=@(Get-ChildItem -LiteralPath 'modules-src\PressureTest\src' -Filter '*.cs' -File|Sort-Object Name|ForEach-Object FullName)
 $sources+=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','R2RoundedButton.cs','ZeroVoltageNoiseReference.cs','LocationCycleRecord.cs','PressureRecordPublication.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
 $contract=Join-Path $PSScriptRoot 'Test-PressureTask39Contract.ps1';$manifestPath=Join-Path $root 'modules-src\Task39InputWorker\provenance\source-manifest.json';$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
 foreach($name in @('BenchModels.cs','PicoCaptureConfiguration.cs','LocationRangeAdvisor.cs','LocationRangeWindow.cs','RangeDecision.cs')){$entry=@($manifest.Files|Where-Object {$_.Path -ceq ('src/'+$name)});if($entry.Count -ne 1){throw 'Pinned input missing'};$sources+=& $contract -Name $name -OriginalHash $entry[0].SHA256}
 $dependency=[IO.Path]::GetFullPath((Join-Path $root $DependencyBuild));$analysis=[IO.Path]::GetFullPath($AnalysisPath)
 $algorithm=Join-Path $dependency 'modules\PressureTest\fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll';$support=@($contract,$manifestPath,$analysis,$algorithm,(Join-Path $PSScriptRoot 'Build-UiModules.ps1'))
}else{
 $build=[IO.Path]::GetFullPath($PublishedBuildRoot);$moduleDir=Join-Path $build 'modules\PressureTest'
 $support=@(Get-ChildItem -LiteralPath $moduleDir -File|ForEach-Object FullName)
}
$dll=Join-Path $moduleDir 'Xiongxin.OWTS.PressureTest.dll'
$inputs=$sources+$support+@($test,$csc,$PSCommandPath,$helper)
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);New-Item -ItemType Directory -Path $qa|Out-Null
$common=@('/nologo','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
if($sourceMode){
 New-Item -ItemType Directory -Path $moduleDir|Out-Null
 $lines=@(& $csc @common /target:library ('/r:'+$analysis) ('/out:'+$dll) @sources 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa 'module.compile.txt') -Encoding UTF8
 if($code -ne 0){throw ($lines -join [Environment]::NewLine)}
 Copy-Item -LiteralPath $analysis -Destination (Join-Path $moduleDir 'HVX.PdReview.V1.0.dll');Copy-Item -LiteralPath $algorithm -Destination (Join-Path $moduleDir 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll')
}
Assert-RegressionUnlinkedPath $dll;$moduleBefore=(Get-FileHash -LiteralPath $dll).Hash
$exe=Join-Path $qa 'PressureLocationReferenceRetentionSmoke.exe'
$lines=@(& $csc @common /target:exe ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE;$lines|Set-Content -LiteralPath ($exe+'.compile.txt') -Encoding UTF8;if($code -ne 0){throw ($lines -join [Environment]::NewLine)}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($build,(Join-Path $qa 'f')) -LogPrefix (Join-Path $qa 'retention') -TimeoutMilliseconds 55000
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress));$moduleAfter=(Get-FileHash -LiteralPath $dll).Hash
[ordered]@{Schema='HVX.LocationReferenceRetention.Focused/1';Status=$(if($run.ExitCode -eq 0 -and $unchanged -and $moduleBefore -eq $moduleAfter){'PASS'}else{'FAIL'});Inputs=$before;SourceCompiled=$sourceMode;ActualModulePath=$dll;ModuleSHA256Before=$moduleBefore;ModuleSHA256After=$moduleAfter;InputsUnchanged=$unchanged;Run=$run;HardwareAccessed=$false;SDKCalled=$false;WorkerEntryExecuted=$false;ProductMainExecuted=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;Scope='Independent raw-reference retention only; strict complete LOC/phase/ACK/automatic plan unchanged; synthetic owned archive predicates and invalid/stale-owner counterexamples. Source mode is NOT final protected-package acceptance.'}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'verification-locref.json') -Encoding UTF8
$run.Output|Write-Output;Write-Output ('LOCATION_REFERENCE_PROOF='+$qa)
if($run.ExitCode -ne 0 -or -not $unchanged -or $moduleBefore -ne $moduleAfter){throw ('Reference retention failed; evidence kept '+$qa)}
