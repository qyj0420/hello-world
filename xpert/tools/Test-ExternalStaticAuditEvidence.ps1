param([Parameter(Mandatory=$true)][string[]]$ProofFiles)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$qa=Join-Path $root ('qa\external-static-evidence-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
New-Item -ItemType Directory -Path $qa|Out-Null
$results=@()
foreach($relative in $ProofFiles){
 $proofPath=if([IO.Path]::IsPathRooted($relative)){[IO.Path]::GetFullPath($relative)}else{[IO.Path]::GetFullPath((Join-Path $root $relative))}
 if(-not $proofPath.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence must remain inside the project.'}
 $j=Get-Content -LiteralPath $proofPath -Raw -Encoding UTF8|ConvertFrom-Json
 $entries=@();$base=$root
 if($j.PSObject.Properties['Inputs']){$entries=@($j.Inputs)}
 elseif($j.PSObject.Properties['CurrentSourceHashes']){$entries=@($j.CurrentSourceHashes)}
 elseif($j.PSObject.Properties['Sources']){$entries=@($j.Sources)}
 elseif($j.PSObject.Properties['SourceInventory']){$entries=@($j.SourceInventory);$base=Join-Path $root 'modules-src\FactoryWorker'}
 else{throw ('No hash-bound source inventory: '+$relative)}
 if($entries.Count -eq 0){throw ('Empty source inventory: '+$relative)}
 $mismatches=@()
 foreach($entry in $entries){
  $path=if([IO.Path]::IsPathRooted($entry.Path)){[IO.Path]::GetFullPath($entry.Path)}else{[IO.Path]::GetFullPath((Join-Path $base $entry.Path))}
  if(-not $path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Source inventory escapes project.'}
  if(-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $entry.SHA256){$mismatches+=$entry.Path}
 }
 if($j.PSObject.Properties['Runtime']){
  foreach($entry in @($j.Runtime)){
   $path=if([IO.Path]::IsPathRooted($entry.Path)){[IO.Path]::GetFullPath($entry.Path)}else{[IO.Path]::GetFullPath((Join-Path $root $entry.Path))}
   if(-not $path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Runtime inventory escapes project.'}
   if(-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $entry.SHA256){$mismatches+=('Runtime:'+ $entry.Path)}
  }
 }
 $passed=$false
 if($j.PSObject.Properties['TestExitCode']){$passed=$j.TestExitCode -eq 0}
 elseif($j.PSObject.Properties['ExitCode']){$passed=$j.ExitCode -eq 0}
 elseif($j.PSObject.Properties['Result']){$passed=$j.Result.ExitCode -eq 0 -and -not $j.Result.TimedOut}
 elseif($j.PSObject.Properties['Execution']){$passed=$j.Execution.ExitCode -eq 0 -and -not $j.Execution.TimedOut}
 elseif($j.PSObject.Properties['Passed']){$passed=$j.Passed -eq $true}
 elseif($j.PSObject.Properties['Tests']){$passed=@($j.Tests).Count -gt 0 -and @($j.Tests|Where-Object {$_.ExitCode -ne 0}).Count -eq 0}
 elseif($j.PSObject.Properties['Checks']){$passed=@($j.Checks|Where-Object{-not $_.Passed}).Count -eq 0 -and @($j.Checks).Count -gt 0}
 foreach($flag in @('Passed','SourceUnchanged','InputsUnchanged','RuntimeUnchanged','OutputVerified')){
  if($j.PSObject.Properties[$flag] -and $j.$flag -ne $true){$passed=$false}
 }
 if($j.PSObject.Properties['Tests']){foreach($test in @($j.Tests)){
  if($test.PSObject.Properties['ExitCode'] -and $test.ExitCode -ne 0){$passed=$false}
  if($test.PSObject.Properties['Result'] -and ($test.Result.ExitCode -ne 0 -or $test.Result.TimedOut)){$passed=$false}
 }}
 if($j.PSObject.Properties['Results']){foreach($test in @($j.Results)){
  if($test.PSObject.Properties['Result'] -and ($test.Result.ExitCode -ne 0 -or $test.Result.TimedOut)){$passed=$false}
 }}
 if($j.PSObject.Properties['Status'] -and $j.Status -cne 'PASS'){$passed=$false}
 foreach($flag in @('HardwareAccessed','SDKCalled','SdkLoaded','ActualWorkerStarted','ProductStarted','ProductEntryStarted','ProductEntryExecuted','WorkerEntryExecuted','NativeSessionCreated','InstallerRun')){
  if($j.PSObject.Properties[$flag] -and $j.$flag -ne $false){$passed=$false}
 }
 $results+=[ordered]@{Proof=$proofPath.Substring($root.Length+1);ProofSHA256=(Get-FileHash -LiteralPath $proofPath).Hash;InputCount=$entries.Count;CurrentInputMismatchCount=$mismatches.Count;MismatchedInputs=@($mismatches);RecordedTestPassed=$passed;CurrentHashBindingPassed=($passed -and $mismatches.Count -eq 0)}
}
$allPassed=@($results|Where-Object{-not $_.CurrentHashBindingPassed}).Count -eq 0
[ordered]@{Schema='HVX.ExternalStaticAudit.CurrentEvidence/1';CheckedUtc=[DateTime]::UtcNow.ToString('o');CurrentSourceOnly=$true;TestsRerunByThisScript=$false;HardwareAccessed=$false;SDKCalled=$false;ProductStarted=$false;InstallerBuilt=$false;PhysicalAcceptance=$false;AllCurrentHashBindingsPassed=$allPassed;Results=$results}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
$results|ForEach-Object{[pscustomobject]@{Proof=$_.Proof;Inputs=$_.InputCount;Mismatches=$_.CurrentInputMismatchCount;Passed=$_.CurrentHashBindingPassed}}|ConvertTo-Json -Depth 4
Write-Output ('EXTERNAL_STATIC_EVIDENCE='+$qa)
if(-not $allPassed){throw 'A recorded pass is stale or failed; see retained evidence. Do not count it as current.'}
