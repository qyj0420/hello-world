param([string]$DependencyBuild='build\main-test-20261009-r38b')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$dependency=[IO.Path]::GetFullPath((Join-Path $root $DependencyBuild))
$qa=Join-Path $root ('build\qa-threshold-drag-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$module=Join-Path $qa 'modules\PressureTest'
$analysis=Join-Path $dependency 'modules\PressureTest\HVX.PdReview.V1.0.dll'
$algorithm=Join-Path $dependency 'modules\PressureTest\fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'
Assert-RegressionUnlinkedPath $analysis;Assert-RegressionUnlinkedPath $algorithm
$sources=@(Get-ChildItem -LiteralPath 'modules-src\PressureTest\src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$sources+=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','ZeroVoltageNoiseReference.cs','LocationCycleRecord.cs','PressureRecordPublication.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$manifest=Get-Content -LiteralPath 'modules-src\Task39InputWorker\provenance\source-manifest.json' -Raw|ConvertFrom-Json
foreach($name in @('BenchModels.cs','PicoCaptureConfiguration.cs','LocationRangeAdvisor.cs','LocationRangeWindow.cs','RangeDecision.cs')){
 $entry=@($manifest.Files|Where-Object {$_.Path -ceq ('src/'+$name)})
 if($entry.Count -ne 1){throw 'Pinned pure input contract missing.'}
 $sources+=& (Join-Path $PSScriptRoot 'Test-PressureTask39Contract.ps1') -Name $name -OriginalHash $entry[0].SHA256
}
$names=@('PressureManualThresholdGuideSmoke','PressureManualThresholdDragSmoke')
$tests=@($names|ForEach-Object{Join-Path $root ('tests\'+$_+'.cs')})
$inputs=@($sources)+@($analysis,$algorithm,$PSCommandPath)+$tests
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
New-Item -ItemType Directory -Path $module|Out-Null
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$common=@('/nologo','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
$dll=Join-Path $module 'Xiongxin.OWTS.PressureTest.dll'
$compiled=@(& $csc @common /target:library ('/r:'+$analysis) ('/out:'+$dll) @sources 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'module.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
Copy-Item -LiteralPath $analysis -Destination (Join-Path $module 'HVX.PdReview.V1.0.dll')
Copy-Item -LiteralPath $algorithm -Destination (Join-Path $module 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll')
$results=New-Object 'System.Collections.Generic.List[object]'
foreach($name in $names){
 $exe=Join-Path $qa ($name+'.exe');$test=Join-Path $root ('tests\'+$name+'.cs')
 $compiled=@(& $csc @common /target:exe ('/main:'+$name) ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ($compiled -join "`n")}
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa,(Join-Path $qa ('case-'+$results.Count))) -LogPrefix (Join-Path $qa $name) -TimeoutMilliseconds 30000
 $results.Add([ordered]@{Name=$name;Result=$run;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash})
 $unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
 [ordered]@{Schema='HVX.Pressure.ManualThresholdDrag.Focused/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');
 HardwareAccessed=$false;SDKCalled=$false;ProductEntryStarted=$false;UserProjectsWritten=$false;SourceUnchanged=$unchanged;DependenciesUnchanged=$unchanged;FullRegression=$false;
 Inputs=$before;ModuleSHA256=(Get-FileHash -LiteralPath $dll).Hash;Tests=$results.ToArray()}|
 ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'verification-threshold-drag.json') -Encoding UTF8
 $run.Output|Write-Output
 Write-Output ('THRESHOLD_DRAG_PROOF='+(Join-Path $qa 'verification-threshold-drag.json'))
 if($run.ExitCode -ne 0 -or -not $unchanged){throw ('Threshold drag focused regression failed: '+$qa)}
}
