param([Parameter(Mandatory=$true)][string]$BuildRoot,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $out.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence must stay under build.'}
if(Test-Path -LiteralPath $out){throw 'Use a new evidence directory.'}
New-Item -ItemType Directory -Path $out | Out-Null
$sources=@(Get-ChildItem -LiteralPath "$root/src" -Filter '*.cs' -File|Sort-Object FullName|ForEach-Object FullName)+@("$root/tests/ProjectCreationGuideSmoke.cs")
$refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Security.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll')
$exe=Join-Path $out 'ProjectCreationGuideSmoke.exe'
& "$root/tools/compiler/csc.exe" /nologo /langversion:7.3 /target:exe /platform:x64 /main:ProjectCreationGuideSmoke ('/out:'+$exe) ('/resource:'+$root+'\assets\xiongxin.ico,Brand.ico') ('/resource:'+$root+'\assets\xiongxin-original.png,Brand.png') @refs @sources 2>&1 | Out-File "$out/compile.txt" -Encoding UTF8
if($LASTEXITCODE -ne 0){Get-Content "$out/compile.txt";throw 'Compile failed.'}
$p=Start-Process $exe -ArgumentList ('"'+$BuildRoot+'" "'+$out+'"') -WindowStyle Hidden -PassThru -RedirectStandardOutput "$out/stdout.txt" -RedirectStandardError "$out/stderr.txt"
$watch=[Diagnostics.Stopwatch]::StartNew();while(-not $p.WaitForExit(250) -and $watch.ElapsedMilliseconds -lt 180000){}
if(-not $p.HasExited){$p.Kill();$p.WaitForExit();$code=-999}else{$p.WaitForExit();$code=$p.ExitCode}
[pscustomobject]@{Test='ProjectCreationGuideSmoke';ExitCode=$code;Milliseconds=$watch.ElapsedMilliseconds}|ConvertTo-Json|Set-Content "$out/results.json" -Encoding UTF8
Get-Content "$out/stdout.txt";Get-Content "$out/stderr.txt"
if($code-ne 0){throw 'Guide regression failed; retained evidence.'}
