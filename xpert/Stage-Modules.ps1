param([Parameter(Mandatory = $true)][string]$OutputRoot,[string]$HybridNativeDirectory='',[string]$HybridPinFile='',[switch]$ProtectedRelease,[switch]$MaintenancePassword,[ValidateSet('Debug','Release')][string]$Configuration='Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Configuration -eq 'Release' -and (-not $ProtectedRelease -or -not $MaintenancePassword)) { throw 'Release module staging requires protected maintenance compilation.' }
if ($ProtectedRelease -and -not $MaintenancePassword) { throw 'Protected module staging requires explicit maintenance password protection.' }

$output = [IO.Path]::GetFullPath($OutputRoot)
$project = [IO.Path]::GetFullPath($PSScriptRoot)
if (-not $output.StartsWith(($project.TrimEnd('\') + '\build\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must remain inside the unified desktop project: $output"
}
$modules = Join-Path $output 'modules'
$modules = [IO.Path]::GetFullPath($modules)
$outputPrefix = $output.TrimEnd('\') + '\'
if (-not $modules.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe modules destination: $modules"
}
$pending = Join-Path (Join-Path $output ('module-build-' + [Guid]::NewGuid().ToString('N'))) 'modules'
New-Item -ItemType Directory -Path $pending -Force | Out-Null

# Every build input is now in this project. Verified algorithm binaries remain
# pinned locally until equivalent source replacements pass their own regression.
foreach ($step in @('Build-UiModules.ps1', 'Build-HardwareModules.ps1', 'Build-Task39InputWorker.ps1', 'Build-FactoryWorker.ps1', 'Stage-AlgorithmDependencies.ps1')) {
    $scriptPath = Join-Path $project ('tools\' + $step)
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { throw "Missing local builder: $step" }
    $stepArgs = @{OutputRoot=$pending}
    if ($step -eq 'Build-UiModules.ps1' -and -not [String]::IsNullOrWhiteSpace($HybridPinFile)) { $stepArgs.HybridPinFile=$HybridPinFile }
    if ($step -eq 'Build-UiModules.ps1') {
        $stepArgs.MaintenancePassword=[bool]$MaintenancePassword
        $stepArgs.ProtectedRelease=[bool]$ProtectedRelease
        $stepArgs.Configuration=$Configuration
    }
    & $scriptPath @stepArgs
    if ($LASTEXITCODE -ne 0) { throw "Module build failed: $step" }
}
# Replace the pinned legacy report binary with the current source build, so
# report fixes are actually used by the staged application.
& (Join-Path $project 'tools\Build-ReportEngine.ps1') -OutputDirectory (Join-Path $pending 'ReportGenerator\runtime')
$reportEngineDll=Join-Path $pending 'ReportGenerator\runtime\HVX.Report.Engine.dll'
$reportEngineHash=(Get-FileHash -LiteralPath $reportEngineDll -Algorithm SHA256).Hash
$reportEngineSources=@('modules-src/ReportEngine/src/AssemblyInfo.cs','modules-src/ReportEngine/src/ReportGenerator.cs','modules-src/ReportEngine/shared/BoundedDocx.cs','modules-src/ReportEngine/shared/ReportOutputSafety.cs')
# The validated calibration acquisition is an isolated process with only
# P0.4/P1.2 and Pico access. Keep it independent of Task38 HV outputs.
# Each candidate owns its compile output; never race another build's shared EXE.
$calibrationBuild = Join-Path (Split-Path -Parent $pending) 'calibration-source-build'
& (Join-Path $project 'modules-src\CalibrationStandalone\Build.ps1') -OutputDirectory $calibrationBuild
if ($LASTEXITCODE -ne 0) { throw 'Calibration acquisition build failed.' }
$calibrationStage = Join-Path $pending 'CalibrationAcquisition'
New-Item -ItemType Directory -Path $calibrationStage -Force | Out-Null
foreach ($name in @('XIONGXIN.Calibration.Verification.exe','XIONGXIN.Calibration.Verification.exe.config')) {
    $sourceFile = Join-Path $calibrationBuild $name
    if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) { throw "Calibration acquisition payload missing: $name" }
    Copy-Item -LiteralPath $sourceFile -Destination (Join-Path $calibrationStage $name)
}
# Task15 conclusion and Task28 location filtering are embedded by Build-UiModules.
# Build the separate Task17 location library outside the payload and verify its identical DLL in both
# consumers before publishing the new modules directory.
$locationOutput = Join-Path (Split-Path -Parent $pending) 'location-source-build'
$locationArgs=@{OutputDirectory=$locationOutput}
if (-not [String]::IsNullOrWhiteSpace($HybridPinFile)) { $locationArgs.HybridPinFile=$HybridPinFile }
$locationArgs.ProtectedRelease=[bool]$ProtectedRelease
& (Join-Path $project 'tools\Build-LocationAlgorithm.ps1') @locationArgs
if ($LASTEXITCODE -ne 0) { throw 'Source location algorithm build failed.' }
$locationName = 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'
$locationDll = Join-Path $locationOutput $locationName
$locationHash = (Get-FileHash -LiteralPath $locationDll -Algorithm SHA256).Hash
foreach ($consumer in @('DataAnalysis','PressureTest')) {
    $locationTarget = Join-Path (Join-Path $pending $consumer) $locationName
    Copy-Item -LiteralPath $locationDll -Destination $locationTarget -Force
    if ((Get-FileHash -LiteralPath $locationTarget -Algorithm SHA256).Hash -ne $locationHash) {
        throw ('Source location staging failed: ' + $consumer)
    }
}
$candidate=& (Join-Path $project 'tools\Test-LocationCandidate.ps1')
& (Join-Path $project 'tools\Build-ResearchRuntime.ps1') -ModulesRoot $pending
Copy-Item -LiteralPath (Join-Path $pending 'research-pins.json') -Destination (Join-Path $output 'research-pins.json')
$required = @(
    'ProjectCreation\Xiongxin.OWTS.ProjectCreation.dll',
    'ProjectManagement\Xiongxin.OWTS.ProjectManagement.dll',
    'DataBrowser\Xiongxin.OWTS.DataBrowser.dll',
    'DataAnalysis\HVX.PdReview.V1.0.dll',
    'ReportGenerator\Xiongxin.OWTS.ReportGenerator.dll',
    'PressureTest\Xiongxin.OWTS.PressureTest.dll',
    'CalibrationTest\Xiongxin.OWTS.CalibrationTest.dll',
    'CalibrationAcquisition\XIONGXIN.Calibration.Verification.exe',
    'Task38\Task38.ControlPanel.exe',
    'Task39\Xiongxin.Task39.InputWorker.exe',
    'Task39\Xiongxin.Task39.InputWorker.exe.config',
    'Task39\source-manifest.json',
    'HardwareIntegration\HardwareIntegration.exe'
    'r71\python.exe'
    'r71\q71.cp312-win_amd64.pyd'
    'r71\q72.cp312-win_amd64.pyd'
    'r71\q73.cp312-win_amd64.pyd'
    'r71\q74.cp312-win_amd64.pyd'
    'r71\q75.cp312-win_amd64.pyd'
    'r71\q76.cp312-win_amd64.pyd'
    'r71\q77.cp312-win_amd64.pyd'
    'r71\q78.cp312-win_amd64.pyd'
    'r71\q79.cp312-win_amd64.pyd'
    'r71\q80.cp312-win_amd64.pyd'
    'PressureRuntime\Xiongxin.Pressure.Runtime.exe'
)
if (-not [String]::IsNullOrWhiteSpace($HybridPinFile)) {
    $hybridStage=Join-Path $pending 'Hybrid'
    New-Item -ItemType Directory -Path $hybridStage -Force | Out-Null
    $native=Join-Path $HybridNativeDirectory 'HvxHybridCore.dll'
    $pin=(Get-Content -LiteralPath $HybridPinFile -Raw).Trim()
    if ((Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash -cne $pin) { throw 'Hybrid library does not match its compiled pin.' }
    Copy-Item -LiteralPath $native -Destination (Join-Path $hybridStage 'HvxHybridCore.dll')
    [ordered]@{schema='HVX.HybridNative/1';abiVersion=1;architecture='x64';libraryFile='HvxHybridCore.dll';librarySha256=$pin} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $hybridStage 'native-manifest.json') -Encoding UTF8
    $required+=@('Hybrid\HvxHybridCore.dll','Hybrid\native-manifest.json')
}
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $pending $relative) -PathType Leaf)) { throw "Incomplete module build: $relative" }
}
$ownedAlgorithmProtection=@()
if($ProtectedRelease){
    $ownedAlgorithmProtection=@(& (Join-Path $project 'tools\Protect-StagedAlgorithms.ps1') -ModulesRoot $pending -BuildRoot $output -EvidenceRoot (Join-Path (Split-Path -Parent $pending) 'protection'))
}
$buildEvidence = Join-Path (Split-Path -Parent $pending) 'verification'
foreach ($file in @(Get-ChildItem -LiteralPath $pending -Recurse -File | Where-Object {
    $_.Name -in @('compile.log', 'ui-module-build.json', 'hardware-build-manifest.json', 'task39-build-manifest.json', 'source-manifest.json', 'research-pins.json', 'research-runtime-input-receipt.json', 'legacy-unit-pins.json')
})) {
    $relative = $file.FullName.Substring($pending.Length).TrimStart('\')
    $destination = [IO.Path]::GetFullPath((Join-Path $buildEvidence $relative))
    if (-not $destination.StartsWith(($buildEvidence.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid build evidence path.' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Move-Item -LiteralPath $file.FullName -Destination $destination
}
$backup = $null
if (Test-Path -LiteralPath $modules) {
    $backup = Join-Path $output ('modules.previous-' + [Guid]::NewGuid().ToString('N'))
    Move-Item -LiteralPath $modules -Destination $backup
}
try { Move-Item -LiteralPath $pending -Destination $modules }
catch {
    if ($null -ne $backup -and -not (Test-Path -LiteralPath $modules)) { Move-Item -LiteralPath $backup -Destination $modules }
    throw
}

$manifest = [ordered]@{
    Schema = 'XIONGXIN.UnifiedDesktop.Modules.v2'
    StagedUtc = [DateTime]::UtcNow.ToString('o')
    Architecture = 'x64'
    LabVIEWRequired = $false
    MaintenancePasswordEnabled = [bool]$MaintenancePassword
    ProtectedRelease = [bool]$ProtectedRelease
    Configuration = $Configuration
    SourceUiRuntime = [ordered]@{
        ManifestPath = (Join-Path $buildEvidence 'ui-module-build.json').Substring($output.Length+1).Replace('\','/')
        ManifestSHA256 = (Get-FileHash -LiteralPath (Join-Path $buildEvidence 'ui-module-build.json') -Algorithm SHA256).Hash
    }
    BuildInputs = 'Project-local UI/hardware/Task39 isolated input worker/Task15 conclusion/Task28 location filtering/Task17 location source and remaining pinned dependencies'
    AlgorithmSourceReplacementComplete = $false
    OwnedAlgorithmProtection = $ownedAlgorithmProtection
    SourcePressureRuntime = [ordered]@{
        ManifestPath = (Join-Path $buildEvidence 'PressureRuntime\source-manifest.json').Substring($output.Length+1).Replace('\','/')
        ManifestSHA256 = (Get-FileHash -LiteralPath (Join-Path $buildEvidence 'PressureRuntime\source-manifest.json') -Algorithm SHA256).Hash
    }
    SourceResearchRuntime = [ordered]@{
        ManifestPath = (Join-Path $buildEvidence 'research-runtime-input-receipt.json').Substring($output.Length+1).Replace('\','/')
        ManifestSHA256 = (Get-FileHash -LiteralPath (Join-Path $buildEvidence 'research-runtime-input-receipt.json') -Algorithm SHA256).Hash
    }
    SourceReportEngine = [ordered]@{
        Sha256 = $reportEngineHash
        Sources = @($reportEngineSources | ForEach-Object {
            [ordered]@{Path=$_;Sha256=(Get-FileHash -LiteralPath (Join-Path $project $_) -Algorithm SHA256).Hash}
        })
    }
    SourceLocationAlgorithm = [ordered]@{
        Assembly = [Reflection.AssemblyName]::GetAssemblyName($locationDll).FullName
        FileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($locationDll).FileVersion
        Sha256 = $locationHash
        Consumers = @('DataAnalysis', 'PressureTest')
        Candidate = $candidate.Candidate
        CandidateManifestSha256 = $candidate.ManifestSha256
        InternalTestOnly = $true
        PhysicalAccuracyAccepted = $false
    }
    Modules = @(Get-ChildItem -LiteralPath $modules -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            Path = $_.FullName.Substring($output.Length).TrimStart('\').Replace('\','/')
            Bytes = $_.Length
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'module-manifest.json') -Encoding UTF8
Write-Output "Staged modules: $modules"
