param([string]$BuildFolder='Internal07-work4',[string]$QaFolder='qa-internal07-endpoint')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\'+$QaFolder)
New-Item -ItemType Directory -Path $qa -Force | Out-Null
$sources=@(Get-ChildItem src -Filter '*.cs' | ForEach-Object FullName)
$refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:Microsoft.CSharp.dll')
$exe=Join-Path $qa 'EndpointManufacturerPolicySmoke.exe'
& tools/compiler/csc.exe /nologo /langversion:7.3 /target:exe /platform:x64 /main:EndpointManufacturerPolicySmoke ('/out:'+$exe) /win32manifest:app.manifest /resource:assets/xiongxin.ico,Brand.ico /resource:assets/xiongxin-original.png,Brand.png @refs @sources tests/EndpointManufacturerPolicySmoke.cs
if($LASTEXITCODE -ne 0){throw 'Test compilation failed.'}
$release=Join-Path $root ('build\'+$BuildFolder)
$process=Start-Process -FilePath $exe -ArgumentList ('"'+$release+'" "'+$qa+'"') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $qa 'stdout.txt') -RedirectStandardError (Join-Path $qa 'stderr.txt')
$process.WaitForExit();$process.Refresh()
Get-Content (Join-Path $qa 'stdout.txt')
if($process.ExitCode -ne 0){Get-Content (Join-Path $qa 'stderr.txt');throw 'Endpoint manufacturer test failed.'}
