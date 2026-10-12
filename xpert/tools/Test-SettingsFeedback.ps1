param([Parameter(Mandatory=$true)][string]$OutputDirectory,[string[]]$Tests=@('SettingsFeedbackSmoke'),[string]$BuildRoot='')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $out.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence must stay under build.'}
if(Test-Path -LiteralPath $out){throw 'Use a new evidence directory.'}
New-Item -ItemType Directory -Path $out | Out-Null
$sources=@(Get-ChildItem -LiteralPath "$root/src" -Filter '*.cs' -File|Sort-Object FullName|ForEach-Object FullName)
$refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Security.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll')
$results=@()
foreach($test in $Tests){
    if($test -notmatch '^[A-Za-z0-9]+$'){throw 'Invalid test name.'}
    $exe=Join-Path $out ($test+'.exe')
    $testOptions=@();if($test -eq 'DpiLayoutSmoke'){$testOptions+=('/win32manifest:'+$root+'\app.manifest')}
    & "$root/tools/compiler/csc.exe" /nologo /langversion:7.3 /target:exe /platform:x64 ('/main:'+$test) ('/out:'+$exe) ('/resource:'+$root+'\assets\xiongxin.ico,Brand.ico') ('/resource:'+$root+'\assets\xiongxin-original.png,Brand.png') @testOptions @refs @sources "$root/tests/$test.cs" 2>&1 | Out-File "$out/$test.compile.txt" -Encoding UTF8
    if($LASTEXITCODE -ne 0){Get-Content "$out/$test.compile.txt";throw 'Compile failed.'}
    $fixture=Join-Path $out ($test+'-fixture');New-Item -ItemType Directory -Path $fixture | Out-Null
    $first=if([String]::IsNullOrWhiteSpace($BuildRoot)){$fixture}else{[IO.Path]::GetFullPath($BuildRoot)}
    $arguments=if($test -in @('SettingsFeedbackSmoke','SecureProjectStoreSmoke')){ '"'+$fixture+'"' }else{ '"'+$first+'" "'+$fixture+'"' }
    $p=Start-Process $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput "$out/$test.stdout.txt" -RedirectStandardError "$out/$test.stderr.txt"
    $watch=[Diagnostics.Stopwatch]::StartNew();while(-not $p.WaitForExit(250) -and $watch.ElapsedMilliseconds -lt 180000){}
    if(-not $p.HasExited){$p.Kill();$p.WaitForExit();$code=-999}else{$p.WaitForExit();$code=$p.ExitCode}
    $results+= [pscustomobject]@{Test=$test;ExitCode=$code;Milliseconds=$watch.ElapsedMilliseconds}
    $results|ConvertTo-Json|Set-Content "$out/results.json" -Encoding UTF8
    Get-Content "$out/$test.stdout.txt";Get-Content "$out/$test.stderr.txt"
}
if(@($results|Where-Object {$_.ExitCode -ne 0}).Count){throw 'Settings regression failed; retained evidence.'}
