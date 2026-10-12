[CmdletBinding()]
param([string]$VisualStudioRoot,[string]$OutputRoot)
$ErrorActionPreference='Stop'
$nativeRoot=[IO.Path]::GetFullPath($PSScriptRoot)
if([string]::IsNullOrWhiteSpace($VisualStudioRoot)) {
    $vswhere='C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    if(!(Test-Path -LiteralPath $vswhere -PathType Leaf)){throw 'Installed VS BuildTools locator not found; this script never installs a compiler.'}
    $locatedPaths=@(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
    $locatorExit=$LASTEXITCODE
    $VisualStudioRoot=$locatedPaths | Select-Object -First 1
    if($locatorExit -ne 0 -or [string]::IsNullOrWhiteSpace($VisualStudioRoot)){throw 'Installed x64 C++ compiler not found.'}
}
$VisualStudioRoot=[IO.Path]::GetFullPath($VisualStudioRoot)
if(!(Test-Path -LiteralPath (Join-Path $VisualStudioRoot 'VC\Auxiliary\Build\vcvars64.bat') -PathType Leaf)){throw 'Selected installed x64 compiler environment is missing.'}
# All generated files live outside source pins. The root project build owns
# final packaging; this helper creates only a distinct offline proof directory.
$projectRoot=[IO.Path]::GetFullPath((Join-Path $nativeRoot '..\..'))
$allowedRoot=[IO.Path]::GetFullPath((Join-Path $projectRoot 'build'))
if([string]::IsNullOrWhiteSpace($OutputRoot)){$OutputRoot=Join-Path $allowedRoot ('HybridNative-'+[Guid]::NewGuid().ToString('N'))}
$buildRoot=[IO.Path]::GetFullPath($OutputRoot)
if(!$buildRoot.StartsWith($allowedRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)-or
    !([IO.Path]::GetFileName($buildRoot).StartsWith('HybridNative-',[StringComparison]::Ordinal))){throw 'Offline helper output must be a new direct project build/HybridNative-* directory.'}
if([IO.Path]::GetDirectoryName($buildRoot)-ne $allowedRoot){throw 'Offline helper output must be a direct child of project build.'}
if(Test-Path -LiteralPath $buildRoot){throw 'Refuse to overwrite an existing proof output directory.'}
if(!(Test-Path -LiteralPath $buildRoot)){[void](New-Item -ItemType Directory -Path $buildRoot)}
if((Get-Item -LiteralPath $buildRoot).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Refuse a reparse output directory.'}
$toolchain=& (Join-Path $projectRoot 'tools\Get-CppToolchain.ps1') -VisualStudioRoot $VisualStudioRoot
$savedInclude=$env:INCLUDE;$savedLib=$env:LIB;$savedPath=$env:PATH
try{
    # Invoke installed compiler binaries directly. Do not require a cmd.exe
    # elevation/compatibility setting merely to initialize environment variables.
    $env:INCLUDE=$toolchain.Include;$env:LIB=$toolchain.Lib;$env:PATH=$toolchain.SearchPath+';'+$savedPath
    Push-Location -LiteralPath $buildRoot
    try{
        $common=@('/nologo','/std:c++17','/EHsc','/O2','/MT','/W4','/WX','/fp:strict','/GS','/sdl','/guard:cf','/Brepro')
        $link=@('/DYNAMICBASE','/NXCOMPAT','/HIGHENTROPYVA','/guard:cf','/INCREMENTAL:NO','/Brepro')
        & (Join-Path $toolchain.Bin 'cl.exe') @common /LD /Fo:hvx_hybrid_core.obj (Join-Path $nativeRoot 'hvx_hybrid_core.cpp') /link /OUT:HvxHybridCore.dll /IMPLIB:HvxHybridCore.lib @link
        if($LASTEXITCODE -ne 0){throw 'Native library compilation failed'}
        & (Join-Path $toolchain.Bin 'cl.exe') @common /Fo:native_smoke.obj (Join-Path $nativeRoot 'native_smoke.cpp') HvxHybridCore.lib /link /OUT:native_smoke.exe @link
        if($LASTEXITCODE -ne 0){throw 'Native test compilation failed'}
        & (Join-Path $toolchain.Bin 'dumpbin.exe') /nologo /exports HvxHybridCore.dll
        if($LASTEXITCODE -ne 0){throw 'Native export inspection failed'}
        & (Join-Path $buildRoot 'native_smoke.exe')
        if($LASTEXITCODE -ne 0){throw 'Native offline test failed'}
    }finally{Pop-Location}
}finally{$env:INCLUDE=$savedInclude;$env:LIB=$savedLib;$env:PATH=$savedPath}
Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $buildRoot 'HvxHybridCore.dll'),(Join-Path $buildRoot 'native_smoke.exe')
