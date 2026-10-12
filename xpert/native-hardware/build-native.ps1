param([switch]$RunOfflineTests)
$ErrorActionPreference='Stop'
$nativeRoot=$PSScriptRoot
$projectRoot=Split-Path -Parent $nativeRoot
$nativeOut=Join-Path $nativeRoot 'out'
$fakeOut=Join-Path $nativeOut 'fake-only'
New-Item -ItemType Directory -Path $nativeOut,$fakeOut -Force | Out-Null
$toolchain=& (Join-Path $projectRoot 'tools\Get-CppToolchain.ps1')
$oldPath=$env:PATH; $oldInclude=$env:INCLUDE; $oldLib=$env:LIB
try {
 $env:PATH=$toolchain.SearchPath+';'+$oldPath
 $env:INCLUDE=$toolchain.Include; $env:LIB=$toolchain.Lib
 $compiler=Join-Path $toolchain.Bin 'cl.exe'
 $common=@('/nologo','/std:c++17','/EHsc','/W4','/WX','/O2','/MT','/DUNICODE','/D_UNICODE','/guard:cf')
 Push-Location $nativeOut
 try {
  & $compiler @common '/LD' (Join-Path $nativeRoot 'HvxAcquisitionNative.cpp') '/Fe:HvxAcquisitionNative.dll' '/link' '/DYNAMICBASE' '/NXCOMPAT' '/HIGHENTROPYVA' 'shell32.lib' 'ole32.lib'
  if($LASTEXITCODE -ne 0){throw 'Production native boundary compile failed'}
  & (Join-Path $toolchain.Bin 'dumpbin.exe') '/exports' 'HvxAcquisitionNative.dll' | Out-File -LiteralPath (Join-Path $nativeOut 'production-exports.txt') -Encoding utf8
  if($LASTEXITCODE -ne 0){throw 'Export audit failed'}
  & (Join-Path $toolchain.Bin 'dumpbin.exe') '/headers' '/loadconfig' '/dependents' 'HvxAcquisitionNative.dll' | Out-File -LiteralPath (Join-Path $nativeOut 'production-pe-audit.txt') -Encoding utf8
  if($LASTEXITCODE -ne 0){throw 'PE mitigation/dependency audit failed'}
 } finally {Pop-Location}
 if($RunOfflineTests) {
  Push-Location $fakeOut
  try {
   # This DLL is a compile-time test variant. Its SDK loader has no real LoadLibrary branch.
   & $compiler @common '/DHVX_FAKE_TEST' '/LD' (Join-Path $nativeRoot 'HvxAcquisitionNative.cpp') '/Fe:HvxAcquisitionNativeFake.dll' '/link' 'shell32.lib' 'ole32.lib'
   if($LASTEXITCODE -ne 0){throw 'Fake-only boundary compile failed'}
   & $compiler @common '/LD' (Join-Path $nativeRoot 'tests\FakeSdk.cpp') '/Fe:HvxFakeSdk.dll'
   if($LASTEXITCODE -ne 0){throw 'Fake SDK compile failed'}
   # PE-only path fixtures contain fake bytes; no test ever loads these SDK-named copies.
   foreach($fixture in @('pico-a','pico-b','pico-missing-dependency','art-a','art-b')) {
    $fixturePath=Join-Path $fakeOut $fixture
    New-Item -ItemType Directory -Path $fixturePath -Force | Out-Null
    if($fixture.StartsWith('pico')) {
     Copy-Item -LiteralPath (Join-Path $fakeOut 'HvxFakeSdk.dll') -Destination (Join-Path $fixturePath 'ps5000a.dll') -Force
     if($fixture -ne 'pico-missing-dependency'){Copy-Item -LiteralPath (Join-Path $fakeOut 'HvxFakeSdk.dll') -Destination (Join-Path $fixturePath 'picoipp.dll') -Force}
    } else {Copy-Item -LiteralPath (Join-Path $fakeOut 'HvxFakeSdk.dll') -Destination (Join-Path $fixturePath 'Art_DAQ.dll') -Force}
   }
   & $compiler @common '/DHVX_FAKE_TEST' (Join-Path $nativeRoot 'tests\NativeBoundaryTests.cpp') 'HvxAcquisitionNativeFake.lib' '/Fe:NativeBoundaryTests.exe'
   if($LASTEXITCODE -ne 0){throw 'Offline test compile failed'}
   & (Join-Path $fakeOut 'NativeBoundaryTests.exe') | Tee-Object -FilePath (Join-Path $fakeOut 'offline-results.txt')
   if($LASTEXITCODE -ne 0){throw 'Offline native boundary tests failed'}
  } finally {Pop-Location}
 }
 $manifest=@{
  scope='Compile-only production DLL; runtime tests exclusively use fake-only variant and HvxFakeSdk.dll'
  utc=[DateTime]::UtcNow.ToString('o'); compiler=$toolchain.Version; sdk=$toolchain.SDKVersion
  files=@(Get-ChildItem -LiteralPath $nativeRoot -Recurse -File | Where-Object {$_.FullName -notlike '*\out\*'} | ForEach-Object {
   @{path=$_.FullName.Substring($nativeRoot.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
  })
  productionDll=@{path=(Join-Path $nativeOut 'HvxAcquisitionNative.dll');sha256=(Get-FileHash -LiteralPath (Join-Path $nativeOut 'HvxAcquisitionNative.dll') -Algorithm SHA256).Hash}
 }
 $manifest | ConvertTo-Json -Depth 5 | Out-File -LiteralPath (Join-Path $nativeOut 'build-evidence.json') -Encoding utf8
} finally {$env:PATH=$oldPath;$env:INCLUDE=$oldInclude;$env:LIB=$oldLib}
