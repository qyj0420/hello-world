param([Parameter(Mandatory=$true)][string]$BuildRoot,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $out.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence must stay under build.'}
New-Item -ItemType Directory -Path $out -Force | Out-Null
$results=[Collections.Generic.List[object]]::new()
$refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:System.Web.Extensions.dll')
function Test($name,$sources){
 $exe=Join-Path $out ($name+'.exe')
 & "$root/tools/compiler/csc.exe" /nologo /langversion:latest /target:exe /platform:x64 ('/main:'+$name) ('/out:'+$exe) @refs @sources 2>&1 | Out-File "$out/$name.compile.txt" -Encoding UTF8
 if($LASTEXITCODE -ne 0){throw "Compile failed: $name"}
 $watch=[Diagnostics.Stopwatch]::StartNew()
 $p=Start-Process $exe -ArgumentList ('"'+$BuildRoot+'" "'+$out+'"') -WindowStyle Hidden -PassThru -RedirectStandardOutput "$out/$name.stdout.txt" -RedirectStandardError "$out/$name.stderr.txt"
 while(-not $p.WaitForExit(250) -and $watch.ElapsedMilliseconds -lt 120000){}
 if(-not $p.HasExited){$p.Kill();$p.WaitForExit();$code=-999}else{$p.WaitForExit();$code=$p.ExitCode}
 $results.Add([pscustomobject]@{Test=$name;ExitCode=$code;Milliseconds=$watch.ElapsedMilliseconds})
 $results | ConvertTo-Json | Set-Content "$out/results.json" -Encoding UTF8
 Get-Content "$out/$name.stdout.txt"; Get-Content "$out/$name.stderr.txt"
}
$fixture=@("$root/tests/BrowserMeanSourceIntegritySmoke.cs","$root/tests/BrowserJointAsyncResponsiveSmoke.cs")
Test 'BrowserJointFirstSaveSmoke' ($fixture+@("$root/tests/BrowserJointFirstSaveSmoke.cs"))
Test 'BrowserJointAsyncResponsiveSmoke' $fixture
Test 'ReportAppendixLayoutSmoke' @("$root/tests/ReportAppendixLayoutSmoke.cs")
Test 'ReportWaveformGeometrySmoke' @("$root/tests/ReportWaveformGeometrySmoke.cs")
Test 'StateGridReportSmoke' @("$root/tests/StateGridReportSmoke.cs")
if(@($results|Where-Object ExitCode -ne 0).Count){throw 'Offline checks failed; retain logs.'}
