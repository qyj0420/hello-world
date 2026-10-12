param(
    [Parameter(Mandatory=$true)][string]$BuildRoot,
    [Parameter(Mandatory=$true)][string]$VerificationPath
)

$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$buildPrefix=$projectRoot+'\build\'
if(-not [IO.Path]::IsPathRooted($BuildRoot) -or -not [IO.Path]::IsPathRooted($VerificationPath)) { throw 'Absolute build and verification paths are required.' }
$BuildRoot=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
$VerificationPath=[IO.Path]::GetFullPath($VerificationPath)
if(-not $BuildRoot.StartsWith($buildPrefix,[StringComparison]::OrdinalIgnoreCase) -or
   -not $VerificationPath.StartsWith($BuildRoot+'\qa\',[StringComparison]::OrdinalIgnoreCase) -or
   [IO.Path]::GetFileName($VerificationPath) -cne 'verification.json') { throw 'Only the complete verification.json under this project build QA may be used.' }

function Assert-NotLinked([string]$Path) {
    $item=Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    while($null -ne $item -and $item.FullName.StartsWith($projectRoot,[StringComparison]::OrdinalIgnoreCase)) {
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Linked QA/input path is prohibited: '+$item.FullName) }
        # Parent/Directory returns plain IO objects without provider-added
        # PSIsContainer; their concrete type remains available in StrictMode.
        $parent=if($item -is [IO.DirectoryInfo]){$item.Parent}else{$item.Directory}
        $item=$parent
    }
}
Assert-NotLinked $BuildRoot
Assert-NotLinked $VerificationPath
$qaRoot=Join-Path $BuildRoot 'qa'
Assert-NotLinked $qaRoot
$evidenceRoot=Join-Path $qaRoot ('ten-round-'+[Guid]::NewGuid().ToString('N').Substring(0,12))
New-Item -ItemType Directory -Path $evidenceRoot | Out-Null
$evidencePath=Join-Path $evidenceRoot 'verification-ten-rounds.json'
$roundResults=New-Object 'System.Collections.Generic.List[object]'
$startedUtc=[DateTime]::UtcNow
$evidence=[ordered]@{
    Schema='HVX.Hybrid.TenRounds/1';Status='RUNNING';StartedUtc=$startedUtc.ToString('o');CompletedUtc=$null
    BuildRoot=$BuildRoot;VerificationPath=$VerificationPath;VerificationSHA256=$null
    RoundCount=0;FullSuiteRuns=1;ScopedRounds=9;ScopedTestRuns=0
    HardwareAccessed=$false;PhysicalAcceptance=$false
    Explanation='Round 1 is one complete frozen-source suite. Rounds 2–10 are nine different focused cross-checks, not nine additional complete suites. No physical-device acceptance is claimed.'
    SourceInputsUnchanged=$false;RuntimeUnchanged=$false;TestBinariesUnchanged=$false
    Rounds=@();Failure=$null
}
function Save-Evidence {
    $evidence.Rounds=$roundResults.ToArray()
    $evidence.RoundCount=@($roundResults | Where-Object {$_.Status -ceq 'PASS'}).Count
    $evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $evidencePath -Encoding UTF8
}
function Resolve-OwnedRelative([string]$Owner,[string]$Relative) {
    if([String]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative -match '(^|[\\/])\.\.([\\/]|$)') { throw ('Invalid evidence relative path: '+$Relative) }
    $path=[IO.Path]::GetFullPath((Join-Path $Owner $Relative))
    if(-not $path.StartsWith($Owner.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw ('Evidence path escaped its owner: '+$Relative) }
    Assert-NotLinked $path
    return $path
}
function Source-Inventory {
    $files=@(Get-Item -LiteralPath (Join-Path $projectRoot 'Build.ps1'),(Join-Path $projectRoot 'Stage-Modules.ps1'),(Join-Path $projectRoot 'App.config'),(Join-Path $projectRoot 'app.manifest'))
    foreach($directory in @('src','modules-src','assets','tools','tests','hybrid-src')) {
        $files+=@(Get-ChildItem -LiteralPath (Join-Path $projectRoot $directory) -Recurse -File -Force)
    }
    @($files | Sort-Object FullName | ForEach-Object {
        Assert-NotLinked $_.FullName
        [pscustomobject]@{Path=$_.FullName.Substring($projectRoot.Length+1).Replace('/','\');SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
}
function Assert-SourceInputs([object[]]$Expected) {
    if($Expected.Count -eq 0) { throw 'The complete proof has no frozen source inputs.' }
    $current=@(Source-Inventory)
    if($current.Count -ne $Expected.Count) { throw 'Build/test input inventory differs from the complete proof.' }
    $expectedMap=@{}
    foreach($entry in $Expected) {
        if($entry.Path -isnot [string] -or $entry.SHA256 -isnot [string] -or $entry.SHA256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Malformed frozen-source evidence.' }
        $relative=([string]$entry.Path).Replace('/','\')
        if($expectedMap.ContainsKey($relative)) { throw ('Duplicate frozen-source path: '+$relative) }
        $null=Resolve-OwnedRelative $projectRoot $relative
        $expectedMap[$relative]=$entry.SHA256
    }
    foreach($entry in $current) {
        if(-not $expectedMap.ContainsKey($entry.Path) -or $expectedMap[$entry.Path] -cne $entry.SHA256) { throw ('Frozen source input changed: '+$entry.Path) }
    }
    return $current.Count
}
function Is-ZeroInteger($Value) { return ($Value -is [int] -or $Value -is [long]) -and $Value -eq 0 }
function Case([string]$Name,[string]$Mode='BuildQa') { [pscustomobject]@{Name=$Name;Mode=$Mode} }
$plans=@(
    [pscustomobject]@{Number=2;Focus='Pressure permission, progress, plan, finish-current-round and locks';Tests=@(
        (Case 'PressureProgressSmoke'),(Case 'PressureFinishCurrentRoundSmoke'),(Case 'PressureP04FeedbackSmoke'),
        (Case 'PressureZeroVoltageBackendSmoke'),(Case 'PressureNavigationLockSmoke' 'Qa'),(Case 'PressureProjectPlanCorrespondenceSmoke' 'ModulesQa'))},
    [pscustomobject]@{Number=3;Focus='Project save/load, exact current-project pin and transactional activation';Tests=@(
        (Case 'ProjectSaveTransactionSmoke'),(Case 'ProjectLoadIntegritySmoke'),(Case 'ProjectCurrentPinSmoke' 'Qa'),(Case 'ProjectActivationSmoke'),(Case 'ProjectFileTransactionsSmoke'))},
    [pscustomobject]@{Number=4;Focus='Cable-joint add/edit/delete, materials and truthful transaction warnings';Tests=@(
        (Case 'CableJointManagerSmoke' 'Qa'),(Case 'CableJointEditingSmoke'),(Case 'CreationMeasuredJointEditSmoke'),
        (Case 'CreationMaterialCatalogSmoke'),(Case 'SharedCableJointWritableTransactionSmoke' 'Qa'),(Case 'CableJointCommitWarningSmoke'))},
    [pscustomobject]@{Number=5;Focus='Calibration averages, hover responsiveness, joint search and geometry';Tests=@(
        (Case 'CalibrationAverageSelectionSmoke'),(Case 'BrowserJointAsyncResponsiveSmoke'),(Case 'BrowserHoverPerformanceSmoke' 'Qa'),
        (Case 'BrowserAverageGeometrySmoke'),(Case 'BrowserSharedMeanJointSmoke'),(Case 'CalibrationGeometryFeedbackSmoke'))},
    [pscustomobject]@{Number=6;Focus='Report optional appendix, saved conclusions, actions and project identity';Tests=@(
        (Case 'ReportAppendixLayoutSmoke'),(Case 'ReportSavedConclusionSmoke'),(Case 'ReportActionsStructureSmoke'),(Case 'ReportProjectIdentitySmoke'))},
    [pscustomobject]@{Number=7;Focus='Injected environment acquisition and actual variable-period phase/data windows';Tests=@(
        (Case 'EnvironmentAutoRefreshSmoke' 'None'),(Case 'EnvironmentUiBridgeSmoke' 'Build'),(Case 'EnvironmentalAcquisitionSmoke' 'None'),
        (Case 'LocationCyclePhaseSmoke'),(Case 'PressureAcquisitionCycleSmoke'))},
    [pscustomobject]@{Number=8;Focus='Actual encrypted secure-store round trip, project transactions, source integrity and input validation';Tests=@(
        (Case 'ProjectFileTransactionsSmoke'),(Case 'SecureProjectStoreSmoke' 'Qa'),(Case 'BrowserMeanSourceIntegritySmoke'),
        (Case 'CreationSaveValidationSmoke'),(Case 'CreationPrecisionTimestampSmoke'))},
    [pscustomobject]@{Number=9;Focus='Home device/project buttons, language, DPI and setup navigation';Tests=@(
        (Case 'HomeDeviceActionsSmoke' 'Qa'),(Case 'HomeConnectionProjectActionsSmoke' 'Qa'),(Case 'DpiLayoutSmoke' 'Qa'),
        (Case 'ThreeModuleLanguageSmoke'),(Case 'DeviceSetupNavigationSmoke'))},
    [pscustomobject]@{Number=10;Focus='Actual mixed native consumer, managed parity, deployment and algorithm boundaries';Tests=@(
        (Case 'HybridRuntimeSmoke'),(Case 'HybridPhasePerformanceSmoke' 'NativePerf'),(Case 'AnalysisDeploymentSmoke'),
        (Case 'Task28SourceIntegrationSmoke' 'Build'),(Case 'Task15EmbeddedIntegrationSmoke'),(Case 'DeferredTask17BoundarySmoke' 'Build'))}
)
$testHashes=@{}
$proofSnapshot=$null
$proof=$null
try {
    # Read once; the parse and SHA identify precisely the same proof bytes.
    $proofBytes=[IO.File]::ReadAllBytes($VerificationPath)
    $hash=[Security.Cryptography.SHA256]::Create()
    try{$proofSha=[BitConverter]::ToString($hash.ComputeHash($proofBytes)).Replace('-','')}finally{$hash.Dispose()}
    $proof=[Text.Encoding]::UTF8.GetString($proofBytes).TrimStart([char]0xFEFF) | ConvertFrom-Json
    if($proof.Status -cne 'PASS' -or -not [String]::IsNullOrWhiteSpace([string]$proof.StartAtTest) -or
       $proof.HardwareAccessed -isnot [bool] -or $proof.HardwareAccessed -or
       $proof.PhysicalAcceptance -isnot [bool] -or $proof.PhysicalAcceptance -or
       $proof.BuildTestInputsUnchanged -isnot [bool] -or -not $proof.BuildTestInputsUnchanged) { throw 'A complete offline PASS proof with unchanged frozen inputs is required.' }
    $tests=@($proof.Tests)
    $names=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $proofTests=@{}
    foreach($test in $tests) {
        if($test.Name -isnot [string] -or [String]::IsNullOrWhiteSpace($test.Name) -or -not $names.Add($test.Name) -or -not (Is-ZeroInteger $test.ExitCode)) { throw 'The complete proof contains invalid, duplicate or failed tests.' }
        $proofTests[$test.Name]=$test
    }
    if($tests.Count -lt 149) { throw 'The complete frozen suite must contain at least 149 actual passing groups.' }
    $sourceCount=Assert-SourceInputs @($proof.BuildTestInputs)
    . (Join-Path $PSScriptRoot 'VerifiedRuntimeInventory.ps1')
    $runtimeCount=Assert-VerifiedRuntimeInventory -PayloadRoot $BuildRoot -Entries @($proof.DistributionFiles) -IncludeModules $true
    $manifest=Get-Content -LiteralPath (Join-Path $BuildRoot 'build-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if($manifest.HybridArchitecture.Included -isnot [bool] -or -not $manifest.HybridArchitecture.Included) { throw 'Ten-round mixed verification requires the actual hybrid build.' }
    $nativePath=Resolve-OwnedRelative $BuildRoot 'modules\Hybrid\HvxHybridCore.dll'
    $nativeSha=$manifest.HybridArchitecture.NativeSha256
    if($nativeSha -isnot [string] -or $nativeSha -notmatch '^[A-Fa-f0-9]{64}$' -or
       (Get-FileHash -LiteralPath $nativePath -Algorithm SHA256).Hash -cne $nativeSha) { throw 'The native performance input must match the verified hybrid manifest.' }
    foreach($case in @($plans | ForEach-Object {$_.Tests})) {
        if(-not $proofTests.ContainsKey($case.Name) -or $proofTests[$case.Name].SHA256 -isnot [string] -or $proofTests[$case.Name].SHA256 -notmatch '^[A-Fa-f0-9]{64}$') { throw ('Scoped test is not authenticated by the complete proof: '+$case.Name) }
        $exe=Resolve-OwnedRelative $BuildRoot ($case.Name+'.exe')
        $actual=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
        if($actual -cne $proofTests[$case.Name].SHA256) { throw ('Scoped test executable changed: '+$case.Name) }
        $testHashes[$case.Name]=$actual
    }
    $evidence.VerificationSHA256=$proofSha
    $evidence.SourceInputCount=$sourceCount
    $evidence.RuntimeFileCount=$runtimeCount
    $roundResults.Add([ordered]@{Number=1;Focus='Complete frozen-source suite';Status='PASS';ProofPath=$VerificationPath;ProofSHA256=$proofSha;TestCount=$tests.Count;Tests=$tests})
    Save-Evidence
    Write-Output ('ROUND_1_COMPLETE_PASS='+$tests.Count)
    foreach($plan in $plans) {
        $roundRoot=Join-Path $evidenceRoot ('round-'+$plan.Number)
        New-Item -ItemType Directory -Path $roundRoot | Out-Null
        $results=New-Object 'System.Collections.Generic.List[object]'
        $round=[ordered]@{Number=$plan.Number;Focus=$plan.Focus;Status='RUNNING';Tests=@();TestCount=0}
        $roundResults.Add($round)
        foreach($case in $plan.Tests) {
            $exe=Resolve-OwnedRelative $BuildRoot ($case.Name+'.exe')
            $before=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
            if($before -cne $testHashes[$case.Name]) { throw ('Test changed before invocation: '+$case.Name) }
            $testRoot=Join-Path $roundRoot $case.Name
            # The native benchmark owns a CreateNew proof directory and rejects
            # an existing destination. Other fixtures accept an existing base.
            if($case.Mode -ne 'NativePerf') { New-Item -ItemType Directory -Path $testRoot | Out-Null }
            [string[]]$arguments=@(switch($case.Mode) {
                'BuildQa' {@($BuildRoot,$testRoot)}
                'Qa' {@($testRoot)}
                'ModulesQa' {@((Join-Path $BuildRoot 'modules'),$testRoot)}
                'Build' {@($BuildRoot)}
                'NativePerf' {@($nativePath,$nativeSha,$testRoot)}
                'None' {@()}
                default {throw 'Unknown scoped-test argument contract.'}
            })
            $argumentText=(@($arguments | ForEach-Object {
                if($_ -match '["\r\n]' -or $_.EndsWith('\')) { throw 'Unsafe scoped-test argument.' }
                '"'+$_+'"'
            }) -join ' ')
            Write-Output ('ROUND_'+$plan.Number+'_START='+$case.Name)
            $process=$null; $output=@(); $code=-1; $clock=[Diagnostics.Stopwatch]::StartNew()
            try {
                $start=New-Object Diagnostics.ProcessStartInfo
                $start.FileName=$exe;$start.Arguments=$argumentText;$start.WorkingDirectory=$projectRoot
                $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
                $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
                $process=[Diagnostics.Process]::Start($start)
                if($null -eq $process) { throw 'Scoped test process did not start.' }
                $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
                while(-not $process.WaitForExit(500)) {
                    if($clock.ElapsedMilliseconds -gt 180000) {
                        try{$process.Kill();$null=$process.WaitForExit(5000)}catch{}
                        throw ('Scoped test timed out: '+$case.Name)
                    }
                }
                $code=$process.ExitCode
                $output=@(($stdout.GetAwaiter().GetResult()+[Environment]::NewLine+$stderr.GetAwaiter().GetResult()) -split '\r?\n' | Where-Object {$_.Length -gt 0})
            } catch {
                $code=-1
                $output+=('EXECUTION_ERROR='+$_.Exception.Message)
            } finally {
                if($null -ne $process){$process.Dispose()}
            }
            $after=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
            if($after -cne $before) {$code=-1;$output+='Test executable changed during invocation.'}
            $result=[ordered]@{Name=$case.Name;ExitCode=$code;SHA256=$before;Arguments=@($arguments);ElapsedMilliseconds=$clock.ElapsedMilliseconds;Output=$output;HardwareAccessed=$false}
            $results.Add($result);$round.Tests=$results.ToArray();$round.TestCount=$results.Count
            $evidence.ScopedTestRuns++
            if($code -ne 0) {$round.Status='FAIL';Save-Evidence;throw ('Round '+$plan.Number+' failed: '+$case.Name)}
            Save-Evidence
            $output | Write-Output
        }
        $round.Status='PASS';Save-Evidence
        Write-Output ('ROUND_'+$plan.Number+'_PASS='+$results.Count)
    }
    if((Get-FileHash -LiteralPath $VerificationPath -Algorithm SHA256).Hash -cne $proofSha) { throw 'The complete proof changed during scoped verification.' }
    $null=Assert-SourceInputs @($proof.BuildTestInputs)
    $null=Assert-VerifiedRuntimeInventory -PayloadRoot $BuildRoot -Entries @($proof.DistributionFiles) -IncludeModules $true
    foreach($name in $testHashes.Keys) {
        if((Get-FileHash -LiteralPath (Resolve-OwnedRelative $BuildRoot ($name+'.exe')) -Algorithm SHA256).Hash -cne $testHashes[$name]) { throw ('Scoped test changed before final commit: '+$name) }
    }
    if($roundResults.Count -ne 10 -or @($roundResults | Where-Object {$_.Status -cne 'PASS'}).Count -ne 0 -or $evidence.ScopedTestRuns -ne 48) { throw 'The promised ten actual rounds are incomplete.' }
    $evidence.SourceInputsUnchanged=$true;$evidence.RuntimeUnchanged=$true;$evidence.TestBinariesUnchanged=$true
    $evidence.Status='PASS';$evidence.CompletedUtc=[DateTime]::UtcNow.ToString('o');Save-Evidence
    Write-Output ('TEN_ROUNDS_VERIFICATION='+$evidencePath)
    Write-Output 'TEN_ROUNDS_PASS=10; FULL_SUITE_RUNS=1; SCOPED_ROUNDS=9; SCOPED_TEST_RUNS=48; HARDWARE_ACCESSED=False; PHYSICAL_ACCEPTANCE=False'
} catch {
    $evidence.Status='FAIL';$evidence.Failure=$_.Exception.Message;$evidence.CompletedUtc=[DateTime]::UtcNow.ToString('o');Save-Evidence
    Write-Output ('TEN_ROUNDS_FAILED_EVIDENCE='+$evidencePath)
    throw
}
