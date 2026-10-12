param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$SkipModules,
    [switch]$Hybrid,
    [switch]$FrozenInternal,
    [switch]$R27Validation,
    [switch]$V30Validation,
    [switch]$V30BatchValidation,
    [switch]$PublicBetaNoDriver,
    [switch]$PressureStandalone,
    [switch]$DacRebuildFactoryPress,
    [switch]$ProtectedRelease,
    [switch]$MaintenancePassword,
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
# Debug is an unpackaged development preview. Release is never an opt-in
# password-free preview, even when ProtectedRelease was accidentally omitted.
if ($Configuration -eq 'Release' -and (-not $V30BatchValidation -or -not $Hybrid -or -not $ProtectedRelease -or -not $MaintenancePassword -or $SkipModules -or $PressureStandalone)) { throw 'Release requires the complete protected V30 batch maintenance profile; use Debug only for an unpackaged development preview.' }
# Algorithm protection does not enable maintenance access control. Every new
# protected distribution must explicitly request both, before any build I/O.
if ($ProtectedRelease -and -not $MaintenancePassword) { throw 'Protected release requires explicit maintenance password protection.' }
if ($PublicBetaNoDriver -and (-not $V30BatchValidation -or -not $Hybrid -or -not $ProtectedRelease -or -not $MaintenancePassword -or $SkipModules -or $PressureStandalone)) { throw 'No-driver customer profile requires the complete protected maintenance build.' }
if ($PressureStandalone -and (-not $V30BatchValidation -or -not $ProtectedRelease -or -not $Hybrid)) { throw 'Pressure standalone requires the protected V30 batch hybrid profile.' }
if ($MaintenancePassword -and (-not $V30BatchValidation -or -not $ProtectedRelease -or -not $Hybrid -or $SkipModules -or $PressureStandalone)) { throw 'Maintenance access requires the complete protected V30 batch hybrid profile.' }
Set-StrictMode -Version Latest
if (@(@($FrozenInternal,$R27Validation,$V30Validation,$V30BatchValidation)|Where-Object {$_}).Count -gt 1) { throw 'Select exactly one isolated release profile.' }
if ($R27Validation -and (-not $Hybrid -or -not $ProtectedRelease -or $SkipModules)) { throw 'R27 validation requires the complete protected hybrid build.' }
if ($V30Validation -and (-not $Hybrid -or -not $ProtectedRelease -or $SkipModules)) { throw 'V30 validation requires the complete protected hybrid build.' }
if ($V30BatchValidation -and (-not $Hybrid -or -not $ProtectedRelease -or $SkipModules)) { throw 'V30 batch validation requires the complete protected hybrid build.' }

