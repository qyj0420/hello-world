param([string]$VisualStudioRoot='')
$ErrorActionPreference='Stop'
if([String]::IsNullOrWhiteSpace($VisualStudioRoot)){
 $vswhere='C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
 $VisualStudioRoot=@(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)[0]
 if($LASTEXITCODE -ne 0 -or [String]::IsNullOrWhiteSpace($VisualStudioRoot)){throw 'Installed C++ toolchain not found'}
}
$version=(Get-Content -LiteralPath (Join-Path $VisualStudioRoot 'VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
$vc=Join-Path $VisualStudioRoot ('VC\Tools\MSVC\'+$version)
$sdk='C:\Program Files (x86)\Windows Kits\10'
$sdkVersion=@(Get-ChildItem -LiteralPath (Join-Path $sdk 'Include') -Directory |
 Where-Object {Test-Path -LiteralPath (Join-Path $sdk ('Lib\'+$_.Name+'\um\x64\kernel32.lib'))} |
 Sort-Object {[Version]$_.Name} -Descending | Select-Object -First 1)[0].Name
$bin=Join-Path $vc 'bin\Hostx64\x64'
$sdkBin=Join-Path $sdk ('bin\'+$sdkVersion+'\x64')
$includes=@((Join-Path $vc 'include'),(Join-Path $sdk ('Include\'+$sdkVersion+'\ucrt')),(Join-Path $sdk ('Include\'+$sdkVersion+'\shared')),(Join-Path $sdk ('Include\'+$sdkVersion+'\um')))
$libs=@((Join-Path $vc 'lib\x64'),(Join-Path $sdk ('Lib\'+$sdkVersion+'\ucrt\x64')),(Join-Path $sdk ('Lib\'+$sdkVersion+'\um\x64')))
foreach($path in @($bin,$sdkBin)+$includes+$libs){if(-not(Test-Path -LiteralPath $path -PathType Container)){throw ('Missing installed C++ path: '+$path)}}
[pscustomobject]@{Bin=$bin;SearchPath=($bin+';'+$sdkBin);Include=($includes -join ';');Lib=($libs -join ';');VC=$vc;Version=$version;SDKVersion=$sdkVersion}
