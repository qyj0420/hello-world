param([string]$OutputRoot='')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(!$OutputRoot){$OutputRoot=Join-Path $root 'build/pico-bridge-status-offline-20261012'}
$output=[IO.Path]::GetFullPath($OutputRoot)
if(!$output.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Offline output must be in this snapshot build directory.'}
New-Item -ItemType Directory -Path $output -Force|Out-Null
$helpers=@('modules-src/HardwareControl/src/PicoScope5242DBlockDevice.cs','modules-src/FactoryWorker/Imported/Task38/PicoScope5242DBlockDevice.cs','modules-src/Task39InputWorker/vendor/Task38/PicoScope5242DBlockDevice.cs','modules-src/CalibrationStandalone/PicoCore/Native.cs')
$inputs=$helpers+@('modules-src/FactoryWorker/Imported/PicoInputCapture.cs','modules-src/Task39InputWorker/src/PicoInputCapture.cs','tests/PicoBridgeStatusOfflineTests.cs','tools/Test-PicoBridgeStatusOffline.ps1')
$manifest=@($inputs|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath (Join-Path $root $_)).Hash}})
function Extract-Method([string]$text,[string]$name){
 $match=[regex]::Match($text,'(?m)^\s*(?:private|internal)\s+(?:static\s+)?void\s+'+$name+'\s*\(')
 if(!$match.Success){throw ('Method not found: '+$name)}
 $start=$text.IndexOf('{',$match.Index);$depth=0;$end=-1
 for($i=$start;$i -lt $text.Length;$i++){if($text[$i] -eq '{'){$depth++};if($text[$i] -eq '}'){$depth--;if($depth -eq 0){$end=$i;break}}}
 if($end -lt 0){throw 'Method extraction did not terminate.'}
 return ($text.Substring($match.Index,$end-$match.Index+1) -replace '\bprivate\s+(static\s+)?void\s+Check','internal $1void Check')
}
$fixture='using System;'+[Environment]::NewLine
$pureBodies=@()
for($i=0;$i -lt $helpers.Count;$i++){
 $text=Get-Content -LiteralPath (Join-Path $root $helpers[$i]) -Raw
 $begin=$text.IndexOf('// BEGIN PURE PICO STATUS CLASSIFICATION')
 $end=$text.IndexOf('// END PURE PICO STATUS CLASSIFICATION',$begin)
 if($begin -lt 0 -or $end -lt 0){throw 'Missing source extraction markers.'}
 $pure=$text.Substring($begin,$end-$begin);$pureBodies+=$pure
 if($pure -match 'DllImport|HvxGetLast|ps5000a[A-Z]|LoadLibrary|Task.Run'){throw 'Pure status classifier unexpectedly references native execution.'}
 $check=Extract-Method $text 'Check'
 if($i -lt 3){
  $check=$check -replace 'internal static void Check\(uint status, string operation\)','internal static void Check(uint status, string operation = "scope-operation")'
  $fixture+="namespace Scope$i { internal static class Pico5000aNative { internal const uint Ok=0; $pure internal static string ReadBridgeDiagnostic(){return TestState.Read();} } internal static class CheckHost { $check } }"+[Environment]::NewLine
 }else{
  $check=$check -replace 'internal static void Check\(uint status, string op\)','internal static void Check(uint status, string op = "calibration-operation")'
  $fixture+="namespace Calibration { internal static class Native { $pure internal static string ReadBridgeDiagnostic(){return TestState.Read();} $check } }"+[Environment]::NewLine
 }
}
if(@($pureBodies|Sort-Object -Unique).Count -ne 1){throw 'Production classifier copies differ.'}
for($i=0;$i -lt 2;$i++){
 $source=Join-Path $root $inputs[4+$i];$check=Extract-Method (Get-Content -LiteralPath $source -Raw) 'Check'
 $space=if($i -eq 0){'ImportedInput'}else{'StandaloneInput'}
 $fixture+="namespace $space { using Pico5000aNative=Scope1.Pico5000aNative; internal sealed class CheckHost { internal Action<string> CommunicationError; $check } }"+[Environment]::NewLine
}
$generated=Join-Path $output 'ActualProductionStatusBodies.cs'
[IO.File]::WriteAllText($generated,$fixture,[Text.UTF8Encoding]::new($false))
$exe=Join-Path $output 'PicoBridgeStatusOfflineTests.exe'
$compiler=Join-Path $root 'tools/compiler/csc.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /deterministic+ /r:System.dll /r:System.Core.dll ('/out:'+$exe) $generated (Join-Path $root 'tests/PicoBridgeStatusOfflineTests.cs')
if($LASTEXITCODE -ne 0){throw 'Offline status fixture compilation failed.'}
# Generated fixture has no P/Invoke declarations and contains only actual pure classifiers/Check bodies.
if((Get-Content -LiteralPath $generated -Raw) -match 'DllImport|LoadLibrary|HvxGetLastErrorInfo'){throw 'Native loader/import escaped into offline fixture.'}
$result=@(& $exe 2>&1);$result|Set-Content -LiteralPath (Join-Path $output 'results.txt') -Encoding UTF8
if($LASTEXITCODE -ne 0){throw ('Offline status fixture failed: '+($result -join [Environment]::NewLine))}
foreach($item in $manifest){if((Get-FileHash -LiteralPath (Join-Path $root $item.Path)).Hash -ne $item.SHA256){throw ('Source changed while testing: '+$item.Path)}}
[ordered]@{Schema='HVX.PicoBridgeStatus.Offline/1';HardwareAccessed=$false;ProductionDllLoaded=$false;NativeSdkLoaded=$false;Files=$manifest;FixtureSHA256=(Get-FileHash $generated).Hash;FixtureExeSHA256=(Get-FileHash $exe).Hash;Results=$result;NativeBridgeSHA256=(Get-FileHash (Join-Path $root 'native-hardware/out/HvxAcquisitionNative.dll')).Hash}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'evidence.json') -Encoding UTF8
$result
