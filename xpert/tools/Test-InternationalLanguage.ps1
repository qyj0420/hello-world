param([string]$BuildRoot = 'build/International-20261002/HV-DAC_Xpert_Multilingual_Preview')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Set-Location $root
$BuildRoot=[IO.Path]::GetFullPath($BuildRoot)
$qa=Join-Path $root 'build/International-20261002/verification'
New-Item -ItemType Directory -Force -Path $qa | Out-Null
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName)
$refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll')
foreach($test in @('InternationalLanguageSmoke','LanguagePreferenceSmoke')) {
    $exe=Join-Path $qa ($test+'.exe')
    & tools/compiler/csc.exe /nologo /langversion:7.3 /target:exe /platform:x64 ('/main:'+$test) ('/out:'+$exe) @refs /resource:assets/xiongxin.ico,Brand.ico /resource:assets/xiongxin-original.png,Brand.png @sources ('tests/'+$test+'.cs') *> (Join-Path $qa ($test+'.compile.log'))
    if($LASTEXITCODE -ne 0){Get-Content (Join-Path $qa ($test+'.compile.log')) -Tail 12;throw ($test+' compilation failed')}
    if($test -eq 'InternationalLanguageSmoke') { & $exe $BuildRoot (Join-Path $qa 'international') *> (Join-Path $qa ($test+'.log')) }
    else { & $exe (Join-Path $qa 'legacy') *> (Join-Path $qa ($test+'.log')) }
    Get-Content (Join-Path $qa ($test+'.log')) -Tail 18
    if($LASTEXITCODE -ne 0){throw ($test+' failed')}
}
