param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$out=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $out.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Tests must stay within build.'}
New-Item -ItemType Directory -Path $out -Force|Out-Null
$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$results=[Collections.Generic.List[object]]::new()
function Test($name,$sources,$arguments,$extra=@()) {
 $oemSource=Join-Path $root 'src\OemUiBranding.cs'
 if(-not @($sources | Where-Object {[IO.Path]::GetFullPath($_) -eq $oemSource}).Count){$sources+= $oemSource}
 $exe=Join-Path $out ($name+'.exe')
 & $compiler /nologo /langversion:latest /target:exe /platform:x64 ('/main:'+$name) ('/out:'+$exe) @refs @extra @sources 2>&1|Out-File -LiteralPath (Join-Path $out ($name+'.compile.txt')) -Encoding UTF8
 if($LASTEXITCODE -ne 0){throw ('Compilation failed: '+$name)}
 $start=@{FilePath=$exe;WindowStyle='Hidden';PassThru=$true;RedirectStandardOutput=(Join-Path $out ($name+'.stdout.txt'));RedirectStandardError=(Join-Path $out ($name+'.stderr.txt'))}
 if($arguments.Count){$start.ArgumentList=($arguments|ForEach-Object {'"'+$_+'"'}) -join ' '}
 $p=Start-Process @start;$watch=[Diagnostics.Stopwatch]::StartNew()
 while(-not $p.WaitForExit(250) -and $watch.ElapsedMilliseconds -lt 55000){}
 if(-not $p.HasExited){$p.Kill();$p.WaitForExit();$code=-999}else{$p.WaitForExit();$code=$p.ExitCode}
 $results.Add([pscustomobject]@{Test=$name;ExitCode=$code;Milliseconds=$watch.ElapsedMilliseconds})
 $results|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $out 'results.json') -Encoding UTF8
 Get-Content -LiteralPath $start.RedirectStandardOutput;Get-Content -LiteralPath $start.RedirectStandardError
}
$shared=@('src\CableJointStore.cs','src\ProjectMeasurementMode.cs')|ForEach-Object {Join-Path $root $_}
$probe=@('src\UiLanguage.cs','src\InternationalText.cs','src\InternationalText.Generated.cs','src\CableJointDialog.cs','modules-src\CalibrationTest\src\CalibrationProjectStore.cs','modules-src\PressureTest\src\PressureTestDrawing.cs','modules-src\PressureTest\src\PressureWaveformPreparedData.cs','modules-src\PressureTest\src\PressureTestTheme.cs','tests\AuditRepairRegression.cs')|ForEach-Object {Join-Path $root $_}
Test 'AuditRepairRegression' ($shared+$probe) @($out,(Join-Path $root 'assets\xiongxin.ico'))
Test 'JointMaterialEditorLayoutSmoke' ($shared+$probe+@(Join-Path $root 'tests\JointMaterialEditorLayoutSmoke.cs')) @($out,(Join-Path $root 'assets\xiongxin.ico'))
foreach($name in @('SharedCableJointStoreSmoke','SharedCableJointMutationSmoke')){Test $name ($shared+@(Join-Path $root ('tests\'+$name+'.cs'))) @($out)}
Test 'LocationCycleWindowSmoke' @((Join-Path $root 'src\LocationCycleRecord.cs'),(Join-Path $root 'tests\LocationCycleWindowSmoke.cs')) @()
& (Join-Path $PSScriptRoot 'Build-LocationAlgorithm.ps1') -OutputDirectory (Join-Path $out 'location')
Test 'LocationCyclePhaseSmoke' @((Join-Path $root 'tests\LocationCyclePhaseSmoke.cs')) @((Join-Path $out 'location'),$out)
& (Join-Path $PSScriptRoot 'Build-ReportEngine.ps1') -OutputDirectory (Join-Path $out 'report')
$engine=Join-Path $out 'report\HVX.Report.Engine.dll';Copy-Item -LiteralPath $engine -Destination (Join-Path $out 'HVX.Report.Engine.dll') -Force
$fake=Join-Path $out 'soffice.exe'
& $compiler /nologo /target:exe ('/out:'+$fake) (Join-Path $root 'tests\ReportConverterFixture.cs')
if($LASTEXITCODE -ne 0){throw 'Converter fixture compilation failed'}
Test 'ReportConverterPipeSmoke' @((Join-Path $root 'tests\ReportConverterPipeSmoke.cs')) @($out,$fake) @('/r:'+$engine)
Test 'SmokeTest' @((Join-Path $root 'modules-src\ReportEngine\tests\SmokeTest.cs')) @($out) @('/r:'+$engine)
Test 'BrowserPostCommitStatusSmoke' @((Join-Path $root 'tests\BrowserMeanSourceIntegritySmoke.cs'),(Join-Path $root 'tests\BrowserPostCommitStatusSmoke.cs')) @((Split-Path $out -Parent),$out)
Test 'AnalysisAiAutomaticEvaluationSmoke' @((Join-Path $root 'tests\AnalysisAiAutomaticEvaluationSmoke.cs')) @((Split-Path $out -Parent),$out)
Test 'ReportSavedConclusionSmoke' @((Join-Path $root 'tests\ReportSavedConclusionSmoke.cs')) @((Split-Path $out -Parent),$out)
Test 'PressureSidebarRefreshSmoke' @((Join-Path $root 'tests\PressureSidebarRefreshSmoke.cs')) @((Split-Path $out -Parent))
if(@($results|Where-Object ExitCode -ne 0).Count){throw 'Some regression tests failed; inspect results.json.'}
