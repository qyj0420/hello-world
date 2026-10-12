param([string]$OutputRoot='D:\codex\HVXUnifiedDesktop\build\public-beta-1.1.1-20261011-r52')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$output=[IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if(-not $output.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Build must remain in the project build tree.'}
if(Test-Path -LiteralPath $output){throw 'Preserve previous builds; use a new output directory.'}
$paths=@()
foreach($folder in @('src','modules-src','native','hybrid-src','assets')){
 $base=Join-Path $root $folder
 if(Test-Path -LiteralPath $base){$paths+=@(Get-ChildItem -LiteralPath $base -Recurse -File|Where-Object {$folder -eq 'assets' -or $_.Extension -in @('.cs','.cpp','.c','.h','.hpp','.py','.pyx','.pxd','.ps1','.json','.resx','.config','.txt','.ico','.png')}|ForEach-Object FullName)}
}
$paths+=@(Get-ChildItem -LiteralPath (Join-Path $root 'tools') -File|Where-Object Name -Match '^(Build|Stage|Protect|Test-FactoryBatchSource)'|ForEach-Object FullName)
$paths+=@((Join-Path $root 'Build.ps1'),(Join-Path $root 'Stage-Modules.ps1'),(Join-Path $root 'App.config'),(Join-Path $root 'app.manifest'),$PSCommandPath)
$inputs=@($paths|Sort-Object -Unique|ForEach-Object{
 if((Get-Item -LiteralPath $_ -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked build input rejected.'}
 [ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}
})
New-Item -ItemType Directory -Path (Join-Path $output 'qa')|Out-Null
$started=[DateTime]::UtcNow.ToString('o')
[ordered]@{Schema='HVX.R52.SourceFreeze/1';Release='r52';StartedUtc=$started;Inputs=$inputs;ProductStarted=$false;HardwareAccessed=$false}|
 ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'qa\source-freeze-initial.json') -Encoding UTF8
& (Join-Path $root 'Build.ps1') -Configuration Release -Hybrid -V30BatchValidation -ProtectedRelease -MaintenancePassword -OutputDirectory $output
$mismatches=@($inputs|Where-Object {(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count
$manifest=Get-Content -LiteralPath (Join-Path $output 'build-manifest.json') -Raw|ConvertFrom-Json
$exe=Join-Path $output 'Xiongxin.OWTS.Desktop.exe'
$pass=$mismatches -eq 0 -and $manifest.ExecutableSha256 -ceq (Get-FileHash -LiteralPath $exe).Hash -and $manifest.ProtectedRelease -and $manifest.MaintenancePasswordEnabled
[ordered]@{Schema='HVX.R52.SourceFreezeBuild/1';Release='r52';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Passed=[bool]$pass;ExitCode=$(if($pass){0}else{1});
 SourceUnchanged=($mismatches -eq 0);InputMismatchCount=$mismatches;Inputs=$inputs;ExecutableSHA256=$manifest.ExecutableSha256;ProductStarted=$false;HardwareAccessed=$false;SDKCalled=$false;InstallerBuilt=$false;PhysicalAcceptance=$false}|
 ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'qa\source-freeze-build.json') -Encoding UTF8
if(-not $pass){throw 'Build input changed; do not deliver this candidate.'}
Write-Output ('R52_FROZEN_BUILD='+$output)
