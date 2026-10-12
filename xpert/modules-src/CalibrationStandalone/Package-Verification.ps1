param([string]$Version = '20260928_v1')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sourceRoot=[IO.Path]::GetFullPath($PSScriptRoot)
$projectRoot=[IO.Path]::GetFullPath((Join-Path $sourceRoot '..\..'))
$codexRoot=[IO.Path]::GetFullPath((Join-Path $projectRoot '..'))
$deliver=Join-Path $projectRoot ('deliverables\XIONGXIN_Calibration_Verification_'+$Version)
$review=Join-Path $codexRoot ('HVXUnifiedDesktop_SourceReview\CalibrationStandalone_'+$Version)
$zip=$deliver+'.zip'
foreach($p in @($deliver,$review,$zip)) {if(Test-Path -LiteralPath $p) {throw "Target already exists; refusing overwrite: $p"}}
$build=Join-Path $projectRoot 'build\CalibrationStandalone'
& (Join-Path $sourceRoot 'Build.ps1') -OutputDirectory $build
$exe=Join-Path $build 'XIONGXIN.Calibration.Verification.exe'
if(-not(Test-Path -LiteralPath $exe -PathType Leaf)) {throw 'Build output missing.'}
New-Item -ItemType Directory -Path $deliver,$review -Force | Out-Null
Copy-Item -LiteralPath $exe -Destination $deliver
Copy-Item -LiteralPath ($exe+'.config') -Destination $deliver
Copy-Item -LiteralPath (Join-Path $sourceRoot 'README-验证版.md') -Destination (Join-Path $deliver 'README-验证版.md')
$smoke=Join-Path $build 'smoke-release'
if(Test-Path -LiteralPath (Join-Path $smoke 'result.txt')){
    Copy-Item -LiteralPath (Join-Path $smoke 'result.txt') -Destination (Join-Path $deliver '离线检查结果.txt')
    Copy-Item -LiteralPath (Join-Path $smoke 'window.png') -Destination (Join-Path $deliver '界面预览.png')
}
Get-ChildItem -LiteralPath $sourceRoot -Force | Where-Object {$_.Name -ne 'build'} | Copy-Item -Destination $review -Recurse -Force
$hash=(Get-FileHash -LiteralPath (Join-Path $deliver 'XIONGXIN.Calibration.Verification.exe') -Algorithm SHA256).Hash
Set-Content -LiteralPath (Join-Path $deliver 'XIONGXIN.Calibration.Verification.exe.sha256') -Value ($hash+' *XIONGXIN.Calibration.Verification.exe') -Encoding ASCII
Compress-Archive -Path (Join-Path $deliver '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Output "EXE: $(Join-Path $deliver 'XIONGXIN.Calibration.Verification.exe')"
Write-Output "SOURCE: $review"
Write-Output "ZIP: $zip"
Write-Output "SHA256: $hash"
