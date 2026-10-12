# Read-only source audit. Creates a receipt only under build; no pin or business write.
param([string]$OutputRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$worker=Join-Path $project 'modules-src\FactoryWorker'
if([String]::IsNullOrWhiteSpace($OutputRoot)){$OutputRoot=Join-Path $project ('build\qa-journal-controlled-diff-'+[Guid]::NewGuid().ToString('N').Substring(0,10))}
$output=[IO.Path]::GetFullPath($OutputRoot)
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be under build.'}
if(Test-Path -LiteralPath $output){throw 'Use a new isolated output directory.'}
$provenancePath=Join-Path $worker 'v30-batch-integration-provenance.json'
$adaptationPath=Join-Path $worker 'v30-batch-local-adaptation-20261008.json'
$p=Get-Content -LiteralPath $provenancePath -Raw -Encoding UTF8|ConvertFrom-Json
$a=Get-Content -LiteralPath $adaptationPath -Raw -Encoding UTF8|ConvertFrom-Json
$currentPath=Join-Path $worker 'Program.cs'
$referencePath=Join-Path $project 'build\full-repair-20261009-r29\qa\source-contract\modules-src\FactoryWorker\Program.cs'
$referenceSHA=(Get-FileHash -LiteralPath $referencePath -Algorithm SHA256).Hash
if($referenceSHA -cne '65F362B491ED1A13F190540747E411C31BB5409AE8B7BEE3A4C3E7803FEC8D11'){throw 'Unexpected older reference hunk source.'}
$currentSHA=(Get-FileHash -LiteralPath $currentPath -Algorithm SHA256).Hash
if($currentSHA -cne '61F025EACB0FCD114765CA9B523838EF0E9D84EE9F1EC8D7E5F7A68E44949671'){throw 'Journal Program changed outside this reviewed audit.'}
$programPin=@($p.Files|Where-Object Path -CEQ 'Program.cs')
if($programPin.Count -ne 1 -or $programPin[0].IntegratedSHA256 -cne 'E7D3C37A3F6A006445E793BC7C09CE0514DB2074007A9AD5F40161F21B609049'){throw 'Old integrated pin differs; do not self-learn it.'}
$current=[IO.File]::ReadAllText($currentPath)
$older=[IO.File]::ReadAllText($referencePath)
function Slice-Between([string]$value,[string]$start,[string]$end,[int]$after=0){
 $s=$value.IndexOf($start,$after,[StringComparison]::Ordinal)
 if($s -lt 0){throw ('Missing start anchor: '+$start)}
 $e=$value.IndexOf($end,$s,[StringComparison]::Ordinal)
 if($e -lt 0){throw ('Missing end anchor: '+$end)}
 return [pscustomobject]@{Start=$s;Length=($e-$s);Text=$value.Substring($s,$e-$s)}
}
$oldReport=Slice-Between $older '                var progressLock = new object();' '                writeLink=report;'
$newReport=Slice-Between $current '                var progress = new FactoryProgressJournal' '                writeLink=report;'
$restored=$current.Remove($newReport.Start,$newReport.Length).Insert($newReport.Start,$oldReport.Text)
$failureDeclaration='                Exception originalFailure = null;'+"`n"
if(([regex]::Matches($restored,[regex]::Escape($failureDeclaration))).Count -ne 1){throw 'Failure declaration must be exactly one hunk.'}
$restored=$restored.Replace($failureDeclaration,'')
$oldTail=Slice-Between $older '                catch (Exception ex)' '        // Executed by the worker' ($older.IndexOf('internal static int Worker'))
$newTail=Slice-Between $restored '                catch (Exception ex)' '        // Executed by the worker' ($restored.IndexOf('internal static int Worker'))
$restored=$restored.Remove($newTail.Start,$newTail.Length).Insert($newTail.Start,$oldTail.Text)
$restoredBytes=[Text.Encoding]::UTF8.GetBytes($restored)
$sha=[Security.Cryptography.SHA256]::Create()
try{$restoredSHA=([BitConverter]::ToString($sha.ComputeHash($restoredBytes))).Replace('-','')}finally{$sha.Dispose()}
if($restoredSHA -cne $programPin[0].IntegratedSHA256){throw ('Reverse reconstruction does not match exact old pin: '+$restoredSHA)}
$inventory=@()
foreach($entry in $p.Files){
 $actual=(Get-FileHash -LiteralPath (Join-Path $worker $entry.Path) -Algorithm SHA256).Hash
 if($entry.Path -cne 'Program.cs' -and $actual -cne $entry.IntegratedSHA256){throw ('Unreviewed imported domain: '+$entry.Path)}
 $inventory+=([ordered]@{Path=$entry.Path;FrozenUpstreamSHA256=$entry.FrozenUpstreamSHA256;PinnedSHA256=$entry.IntegratedSHA256;CurrentSHA256=$actual;MatchesPin=($actual -ceq $entry.IntegratedSHA256)})
}
foreach($entry in $a.NewFiles){
 $actual=(Get-FileHash -LiteralPath (Join-Path $worker $entry.Path) -Algorithm SHA256).Hash
 if($actual -cne $entry.SHA256){throw ('Unreviewed existing local adapter: '+$entry.Path)}
 $inventory+=([ordered]@{Path=$entry.Path;PinnedSHA256=$entry.SHA256;CurrentSHA256=$actual;MatchesPin=$true})
}
$journalPath=Join-Path $worker 'FactoryProgressJournal.cs'
$journalSHA=(Get-FileHash -LiteralPath $journalPath -Algorithm SHA256).Hash
if($journalSHA -cne '24B8D558D813F6671C79DB6C62409B2A65879E9033E5F21B41115ACBF14BE98A'){throw 'Unexpected new journal hash.'}
$pinBefore=@($provenancePath,$adaptationPath)|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}}
New-Item -ItemType Directory -Path $output|Out-Null
[ordered]@{
 Schema='HVX.Factory.ProgressJournal.ControlledDiff/1';CapturedUtc=[DateTime]::UtcNow.ToString('o');TestPassed=$true
 Program=[ordered]@{Path=$currentPath;FrozenUpstreamSHA256=$programPin[0].FrozenUpstreamSHA256;OldIntegratedSHA256=$programPin[0].IntegratedSHA256;CurrentSHA256=$currentSHA;ReverseExactSHA256=$restoredSHA;ReverseByteCount=$restoredBytes.Length;ReverseWrittenToDisk=$false}
 OldHunkReference=[ordered]@{Path=$referencePath;SHA256=$referenceSHA;EntireReferenceIsOldIntegrated=$false;ExactWholeProgramIdentityProvedByReverseHash=$true}
 ControlledDomains=@('Worker report delegate -> bounded single managed writer','Worker originalFailure bookkeeping and catch error aggregation','Worker finally: original physical cleanup precedes guaranteed journal drain')
 NewLocalAdapter=[ordered]@{Path=$journalPath;SHA256=$journalSHA;FrozenUpstreamFile=$false;RequiresExplicitLocalAdapterAdmission=$true}
 ImportedAndExistingLocalInventory=$inventory;PinInputs=$pinBefore
 FocusedProof='build/qa-progress-journal-98224e030f/verification-progress-journal.json';FocusedTests='32 managed journal + 30 reached-feedback-before-ARM checks'
 PinsWritten=$false;BusinessWritten=$false;ProductEntryExecuted=$false;WorkerEntryExecuted=$false;ResidentStarted=$false;SDKCalled=$false;HardwareAccessed=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;FinalExeIntegration=$false
}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'verification-controlled-diff.json') -Encoding UTF8
foreach($entry in $pinBefore){if((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -cne $entry.SHA256){throw 'Pin changed concurrently; receipt is not an unchanged audit.'}}
Write-Output ('WORKER_JOURNAL_EXACT_REVERSE_PASS; OldSHA256='+$restoredSHA+'; NewSHA256='+$currentSHA+'; ExistingDomainsChecked='+$inventory.Count)
Write-Output ('ControlledDiffProof='+$output+'\verification-controlled-diff.json')
