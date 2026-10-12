param([Parameter(Mandatory=$true)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$qa=[IO.Path]::GetFullPath($EvidenceDirectory)
if(-not $qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $qa)){throw 'Use a new isolated evidence directory under build.'}
$publisher=Join-Path $PSScriptRoot 'Publish-R27Validation.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($publisher,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Publisher parse failure.'}
foreach($name in @('Assert-R27Unlinked','Get-R27File','Get-R27TreeFiles','Get-R27Inventory','Assert-R27Hash','Convert-R27Canonical','Assert-R27SameInventory','Assert-R27Boolean','Assert-R27TestResult','Assert-R27Proof')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw ('Missing unique publisher helper: '+$name)}
 . ([scriptblock]::Create($node[0].Extent.Text))
}
$null=Assert-R27Unlinked $qa $root
$null=New-Item -ItemType Directory -Path $qa
$checks=0
function Check([bool]$ok,[string]$message){$script:checks++;if(-not $ok){throw $message}}
function Reject([scriptblock]$action,[string]$message){$caught=$false;try{& $action|Out-Null}catch{$caught=$true};Check $caught $message}
$source=Join-Path $root 'tests/R27ValidationIsolationSmoke.cs'
$sourceHash=(Get-FileHash -LiteralPath $source).Hash
$fixture=Join-Path $qa 'sample.txt'
[IO.File]::WriteAllText($fixture,'offline publisher guard fixture')
$hash=(Get-FileHash -LiteralPath $fixture).Hash
Check ((Get-R27File $qa 'sample.txt') -ceq $fixture) 'Contained file accepted'
Check ((Assert-R27Hash $qa 'sample.txt' $hash) -ceq $fixture) 'Exact hash accepted'
Reject {Get-R27File $qa '../escape.txt'} 'Traversal rejected'
Reject {Get-R27File $qa 'C:\Windows\win.ini'} 'Absolute file rejected'
Reject {Get-R27File $qa 'sample.txt:alternate'} 'Alternate stream rejected'
Reject {Get-R27File $qa 'missing.txt'} 'Missing file rejected'
Reject {Assert-R27Hash $qa 'sample.txt' ('0'*64)} 'Tampered hash rejected'
Reject {Assert-R27Hash $qa 'sample.txt' 'INVALID'} 'Malformed hash rejected'
Assert-R27SameInventory @([pscustomobject]@{Path='sample.txt';SHA256=$hash}) @([pscustomobject]@{Path='sample.txt';Sha256=$hash}) 'Valid inventory';Check $true 'Canonical inventory accepted'
Reject {Assert-R27SameInventory @([pscustomobject]@{Path='sample.txt';SHA256=$hash}) @() 'Omitted file'} 'Missing inventory entry rejected'
Reject {Assert-R27SameInventory @([pscustomobject]@{Path='sample.txt';SHA256=$hash}) @([pscustomobject]@{Path='sample.txt';SHA256=('0'*64)}) 'Changed file'} 'Changed inventory hash rejected'
Reject {Assert-R27SameInventory @([pscustomobject]@{Path='sample.txt';SHA256=$hash}) @([pscustomobject]@{Path='sample.txt';SHA256=$hash},[pscustomobject]@{Path='sample.txt';SHA256=$hash}) 'Duplicate'} 'Duplicate inventory rejected'
Assert-R27Boolean $false $false 'typed boolean';Check $true 'Boolean accepted'
Reject {Assert-R27Boolean 'false' $false 'string'} 'String Boolean rejected'
$validTest=[pscustomobject]@{Name='R27ValidationIsolationSmoke';CompileExit=0;ExitCode=0;TimedOut=$false;ProcessExitConfirmed=$true;SourceSHA256=$sourceHash}
Assert-R27TestResult $validTest;Check $true 'Successful result accepted'
foreach($case in @(@('CompileExit',1),@('ExitCode',1),@('TimedOut',$true),@('ProcessExitConfirmed',$false),@('SourceSHA256',('0'*64)),@('Name','../Injected'))){
 $copy=$validTest|ConvertTo-Json|ConvertFrom-Json;$copy.($case[0])=$case[1]
 Reject {Assert-R27TestResult $copy} ('Bad test '+$case[0]+' rejected')
}
$build=$qa;$runtimeTestFiles=@([pscustomobject]@{Path='sample.txt';SHA256=$hash})
$frozen=[datetime]::Parse('2026-10-07T00:00:00Z').ToUniversalTime()
$validProof=[pscustomobject]@{Schema='HVX.UiRepair.Focused/1';BuildRoot=$qa;HardwareAccessed=$false;RuntimeUnchanged=$true;StartedUtc='2026-10-07T01:00:00Z';CompletedUtc='2026-10-07T01:01:00Z';Runtime=$runtimeTestFiles;Tests=@($validTest)}
Assert-R27Proof $validProof 'HVX.UiRepair.Focused/1' $frozen;Check $true 'Same frozen build proof accepted'
foreach($case in @(@('BuildRoot',(Join-Path $root 'build/other')), @('Schema','Wrong'),@('HardwareAccessed',$true),@('RuntimeUnchanged',$false),@('StartedUtc','2026-10-06T00:00:00Z'),@('CompletedUtc','2026-10-06T00:00:00Z'))){
 $copy=$validProof|ConvertTo-Json -Depth 6|ConvertFrom-Json;$copy.($case[0])=$case[1]
 Reject {Assert-R27Proof $copy 'HVX.UiRepair.Focused/1' $frozen} ('Bad proof '+$case[0]+' rejected')
}
$copy=$validProof|ConvertTo-Json -Depth 6|ConvertFrom-Json;$copy.Tests=@($validTest,$validTest)
Reject {Assert-R27Proof $copy 'HVX.UiRepair.Focused/1' $frozen} 'Duplicate tests rejected'
$installer=Get-Content -LiteralPath (Join-Path $root 'installer/XIONGXIN-R27-Validation.iss') -Raw
Check ($installer -match '(?m)^AppVersion=1\.1\.9\.8$') 'Installer exact version'
Check ($installer -match '(?m)^PrivilegesRequired=admin$') 'Installer UAC retained'
Check ($installer -notmatch '(?m)^\[Run\]') 'No product autostart'
Check ($installer -notmatch 'Source:.*\\\*"; DestDir: "\{app\}"') 'No broad package-root copy'
Check (([regex]::Matches($installer,'IconFilename: "\{app\}\\assets\\shortcuts\\256\.ico"')).Count -eq 2) 'Both shortcuts use requested icon'
Check ($installer.Contains('#include "EncryptedModels.iss"')) 'External models included'
Check ($installer.Contains('#include "InteractiveInstall.iss"')) 'Interactive installation included'
$text=Get-Content -LiteralPath $publisher -Raw
Check ($text.Contains("Assert-R27Proof `$proof 'HVX.PasswordFree.Source/2'")) 'Host proof provenance required'
Check ($text.Contains('Never overwrite an existing delivery.')) 'Existing delivery not overwritten'
Check ($text.Contains('Frozen inputs after copy')) 'Post-copy source freeze check retained'
Check ($text.Contains('FullRegression=$false')) 'Focused proof not represented as full regression'
[ordered]@{Schema='HVX.R27.PublisherContractTests/1';Status='PASS';Checks=$checks;HardwareAccessed=$false;ProductLaunched=$false;InstallerLaunched=$false;PublisherSHA256=(Get-FileHash -LiteralPath $publisher).Hash}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $qa 'publisher-contract-tests.json') -Encoding UTF8
Write-Output ('R27_PUBLISHER_GUARDS_PASS='+$checks+'; EVIDENCE='+$qa)
