param([string]$BuildRoot='build/International-20261002/HV-DAC_Xpert_Multilingual_Preview')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Set-Location $root
$BuildRoot=[IO.Path]::GetFullPath($BuildRoot)
$qa=Join-Path $root 'build/International-20261002/verification'
$refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll')
foreach($test in @('InternationalCatalogAudit','ThreeModuleLanguageSmoke','CreationTraditionalLanguageSmoke','AnalysisLanguageNoOpSmoke')) {
    $exe=Join-Path $qa ($test+'.exe')
    $sources=@('tests/'+$test+'.cs')
    if($test -eq 'InternationalCatalogAudit'){$sources+=@('src/UiLanguage.cs','src/InternationalText.cs','src/InternationalText.Generated.cs')}
    & tools/compiler/csc.exe /nologo /langversion:7.3 /target:exe /platform:x64 ('/out:'+$exe) @refs @sources *> (Join-Path $qa ($test+'.compile.log'))
    if($LASTEXITCODE -ne 0){Get-Content (Join-Path $qa ($test+'.compile.log')) -Tail 12;throw ($test+' compilation failed')}
    & $exe $BuildRoot (Join-Path $qa $test) *> (Join-Path $qa ($test+'.log'))
    Get-Content (Join-Path $qa ($test+'.log')) -Tail 20
    if($LASTEXITCODE -ne 0){throw ($test+' failed')}
}
