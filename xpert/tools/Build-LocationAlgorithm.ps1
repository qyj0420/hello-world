param([Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$HybridPinFile='',[switch]$ProtectedRelease)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Location algorithm output must remain beneath build.'}
$walk=$output
while($walk.Length -ge $project.Length){
    if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked output is prohibited.'}
    $walk=[IO.Path]::GetDirectoryName($walk)
}
$verified=& (Join-Path $PSScriptRoot 'Test-LocationCandidate.ps1')
$source=Join-Path $verified.Root 'AB'
$files=@(Get-ChildItem -LiteralPath (Join-Path $source 'source') -Filter '*.cs' -File | Sort-Object Name | ForEach-Object FullName)
$overlay=Join-Path $project 'modules-src\LocationAlgorithmOverlay\RecordedPhaseV2'
$overlayNames=@('Baseline240.cs','AuditedWaveformReader.cs','AstraSignal.cs','AstraAnalysisPhaseQuadrants.cs','AstraProjectInput.cs','AstraSixthProjectInput.cs','AstraManualReview.cs','AstraPeakTimingOutput.cs','AstraProjectRetainedOutput.cs','AstraSixthUnpairedOutput.cs','EighthMetadata.cs','EighthProjectInput.cs','ReviewedOutputPolicy.cs','AstraAttenuationMeasurement.cs','AstraSeventhCalibrationCapabilities.cs')
$foundOverlay=@(Get-ChildItem -LiteralPath $overlay -Filter '*.cs' -File | ForEach-Object Name)
if(@(Compare-Object ($overlayNames|Sort-Object) ($foundOverlay|Sort-Object)).Count -ne 0){throw 'Recorded phase overlay must have exactly the reviewed source set.'}
$overlayReceipt=@()
for($fileIndex=0;$fileIndex -lt $files.Count;$fileIndex++){
    $baseFile=$files[$fileIndex];$baseName=[IO.Path]::GetFileName($baseFile)
    if($overlayNames -contains $baseName){
        $overlayFile=Join-Path $overlay $baseName
        $overlayReceipt += [ordered]@{File=$baseName;FrozenSource=$baseFile;FrozenSHA256=(Get-FileHash -LiteralPath $baseFile).Hash;OverlaySource=$overlayFile;OverlaySHA256=(Get-FileHash -LiteralPath $overlayFile).Hash}
        $files[$fileIndex]=$overlayFile
    }
}
if($overlayReceipt.Count -ne $overlayNames.Count){throw 'Recorded phase overlay did not replace every reviewed base file.'}
$files += Join-Path $project 'src\LocationCycleRecord.cs'
$files += Join-Path $project 'src\CalibrationPacketIdentityContract.cs'
if($files.Count -ne 124){throw 'Location algorithm source set is incomplete.'}
$runtime=@(Get-ChildItem -LiteralPath (Join-Path $source 'runtime') -Filter '*.dll' -File | Sort-Object Name)
if($runtime.Count -ne 3){throw 'Expected three preserved private legacy runtime dependencies.'}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$dll=Join-Path $output 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'
$arguments=@('/nologo','/noconfig','/langversion:latest','/optimize+','/deterministic+','/debug-','/target:library','/platform:x64',
    '/r:System.dll','/r:System.Core.dll','/r:System.Numerics.dll','/r:System.Web.Extensions.dll','/r:System.Xml.dll',
    '/r:System.Xml.Linq.dll','/r:System.Runtime.Serialization.dll','/r:Microsoft.CSharp.dll',('/out:'+$dll))
foreach($item in $runtime){$arguments+=('/resource:'+$item.FullName+',Task17.Runtime.'+$item.Name)}
if (-not [String]::IsNullOrWhiteSpace($HybridPinFile)) {
    if (-not (Test-Path -LiteralPath $HybridPinFile -PathType Leaf)) { throw 'Hybrid native pin is missing.' }
    $files += Join-Path $project 'src\HybridPhaseCore.cs'
    $arguments += @('/define:HYBRID_CORE',('/resource:'+$HybridPinFile+',HVX.HybridNative.Pin'))
}
& (Join-Path $PSScriptRoot 'compiler\csc.exe') @arguments @files
if($LASTEXITCODE -ne 0){throw 'Location algorithm compilation failed.'}
$hybridCompiled=-not [String]::IsNullOrWhiteSpace($HybridPinFile)
$sourceManifest=[ordered]@{Schema='HVX.LocationRecordedPhaseOverlay/2';Scope='Reviewed local recorded-phase, validated CAL packet-identity/timing-separation, and conditional immutable hybrid session/candidate-cache overlays; frozen AB source bytes untouched; not an upstream implementation claim';CalibrationPacketContract='HVX.CalibrationPacketIdentity/1';LegacyWithoutSidecar='Original behavior retained';QualityGates='Original separation/clipping/quiet/SNR/correlation gates retained';MethodB='Original combined-packet rejection retained';Files=$overlayReceipt;SharedPhaseSourceSHA256=(Get-FileHash -LiteralPath (Join-Path $project 'src\LocationCycleRecord.cs')).Hash;SharedPacketContractSourceSHA256=(Get-FileHash -LiteralPath (Join-Path $project 'src\CalibrationPacketIdentityContract.cs')).Hash;OutputSHA256=(Get-FileHash -LiteralPath $dll).Hash
    HybridHotPath=[ordered]@{Compiled=$hybridCompiled;MaximumCachedCandidates=4096;Scope='Immutable recorded-cycle snapshot per analysis, shared read-only with manual review; candidate-only native batch cache; no waveform/quality/timing rewrite';OverlayFiles=@('AstraAnalysisPhaseQuadrants.cs','AstraSignal.cs','AstraManualReview.cs','AstraProjectRetainedOutput.cs')}
    CompilationSources=@($files|Sort-Object|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
    RuntimeResources=@($runtime|ForEach-Object{[ordered]@{Name=$_.Name;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
}
if($hybridCompiled){$sourceManifest.SharedHybridSourceSHA256=(Get-FileHash -LiteralPath (Join-Path $project 'src\HybridPhaseCore.cs')).Hash;$sourceManifest.HybridPinSHA256=(Get-FileHash -LiteralPath $HybridPinFile).Hash}
$sourceManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'recorded-phase-overlay-source-manifest.json') -Encoding UTF8
$identity=[Reflection.AssemblyName]::GetAssemblyName($dll)
if($identity.Name -ne 'fc40b1d6ae7648e9b4abf7b9.V1.0.17'){throw 'Unexpected algorithm assembly identity.'}
if([version][Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion -ne [version]'3.0.22.0'){throw 'Unexpected frozen candidate file version.'}
Write-Output ('LOCATION_ALGORITHM='+$dll)
Write-Output ('LOCATION_ALGORITHM_SHA256='+(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash)
if($ProtectedRelease){
    $protected=& (Join-Path $PSScriptRoot 'Protect-LocationAlgorithm.ps1') -InputDll $dll -OutputDirectory (Join-Path $output 'protection')
    Copy-Item -LiteralPath $dll -Destination (Join-Path $output 'protection\unprotected-input.dll')
    Copy-Item -LiteralPath $protected -Destination $dll -Force
    # QA-only private mapping authority: never shipped as a product dependency.
    $sourceManifest.ProtectedOutputSHA256=(Get-FileHash -LiteralPath $dll).Hash
    $sourceManifest.ProtectionMappingSHA256=(Get-FileHash -LiteralPath (Join-Path $output 'protection\mapping.xml')).Hash
    $sourceManifest.ProtectionReceiptSHA256=(Get-FileHash -LiteralPath (Join-Path $output 'protection\receipt.json')).Hash
    $sourceManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'recorded-phase-overlay-source-manifest.json') -Encoding UTF8
    Write-Output ('PROTECTED_LOCATION_SHA256='+(Get-FileHash -LiteralPath $dll).Hash)
}
