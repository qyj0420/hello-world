param([Parameter(Mandatory=$true)][string]$BuildRoot,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $out.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence must stay under build.'}
if(Test-Path -LiteralPath $out){throw 'Use a new evidence directory; earlier results must be retained.'}
New-Item -ItemType Directory -Path $out | Out-Null
# Match the report consumer's actual runtime lookup, and retain the brand
# resources used by the dialog ownership checks. All staging stays in QA.
$report=Join-Path $BuildRoot 'modules\ReportGenerator'
New-Item -ItemType Directory -Path (Join-Path $report 'runtime'),(Join-Path $report 'templates'),(Join-Path $BuildRoot 'assets') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $BuildRoot 'engine\HVX.Report.Engine.dll') -Destination (Join-Path $report 'runtime\HVX.Report.Engine.dll') -Force
Copy-Item -LiteralPath (Join-Path $root 'dependencies\Report\Templates\EPFT_OWTS_Report_Template_v1.0.docx') -Destination (Join-Path $report 'templates') -Force
Copy-Item -LiteralPath (Join-Path $root 'assets\xiongxin.ico') -Destination (Join-Path $BuildRoot 'assets') -Force
$refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:System.Web.Extensions.dll')
$results=[Collections.Generic.List[object]]::new()
function Test($name,$sources,$extra=@()){
 $exe=Join-Path $out ($name+'.exe')
 & "$root/tools/compiler/csc.exe" /nologo /langversion:latest /target:exe /platform:x64 ('/main:'+$name) ('/out:'+$exe) ('/resource:'+$root+'\assets\xiongxin.ico,Brand.ico') @refs @extra @sources 2>&1 | Out-File "$out/$name.compile.txt" -Encoding UTF8
 if($LASTEXITCODE -ne 0){throw "Compile failed: $name"}
 $p=Start-Process $exe -ArgumentList ('"'+$BuildRoot+'" "'+$out+'"') -WindowStyle Hidden -PassThru -RedirectStandardOutput "$out/$name.stdout.txt" -RedirectStandardError "$out/$name.stderr.txt"
 $watch=[Diagnostics.Stopwatch]::StartNew();while(-not $p.WaitForExit(250) -and $watch.ElapsedMilliseconds -lt 180000){}
 if(-not $p.HasExited){$p.Kill();$p.WaitForExit();$code=-999}else{$p.WaitForExit();$code=$p.ExitCode}
 $results.Add([pscustomobject]@{Test=$name;ExitCode=$code;Milliseconds=$watch.ElapsedMilliseconds})
 ConvertTo-Json -InputObject @($results) | Set-Content "$out/results.json" -Encoding UTF8
 Get-Content "$out/$name.stdout.txt";Get-Content "$out/$name.stderr.txt"
}
foreach($name in @('ReportTemplateQuotaSmoke','ReportPhaseLanguageSmoke','ReportWaveformGeometrySmoke','ReportAppendixLayoutSmoke','StateGridReportSmoke','PressureSidebarLayoutSmoke')){
 Test $name @("$root/tests/$name.cs")
}
# SmokeTest expects its output directory as its sole argument; avoid the module
# build root, where its synthetic project would collide with build artifacts.
Copy-Item -LiteralPath "$BuildRoot/engine/HVX.Report.Engine.dll" -Destination "$out/HVX.Report.Engine.dll"
$exe=Join-Path $out 'SmokeTest.exe'
& "$root/tools/compiler/csc.exe" /nologo /target:exe ('/out:'+$exe) @refs ('/r:'+$out+'\HVX.Report.Engine.dll') "$root/modules-src/ReportEngine/tests/SmokeTest.cs" 2>&1 | Out-File "$out/SmokeTest.compile.txt" -Encoding UTF8
if($LASTEXITCODE -ne 0){throw 'Compile failed: SmokeTest'}
$p=Start-Process $exe -ArgumentList ('"'+$out+'"') -WindowStyle Hidden -PassThru -RedirectStandardOutput "$out/SmokeTest.stdout.txt" -RedirectStandardError "$out/SmokeTest.stderr.txt"
if(-not $p.WaitForExit(60000)){$p.Kill();$p.WaitForExit();$code=-999}else{$p.WaitForExit();$code=$p.ExitCode}
$results.Add([pscustomobject]@{Test='SmokeTest';ExitCode=$code});ConvertTo-Json -InputObject @($results) | Set-Content "$out/results.json" -Encoding UTF8
Get-Content "$out/SmokeTest.stdout.txt";Get-Content "$out/SmokeTest.stderr.txt"
if(@($results|Where-Object ExitCode -ne 0).Count){throw 'Regression failures retained; inspect results.json.'}
