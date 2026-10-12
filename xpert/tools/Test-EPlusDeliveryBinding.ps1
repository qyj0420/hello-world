[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ReplayDirectory)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\');$qa=[IO.Path]::GetFullPath($ReplayDirectory).TrimEnd('\')
if(-not $qa.StartsWith($root+'\qa\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an existing owned replay QA directory.'}
$original=Join-Path $qa 'verification-final-binding.json';$output=Join-Path $qa 'verification-delivery-binding.json'
if(Test-Path -LiteralPath $output){throw 'Do not overwrite prior proof.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded path helper.'}
$node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq 'Assert-RegressionUnlinkedPath')
if($node.Count-ne 1){throw 'Missing path helper.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
function Hash([string]$Path){Assert-RegressionUnlinkedPath $Path;(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
$oldHash=Hash $original;$old=Get-Content -Raw -LiteralPath $original -Encoding UTF8|ConvertFrom-Json
if($old.Status-cne'PASS'-or-not$old.SourceUnchanged){throw 'Original exact-final replay binding must pass.'}
$inputs=New-Object 'System.Collections.Generic.List[object]';$references=New-Object 'System.Collections.Generic.List[object]'
foreach($entry in $old.Inputs){
 $path=[IO.Path]::GetFullPath($entry.Path);if((Hash $path)-cne$entry.SHA256){throw ('Original replay binding changed: '+$path)}
 $bound=[ordered]@{Path=$path;SHA256=$entry.SHA256}
 if($path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){$inputs.Add($bound)}else{
  if(-not$path.StartsWith('D:\OWTSData\',[StringComparison]::OrdinalIgnoreCase)-or([IO.Path]::GetExtension($path)-ine'.loc'-and[IO.Path]::GetFileName($path)-ine'CABLE.in')){throw 'Unexpected external reference kind.'}
  $references.Add($bound)
 }
}
if($references.Count-ne5-or@($references|Where-Object{[IO.Path]::GetExtension($_.Path)-ieq'.loc'}).Count-ne3){throw 'Expected exactly three original LOC and two geometry references.'}
$jsons=@(Get-ChildItem -LiteralPath (Join-Path $qa 'same-inputs') -File -Filter '*.reference-*.json'|Sort-Object Name)
if($jsons.Count-ne6){throw 'Expected six named-reference result JSON files.'}
foreach($file in $jsons){$inputs.Add([ordered]@{Path=$file.FullName;SHA256=(Hash $file.FullName)})}
foreach($path in @($original,$PSCommandPath,$helper)){$inputs.Add([ordered]@{Path=$path;SHA256=(Hash $path)})}
$drift=@(@($inputs.ToArray())+@($references.ToArray())|Where-Object{(Hash $_.Path)-cne$_.SHA256})
if($drift.Count-or(Hash $original)-cne$oldHash){throw 'Delivery binding changed during read-only verification.'}
[ordered]@{Schema='HVX.EPlusPacket.DeliveryRuntimeBinding/1';UTC=[DateTime]::UtcNow.ToString('o');Status='PASS';SourceUnchanged=$true;Inputs=$inputs.ToArray();ReferenceInputs=$references.ToArray();
 ExternalReferencesCopied=$false;ReferenceInputsVerified=$true;ReferenceMismatchCount=0;NamedReferenceJSONCount=$jsons.Count;OriginalProofSHA256=$oldHash;
 Children=@([ordered]@{Path=$original;SHA256=$oldHash});FinalBuild=$old.FinalBuild;NativePinnedFiles=$old.NativePinnedFiles;NativeReplayChecks=$old.NativeReplayChecks;ManagedAdaptChecks=$old.ManagedAdaptChecks;OriginalReaderChecks=$old.OriginalReaderChecks;
 Scope='Exact final r52 native E+ and protected managed adaptation; project-local delivery Inputs are separate from read-only original user LOC/CABLE reference inputs. No user reference files are copied by this wrapper.';
 HardwareAccessed=$false;SDKCalled=$false;ProductEntryExecuted=$false;ActualWorkerStarted=$false;UserProjectWritten=$false;PhysicalAccuracyVerified=$false;FullRegression=$false;Limitations=$old.Limitations
}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $output -Encoding UTF8
Write-Output ('EPLUS_DELIVERY_BINDING_PASS;INPUTS='+$inputs.Count+';REFERENCES='+$references.Count+';PROOF='+$output)
