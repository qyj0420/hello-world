[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$BuildRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$build=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
if(-not $build.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Freeze requires an isolated project build.'}
foreach($relative in @('build-manifest.json','module-manifest.json','Xiongxin.OWTS.Desktop.exe')){
    $path=Join-Path $build $relative
    for($walk=$path;$walk;$walk=[IO.Path]::GetDirectoryName($walk)){
        if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked freeze paths are prohibited.'}
    }
    if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw ('Incomplete build: '+$relative)}
}
$manifest=Get-Content -LiteralPath (Join-Path $build 'build-manifest.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($manifest.ConfigurationProfile -cne 'PublicBeta-NoDriver-1.1.2-20261011' -or $manifest.FileVersion -cne '1.1.2.0' -or $manifest.Configuration -cne 'Release' -or
   $manifest.ProtectedRelease -ne $true -or $manifest.MaintenancePasswordEnabled -ne $true){throw 'Wrong build profile or protection state.'}
$exeHash=(Get-FileHash -LiteralPath (Join-Path $build 'Xiongxin.OWTS.Desktop.exe') -Algorithm SHA256).Hash
if($exeHash -cne $manifest.ExecutableSha256){throw 'Build manifest does not match the final EXE.'}
$output=Join-Path $build 'qa/public-beta-no-driver-freeze.json'
if(Test-Path -LiteralPath $output){throw 'A freeze already exists. Retain it; use a new build if runtime changes.'}
New-Item -ItemType Directory -Path (Join-Path $build 'qa') -Force|Out-Null
[ordered]@{
    Schema='HVX.PublicBetaNoDriver.Freeze/1';BuildRoot=$build;FrozenUtc=[DateTime]::UtcNow.ToString('o')
    ExecutableSHA256=$exeHash
    BuildManifestSHA256=(Get-FileHash -LiteralPath (Join-Path $build 'build-manifest.json') -Algorithm SHA256).Hash
    ModuleManifestSHA256=(Get-FileHash -LiteralPath (Join-Path $build 'module-manifest.json') -Algorithm SHA256).Hash
    HardwareAccessed=$false
}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $output -Encoding UTF8
Write-Output ('FREEZE_EVIDENCE='+$output)