$root = [IO.Path]::GetFullPath($PSScriptRoot)
$batchSource = $null
if (Test-Path -LiteralPath (Join-Path $root 'modules-src\FactoryWorker\v30-batch-integration-provenance.json')) {
    if ($FrozenInternal -or $R27Validation -or $V30Validation) { throw 'The imported V1.0.30.9 batch runtime cannot be packaged under an older frozen release profile.' }
    if (-not $SkipModules) { $batchSource = & (Join-Path $root 'tools\Test-FactoryBatchSource.ps1') }
}
if ($V30BatchValidation -and $null -eq $batchSource) { throw 'The reviewed V1.0.30.9 batch source is missing.' }
# V30 remains pending; never label the existing pressure runtime as V30 merely
# by selecting a new desktop profile. This gate precedes all output/build work.
if ($V30Validation) {
    $v30SourceRoot = Join-Path $root 'modules-src\FactoryWorker'
    $v30ProvenancePath = Join-Path $v30SourceRoot 'v30-integration-provenance.json'
    if (-not (Test-Path -LiteralPath $v30ProvenancePath -PathType Leaf)) {
        throw 'V30 validation is unavailable: frozen V30 integration provenance is missing. No build was started.'
    }
    function Assert-V30BuildInput([string]$Relative,[string]$ExpectedHash) {
        if ([String]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':') -or
            $ExpectedHash -cnotmatch '^[A-F0-9]{64}$') { throw 'Invalid V30 source entry.' }
        $path = [IO.Path]::GetFullPath((Join-Path $v30SourceRoot $Relative))
        if (-not $path.StartsWith($v30SourceRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'V30 source path escaped its root.' }
        for ($walk=$path;-not [String]::IsNullOrWhiteSpace($walk);$walk=[IO.Path]::GetDirectoryName($walk)) {
            if ((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked V30 source paths are prohibited.' }
        }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $ExpectedHash) {
            throw ('Frozen V30 source mismatch: '+$Relative)
        }
        return $path
    }
    $v30Provenance = Get-Content -LiteralPath $v30ProvenancePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($v30Provenance.Schema -cne 'HVX.Pressure.V30.Import/1' -or $v30Provenance.SourceVersion -cne '1.0.30.0' -or
        $v30Provenance.SourceExecutableSHA256 -cne 'DD1481226E15AA47611D6DB798B14A7908483E13718406A387B4A90041880114' -or
        $v30Provenance.HardwareAccessed -isnot [bool] -or $v30Provenance.HardwareAccessed -ne $false -or
        $v30Provenance.PhysicalAcceptance -isnot [bool] -or $v30Provenance.PhysicalAcceptance -ne $false) { throw 'V30 source identity or evidence boundary is invalid.' }
    $null = Assert-V30BuildInput 'v30-upstream-manifest.json' 'B0E86C0C3B87127511E95B6AAC05CC7C898B90D937FEE0BF93056E49A9D0BE3A'
    $v30ProofPath = Join-Path $v30SourceRoot 'v30-source-correspondence.json'
    $null = Assert-V30BuildInput 'v30-source-correspondence.json' (Get-FileHash -LiteralPath $v30ProofPath -Algorithm SHA256).Hash
    $v30Proof = Get-Content -LiteralPath $v30ProofPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($v30Proof.Schema -cne 'HVX.Factory.SourceCorrespondence/1' -or $v30Proof.Version -cne '1.0.30.0' -or
        $v30Proof.ReleasedBinarySHA256 -cne $v30Provenance.SourceExecutableSHA256 -or
        $v30Proof.ManifestSHA256 -cne 'B0E86C0C3B87127511E95B6AAC05CC7C898B90D937FEE0BF93056E49A9D0BE3A' -or
        $v30Proof.SourceUnchanged -isnot [bool] -or $v30Proof.SourceUnchanged -ne $true -or
        $v30Proof.CodeAndResourcesByteIdentical -isnot [bool] -or $v30Proof.CodeAndResourcesByteIdentical -ne $true) { throw 'V30 source-to-release correspondence is invalid.' }
    $v30Files = @($v30Provenance.Files)
    $v30FileNames = @($v30Files | ForEach-Object { ([string]$_.Path).Replace('\','/') } | Sort-Object)
    $v30CompiledNames = @($v30Provenance.CompiledFiles | ForEach-Object { ([string]$_).Replace('\','/') } | Sort-Object)
    if ($v30Files.Count -eq 0 -or @($v30FileNames | Group-Object | Where-Object Count -ne 1).Count -or
        @($v30CompiledNames | Group-Object | Where-Object Count -ne 1).Count -or
        (ConvertTo-Json -InputObject $v30FileNames -Compress) -cne (ConvertTo-Json -InputObject $v30CompiledNames -Compress)) {
        throw 'V30 compiled source set must match the complete, unique reviewed source list.'
    }
    foreach ($entry in $v30Files) {
        if ([IO.Path]::GetExtension([string]$entry.Path) -ine '.cs') { throw 'V30 compilation entries must be source files.' }
        $null = Assert-V30BuildInput $entry.Path $entry.IntegratedSHA256
        if ($entry.ExactUpstream -isnot [bool] -or ($entry.ExactUpstream -and $entry.IntegratedSHA256 -cne $entry.FrozenUpstreamSHA256)) { throw 'V30 adaptation evidence is invalid.' }
        $v30Matches = @($v30Proof.SourceFiles | Where-Object { ([string]$_.Path).Replace('\','/') -ceq ([string]$entry.Path).Replace('\','/') -and $_.SHA256 -ceq $entry.FrozenUpstreamSHA256 })
        if ($v30Matches.Count -ne 1) { throw ('V30 source lacks a unique upstream match: '+$entry.Path) }
    }
}
$compiler = Join-Path $root 'tools\compiler\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw "Missing repository compiler: $compiler" }
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File | Sort-Object FullName | ForEach-Object FullName)
if ($sources.Count -lt 8) { throw 'Unified desktop source set is incomplete.' }
$output = if ([String]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $root ('build\' + $Configuration) } else { [IO.Path]::GetFullPath($OutputDirectory) }
if (-not $output.StartsWith(($root.TrimEnd('\') + '\build\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Build output must be a directory beneath this project build directory.'
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
# Stage the complete verified runtime before compiling the shell. The embedded
# payload must include algorithms and report assets, not just UI assemblies.
$embeddedPayload = $null
$hybridBuild = $null
if ($Hybrid) {
    if ($SkipModules) { throw 'Hybrid builds require complete module staging; SkipModules is prohibited.' }
    $hybridBuild = & (Join-Path $root 'tools\Build-HybridNative.ps1') -OutputDirectory (Join-Path $output 'hybrid-build')
}
if (-not $SkipModules) {
    $stageArgs = @{ OutputRoot = $output; ProtectedRelease = [bool]$ProtectedRelease; MaintenancePassword = [bool]$MaintenancePassword; Configuration = $Configuration }
    if ($Hybrid) { $stageArgs.HybridNativeDirectory = $hybridBuild.Directory; $stageArgs.HybridPinFile = $hybridBuild.PinFile }
    & (Join-Path $root 'Stage-Modules.ps1') @stageArgs
    # FactoryPressMerge path: XpertPressBridge → OpenForXpert / RunRoundAsync / PollEmergencyStage.
    # Always stage modules\FactoryPress from factory-press\dist\net48 (build first if missing).
    $factoryPressDist = Join-Path $root 'factory-press\dist\net48'
    $factoryBridge = Join-Path $factoryPressDist 'Xiongxin.Owts.XpertPressBridge.dll'
    if (-not (Test-Path -LiteralPath $factoryBridge -PathType Leaf)) {
        & (Join-Path $root 'tools\Build-FactoryPressMerge.ps1') -Configuration $Configuration
    }
    if (-not (Test-Path -LiteralPath $factoryBridge -PathType Leaf)) {
        throw "Missing factory-press net48 bridge. Run: powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-FactoryPressMerge.ps1 -Configuration $Configuration"
    }
    & (Join-Path $root 'tools\Stage-DefectModels.ps1') -OutputDirectory (Join-Path $output 'model-payload')
    $resourceDirectory = Join-Path $output ('embedded-resources-' + [Guid]::NewGuid().ToString('N'))
    $embeddedPayload = & (Join-Path $root 'tools\Build-EmbeddedPayload.ps1') -ModulesRoot (Join-Path $output 'modules') -OutputDirectory $resourceDirectory
}
$exe = Join-Path $output 'Xiongxin.OWTS.Desktop.exe'
$args = @(
    '/nologo','/langversion:7.3','/deterministic+','/target:winexe','/platform:x64',
    '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',
    '/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll',
    '/r:System.Security.dll',
    # User-supplied neutral shortcut artwork; OEM branding stays opt-in.
    ('/win32icon:' + (Join-Path $root 'assets\shortcuts\256.ico')),
    ('/resource:' + (Join-Path $root 'assets\shortcuts\256.ico') + ',HVX.NeutralWindow.ico'),
    ('/win32manifest:' + (Join-Path $root 'app.manifest')),
    ('/resource:' + (Join-Path $root 'assets\xiongxin.ico') + ',Brand.ico'),
    ('/resource:' + (Join-Path $root 'assets\xiongxin-original.png') + ',Brand.png'),
    ('/out:' + $exe)
)
if ($Configuration -eq 'Release') { $args += '/optimize+' } else { $args += @('/optimize-','/debug:full') }
if ($null -ne $embeddedPayload) { $args += @($embeddedPayload.ResourceArguments) }
if (-not $SkipModules) { $args += '/resource:'+(Join-Path $output 'research-pins.json')+',HVX.Research.Pins' }
if ($Hybrid) { $args += @('/define:HYBRID_CORE;HYBRID_ROOT',('/resource:' + $hybridBuild.PinFile + ',HVX.HybridNative.Pin')) }
if ($FrozenInternal) { $args += '/define:FROZEN_INTERNAL_20261006' }
if ($R27Validation) { $args += '/define:R27_VALIDATION_20261007' }
if ($V30Validation) { $args += '/define:V30_VALIDATION_20261007' }
if ($V30BatchValidation) { $args += '/define:V30_BATCH_VALIDATION_20261008' }
if ($PublicBetaNoDriver) { $args += '/define:PUBLIC_BETA_NO_DRIVER_20261011;PUBLIC_BETA_NO_DRIVER_V112_20261011' }
if ($PressureStandalone) { $args += '/define:PRESSURE_STANDALONE_20261008' }
if ($DacRebuildFactoryPress) { $args += '/define:DAC_REBUILD_FACTORYPRESS_20261012' }
if ($MaintenancePassword) { $args += '/define:MAINTENANCE_PASSWORD_GUARD' }
$responseFile=Join-Path $output 'desktop-compile.rsp'
# Native runtime resources exceed Windows' command-line limit. The response
# file is build evidence only, never included in the operator distribution.
@(@($args)+@($sources) | ForEach-Object {'"'+$_.Replace('"','\"')+'"'}) | Set-Content -LiteralPath $responseFile -Encoding UTF8
& $compiler ('@'+$responseFile)
if ($LASTEXITCODE -ne 0) { throw 'Unified desktop compilation failed.' }
$hostCompilation=[ordered]@{Schema='HVX.HostCompilation/1';CompilerDefines=@($args|Where-Object { $_.StartsWith('/define:',[StringComparison]::Ordinal) }|ForEach-Object { $_.Substring(8).Split(';') });OutputSHA256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash}
$hostProtection=$null
if($ProtectedRelease){
    $hostProtectionRoot=Join-Path $output ('host-protection-'+[Guid]::NewGuid().ToString('N'))
    $protectedHost=& (Join-Path $root 'tools\Protect-OwnedAlgorithms.ps1') -InputAssembly $exe -OutputDirectory $hostProtectionRoot -Profile HostPure
    $hostReceiptPath=Join-Path $hostProtectionRoot 'receipt.json'
    $hostReceipt=Get-Content -LiteralPath $hostReceiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if($hostReceipt.InputSHA256 -cne (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -or
       $hostReceipt.OutputSHA256 -cne (Get-FileHash -LiteralPath $protectedHost -Algorithm SHA256).Hash -or
       -not $hostReceipt.ExistingMetadataPreserved -or -not $hostReceipt.InputUnchanged){throw 'Host algorithm protection evidence mismatch.'}
    Copy-Item -LiteralPath $protectedHost -Destination $exe -Force
    $hostProtection=[ordered]@{Path='Xiongxin.OWTS.Desktop.exe';Profile='HostPure';InputSHA256=$hostReceipt.InputSHA256;
        OutputSHA256=$hostReceipt.OutputSHA256;ReceiptPath=$hostReceiptPath.Substring($output.Length+1).Replace('\','/');
        ReceiptSHA256=(Get-FileHash -LiteralPath $hostReceiptPath -Algorithm SHA256).Hash}
}
Copy-Item -LiteralPath (Join-Path $root 'App.config') -Destination ($exe + '.config') -Force
$catalogSource = Join-Path $root 'assets\device-asset-catalog.v1.json'
$catalogOutputDirectory = Join-Path $output 'assets'
if (-not (Test-Path -LiteralPath $catalogSource -PathType Leaf)) { throw "Missing device asset catalog: $catalogSource" }
New-Item -ItemType Directory -Path $catalogOutputDirectory -Force | Out-Null
Copy-Item -LiteralPath $catalogSource -Destination (Join-Path $catalogOutputDirectory 'device-asset-catalog.v1.json') -Force
foreach ($brandFile in @('xiongxin.ico','xiongxin-original.png')) {
    Copy-Item -LiteralPath (Join-Path $root ('assets\'+$brandFile)) -Destination (Join-Path $catalogOutputDirectory $brandFile) -Force
}
$shortcutOutput = Join-Path $catalogOutputDirectory 'shortcuts'
New-Item -ItemType Directory -Path $shortcutOutput -Force | Out-Null
foreach ($shortcutFile in @('256.ico','512.ico')) {
    Copy-Item -LiteralPath (Join-Path $root ('assets\shortcuts\'+$shortcutFile)) -Destination (Join-Path $shortcutOutput $shortcutFile) -Force
}
$languageDocs = Join-Path $root 'docs\international'
if (Test-Path -LiteralPath $languageDocs -PathType Container) {
    $languageOutput = Join-Path $output 'docs\international'
    New-Item -ItemType Directory -Path $languageOutput -Force | Out-Null
    Get-ChildItem -LiteralPath $languageDocs -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $languageOutput $_.Name) -Force
    }
}
$referenceSource = Join-Path $root 'assets\reference-simulation\rdfz-jmf-v1.json'
$referenceOutput = Join-Path $catalogOutputDirectory 'reference-simulation'
if (-not (Test-Path -LiteralPath $referenceSource -PathType Leaf)) { throw 'Reference simulation asset is missing.' }
$referenceHash = (Get-FileHash -LiteralPath $referenceSource -Algorithm SHA256).Hash
if ($referenceHash -ne 'D27E8D8BE663D42F5A9D12830A1CD2B0552F22D1B2EBD505C7F45C4DEBDDC0A8') { throw 'Reference simulation asset differs from the reviewed fixture.' }
New-Item -ItemType Directory -Path $referenceOutput -Force | Out-Null
Copy-Item -LiteralPath $referenceSource -Destination (Join-Path $referenceOutput 'rdfz-jmf-v1.json') -Force

$identity = [Reflection.AssemblyName]::GetAssemblyName($exe)
if ($null -ne $batchSource) {
    $batchSourceAfter = & (Join-Path $root 'tools\Test-FactoryBatchSource.ps1')
    if ((ConvertTo-Json -InputObject $batchSource -Depth 5 -Compress) -cne (ConvertTo-Json -InputObject $batchSourceAfter -Depth 5 -Compress)) {
        throw 'Factory batch source or adapters changed during the build; do not publish this output.'
    }
}
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
$manifest = [ordered]@{
    Schema = 'XIONGXIN.UnifiedDesktop.Build.v1'
    BuiltUtc = [DateTime]::UtcNow.ToString('o')
    Configuration = $Configuration
    Architecture = 'x64'
    Framework = '.NET Framework 4.8'
    LabVIEWRequired = $false
    StandaloneBuild = $true
    PressureStandalone = [bool]$PressureStandalone
    ProtectedRelease = [bool]$ProtectedRelease
    MaintenancePasswordEnabled = [bool]$MaintenancePassword
    HostCompilation = $hostCompilation
    ReleaseStatus = 'IntegrationPreview'
    FrozenInternalProfile = [bool]$FrozenInternal
    R27ValidationProfile = [bool]$R27Validation
    V30ValidationProfile = [bool]$V30Validation
    V30BatchValidationProfile = [bool]$V30BatchValidation
    FactoryBatchSource = $batchSource
    ConfigurationProfile = if ($PublicBetaNoDriver) { 'PublicBeta-NoDriver-1.1.2-20261011' } elseif ($PressureStandalone) { 'Pressure-Standalone-20261008' } elseif ($V30BatchValidation) { 'Validation-V30-Batch-20261008' } elseif ($V30Validation) { 'Validation-V30-20261007' } elseif ($R27Validation) { 'Validation-R27-20261007' } elseif ($FrozenInternal) { 'Frozen-20261006-Internal01' } else { 'Preview-1.1.3' }
    HybridArchitecture = [ordered]@{
        Included = [bool]$Hybrid
        Interface = 'Existing C# Windows Forms; unchanged pressure-page controls'
        NativeScope = if ($Hybrid) { 'C++ x64 ABI 1: per-record variable-cycle phase interpolation' } else { 'Not enabled' }
        NativeSha256 = if ($Hybrid) { $hybridBuild.Sha256 } else { $null }
        PythonScope = 'Bundled CPython 3.12.14 / NumPy 2.3.5; C 0.3.0 and D 0.1.1 compiled with Cython 3.1.6; offline review only'
        TrainedNeuralModelIncluded = $false
        Protection = 'Compiled native library with immutable assembly hash pin and restricted loading; not absolute anti-reverse-engineering'
    }
    AlgorithmSourceReplacementComplete = $false
    OwnedHostAlgorithmProtection = $hostProtection
    SourceAlgorithmReplacements = @(
        [ordered]@{ Task = 15; Version = '3.0.0-source-20260930'; Api = 'hvx-conclusion-dotnet-3.0.0-evidence'
            Path = 'modules-src/ConclusionAlgorithm/EvidenceV3/HVX.Task15.Embedded.cs'
            Sha256 = (Get-FileHash -LiteralPath (Join-Path $root 'modules-src\ConclusionAlgorithm\EvidenceV3\HVX.Task15.Embedded.cs') -Algorithm SHA256).Hash
            Consumers = @('DataAnalysis', 'PressureTest', 'ReportGenerator') }
        [ordered]@{ Task = 28; Version = '1.2.1.0'
            Path = 'modules-src/DataAnalysis/vendor/Task28/LocationClusterFilterEngine.cs'
            Sha256 = (Get-FileHash -LiteralPath (Join-Path $root 'modules-src\DataAnalysis\vendor\Task28\LocationClusterFilterEngine.cs') -Algorithm SHA256).Hash
            Consumers = @('DataAnalysis', 'PressureTest') }
    )
    EmbeddedModules = if ($null -eq $embeddedPayload) { $null } else { [ordered]@{
        BundleHash = $embeddedPayload.BundleHash
        FileCount = $embeddedPayload.FileCount
        UniqueResources = $embeddedPayload.UniqueResources
        ExpandedBytes = $embeddedPayload.ExpandedBytes
        ResourceName = 'HVX.Modules.Index.v1'
        Protection = 'Packaging and integrity verification; not encryption or obfuscation'
    } }
    Assembly = $identity.FullName
    FileVersion = $fileVersion
    ExecutableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    DeviceAssetCatalog = [ordered]@{
        Path = 'assets/device-asset-catalog.v1.json'
        Sha256 = (Get-FileHash -LiteralPath $catalogSource -Algorithm SHA256).Hash
    }
    ReferenceSimulation = [ordered]@{
        Path = 'assets/reference-simulation/rdfz-jmf-v1.json'
        Sha256 = $referenceHash
        SimulationOnly = $true
    }
    PressureIntegration = [ordered]@{
        PhysicalAcceptance = $false
        P04HighPermitsNonzeroPressure = $true
        MissingOrExpiredFeedbackBlocksNonzeroPressure = $true
        P04ProductionPublisherConnected = (-not [bool]$SkipModules)
        LocationCycleProductionWriterConnected = (-not [bool]$SkipModules)
        AutomaticPressureProductionSchedulerConnected = (-not [bool]$SkipModules)
        IntegratedSource = if ($null -ne $batchSource) { '出厂测试程序_V1.0.30.9_同电压批次连续测试版' } elseif(Test-Path -LiteralPath (Join-Path $root 'modules-src/FactoryWorker/v30-integration-provenance.json')) { '出厂测试程序_V1.0.30' } else { 'FactoryIntegrated_20261007_R27' }
        EachRoundOperatorConfirmationRequired = ($null -eq $batchSource)
        AutomaticBatchScope = if ($null -ne $batchSource) { 'Current same-voltage plan row only; a new row/voltage or fault requires a new explicit start.' } else { 'Legacy profile' }
        AutomaticPressurePhysicalAcceptance = $false
        NextRangePhysicalAcceptance = $false
        DefaultLocationCycles = 1
        CycleStart = 'First effective voltage zero crossing, either direction'
        StandardTanDeltaColorCriteriaConfigured = $false
    }
    Task17NeuralLearning = [ordered]@{
        Included = $false
        Status = 'DeferredAwaitingTask17FullCompletion'
        ModelSchema = 'reflection-neural-model-v1'
        EngineVersion = '1.0.0'
        AlgorithmIndex = 6
        SuggestionsOnly = $true
        AutomaticAcceptance = $false
        BundledTrainedWeights = $false
        RealUserModelTrainedByBuild = $false
        TrainingTrigger = 'Not available in this Task41 release; deferred candidate is retained separately'
        AddonSources = @()
    }
    Sources = @($sources | ForEach-Object {
        [ordered]@{
            Path = $_.Substring($root.Length).TrimStart('\').Replace('\','/')
            Sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
        }
    })
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'build-manifest.json') -Encoding UTF8
$factoryPressDist = Join-Path $root 'factory-press\dist\net48'
$factoryBridge = Join-Path $factoryPressDist 'Xiongxin.Owts.XpertPressBridge.dll'
if (-not (Test-Path -LiteralPath $factoryBridge -PathType Leaf)) {
    throw "Missing factory-press net48 bridge. Run: powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-FactoryPressMerge.ps1 -Configuration $Configuration"
}
$factoryDest = Join-Path $output 'modules\FactoryPress'
New-Item -ItemType Directory -Path $factoryDest -Force | Out-Null
Copy-Item -Path (Join-Path $factoryPressDist '*') -Destination $factoryDest -Force
Copy-Item -Path (Join-Path $factoryPressDist '*.dll') -Destination $output -Force
Write-Output "Staged factory-press bridge: $factoryDest"
Write-Output "Built: $exe"
