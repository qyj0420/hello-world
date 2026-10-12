param([Parameter(Mandatory=$true)][string]$PayloadDirectory,
      [ValidateSet('Internal07','Preview20261003','Frozen20261006')][string]$Edition='Internal07')
$ErrorActionPreference='Stop'
throw 'Legacy installer compilation is retired; historical payloads remain unchanged. Use the guarded V30 batch installer lane.'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$payload=[IO.Path]::GetFullPath($PayloadDirectory)
if(-not $payload.StartsWith(($root+'\deliverables\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Select a verified release directory.'}
$name=[IO.Path]::GetFileName($payload)+'.Setup'
$output=Join-Path $root ('deliverables\'+$name+'.exe')
if(Test-Path -LiteralPath $output){throw 'Existing installers must not be overwritten.'}
$checksums=Join-Path $payload 'SHA256SUMS.txt'
foreach($line in Get-Content -LiteralPath $checksums){
    if($line -notmatch '^([a-fA-F0-9]{64})  (.+)$'){throw 'Invalid payload manifest.'}
    $expected=$Matches[1];$file=[IO.Path]::GetFullPath((Join-Path $payload $Matches[2]))
    if(-not $file.StartsWith(($payload+'\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Payload traversal rejected.'}
    if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $expected){throw 'Payload manifest mismatch.'}
}
$runtime=Join-Path $payload 'prerequisites\NDP48-x86-x64-AllOS-ENU.exe'
$sig=Get-AuthenticodeSignature -LiteralPath $runtime
if($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Microsoft Corporation'){throw '.NET runtime signature invalid.'}
$compiler=Join-Path $PSScriptRoot 'installer-toolchain\InnoSetup\ISCC.exe'
$sig=Get-AuthenticodeSignature -LiteralPath $compiler
if($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Pyrsys B.V.'){throw 'Inno compiler signature invalid.'}
$scriptName=if($Edition -eq 'Frozen20261006'){'XIONGXIN-Frozen-20261006.iss'}elseif($Edition -eq 'Preview20261003'){'XIONGXIN-Preview-20261003.iss'}else{'XIONGXIN-Standard.iss'}
& $compiler ('/DPayloadDir='+$payload) ('/DOutputDirPath='+(Join-Path $root 'deliverables')) ('/DSetupFileName='+$name) (Join-Path $root ('installer\'+$scriptName))
if($LASTEXITCODE -ne 0){throw 'Standard installer compilation failed.'}
Get-FileHash -LiteralPath $output -Algorithm SHA256
