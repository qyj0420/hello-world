param(
 [string]$DependencyBuild='build\main-repair-20261010-r42',
 [string]$AnalysisPath='D:\codex\Task28_FilterB_20261010\qa\host\modules\DataAnalysis\HVX.PdReview.V1.0.dll'
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$dependency=[IO.Path]::GetFullPath((Join-Path $root $DependencyBuild))
$qa=Join-Path $root ('build\qa-pressure-value-refresh-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$module=Join-Path $qa 'modules\PressureTest'
$analysis=[IO.Path]::GetFullPath($AnalysisPath)
$algorithm=Join-Path $dependency 'modules\PressureTest\fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'
Assert-RegressionUnlinkedPath $analysis;Assert-RegressionUnlinkedPath $algorithm
$sources=@(Get-ChildItem -LiteralPath 'modules-src\PressureTest\src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$sources+=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','ZeroVoltageNoiseReference.cs','LocationCycleRecord.cs','PressureRecordPublication.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$contract=Join-Path $PSScriptRoot 'Test-PressureTask39Contract.ps1'
$manifestPath=Join-Path $root 'modules-src\Task39InputWorker\provenance\source-manifest.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
foreach($name in @('BenchModels.cs','PicoCaptureConfiguration.cs','LocationRangeAdvisor.cs','LocationRangeWindow.cs','RangeDecision.cs')){
 $entry=@($manifest.Files|Where-Object {$_.Path -ceq ('src/'+$name)})
 if($entry.Count -ne 1){throw 'Pinned pure input contract missing.'}
 $sources+=& $contract -Name $name -OriginalHash $entry[0].SHA256
}
$names=@('PressureValueOnlyRefreshSmoke','PressureSidebarRefreshSmoke','PressurePresetVoltageDisplaySmoke','PressureLatchedEmergencyDisplaySmoke','PressurePlanLayoutSmoke','PressureEmergencyCaptionLayoutSmoke')
$tests=@($names|ForEach-Object{Join-Path $root ('tests\'+$_+'.cs')})
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=@($sources)+@($analysis,$algorithm)+$tests+@($PSCommandPath,$helper,$contract,$manifestPath,$csc)
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
New-Item -ItemType Directory -Path $module|Out-Null
$common=@('/nologo','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
$dll=Join-Path $module 'Xiongxin.OWTS.PressureTest.dll'
$compiled=@(& $csc @common /target:library ('/r:'+$analysis) ('/out:'+$dll) @sources 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'module.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
Copy-Item -LiteralPath $analysis -Destination (Join-Path $module 'HVX.PdReview.V1.0.dll')
Copy-Item -LiteralPath $algorithm -Destination (Join-Path $module 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll')
$analysisModule=Join-Path $qa 'modules\DataAnalysis'
New-Item -ItemType Directory -Path $analysisModule|Out-Null
Copy-Item -LiteralPath $analysis -Destination (Join-Path $analysisModule 'HVX.PdReview.V1.0.dll')
$runs=New-Object 'System.Collections.Generic.List[object]'
foreach($name in $names){
 $exe=Join-Path $qa ($name+'.exe');$source=Join-Path $root ('tests\'+$name+'.cs')
 $compiled=@(& $csc @common /target:exe ('/main:'+$name) ('/out:'+$exe) $source 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ($compiled -join "`n")}
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa,(Join-Path $qa ($name+'-fixture'))) -LogPrefix (Join-Path $qa $name) -TimeoutMilliseconds 45000
 $runs.Add([ordered]@{Name=$name;Execution=$run});$run.Output|Write-Output
}
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$passed=@($runs|Where-Object {$_.Execution.ExitCode -ne 0}).Count -eq 0
$proof=Join-Path $qa 'verification-pressure-value-refresh.json'
[ordered]@{Schema='HVX.Pressure.ValueOnlyRefresh.Focused/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');
 Status=$(if($passed -and $unchanged){'PASS'}else{'FAIL'});HardwareAccessed=$false;SDKCalled=$false;ProductEntryStarted=$false;UserProjectsWritten=$false;
 SourceUnchanged=$unchanged;PhysicalAcceptance=$false;FullRegression=$false;DependencyBuild=$dependency;Inputs=$before;ModuleSHA256=(Get-FileHash -LiteralPath $dll).Hash;Runs=$runs.ToArray();
 Scope='Real managed controls; progress-only fill, final metric/status text, same-key geometry/window guards, resize/font/language, producer LOW/UNKNOWN fail-closed. No hardware or workflow policy changes.'}|
 ConvertTo-Json -Depth 15|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output ('VALUE_REFRESH_PROOF='+$proof)
if(-not $passed -or -not $unchanged){throw ('Value-only focused regression failed: '+$qa)}
