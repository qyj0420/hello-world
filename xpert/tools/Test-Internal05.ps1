param([string]$BuildFolder='Preview-1.1.3-Internal05',[string]$QaFolder='qa-internal05')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
$release=Join-Path $root ('build\'+$BuildFolder)
$qa=Join-Path $root ('build\'+$QaFolder)
New-Item -ItemType Directory -Path $qa -Force | Out-Null
$sources=@(Get-ChildItem src -Filter '*.cs' | ForEach-Object FullName)
$refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:Microsoft.CSharp.dll')
$results=@()
foreach($name in @('PressureInternal05Smoke','Internal04Smoke','AnalysisReadableLayoutSmoke','AnalysisPositionSettingsSmoke','AnalysisSaveDirtySmoke','CreationFormLayoutSmoke','CreationTraditionalLanguageSmoke','CalibrationViewportFeedbackSmoke','CalibrationNavigationSmoke')) {
    $exe=Join-Path $qa ($name+'.exe')
    & tools/compiler/csc.exe /nologo /langversion:7.3 /target:exe /platform:x64 ('/main:'+$name) ('/out:'+$exe) /win32manifest:app.manifest /resource:assets/xiongxin.ico,Brand.ico /resource:assets/xiongxin-original.png,Brand.png @refs @sources ('tests/'+$name+'.cs')
    if($LASTEXITCODE -ne 0){throw 'Test compile failed'}
    $arguments=@($release,(Join-Path $qa $name))
    if($name -eq 'Internal04Smoke'){$arguments+='D:\OWTSData\050537-050538\2026年05月05日16时31分\C_18.30kV_2026-05-05-16''35''08.loc'}
    $p=Start-Process -FilePath $exe -ArgumentList (($arguments|ForEach-Object{'"'+$_+'"'}) -join ' ') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $qa ($name+'.stdout.txt')) -RedirectStandardError (Join-Path $qa ($name+'.stderr.txt'))
    $p.WaitForExit();$p.Refresh()
    Get-Content (Join-Path $qa ($name+'.stdout.txt'))
    if($p.ExitCode -ne 0){Get-Content (Join-Path $qa ($name+'.stderr.txt'));throw "Test failed: $name"}
    $results += [pscustomobject]@{Test=$name;ExitCode=$p.ExitCode}
}
$results|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
