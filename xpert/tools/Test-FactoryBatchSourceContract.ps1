param([Parameter(Mandatory=$true)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceRoot=Join-Path $root 'modules-src/FactoryWorker'
$inputPaths=@(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File|Where-Object{$_.Extension -in '.cs','.json'}|ForEach-Object FullName)+@($PSCommandPath,(Join-Path $PSScriptRoot 'Test-FactoryBatchSource.ps1'))
function Inventory {
 @($inputPaths|Sort-Object -Unique|ForEach-Object{
  for($walk=$_;$walk;$walk=[IO.Path]::GetDirectoryName($walk)){
   if((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked source contract input rejected.'}
  }
  [ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}
 })
}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
$qa=[IO.Path]::GetFullPath($EvidenceDirectory)
if(-not $qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $qa)){throw 'Use a new project build evidence directory.'}
for($walk=[IO.Path]::GetDirectoryName($qa);$walk;$walk=[IO.Path]::GetDirectoryName($walk)){
 if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked evidence path rejected.'}
}
$null=New-Item -ItemType Directory -Path (Join-Path $qa 'tools'),(Join-Path $qa 'modules-src') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-FactoryBatchSource.ps1') -Destination (Join-Path $qa 'tools')
Copy-Item -LiteralPath (Join-Path $root 'modules-src/FactoryWorker') -Destination (Join-Path $qa 'modules-src') -Recurse
$gate=Join-Path $qa 'tools/Test-FactoryBatchSource.ps1'
$worker=Join-Path $qa 'modules-src/FactoryWorker'
$checks=0
function Check([bool]$ok,[string]$message){$script:checks++;if(-not $ok){throw $message}}
function RejectChange([string]$relative,[scriptblock]$change,[string]$message){
 $file=Join-Path $worker $relative
 $bytes=[IO.File]::ReadAllBytes($file)
 try{
  & $change $file
  $rejected=$false;try{& $gate|Out-Null}catch{$rejected=$true}
  Check $rejected $message
 }finally{[IO.File]::WriteAllBytes($file,$bytes)}
}
$accepted=& $gate
Check ($accepted.SourceCount -eq 198 -and $accepted.ExactUpstreamSourceCount -eq 151) 'Original identities and forty-seven reviewed local differences counted separately, including five diagnostic-default adaptations'
Check (@($accepted.LocalAdapters).Count -eq 24 -and $accepted.LocalAdaptationSHA256 -cmatch '^[A-F0-9]{64}$') 'Exact two entry adapters plus twenty-two reviewed helpers and pinned digest accepted'
$hpIdentity=@((Get-Content -LiteralPath (Join-Path $worker 'v30-batch-integration-provenance.json') -Raw|ConvertFrom-Json).Files|Where-Object Path -CEQ 'Imported/Unified100/PdLinearHighPass.cs')
Check ($hpIdentity.Count -eq 1 -and $hpIdentity[0].ExactUpstream -eq $false -and
 $hpIdentity[0].FrozenUpstreamSHA256 -ceq '28780887CE26F71C84115A0E32E06DB9AFB9F85DD861E21E6C5F5B6FC6823A22' -and
 $hpIdentity[0].IntegratedSHA256 -cne $hpIdentity[0].FrozenUpstreamSHA256) 'Fast high-pass explicitly remains a locally modified snapshot with immutable frozen identity'
RejectChange 'FactoryModels.cs' {param($file)[IO.File]::AppendAllText($file,"`n// synthetic tamper")} 'Core source modification rejected'
RejectChange 'Imported/BenchModels.cs' {param($file)[IO.File]::AppendAllText($file,"`n// synthetic tamper")} 'Reviewed binary IO adapter modification rejected'
RejectChange 'Imported/Unified100/PdLinearHighPass.cs' {param($file)[IO.File]::AppendAllText($file,"`n// synthetic tamper")} 'Reviewed local high-pass source modification rejected'
RejectChange 'FactoryAcceptanceChecks.cs' {param($file)[IO.File]::AppendAllText($file,"`n// synthetic tamper")} 'Unadapted source modification rejected'
RejectChange 'UnifiedFinePressureRamp.cs' {param($file)[IO.File]::AppendAllText($file,"`n// synthetic tamper")} 'Reviewed new helper modification rejected'
RejectChange 'v30-batch-local-adaptation-20261008.json' {param($file)[IO.File]::AppendAllText($file,"`n")} 'Reviewed adaptation is byte pinned'
RejectChange 'v30-batch-upstream-manifest.json' {param($file)[IO.File]::AppendAllText($file,"`n")} 'Upstream manifest remains byte pinned'
RejectChange 'v30-batch-integration-provenance.json' {param($file)
 $p=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json
 ($p.Files|Where-Object Path -CEQ 'FactoryModels.cs').ExactUpstream=$true
 [IO.File]::WriteAllText($file,($p|ConvertTo-Json -Depth 12))
} 'Locally changed source cannot claim exact upstream'
RejectChange 'v30-batch-integration-provenance.json' {param($file)
 $p=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json
 ($p.Files|Where-Object Path -CEQ 'Imported/Unified100/PdLinearHighPass.cs').ExactUpstream=$true
 [IO.File]::WriteAllText($file,($p|ConvertTo-Json -Depth 12))
} 'Locally optimized high-pass cannot claim exact upstream'
RejectChange 'v30-batch-integration-provenance.json' {param($file)
 $p=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json
 $p.LocalAdapters=@('UnifiedPressureBridge.cs','UnifiedWorkerEntry.cs')
 [IO.File]::WriteAllText($file,($p|ConvertTo-Json -Depth 12))
} 'Omitted new helper closure rejected'
RejectChange 'v30-batch-integration-provenance.json' {param($file)
 $p=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json
 $p.Files+=@($p.Files[0]);[IO.File]::WriteAllText($file,($p|ConvertTo-Json -Depth 12))
} 'Duplicate frozen source identity rejected'
RejectChange 'v30-batch-integration-provenance.json' {param($file)
 $p=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json
 $p.PhysicalAcceptance=$true;[IO.File]::WriteAllText($file,($p|ConvertTo-Json -Depth 12))
} 'Physical acceptance cannot be manufactured by source import'
$again=& $gate
Check (($accepted|ConvertTo-Json -Depth 8 -Compress) -ceq ($again|ConvertTo-Json -Depth 8 -Compress)) 'Fixture restored; same evidence after rejection cases'
$after=@(Inventory);$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq ($after|ConvertTo-Json -Depth 5 -Compress))
$mismatches=@($before|Where-Object{(Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -cne $_.SHA256}).Count
[ordered]@{Schema='HVX.FactoryBatchSourceContract/1';Status=$(if($unchanged){'PASS'}else{'FAIL'});Checks=$checks;HardwareAccessed=$false;
 StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Inputs=$before;SourceUnchanged=$unchanged;InputMismatchCount=$mismatches;
 ProductEntryExecuted=$false;UserSourcesWritten=$false;GateSHA256=(Get-FileHash -LiteralPath $gate).Hash;
 LocalAdaptationSHA256=$again.LocalAdaptationSHA256;EvidenceDirectory=$qa}|ConvertTo-Json -Depth 4|
 Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
if(-not $unchanged){throw 'Original source contract inputs changed; failure evidence retained.'}
Write-Output ('FACTORY_SOURCE_GUARDS_PASS='+$checks+'; '+$qa)
