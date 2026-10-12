param([Parameter(Mandatory=$true)][string]$InputDll,[Parameter(Mandatory=$true)][string]$OutputDirectory,[switch]$MethodEncryption)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$analysisDll=[IO.Path]::GetFullPath($InputDll)
if(-not $output.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Use a fresh isolated build output.'}
if(-not $analysisDll.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($analysisDll) -cne 'HVX.PdReview.V1.0.dll'){throw 'Only a project-local QA copy of the owned review assembly is allowed.'}
foreach($path in @($output,$analysisDll)){$walk=$path;while($walk.Length -gt $root.Length){if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked path is prohibited.'};$walk=[IO.Path]::GetDirectoryName($walk)}}
$identity=[Reflection.AssemblyName]::GetAssemblyName($analysisDll)
if($identity.Name -cne 'HVX.PdReview.V1.0' -or $identity.GetPublicKeyToken().Length -ne 0){throw 'Unexpected or strong-named input; this experiment never reads signing keys.'}
$tool='D:\PD_AI_LabVIEW2026_Project\Pulse400Hz_Extraction_DLL\tools\ConfuserEx-CLI-1.6.0\Confuser.CLI.exe'
if((Get-FileHash -LiteralPath $tool).Hash -cne '0790671F930BD1375FA06ECE29A776404CEE44695E083CACE46C905433644130'){throw 'Existing tool differs from audit.'}
$inputHash=(Get-FileHash -LiteralPath $analysisDll).Hash
$null=New-Item -ItemType Directory -Path (Join-Path $output 'input')
Copy-Item -LiteralPath $analysisDll -Destination (Join-Path $output 'input\HVX.PdReview.V1.0.dll')
$base=[Security.SecurityElement]::Escape((Join-Path $output 'input'))
$dest=[Security.SecurityElement]::Escape((Join-Path $output 'protected'))
# No rename, packing, signing, anti-debug or anti-dump. Only the two reproduced
# owned calculation types receive body-level control-flow/constant protection.
# Optional second experiment uses the installed AntiTamperProtection's verified
# ID and default Normal mode, restricted to those same method targets.
$methodEncryptionXml=if($MethodEncryption){'<protection id="anti tamper" />'}else{''}
$config=@"
<project outputDir="$dest" baseDir="$base" seed="HVX-offline-feasibility-only" debug="false" xmlns="http://confuser.codeplex.com">
 <rule pattern="decl-type('HVX.PD.LocationFiltering.LocationClusterFilterEngine') or decl-type('HVX.PdReview.ReviewCapacitorAnalysis')" preset="none" inherit="false">
  <protection id="ctrl flow" />
  <protection id="constants" />
  $methodEncryptionXml
 </rule>
 <module path="HVX.PdReview.V1.0.dll" />
</project>
"@
$config|Set-Content -LiteralPath (Join-Path $output 'trial.crproj') -Encoding UTF8
$log=@(& $tool -n (Join-Path $output 'trial.crproj') 2>&1|ForEach-Object {$_.ToString()});$exit=$LASTEXITCODE
$log|Set-Content -LiteralPath (Join-Path $output 'protection.log') -Encoding UTF8
if($exit -ne 0){throw ('Protection tool failed; evidence retained: '+$output)}
$probe=Join-Path $output 'PdReviewProtectionProbe.exe'
& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /target:exe /platform:x64 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:'+$probe) (Join-Path $root 'tests\PdReviewProtectionProbe.cs')
if($LASTEXITCODE -ne 0){throw 'Offline probe compilation failed.'}
$results=@()
foreach($label in @('input','protected')){
 $dll=Join-Path $output ($label+'\HVX.PdReview.V1.0.dll')
 $stdout=Join-Path $output ($label+'.stdout.txt');$stderr=Join-Path $output ($label+'.stderr.txt')
 $arguments='"'+$dll+'" "'+(Join-Path $output 'fixture')+'"'
 $process=Start-Process -FilePath $probe -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
 $done=$process.WaitForExit(45000)
 if(-not $done){$process.Kill();$process.WaitForExit();throw ('Offline probe timed out: '+$label)}
 $process.Refresh();$code=$process.ExitCode
 $results+=[ordered]@{Label=$label;ExitCode=$code;DLLSHA256=(Get-FileHash -LiteralPath $dll).Hash;OutputSHA256=(Get-FileHash -LiteralPath $stdout).Hash}
 if($code -ne 0){throw ('Offline probe failed: '+$label+'; '+(Get-Content -LiteralPath $stderr -Raw))}
}
$same=(Get-Content -LiteralPath (Join-Path $output 'input.stdout.txt') -Raw) -ceq (Get-Content -LiteralPath (Join-Path $output 'protected.stdout.txt') -Raw)
$unchanged=(Get-FileHash -LiteralPath $analysisDll).Hash -ceq $inputHash
[ordered]@{Schema='HVX.PdReviewProtectionFeasibility/1';Input=$analysisDll;InputSHA256=$inputHash;SourceUnchanged=$unchanged;Results=$results;ExactNumericAndApiMatch=$same;MethodEncryption=[bool]$MethodEncryption;HardwareAccessed=$false;UserDataWritten=$false;ModelInference=$false;ProductionIntegrated=$false;FullConfidentialityAccepted=$false;Scope=@('LocationClusterFilterEngine','ReviewCapacitorAnalysis')}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
if(-not $same -or -not $unchanged){throw 'Calculation/API parity or source preservation failed.'}
Write-Output ('PROTECTION_FEASIBILITY_PROOF='+$output)
