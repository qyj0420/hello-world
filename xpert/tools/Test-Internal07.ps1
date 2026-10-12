param([string]$BuildFolder='Internal07-work6',[string]$QaFolder='qa-internal07-r6')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
$release=Join-Path $root ('build\'+$BuildFolder)
$qa=Join-Path $root ('build\'+$QaFolder)
New-Item -ItemType Directory -Path $qa -Force | Out-Null
$refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:Microsoft.CSharp.dll')
$results=@()
foreach($name in @('EndpointManufacturerPolicySmoke','CalibrationPersistenceTimingSmoke','Internal07FeedbackSmoke','RawCaptureStoreSmoke','ModulePresentationSmoke')){
    $extra=@();$arguments=@($release,(Join-Path $qa $name))
    if($name -in @('EndpointManufacturerPolicySmoke','ModulePresentationSmoke')){$extra=@(Get-ChildItem src -Filter '*.cs' | ForEach-Object FullName)}
    if($name -eq 'Internal07FeedbackSmoke'){$arguments=@($release,'D:\OWTSData\尖端放电-6.14\2023年06月14日-14时34分')}
    if($name -eq 'RawCaptureStoreSmoke'){
        $dll=Join-Path $release 'modules\Task38\HVX.OWTS.Task38.V1.0.38.dll'
        Copy-Item -LiteralPath $dll -Destination $qa -Force
        $extra=@('/r:'+$dll);$arguments=@((Join-Path $qa $name))
    }
    $exe=Join-Path $qa ($name+'.exe')
    & tools/compiler/csc.exe /nologo /langversion:7.3 /target:exe /platform:x64 ('/main:'+$name) ('/out:'+$exe) /win32manifest:app.manifest /resource:assets/xiongxin.ico,Brand.ico /resource:assets/xiongxin-original.png,Brand.png @refs @extra ('tests/'+$name+'.cs')
    if($LASTEXITCODE -ne 0){throw "Compile failed: $name"}
    $stdout=Join-Path $qa ($name+'.stdout.txt');$stderr=Join-Path $qa ($name+'.stderr.txt')
    $p=Start-Process -FilePath $exe -ArgumentList (($arguments|ForEach-Object{'"'+$_+'"'}) -join ' ') -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $p.WaitForExit();$p.Refresh();Get-Content $stdout
    if($p.ExitCode -ne 0){Get-Content $stderr;throw "Failed: $name"}
    $results += [pscustomobject]@{Test=$name;ExitCode=$p.ExitCode;HardwareCommands=0}
}
$results|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $qa 'verification-internal07.json') -Encoding UTF8
