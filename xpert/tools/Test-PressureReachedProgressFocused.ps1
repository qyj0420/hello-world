param([string]$AnalysisLibrary='build\main-test-20261009-r39\modules\PressureTest\HVX.PdReview.V1.0.dll',[string]$BuildRoot='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-reached-progress-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Get-RegressionTreeFiles','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
New-Item -ItemType Directory -Path $qa|Out-Null
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$common=@('/nologo','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
$test=Join-Path $PSScriptRoot 'PressureReachedProgressSmoke.cs'
$inputs=@($test,$PSCommandPath,$csc)
$sourceBuild=[string]::IsNullOrWhiteSpace($BuildRoot)
if($sourceBuild){
 $runRoot=$qa
 $analysis=[IO.Path]::GetFullPath((Join-Path $root $AnalysisLibrary));Assert-RegressionUnlinkedPath $analysis
 $algorithm=Join-Path ([IO.Path]::GetDirectoryName($analysis)) 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll';Assert-RegressionUnlinkedPath $algorithm
 $module=Join-Path $qa 'modules\PressureTest';$review=Join-Path $qa 'modules\DataAnalysis'
 New-Item -ItemType Directory -Path $module,$review|Out-Null
 $hostSources=@(Get-ChildItem -LiteralPath 'src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
 $moduleSources=@(Get-ChildItem -LiteralPath 'modules-src\PressureTest\src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
 $moduleSources+=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','ZeroVoltageNoiseReference.cs','LocationCycleRecord.cs','PressureRecordPublication.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
 $manifestPath=Join-Path $root 'modules-src\Task39InputWorker\provenance\source-manifest.json'
 $contractTool=Join-Path $PSScriptRoot 'Test-PressureTask39Contract.ps1'
 $manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
 foreach($name in @('BenchModels.cs','PicoCaptureConfiguration.cs','LocationRangeAdvisor.cs','LocationRangeWindow.cs','RangeDecision.cs')){
  $entry=@($manifest.Files|Where-Object {$_.Path -ceq ('src/'+$name)})
  if($entry.Count -ne 1){throw 'Pinned pure input contract missing.'}
  $moduleSources+=& $contractTool -Name $name -OriginalHash $entry[0].SHA256
 }
 $inputs+=@($hostSources)+@($moduleSources)+@($analysis,$algorithm,$manifestPath,$contractTool,(Join-Path $root 'modules-src\PressureTest\vendor\Task39\pressure-contract-adaptation.json'))
 $hostAssembly=Join-Path $qa 'Xiongxin.OWTS.Desktop.exe'
 $dll=Join-Path $module 'Xiongxin.OWTS.PressureTest.dll'
}else{
 $runRoot=[IO.Path]::GetFullPath($BuildRoot);Assert-RegressionUnlinkedPath $runRoot
 $hostAssembly=Join-Path $runRoot 'Xiongxin.OWTS.Desktop.exe'
 $dll=Join-Path $runRoot 'modules\PressureTest\Xiongxin.OWTS.PressureTest.dll'
 Assert-RegressionUnlinkedPath $hostAssembly;Assert-RegressionUnlinkedPath $dll
 # Other focused tests may write their own QA directories concurrently. Hash
 # only immutable product runtime plus this test's inputs, not unrelated logs.
 $inputs+=@($hostAssembly)
 $config=$hostAssembly+'.config';if(Test-Path -LiteralPath $config){$inputs+=@($config)}
 $inputs+=@(Get-RegressionTreeFiles (Join-Path $runRoot 'modules')|Sort-Object FullName|ForEach-Object FullName)
}
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
if($sourceBuild){
 $compiled=@(& $csc @common /target:library ('/out:'+$hostAssembly) @hostSources 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'host.compile.txt') -Encoding UTF8
 if($code -ne 0){throw ($compiled -join "`n")}
 $compiled=@(& $csc @common /target:library ('/r:'+$analysis) ('/out:'+$dll) @moduleSources 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'module.compile.txt') -Encoding UTF8
 if($code -ne 0){throw ($compiled -join "`n")}
 Copy-Item -LiteralPath $analysis -Destination (Join-Path $module 'HVX.PdReview.V1.0.dll')
 Copy-Item -LiteralPath $analysis -Destination (Join-Path $review 'HVX.PdReview.V1.0.dll')
 Copy-Item -LiteralPath $algorithm -Destination (Join-Path $review 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll')
}
$exe=Join-Path $qa 'PressureReachedProgressSmoke.exe'
$compiled=@(& $csc @common /target:exe /main:PressureReachedProgressSmoke ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'test.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($runRoot,(Join-Path $qa 'case')) -LogPrefix (Join-Path $qa 'host-progress') -TimeoutMilliseconds 60000
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$proof=Join-Path $qa 'verification-reached-progress.json'
[ordered]@{Schema='HVX.Pressure.ReachedProgress.Focused/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');
 HardwareAccessed=$false;ProductEntryStarted=$false;WorkerStarted=$false;ResidentStarted=$false;SDKCalled=$false;UserProjectsWritten=$false;
 SourceBuild=$sourceBuild;InputsUnchanged=$unchanged;FullRegression=$false;Inputs=$before;
 HostSHA256=(Get-FileHash -LiteralPath $hostAssembly).Hash;ModuleSHA256=(Get-FileHash -LiteralPath $dll).Hash;Result=$run}|
 ConvertTo-Json -Depth 10|Set-Content -LiteralPath $proof -Encoding UTF8
$run.Output|Write-Output
Write-Output ('REACHED_PROGRESS_PROOF='+$proof)
if($run.ExitCode -ne 0 -or -not $unchanged){throw ('Reached progress regression failed: '+$qa)}
