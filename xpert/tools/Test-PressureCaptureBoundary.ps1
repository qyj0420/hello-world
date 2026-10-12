param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
if(-not $OutputRoot.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an isolated project build output.'}
if(Test-Path -LiteralPath $OutputRoot){throw 'Use a new output directory; preserve previous evidence.'}
New-Item -ItemType Directory -Path $OutputRoot|Out-Null
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File|Sort-Object FullName|ForEach-Object FullName)
$test=Join-Path $root 'tests\PressureCaptureBoundarySmoke.cs'
$review=@('modules-src\PressureTest\src\PressureTestPreferences.cs','modules-src\PressureTest\src\PressureP04Feedback.cs','modules-src\PressureTest\src\PressureFinishCurrentRound.cs','modules-src\PressureTest\src\PressureTestControl.ZeroVoltageHardware.cs')|ForEach-Object{Join-Path $root $_}
$inputs=@(($sources+@($test)+$review)|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll')
$library=Join-Path $OutputRoot 'Host.CompileOnly.dll'
$lines=@(& $compiler /nologo /target:library /platform:x64 /langversion:7.3 @refs ('/out:'+$library) @sources 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $OutputRoot 'host-compile.txt') -Encoding UTF8
if($code-ne 0){throw ($lines-join "`n")}
$exe=Join-Path $OutputRoot 'PressureCaptureBoundarySmoke.exe'
$lines=@(& $compiler /nologo /target:exe /main:PressureCaptureBoundarySmoke /platform:x64 /langversion:7.3 @refs ('/out:'+$exe) @sources $test 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $OutputRoot 'test-compile.txt') -Encoding UTF8
if($code-ne 0){throw ($lines-join "`n")}
# Explicit test entry point: injected managed delegates only. The product's
# Program.Main, device bridges, native libraries and monitor are not executed.
$p=Start-Process -FilePath $exe -ArgumentList ('"'+$root+'"') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $OutputRoot 'stdout.txt') -RedirectStandardError (Join-Path $OutputRoot 'stderr.txt')
if(-not $p.WaitForExit(30000)){$p.Kill();$p.WaitForExit();throw 'Managed boundary test timed out.'}
$code=$p.ExitCode
$unchanged=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash-ne $_.SHA256}).Count-eq 0
[ordered]@{Schema='HVX.PressureCaptureBoundary.QA/1';HardwareAccessed=$false;NativeCalls=$false;ProductStarted=$false;WorkersStarted=0;HostCompileExit=0;TestCompileExit=0;ExitCode=$code;InputsUnchanged=$unchanged;Inputs=$inputs;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash;HostSHA256=(Get-FileHash -LiteralPath $library).Hash}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $OutputRoot 'verification.json') -Encoding UTF8
Get-Content -LiteralPath (Join-Path $OutputRoot 'stdout.txt')
if($code-ne 0-or-not $unchanged){Get-Content -LiteralPath (Join-Path $OutputRoot 'stderr.txt');throw ('Boundary test failed: '+$OutputRoot)}
Write-Output ('PRESSURE_BOUNDARY_PROOF='+$OutputRoot)
