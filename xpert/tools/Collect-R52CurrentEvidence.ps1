[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [string[]]$AdditionalProofs=@(),
    [string[]]$ExplicitProofs=@(),
    [ValidatePattern('^r[0-9]+$')][string]$Release='r52'
)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$output=[IO.Path]::GetFullPath($OutputRoot)
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){
    throw 'Use a new directory below the project build directory.'
}
$proofs=@(
 'build/public-beta-1.1.1-20261011-r52/qa/source-freeze-build.json',
 'build/qa-factory-source-final-20261011-01/proof.json',
 'build/qa-pressure-desktop-dataflow-20261011-04/proof.json',
 'build/qa-capacitor-save-a2aaeef126/verification-capacitor-save.json',
 'build/qa-manual-overrange-9f49419ada/proof.json',
 'build/qa-pressure-button-current-20261011-02/verification-pressure-action-text-dpi.json',
 'build/qa-pressure-gui-preview-20261011-01/proof.json',
 'build/qa-r52-save-queue-current-20261011-01/proof.json',
 'build/r52-deployment-preflight-0f3b1e2bd9/verification.json',
 'build/r50-pressure-noise-initialization-737802b4d1/verification.json',
 'build/qa-zero-range-reminder-c5a030a21f/proof.json',
 'build/qa-calibration-short-cable-75d6504372/proof.json',
 'build/qa-calibration-short-cable-134c44bde9/proof.json',
 'build/qa-calibration-short-cable-64a3bc2ece/proof.json',
 'build/qa-calibration-short-cable-54b4941941/proof.json',
 'build/qa-calibration-short-cable-6eded34349/proof.json',
 'build/qa-calibration-short-cable-c42eae7d1d/proof.json',
 'build/qa-calibration-short-cable-26446547b8/proof.json',
 'build/qa-calibration-short-cable-d004d05437/proof.json',
 'build/qa-calibration-short-cable-dcef79df7a/proof.json',
 'build/qa-calibration-localization-expanded-25fff63be8/proof.json',
 'build/qa-calibration-compatibility-final-fcebd5c794/proof.json',
 'build/qa-calibration-compatibility-final-086a029014/proof.json'
)+@($AdditionalProofs)
if($ExplicitProofs.Count -gt 0){$proofs=@($ExplicitProofs)+@($AdditionalProofs)}
$seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$snapshots=New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
$rows=New-Object 'System.Collections.Generic.List[object]'
$problems=New-Object 'System.Collections.Generic.List[string]'
function Resolve-ProjectFile([string]$Path,[string]$Base=$project){
    $full=if([IO.Path]::IsPathRooted($Path)){[IO.Path]::GetFullPath($Path)}else{[IO.Path]::GetFullPath((Join-Path $Base $Path))}
    if(-not $full.StartsWith($project+'\',[StringComparison]::OrdinalIgnoreCase) -or -not(Test-Path -LiteralPath $full -PathType Leaf)){throw ('Missing or out-of-project file: '+$Path)}
    $item=Get-Item -LiteralPath $full
    if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'A bound file cannot be a reparse point.'}
    return $full
}
function Check-BoundFile([object]$Entry,[string]$Base=$project){
    $full=Resolve-ProjectFile ([string]$Entry.Path) $Base
    $expected=[string]$Entry.SHA256
    if($expected -cnotmatch '^[A-F0-9]{64}$'){throw ('Invalid SHA256: '+$full)}
    $actual=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
    if($actual -cne $expected){throw ('Current hash mismatch: '+$full)}
    if($snapshots.ContainsKey($full) -and $snapshots[$full] -cne $expected){throw ('Conflicting input hashes: '+$full)}
    $snapshots[$full]=$expected
}
function Check-ReferenceFile([object]$Entry){
    $path=[string]$Entry.Path
    if(-not [IO.Path]::IsPathRooted($path) -or $path.StartsWith('\\') -or $path -match '^[A-Za-z]:.*:' -or $path.Replace('/','\').Split('\') -contains '..'){throw 'A reference must be an explicit local absolute file.'}
    $full=[IO.Path]::GetFullPath($path)
    $expected=[string]$Entry.SHA256
    if($expected -cnotmatch '^[A-F0-9]{64}$' -or -not(Test-Path -LiteralPath $full -PathType Leaf)){throw 'Invalid reference file or hash.'}
    $item=Get-Item -LiteralPath $full
    if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'A reference cannot be a reparse point.'}
    if((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -cne $expected){throw ('Reference hash mismatch: '+$full)}
    if($snapshots.ContainsKey($full) -and $snapshots[$full] -cne $expected){throw ('Conflicting reference hashes: '+$full)}
    $snapshots[$full]=$expected
}
function Check-Execution([object]$Run){
    if($null -eq $Run -or -not $Run.PSObject.Properties['ExitCode']){throw 'A directed execution record is incomplete.'}
    if([int]$Run.ExitCode -ne 0 -or ($Run.PSObject.Properties['TimedOut'] -and $Run.TimedOut) -or
      ($Run.PSObject.Properties['ProcessExitConfirmed'] -and -not $Run.ProcessExitConfirmed) -or
      ($Run.PSObject.Properties['ExecutionFailure'] -and -not [string]::IsNullOrEmpty([string]$Run.ExecutionFailure))){throw 'A directed execution did not pass.'}
    foreach($pair in @(@('StdoutPath','StdoutSHA256'),@('StderrPath','StderrSHA256'))){
        if($Run.PSObject.Properties[$pair[0]]){Check-BoundFile ([pscustomobject]@{Path=$Run.($pair[0]);SHA256=$Run.($pair[1])})}
    }
}
foreach($relative in $proofs){
    try{
        if([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative.Replace('/','\').Split('\') -contains '..' -or -not $seen.Add($relative.Replace('/','\'))){throw 'Proof names must be distinct project-relative paths.'}
        $path=Resolve-ProjectFile $relative
        $hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $doc=Get-Content -LiteralPath $path -Encoding UTF8 -Raw | ConvertFrom-Json
        $passed=$false
        if($doc.PSObject.Properties['Status']){if($doc.Status -cne 'PASS'){throw 'Recorded status is not PASS.'};$passed=$true}
        if($doc.PSObject.Properties['Passed']){if($doc.Passed -isnot [bool] -or -not $doc.Passed){throw 'Recorded Passed flag is not true.'};$passed=$true}
        foreach($flag in @('SourceUnchanged','RuntimeUnchanged','SourceBoundCompile','OutputVerified','AllCurrentHashBindingsPassed')){
            if($doc.PSObject.Properties[$flag] -and ($doc.$flag -isnot [bool] -or -not $doc.$flag)){throw ('Invalid current binding: '+$flag)}
        }
        foreach($flag in @('HardwareAccessed','SDKCalled','ProductStarted','ProductMainExecuted','ProductEntryExecuted','WorkerEntryExecuted','ActualWorkerStarted','PhysicalAcceptance')){
            if($doc.PSObject.Properties[$flag] -and ($doc.$flag -isnot [bool] -or $doc.$flag)){throw ('Out-of-scope execution: '+$flag)}
        }
        foreach($name in @('Execution','Result','Run','GestureExecution')){
            if($doc.PSObject.Properties[$name]){Check-Execution $doc.$name;$passed=$true}
        }
        foreach($name in @('Runs','ExistingRegression','Tests')){
            if($doc.PSObject.Properties[$name]){
                foreach($run in @($doc.$name)){
                    if($run.PSObject.Properties['Execution']){Check-Execution $run.Execution}
                    elseif($run.PSObject.Properties['Result']){Check-Execution $run.Result}
                    else{Check-Execution $run}
                    $passed=$true
                }
            }
        }
        if($doc.PSObject.Properties['ExitCode']){if([int]$doc.ExitCode -ne 0){throw 'Recorded ExitCode is not zero.'};$passed=$true}
        if(-not $passed){throw 'No supported passing execution or result was recorded.'}
        $entries=@();$base=$project
        if($doc.PSObject.Properties['Inputs']){$entries=@($doc.Inputs)}
        elseif($doc.PSObject.Properties['CurrentSourceHashes']){$entries=@($doc.CurrentSourceHashes)}
        elseif($doc.PSObject.Properties['Sources']){$entries=@($doc.Sources)}
        elseif($doc.PSObject.Properties['SourceInventory']){$entries=@($doc.SourceInventory);$base=Join-Path $project 'modules-src\FactoryWorker'}
        if($entries.Count -eq 0){throw 'No source hash inventory was recorded.'}
        foreach($entry in $entries){Check-BoundFile $entry $base}
        if($doc.PSObject.Properties['ReferenceInputs']){foreach($entry in @($doc.ReferenceInputs)){Check-ReferenceFile $entry}}
        foreach($name in @('Runtime','ProofHashes','Children')){
            if($doc.PSObject.Properties[$name]){foreach($entry in @($doc.$name)){Check-BoundFile $entry}}
        }
        if($doc.PSObject.Properties['PreviewPath']){Check-BoundFile ([pscustomobject]@{Path=$doc.PreviewPath;SHA256=$doc.PreviewSHA256})}
        $snapshots[$path]=$hash
        $rows.Add([ordered]@{Proof=$relative.Replace('\','/');ProofSHA256=$hash;Schema=[string]$doc.Schema;RecordedTestPassed=$true;CurrentHashBindingPassed=$true;InputCount=$entries.Count;CurrentInputMismatchCount=0})
    }catch{$problems.Add($relative+': '+$_.Exception.Message)}
}
foreach($path in @($snapshots.Keys)){
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $snapshots[$path]){$problems.Add('Changed during evidence collection: '+$path)}
}
New-Item -ItemType Directory -Path $output | Out-Null
$result=[ordered]@{
    Schema='HVX.ExternalStaticAudit.CurrentEvidence/1';CompletedUtc=[DateTime]::UtcNow.ToString('o');Release=$Release;
    AllCurrentHashBindingsPassed=($problems.Count -eq 0 -and $rows.Count -eq $proofs.Count);Results=$rows.ToArray();
    UniqueBoundFiles=$snapshots.Count;Problems=$problems.ToArray();FullRegression=$false;PhysicalAcceptance=$false;
    ProductStarted=$false;HardwareAccessed=$false;SDKCalled=$false;InstallerBuilt=$false;
    CollectorSHA256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash;
    Remaining=@('Target-machine electrical safety, hardware lifecycle and latency need user verification.',
      'All language choices are retained; all-page, all-language professional translation and physical DPI review are not complete.',
      'Four obsolete Factory residual/AI-policy suites remain recorded FAIL, not full-suite success.',
      'Directed offline evidence is not full-suite acceptance, a comprehensive security audit or an Authenticode signature.')
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'current-evidence.json') -Encoding UTF8
if(-not $result.AllCurrentHashBindingsPassed){throw ('Current evidence failed: '+($problems -join '; '))}
Write-Output ('CURRENT_EVIDENCE_PASS='+$rows.Count+';UNIQUE_BOUND_FILES='+$snapshots.Count)
Write-Output ('CURRENT_EVIDENCE='+ (Join-Path $output 'current-evidence.json'))
