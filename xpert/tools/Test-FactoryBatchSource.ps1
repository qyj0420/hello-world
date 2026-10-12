param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceRoot = Join-Path $project 'modules-src\FactoryWorker'
$pinnedManifest = '27BF37B8322A7D255F3B87FC3E110839B463F75BE4A84C25D8EF762CFD138B7B'
$pinnedExe = '181A121A5AAE4E8F10D3BDFB99B3BBF16AED325267DB940E496F80831505FD41'
function Get-CheckedSource([string]$Relative) {
    if ([String]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or
        $Relative.Contains(':') -or $Relative.Split([char[]]'\/') -contains '..') { throw 'Invalid batch source path.' }
    $path = [IO.Path]::GetFullPath((Join-Path $sourceRoot $Relative))
    if (-not $path.StartsWith($sourceRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Batch source escaped its root.' }
    for ($walk=$path; -not [String]::IsNullOrWhiteSpace($walk); $walk=[IO.Path]::GetDirectoryName($walk)) {
        if ((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Linked batch source paths are prohibited.'
        }
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Missing batch source: '+$Relative) }
    return $path
}
function Assert-SourceHash([string]$Relative,[string]$ExpectedHash) {
    $path=Get-CheckedSource $Relative
    if ($ExpectedHash -cnotmatch '^[A-F0-9]{64}$' -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $ExpectedHash) {
        throw ('Batch source hash mismatch: '+$Relative)
    }
    return $path
}
$upstreamPath=Assert-SourceHash 'v30-batch-upstream-manifest.json' $pinnedManifest
$upstream=Get-Content -LiteralPath $upstreamPath -Raw -Encoding UTF8|ConvertFrom-Json
$provenancePath=Get-CheckedSource 'v30-batch-integration-provenance.json'
$provenance=Get-Content -LiteralPath $provenancePath -Raw -Encoding UTF8|ConvertFrom-Json
if ($provenance.Schema -cne 'HVX.Pressure.BatchSourceImport/1' -or $provenance.SourceVersion -cne '1.0.30.9' -or
    $provenance.SourceManifestSHA256 -cne $pinnedManifest -or $provenance.SourceExecutableSHA256 -cne $pinnedExe -or
    $provenance.HardwareAccessed -isnot [bool] -or $provenance.HardwareAccessed -ne $false -or
    $provenance.PhysicalAcceptance -isnot [bool] -or $provenance.PhysicalAcceptance -ne $false -or
    $upstream.Version -cne '1.0.30.9' -or $upstream.SourceUnchangedDuringBuildAndTests -ne $true) {
    throw 'Batch source identity or offline evidence boundary is invalid.'
}
$expected=@($upstream.Sources|Where-Object {[IO.Path]::GetExtension($_.RelativePath) -ieq '.cs'})
$files=@($provenance.Files)
$adaptation=$null
$adaptationHash=$null
$adaptationName='v30-batch-local-adaptation-20261008.json'
if(Test-Path -LiteralPath (Join-Path $sourceRoot $adaptationName)){
    # Replaced only after the exact local differences and fake tests have been
    # reviewed. A changed source/provenance file alone cannot authorize a patch.
    $adaptationHash='26A5D98F3C81BD37E4D6342CFA467AD9A60EE9ACAC10BA6685E433F70B2F8E1D'
    $adaptationPath=Assert-SourceHash $adaptationName $adaptationHash
    $adaptation=Get-Content -LiteralPath $adaptationPath -Raw -Encoding UTF8|ConvertFrom-Json
    if($adaptation.Schema -cne 'HVX.Pressure.LocalAdaptation/1' -or
       $adaptation.SourceManifestSHA256 -cne $pinnedManifest -or $adaptation.SourceExecutableSHA256 -cne $pinnedExe -or
       $adaptation.HardwareAccessed -isnot [bool] -or $adaptation.HardwareAccessed -ne $false -or
       $adaptation.PhysicalAcceptance -isnot [bool] -or $adaptation.PhysicalAcceptance -ne $false){throw 'Invalid reviewed local adaptation identity.'}
    $allowedPatches=@('FactoryEmergencyMonitor.cs','FactoryModels.cs','FactoryRunner.cs','NativeFactoryBackend.cs','Program.cs','FactoryNativeSessionPolicy.cs','FactoryRangePhase.cs','FactoryResidentDeviceServer.cs','FactoryResidentDevices.cs','FactoryResidentDeviceServerTests.cs','FactoryZeroInputTests.cs','FactoryZeroInputRunner.cs','Imported/HardwareCapture.cs','FactoryBatchAuthorization.cs','FactoryBatchAuthorizationTests.cs','FactoryPressureSequence.cs','Imported/BenchModels.cs','FactoryArtFeedback.cs','FactoryFaultResidualTests.cs','FactoryFinalAi4Observation.cs','FactoryStartupFeedbackTests.cs','NativeFactoryBackend.FinalAi4.cs','FactoryFaultResidual.cs','FactoryResidualStageTests.cs','FactoryFeedbackWindow.cs','FactoryTargetHold.cs','FactoryAiTrace.cs','FactoryManualEngine.cs','FactoryStartConfirmation.cs','FactoryNativeSessionTests.cs')
    $allowedPatches+='FactorySessionFault.cs'
    # Reviewed r42 fake-test input only; no runtime protection-policy change.
    $allowedPatches+='FactoryR30Tests.cs'
    $allowedPatches+='FactoryPressureSequenceTests.cs'
    $allowedPatches+='FactoryLocationCycleTests.cs'
    # Latest explicit 1000 ms optical/desktop post-processing and equivalent
    # FIR identity fast-path review. Frozen source hashes are never replaced.
    $allowedPatches+=@('FactoryFinalAi4WorkflowTests.cs','FactoryMainForm.cs','FactoryMainForm.InteractionTests.cs','FactoryManualTests.cs','FactoryTests.cs','FactoryZeroTriggerRunner.cs','FactoryZeroTriggerTests.cs','Imported/Unified100/PdLinearHighPass.cs')
    # Explicit reviewed diagnostic-default change; physical attestations and
    # real raw/timebase/phase/cycle validity gates remain independent.
    $allowedPatches+=@('FactoryAnalysis.cs','FactoryAnalysisTests.cs','FactoryPressureStartPrompt.cs','FactoryR29Tests.cs','FactoryMainForm.IntegratedSmoke.cs')
    # Availability-only fake buffer updates, not changed historical assertions.
    $allowedPatches+=@('FactoryFeedbackWindowTests.cs','FactoryBufferEmergencyTests.cs')
    $patchNames=@($adaptation.Patches|ForEach-Object{[string]$_.Path}|Sort-Object)
    if((ConvertTo-Json -InputObject $patchNames -Compress) -cne (ConvertTo-Json -InputObject @($allowedPatches|Sort-Object) -Compress)){
        throw 'Local adaptation must contain exactly the explicitly reviewed local differences.'
    }
    $allowedNew=@('FactoryEmergencyReadPolicy.cs','FactoryEmergencyTimingContractTests.cs','FactoryBufferEmergencyCurrentTests.cs','UnifiedFinePressureRamp.cs','UnifiedWorkerLaunchEvidence.cs','NativeFactoryBackend.ZeroInput.cs','FactoryResidentZeroInputTests.cs','FactoryInputStopEvidence.cs','FactoryArtNegativeFeedbackTests.cs','FactoryFailedRoundReleaseEvidence.cs','FactorySelectedFeedbackTests.cs','FactoryFeedbackConfigurationTests.cs','FactoryNoResidualPressureTests.cs','FactoryEmergencySnapshotPublisher.cs','FactoryEmergencyDisplayTests.cs','FactoryPressurePrecisionTests.cs')
    $allowedNew+='FactoryProgressJournal.cs'
    $allowedNew+='FactoryHealthyRoundStopEvidence.cs'
    $allowedNew+='FactoryEmergencyPendingTests.cs'
    $allowedNew+='AdjacentHardwareRangePolicy.cs'
    $allowedNew+='FactoryEmergencyRoundStopEvidence.cs'
    $allowedNew+='FactoryEmergencyRetainedOfflineHarness.cs'
    $allowedNew+='NativeFactoryBackend.Feedback.cs'
    $allowedNew+='FactoryFeedbackAvailabilityTests.cs'
    $newNames=@($adaptation.NewFiles|ForEach-Object{[string]$_.Path}|Sort-Object)
    if((ConvertTo-Json -InputObject $newNames -Compress) -cne (ConvertTo-Json -InputObject @($allowedNew|Sort-Object) -Compress)){
        throw 'Unexpected local adaptation source set.'
    }
    foreach($entry in $adaptation.NewFiles){$null=Assert-SourceHash $entry.Path $entry.SHA256}
    $entryNames=@($adaptation.ReviewedEntryAdapters|ForEach-Object{[string]$_.Path}|Sort-Object)
    if((ConvertTo-Json -InputObject $entryNames -Compress) -cne
       (ConvertTo-Json -InputObject @('UnifiedPressureBridge.cs','UnifiedWorkerEntry.cs') -Compress)){
        throw 'Reviewed local entry adapters must match the exact two entry sources.'
    }
    foreach($entry in $adaptation.ReviewedEntryAdapters){$null=Assert-SourceHash $entry.Path $entry.SHA256}
}
$expectedNames=@($expected|ForEach-Object {([string]$_.RelativePath).Replace('\','/')}|Sort-Object)
$actualNames=@($files|ForEach-Object {([string]$_.Path).Replace('\','/')}|Sort-Object)
if ($expectedNames.Count -ne 198 -or @($actualNames|Group-Object|Where-Object Count -ne 1).Count -ne 0 -or
    (ConvertTo-Json -InputObject $expectedNames -Compress) -cne (ConvertTo-Json -InputObject $actualNames -Compress)) {
    throw 'Batch import must contain the complete, unique frozen C# source set.'
}
foreach ($entry in $files) {
    $match=@($expected|Where-Object {([string]$_.RelativePath).Replace('\','/') -ceq ([string]$entry.Path).Replace('\','/')})
    if ($match.Count -ne 1 -or $entry.ExactUpstream -isnot [bool] -or $entry.FrozenUpstreamSHA256 -cne $match[0].SHA256) {
        throw ('Batch import has no exact upstream identity: '+$entry.Path)
    }
    $patch=@();if($null -ne $adaptation){$patch=@($adaptation.Patches|Where-Object Path -CEQ $entry.Path)}
    if($patch.Count -eq 0){
        if(-not $entry.ExactUpstream -or $entry.IntegratedSHA256 -cne $match[0].SHA256){throw ('Unreviewed core change: '+$entry.Path)}
    }else{
        if($patch.Count -ne 1 -or $entry.ExactUpstream -or $patch[0].FrozenUpstreamSHA256 -cne $match[0].SHA256 -or
           $patch[0].IntegratedSHA256 -cne $entry.IntegratedSHA256 -or $entry.IntegratedSHA256 -ceq $match[0].SHA256 -or
           [String]::IsNullOrWhiteSpace([string]$patch[0].Reason)){throw ('Local adaptation evidence differs: '+$entry.Path)}
    }
    $null=Assert-SourceHash $entry.Path $entry.IntegratedSHA256
}
$adapters=@('UnifiedPressureBridge.cs','UnifiedWorkerEntry.cs')
if($null -ne $adaptation){$adapters+=@($adaptation.NewFiles|ForEach-Object{[string]$_.Path})}
if ((ConvertTo-Json -InputObject @($provenance.LocalAdapters|Sort-Object) -Compress) -cne
    (ConvertTo-Json -InputObject @($adapters|Sort-Object) -Compress)) { throw 'Unreviewed local batch adapter list.' }
$compiled=@(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter '*.cs'|ForEach-Object {$_.FullName.Substring($sourceRoot.Length+1).Replace('\','/')}|Sort-Object)
if ((ConvertTo-Json -InputObject $compiled -Compress) -cne
    (ConvertTo-Json -InputObject @(@($expectedNames)+@($adapters)|Sort-Object) -Compress)) { throw 'Unreviewed compiled batch sources.' }
# Local files and reviewed differences are never claimed to be exact upstream.
# Their hashes become build evidence and must remain unchanged during that build.
[pscustomobject]@{
    SourceVersion='1.0.30.9'; SourceManifestSHA256=$pinnedManifest; SourceExecutableSHA256=$pinnedExe
    SourceCount=$files.Count; HardwareAccessed=$false; PhysicalAcceptance=$false
    ExactUpstreamSourceCount=@($files|Where-Object ExactUpstream -EQ $true).Count
    LocalAdaptationSHA256=$adaptationHash
    ProvenanceSHA256=(Get-FileHash -LiteralPath $provenancePath -Algorithm SHA256).Hash
    LocalAdapters=@($adapters|ForEach-Object {[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath (Get-CheckedSource $_) -Algorithm SHA256).Hash}})
}
