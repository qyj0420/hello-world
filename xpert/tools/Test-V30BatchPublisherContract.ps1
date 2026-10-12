param([Parameter(Mandatory=$true)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$qa=[IO.Path]::GetFullPath($EvidenceDirectory)
if(-not $qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $qa)){throw 'Use a new isolated evidence directory under build.'}
$publisher=Join-Path $PSScriptRoot 'Publish-V30BatchValidation.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($publisher,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Publisher parse failure.'}
foreach($name in @('Assert-V30BatchUnlinked','Get-V30BatchFile','Get-V30BatchTreeFiles','Get-V30BatchInventory','Assert-V30BatchHash','Convert-V30BatchCanonical','Assert-V30BatchSameInventory','Assert-V30BatchBoolean','Assert-V30BatchTestResult','Assert-V30BatchProof')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw ('Missing unique publisher helper: '+$name)}
 . ([scriptblock]::Create($node[0].Extent.Text))
}
$null=Assert-V30BatchUnlinked $qa $root
$null=New-Item -ItemType Directory -Path $qa
$checks=0
function Check([bool]$ok,[string]$message){$script:checks++;if(-not $ok){throw $message}}
function Reject([scriptblock]$action,[string]$message){$caught=$false;try{& $action|Out-Null}catch{$caught=$true};Check $caught $message}
$source=Join-Path $root 'tests/V30BatchValidationIsolationSmoke.cs'
$sourceHash=(Get-FileHash -LiteralPath $source).Hash
$fixture=Join-Path $qa 'sample.txt'
[IO.File]::WriteAllText($fixture,'offline publisher guard fixture')
$hash=(Get-FileHash -LiteralPath $fixture).Hash
Check ((Get-V30BatchFile $qa 'sample.txt') -ceq $fixture) 'Contained file accepted'
Check ((Assert-V30BatchHash $qa 'sample.txt' $hash) -ceq $fixture) 'Exact hash accepted'
Reject {Get-V30BatchFile $qa '../escape.txt'} 'Traversal rejected'
Reject {Get-V30BatchFile $qa 'C:\Windows\win.ini'} 'Absolute file rejected'
Reject {Get-V30BatchFile $qa 'sample.txt:alternate'} 'Alternate stream rejected'
Reject {Get-V30BatchFile $qa 'missing.txt'} 'Missing file rejected'
Reject {Assert-V30BatchHash $qa 'sample.txt' ('0'*64)} 'Tampered hash rejected'
Reject {Assert-V30BatchHash $qa 'sample.txt' 'INVALID'} 'Malformed hash rejected'
Assert-V30BatchSameInventory @([pscustomobject]@{Path='sample.txt';SHA256=$hash}) @([pscustomobject]@{Path='sample.txt';Sha256=$hash}) 'Valid inventory';Check $true 'Canonical inventory accepted'
Reject {Assert-V30BatchSameInventory @([pscustomobject]@{Path='sample.txt';SHA256=$hash}) @() 'Omitted file'} 'Missing inventory entry rejected'
Reject {Assert-V30BatchSameInventory @([pscustomobject]@{Path='sample.txt';SHA256=$hash}) @([pscustomobject]@{Path='sample.txt';SHA256=('0'*64)}) 'Changed file'} 'Changed inventory hash rejected'
Reject {Assert-V30BatchSameInventory @([pscustomobject]@{Path='sample.txt';SHA256=$hash}) @([pscustomobject]@{Path='sample.txt';SHA256=$hash},[pscustomobject]@{Path='sample.txt';SHA256=$hash}) 'Duplicate'} 'Duplicate inventory rejected'
$unordered=@([ordered]@{Path='LICENSE';SHA256=$hash},[ordered]@{Path='DLLs/_asyncio.pyd';SHA256=('A'*64)},[ordered]@{Path='modules/z.dll';SHA256=('B'*64)})
$reordered=@([pscustomobject]@{Path='modules/z.dll';SHA256=('B'*64)},[pscustomobject]@{Path='DLLs/_asyncio.pyd';SHA256=('A'*64)},[pscustomobject]@{Path='LICENSE';SHA256=$hash})
Assert-V30BatchSameInventory $unordered $reordered 'Unordered dictionary inventory';Check $true 'OrderedDictionary and object rows compare independently of enumeration order'
Reject {Assert-V30BatchSameInventory $unordered @($reordered[0],$reordered[2]) 'Missing shuffled row'} 'Missing entry in shuffled inventory rejected'
$changed=@([pscustomobject]@{Path='modules/z.dll';SHA256=('C'*64)},$reordered[1],$reordered[2])
Reject {Assert-V30BatchSameInventory $unordered $changed 'Tampered shuffled row'} 'Tampered entry in shuffled inventory rejected'
$gate=Join-Path $PSScriptRoot 'AlgorithmConfidentialityGate.ps1'
$gateAst=[Management.Automation.Language.Parser]::ParseFile($gate,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Confidentiality gate parse failure.'}
$gateHelper=@($gateAst.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq 'Get-ConfidentialityInventory')
if($gateHelper.Count -ne 1){throw 'Missing unique confidentiality inventory helper.'}
. ([scriptblock]::Create($gateHelper[0].Extent.Text))
Check ((Get-ConfidentialityInventory $unordered) -ceq (Get-ConfidentialityInventory $reordered)) 'Confidentiality scope compares independently of enumeration order'
Check ((Get-ConfidentialityInventory $unordered) -cne (Get-ConfidentialityInventory @($reordered[0],$reordered[2]))) 'Confidentiality scope detects missing shuffled entry'
Check ((Get-ConfidentialityInventory $unordered) -cne (Get-ConfidentialityInventory $changed)) 'Confidentiality scope detects tampered shuffled entry'
Reject {Get-ConfidentialityInventory @($unordered[0],$unordered[0])} 'Confidentiality duplicate scope rejected'
Reject {Get-ConfidentialityInventory @([ordered]@{Path='modules/z.dll';SHA256='bad'})} 'Confidentiality malformed hash rejected'
Assert-V30BatchBoolean $false $false 'typed boolean';Check $true 'Boolean accepted'
Reject {Assert-V30BatchBoolean 'false' $false 'string'} 'String Boolean rejected'
$validTest=[pscustomobject]@{Name='V30BatchValidationIsolationSmoke';CompileExit=0;ExitCode=0;TimedOut=$false;ProcessExitConfirmed=$true;SourceSHA256=$sourceHash}
Assert-V30BatchTestResult $validTest;Check $true 'Successful result accepted'
foreach($case in @(@('CompileExit',1),@('ExitCode',1),@('TimedOut',$true),@('ProcessExitConfirmed',$false),@('SourceSHA256',('0'*64)),@('Name','../Injected'))){
 $copy=$validTest|ConvertTo-Json|ConvertFrom-Json;$copy.($case[0])=$case[1]
 Reject {Assert-V30BatchTestResult $copy} ('Bad test '+$case[0]+' rejected')
}
$build=$qa;$runtimeTestFiles=@([pscustomobject]@{Path='sample.txt';SHA256=$hash})
$frozen=[datetime]::Parse('2026-10-07T00:00:00Z').ToUniversalTime()
$validProof=[pscustomobject]@{Schema='HVX.UiRepair.Focused/1';BuildRoot=$qa;HardwareAccessed=$false;RuntimeUnchanged=$true;StartedUtc='2026-10-07T01:00:00Z';CompletedUtc='2026-10-07T01:01:00Z';Runtime=$runtimeTestFiles;Tests=@($validTest)}
Assert-V30BatchProof $validProof 'HVX.UiRepair.Focused/1' $frozen;Check $true 'Same frozen build proof accepted'
foreach($case in @(@('BuildRoot',(Join-Path $root 'build/other')), @('Schema','Wrong'),@('HardwareAccessed',$true),@('RuntimeUnchanged',$false),@('StartedUtc','2026-10-06T00:00:00Z'),@('CompletedUtc','2026-10-06T00:00:00Z'))){
 $copy=$validProof|ConvertTo-Json -Depth 6|ConvertFrom-Json;$copy.($case[0])=$case[1]
 Reject {Assert-V30BatchProof $copy 'HVX.UiRepair.Focused/1' $frozen} ('Bad proof '+$case[0]+' rejected')
}
$copy=$validProof|ConvertTo-Json -Depth 6|ConvertFrom-Json;$copy.Tests=@($validTest,$validTest)
Reject {Assert-V30BatchProof $copy 'HVX.UiRepair.Focused/1' $frozen} 'Duplicate tests rejected'
$installer=Get-Content -LiteralPath (Join-Path $root 'installer/HVX-V30Batch-Validation.iss') -Raw -Encoding UTF8
Check ($installer -match '(?m)^AppVersion=1\.0\.30\.9$') 'Installer exact version'
Check ($installer -match '(?m)^VersionInfoVersion=1\.0\.30\.9$') 'Installer fixed file version matches the validation version'
Check ($installer -match '(?m)^VersionInfoProductVersion=1\.0\.30\.9$') 'Installer fixed product version matches the validation version'
foreach($previous in @('XIONGXIN-Preview-20261003.iss','XIONGXIN-Frozen-20261006.iss','XIONGXIN-R27-Validation.iss','XIONGXIN-V30-Validation.iss')){
 $older=Get-Content -LiteralPath (Join-Path $root ('installer/'+$previous)) -Raw -Encoding UTF8
 Check ([regex]::Match($installer,'(?m)^AppId=(.+)$').Groups[1].Value -cne [regex]::Match($older,'(?m)^AppId=(.+)$').Groups[1].Value) ('Independent registration from '+$previous)
 Check ([regex]::Match($installer,'(?m)^DefaultDirName=(.+)$').Groups[1].Value -cne [regex]::Match($older,'(?m)^DefaultDirName=(.+)$').Groups[1].Value) ('Independent default directory from '+$previous)
}
Check ($installer -match '(?m)^Source: "\{#PayloadDir\}\\Xiongxin\.OWTS\.Desktop\.exe"; DestDir: "\{app\}"; Flags: ignoreversion$') 'Explicit package file is not skipped for an older numeric file version'
Check ($installer -match '(?m)^PrivilegesRequired=admin$') 'Installer UAC retained'
Check ($installer -notmatch '(?m)^\[Run\]') 'No product autostart'
Check ($installer -notmatch 'Source:.*\\\*"; DestDir: "\{app\}"') 'No broad package-root copy'
Check ($installer -notmatch 'IconFilename:|SetupIconFile=|AppPublisher=') 'Neutral installer and shortcut defaults'
Check ($installer.Contains('#include "EncryptedModels.iss"')) 'External models included'
Check ($installer.Contains('#include "InteractiveInstall.iss"')) 'Interactive installation included'
$text=Get-Content -LiteralPath $publisher -Raw -Encoding UTF8
Check ($text.Contains("Assert-V30BatchProof `$proof 'HVX.PasswordFree.Source/2'")) 'Host proof provenance required'
Check ($text.Contains('Never overwrite an existing delivery.')) 'Existing delivery not overwritten'
Check ($text.Contains('Frozen inputs after copy')) 'Post-copy source freeze check retained'
Check ($text.Contains('FullRegression=$false')) 'Focused proof not represented as full regression'
[ordered]@{Schema='HVX.V30Batch.PublisherContractTests/1';Status='PASS';Checks=$checks;HardwareAccessed=$false;ProductLaunched=$false;InstallerLaunched=$false;PublisherSHA256=(Get-FileHash -LiteralPath $publisher).Hash;ConfidentialityGateSHA256=(Get-FileHash -LiteralPath $gate).Hash;PowerShellVersion=$PSVersionTable.PSVersion.ToString()}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $qa 'publisher-contract-tests.json') -Encoding UTF8
Write-Output ('V30Batch_PUBLISHER_GUARDS_PASS='+$checks+'; EVIDENCE='+$qa)
