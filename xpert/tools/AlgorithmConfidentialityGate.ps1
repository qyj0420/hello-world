# Evidence validation only. This does not perform decompilation or certify that
# a local program cannot be reverse engineered. Never manufacture a PASS here.
function Assert-AlgorithmConfidentialityReview {
 param([Parameter(Mandatory=$true)][string]$BuildRoot,
       [Parameter(Mandatory=$true)][AllowEmptyString()][string]$ReviewPath,
       [Parameter(Mandatory=$true)][object[]]$RuntimeInventory)
 $ErrorActionPreference='Stop'
 Set-StrictMode -Version Latest
 $build=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
 if([String]::IsNullOrWhiteSpace($ReviewPath)){throw 'Algorithm confidentiality review is required before publishing. No installer or delivery was created.'}
 function Get-ConfidentialityFile([string]$Path) {
  $full=[IO.Path]::GetFullPath($Path)
  if(-not $full.StartsWith($build+'\qa\',[StringComparison]::OrdinalIgnoreCase)){throw 'Confidentiality evidence must belong to this build/qa directory.'}
  for($walk=$full;$walk;$walk=[IO.Path]::GetDirectoryName($walk)){
   if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked confidentiality evidence is prohibited.'}
  }
  if(-not(Test-Path -LiteralPath $full -PathType Leaf)){throw 'Confidentiality evidence is missing.'}
  return $full
 }
 function Get-ConfidentialityInventory($Entries) {
  $rows=@();$names=@{}
  foreach($entry in $Entries){
   $name=([string]$entry.Path).Replace('\','/')
   if($name -notmatch '^[^/:]+(/[^/:]+)*$' -or @($name.Split('/')|Where-Object {$_ -in @('.','..')}).Count -or
      $names.ContainsKey($name) -or [string]$entry.SHA256 -notmatch '^[A-Fa-f0-9]{64}$'){throw 'Invalid or duplicate confidentiality scope entry.'}
   $names[$name]=$true;$rows+=([ordered]@{Path=$name;SHA256=([string]$entry.SHA256).ToUpperInvariant()})
  }
  if(-not $rows.Count){throw 'Confidentiality scope is empty.'}
  return ConvertTo-Json -InputObject @($rows|Sort-Object { $_.Path }) -Compress
 }
 $file=Get-ConfidentialityFile $ReviewPath
 if((Get-Item -LiteralPath $file).Length -gt 16MB){throw 'Confidentiality review exceeds size limit.'}
 $sha=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
 $review=Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json
 if($review.Schema -cne 'HVX.AlgorithmConfidentialityReview/1' -or $review.Status -cne 'PASS' -or
    [IO.Path]::GetFullPath([string]$review.BuildRoot).TrimEnd('\') -ine $build){throw 'Confidentiality review did not pass for this exact build.'}
 foreach($field in @('HardwareAccessed','DirectReadableProprietaryImplementationFound','UnresolvedBlockingFindings','AbsoluteProtectionClaimed')){
  if($review.$field -isnot [bool] -or $review.$field){throw ('Confidentiality review has an invalid or blocking condition: '+$field)}
 }
 foreach($field in @('FullProprietaryScopeReviewed','RuntimeCacheCovered','ModelLoadingCovered','CompatibilityPassed')){
  if($review.$field -isnot [bool] -or -not $review.$field){throw ('Confidentiality review is incomplete: '+$field)}
 }
 foreach($field in @('Reviewer','Limitations')){
  if($review.$field -isnot [string] -or [String]::IsNullOrWhiteSpace($review.$field)){throw ('Confidentiality review lacks '+$field)}
 }
 if((Get-ConfidentialityInventory $RuntimeInventory) -cne (Get-ConfidentialityInventory $review.Runtime)){
  throw 'Confidentiality review does not cover the complete current runtime and model bytes.'
 }
 $proofs=@($review.Evidence);$seen=@{};$kinds=@{}
 if(-not $proofs.Count){throw 'Confidentiality review has no inspection or regression evidence.'}
 foreach($proof in $proofs){
  if($proof.Kind -cnotin @('ScopeReport','StaticInspection','Compatibility') -or $proof.SHA256 -notmatch '^[A-Fa-f0-9]{64}$'){
   throw 'Invalid confidentiality evidence identity.'
  }
  $relative=[string]$proof.Path
  if([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':')){throw 'Evidence references must be relative to the review file.'}
  $evidence=Get-ConfidentialityFile (Join-Path ([IO.Path]::GetDirectoryName($file)) $relative)
  if($seen.ContainsKey($evidence)){throw 'Duplicate confidentiality evidence cannot fill another required category.'}
  $seen[$evidence]=[string]$proof.SHA256;$kinds[[string]$proof.Kind]=$true
  if((Get-FileHash -LiteralPath $evidence -Algorithm SHA256).Hash -ine $proof.SHA256){throw 'Confidentiality evidence bytes changed.'}
 }
 foreach($kind in @('ScopeReport','StaticInspection','Compatibility')){if(-not $kinds.ContainsKey($kind)){throw ('Confidentiality evidence is missing: '+$kind)}}
 if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne $sha){throw 'Confidentiality review changed during verification.'}
 # The caller must re-run before final publication, after payload copying.
 [pscustomobject]@{Path=$file;SHA256=$sha;Evidence=$seen;Scope='Evidence contract only; not an independent secrecy or signature guarantee'}
}
