[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Hybrid output must remain below project build.'}
$walk=$output
while($walk.Length -ge $project.Length){
 if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked hybrid build output is prohibited.'}
 $walk=[IO.Path]::GetDirectoryName($walk)
}
$nativeProof=Join-Path $project ('build\HybridNative-'+[Guid]::NewGuid().ToString('N'))
$lines=@(& (Join-Path $project 'hybrid-src\native\build-native.ps1') -OutputRoot $nativeProof 6>&1 | ForEach-Object {$_.ToString()})
if(-not (Test-Path -LiteralPath (Join-Path $nativeProof 'HvxHybridCore.dll') -PathType Leaf)){throw 'Native compiler did not produce the hybrid core.'}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$lines | Set-Content -LiteralPath (Join-Path $output 'native-build-result.txt') -Encoding UTF8
$library=Join-Path $output 'HvxHybridCore.dll'
Copy-Item -LiteralPath (Join-Path $nativeProof 'HvxHybridCore.dll') -Destination $library -Force
$hash=(Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash
$pin=Join-Path $output 'native-pin.txt'
[IO.File]::WriteAllText($pin,$hash,[Text.Encoding]::ASCII)
Write-Host ('Native core built and offline-tested: '+$hash)
[pscustomobject]@{Directory=$output;PinFile=$pin;Library=$library;Sha256=$hash;NativeProof=$nativeProof}
