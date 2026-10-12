param([string]$AnalysisLibrary='build\main-test-20261009-r37\modules\DataAnalysis\HVX.PdReview.V1.0.dll',[string]$AlgorithmLibrary='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-background-analysis-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$analysis=[IO.Path]::GetFullPath((Join-Path $root $AnalysisLibrary));Assert-RegressionUnlinkedPath $analysis
$algorithm=if($AlgorithmLibrary){[IO.Path]::GetFullPath((Join-Path $root $AlgorithmLibrary))}else{Join-Path ([IO.Path]::GetDirectoryName($analysis)) 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'};Assert-RegressionUnlinkedPath $algorithm
$module=Join-Path $qa 'modules\PressureTest';$review=Join-Path $qa 'modules\DataAnalysis'
New-Item -ItemType Directory -Path $module,$review|Out-Null
$hostSources=@(Get-ChildItem -LiteralPath 'src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$moduleSources=@(Get-ChildItem -LiteralPath 'modules-src\PressureTest\src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$moduleSources+=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','R2RoundedButton.cs','ZeroVoltageNoiseReference.cs','LocationCycleRecord.cs','PressureRecordPublication.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$moduleSources+=Join-Path $root 'modules-src\FactoryWorker\AdjacentHardwareRangePolicy.cs'
$manifest=Get-Content -LiteralPath 'modules-src\Task39InputWorker\provenance\source-manifest.json' -Raw|ConvertFrom-Json
foreach($name in @('BenchModels.cs','PicoCaptureConfiguration.cs','LocationRangeAdvisor.cs','LocationRangeWindow.cs','RangeDecision.cs')){
 $entry=@($manifest.Files|Where-Object {$_.Path -ceq ('src/'+$name)})
 if($entry.Count -ne 1){throw 'Pinned pure input contract missing.'}
 $moduleSources+=& (Join-Path $PSScriptRoot 'Test-PressureTask39Contract.ps1') -Name $name -OriginalHash $entry[0].SHA256
}
$test=Join-Path $root 'tests\PressureBackgroundAnalysisSmoke.cs'
$hostTest=Join-Path $root 'tests\PressureAnalysisHostOptionsSmoke.cs'
$fixture=Join-Path $root 'tests\PressureCumulativeAnalysisSmoke.cs'
$inputs=@($hostSources)+@($moduleSources)+@($analysis,$algorithm,$test,$hostTest,$fixture,$PSCommandPath)
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$common=@('/nologo','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
$hostAssembly=Join-Path $qa 'Xiongxin.OWTS.Desktop.exe'
$compiled=@(& $csc @common /target:library ('/out:'+$hostAssembly) @hostSources 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'host.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
$dll=Join-Path $module 'Xiongxin.OWTS.PressureTest.dll'
$compiled=@(& $csc @common /target:library ('/r:'+$analysis) ('/out:'+$dll) @moduleSources 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'module.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
Copy-Item -LiteralPath $analysis -Destination (Join-Path $module 'HVX.PdReview.V1.0.dll')
Copy-Item -LiteralPath $analysis -Destination (Join-Path $review 'HVX.PdReview.V1.0.dll')
Copy-Item -LiteralPath $algorithm -Destination (Join-Path $review 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll')
$exe=Join-Path $qa 'PressureBackgroundAnalysisSmoke.exe'
$compiled=@(& $csc @common /target:exe /main:PressureBackgroundAnalysisSmoke ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'test.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa,(Join-Path $qa 'case')) -LogPrefix (Join-Path $qa 'background') -TimeoutMilliseconds 60000
$hostTestExe=Join-Path $qa 'PressureAnalysisHostOptionsSmoke.exe'
$compiled=@(& $csc @common /target:exe /main:PressureAnalysisHostOptionsSmoke ('/out:'+$hostTestExe) $hostTest $fixture 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'hosttest.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
$hostRun=Invoke-RegressionExecutable -Executable $hostTestExe -Arguments @($qa,(Join-Path $qa 'hostcase')) -LogPrefix (Join-Path $qa 'host-integration') -TimeoutMilliseconds 60000
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
[ordered]@{Schema='HVX.Pressure.BackgroundAnalysis.Focused/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');
 HardwareAccessed=$false;ProductEntryStarted=$false;UserProjectsWritten=$false;SourceUnchanged=$unchanged;FullRegression=$false;
 Inputs=$before;HostSHA256=(Get-FileHash -LiteralPath $hostAssembly).Hash;ModuleSHA256=(Get-FileHash -LiteralPath $dll).Hash;Result=$run;HostIntegrationResult=$hostRun}|
 ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $qa 'verification-background-analysis.json') -Encoding UTF8
$run.Output|Write-Output
$hostRun.Output|Write-Output
Write-Output ('BACKGROUND_ANALYSIS_PROOF='+(Join-Path $qa 'verification-background-analysis.json'))
if($run.ExitCode -ne 0 -or $hostRun.ExitCode -ne 0 -or -not $unchanged){throw ('Background analysis focused regression failed: '+$qa)}
