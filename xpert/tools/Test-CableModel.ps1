param([Parameter(Mandatory=$true)][string]$BuildRoot,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$out=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $out.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence must stay under build.'}
if(Test-Path -LiteralPath $out){throw 'Use a new evidence directory.'}
New-Item -ItemType Directory -Path $out|Out-Null
$refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$results=[Collections.Generic.List[object]]::new()
foreach($name in @('TerminalVisualSmoke','CreationFormLayoutSmoke','CreationTopologyViewportSmoke','CreationTopologyJointSmoke','CreationJointInsulationSmoke','ProjectSaveTransactionSmoke')){
 $exe=Join-Path $out ($name+'.exe')
 & "$root/tools/compiler/csc.exe" /nologo /langversion:latest /target:exe /platform:x64 ('/main:'+$name) ('/out:'+$exe) @refs "$root/tests/$name.cs" 2>&1|Out-File "$out/$name.compile.txt" -Encoding UTF8
 if($LASTEXITCODE-ne 0){throw "Compile failed: $name"}
 $p=Start-Process $exe -ArgumentList ('"'+$BuildRoot+'" "'+$out+'"') -WindowStyle Hidden -PassThru -RedirectStandardOutput "$out/$name.stdout.txt" -RedirectStandardError "$out/$name.stderr.txt"
 $watch=[Diagnostics.Stopwatch]::StartNew();while(-not $p.WaitForExit(250)-and $watch.ElapsedMilliseconds-lt 180000){}
 if(-not $p.HasExited){$p.Kill();$p.WaitForExit();$code=-999}else{$p.WaitForExit();$code=$p.ExitCode}
 $results.Add([pscustomobject]@{Test=$name;ExitCode=$code;Milliseconds=$watch.ElapsedMilliseconds});ConvertTo-Json -InputObject @($results)|Set-Content "$out/results.json" -Encoding UTF8
 Get-Content "$out/$name.stdout.txt";Get-Content "$out/$name.stderr.txt"
}
if(@($results|Where-Object ExitCode -ne 0).Count){throw 'Cable model regressions failed; retained evidence.'}
