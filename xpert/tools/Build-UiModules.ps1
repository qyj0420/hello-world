[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OutputRoot,[string]$HybridPinFile='',[switch]$MaintenancePassword,[switch]$ProtectedRelease,[ValidateSet('Debug','Release')][string]$Configuration='Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Configuration -eq 'Release' -and (-not $ProtectedRelease -or -not $MaintenancePassword)) { throw 'Release UI modules require protected maintenance compilation.' }
if ($ProtectedRelease -and -not $MaintenancePassword) { throw 'Protected UI modules require explicit maintenance password protection.' }

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceRoot = Join-Path $projectRoot 'modules-src'
$compiler = Join-Path $PSScriptRoot 'compiler\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw "Local compiler is missing: $compiler" }
if (-not [IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot = Join-Path $projectRoot $OutputRoot }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

$common = @('/nologo', '/noconfig', '/langversion:7.3', '/optimize+', '/deterministic+', '/debug-', '/target:library', '/platform:x64',
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:Accessibility.dll',
    '/r:System.Xml.dll', '/r:System.Xml.Linq.dll', '/r:System.Web.Extensions.dll',
    '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll', '/r:Microsoft.CSharp.dll')
# Standalone/custom hosts can lack the desktop entry resource. Each owned UI
# assembly must therefore carry the same byte-identical neutral window icon.
$common += '/resource:'+(Join-Path $projectRoot 'assets\shortcuts\256.ico')+',HVX.NeutralWindow.ico'
$records = New-Object System.Collections.Generic.List[object]
if ($MaintenancePassword) { $common += '/define:MAINTENANCE_PASSWORD_GUARD' }
if (-not [String]::IsNullOrWhiteSpace($HybridPinFile)) {
    if (-not (Test-Path -LiteralPath $HybridPinFile -PathType Leaf)) { throw 'Hybrid native pin is missing.' }
    $common += @('/define:HYBRID_CORE',('/resource:'+$HybridPinFile+',HVX.HybridNative.Pin'))
}
$jointSources = @('CableJointStore.cs','CableJointDialog.cs') | ForEach-Object { Join-Path $projectRoot ('src\'+$_) }
$jointResources = @(((Join-Path $projectRoot 'assets\xiongxin.ico') + ',XIONGXIN.Brand.ico'))
$conclusionSource = Join-Path $sourceRoot 'ConclusionAlgorithm\EvidenceV3\HVX.Task15.Embedded.cs'
if (-not (Test-Path -LiteralPath $conclusionSource -PathType Leaf)) { throw 'Task15 V3 embedded source is missing.' }

function Get-ModuleSources {
    param([string]$Module, [string[]]$Parts = @('src'))
    $files = @((Join-Path $projectRoot 'src\UiLanguage.cs'),(Join-Path $projectRoot 'src\InternationalText.cs'),(Join-Path $projectRoot 'src\InternationalText.Generated.cs'),(Join-Path $projectRoot 'src\ProjectMeasurementMode.cs'))
    foreach ($part in $Parts) {
        $directory = Join-Path (Join-Path $sourceRoot $Module) $part
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "Source directory is missing: $directory" }
        $files += @(Get-ChildItem -LiteralPath $directory -File -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
    }
    $files += Join-Path $projectRoot 'src\OemUiBranding.cs'
    $files += Join-Path $projectRoot 'src\R2RoundedButton.cs'
    $files += Join-Path $projectRoot 'src\PressureRecordPublication.cs'
    # Keep every independently compiled spectrum/analysis closure on the same
    # pure score policy as the host (which compiles all src files).
    if ($Module -in @('DataAnalysis','Spectrum')) {
        $files += Join-Path $projectRoot 'src\LocationScorePolicy.cs'
    }
    if ($files.Count -eq 0) { throw "No source files found for $Module" }
    return $files
}

function Build-Module {
    param([string]$Module, [string]$AssemblyName, [string[]]$Sources,
        [string[]]$References = @(), [string[]]$Resources = @())
    $directory = Join-Path $OutputRoot $Module
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $output = Join-Path $directory ($AssemblyName + '.dll')
    # Bind the actual compile inputs, not just a self-reported output hash or
    # source count. This also rejects edits while a module is being compiled.
    $compileInputs = New-Object System.Collections.Generic.List[object]
    foreach ($kind in @('Source','Reference','Resource')) {
        $paths = switch ($kind) {
            'Source' { $Sources }
            'Reference' { $References }
            'Resource' {
                @($Resources | ForEach-Object { $_.Split(',')[0] }) +
                @($common | Where-Object { $_.StartsWith('/resource:',[StringComparison]::Ordinal) } |
                    ForEach-Object { $_.Substring(10).Split(',')[0] })
            }
        }
        foreach ($path in @($paths | Sort-Object -Unique)) {
            if ([string]::IsNullOrWhiteSpace($path)) { continue }
            $fullPath = [IO.Path]::GetFullPath($path)
            if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "Compile input missing: $fullPath" }
            if (-not $fullPath.StartsWith($projectRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) {
                throw "Compile input is outside the owned source/build tree: $fullPath"
            }
            $compileInputs.Add([ordered]@{Kind=$kind;Path=$fullPath.Substring($projectRoot.Length+1).Replace('\','/');SHA256=(Get-FileHash -LiteralPath $fullPath).Hash})
        }
    }
    $arguments = @($common) + @('/out:' + $output)
    foreach ($reference in $References) {
        if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) { throw "Build dependency is missing: $reference" }
        $arguments += '/r:' + $reference
    }
    foreach ($resource in $Resources) { $arguments += '/resource:' + $resource }
    $lines = @(& $compiler @arguments @Sources 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
    $lines | Set-Content -LiteralPath (Join-Path $directory 'compile.log') -Encoding UTF8
    foreach ($line in $lines) { Write-Host $line }
    if ($exitCode -ne 0 -or -not (Test-Path -LiteralPath $output -PathType Leaf)) { throw "$Module compilation failed." }
    $identity = [Reflection.AssemblyName]::GetAssemblyName($output)
    if ($identity.Name -ne $AssemblyName) { throw "Unexpected assembly identity: $output" }
    foreach ($inputRecord in $compileInputs) {
        if ((Get-FileHash -LiteralPath (Join-Path $projectRoot $inputRecord.Path)).Hash -cne $inputRecord.SHA256) {
            throw "Compile input changed during $Module compilation: $($inputRecord.Path)"
        }
    }
    $records.Add([ordered]@{
        Module = $Module; Assembly = $identity.Name; Version = $identity.Version.ToString(); Architecture = 'x64'
        File = $Module + '/' + $AssemblyName + '.dll'; SHA256 = (Get-FileHash -LiteralPath $output).Hash
        SourceCount = $Sources.Count; WarningCount = @($lines | Where-Object { $_ -match '\bwarning CS\d+' }).Count
        InputsUnchanged = $true; CompileInputs = @($compileInputs.ToArray())
    })
    Write-Host ("Built {0}: {1} source files" -f $Module, $Sources.Count)
    return $output
}

# Compile the shared spectrum implementation first. All consumers reference this
# same locally produced assembly; no external project checkout is required.
$analysisSources = Get-ModuleSources 'DataAnalysis' @('src', 'vendor\PdMap', 'vendor\PdMap\Common',
    'vendor\Spectrum', 'vendor\Task7', 'vendor\Task28', 'vendor\LocationTree', 'shared')
$analysisSources += Join-Path $projectRoot 'src\ZeroVoltageNoiseReference.cs'
$analysisSources += Join-Path $projectRoot 'src\DefectModelCatalog.cs'
$analysisSources += Join-Path $projectRoot 'src\AnalysisDateText.cs'
$analysisSources += Join-Path $projectRoot 'src\PdLinearHighPass.cs'
$analysisSources += Join-Path $projectRoot 'src\PdLinearLowPass.cs'
$analysisSources += Join-Path $projectRoot 'src\NoisePulseTemplates.cs'
$analysisSources += Join-Path $projectRoot 'src\LocationCycleRecord.cs'
if (-not [String]::IsNullOrWhiteSpace($HybridPinFile)) { $analysisSources += Join-Path $projectRoot 'src\HybridPhaseCore.cs' }
$analysisSources += Join-Path $projectRoot 'src\LiveCalibrationRecord.cs'
$analysisSources += Join-Path $projectRoot 'src\CalibrationPacketIdentityContract.cs'
$analysisSources += Join-Path $projectRoot 'src\LegacyCalibrationBinding.cs'
$analysisSources += Join-Path $sourceRoot 'LocationABCD\ABRelease\AB\source\PeakAnchoredLeadingEdgeMath.cs'
$analysisSources += $jointSources
$analysisSources += $conclusionSource
$legacyInput=Join-Path $sourceRoot 'LocationABCD\ABRelease\confirmed-legacy-inputs.json'
if((Get-FileHash -LiteralPath $legacyInput -Algorithm SHA256).Hash -ne '5BE1895AA9BF2D9449A4875112C1A3D976F0119D23F624E976141FE955C6D37D'){throw 'Legacy unit confirmation changed'}
$legacyPins=Join-Path $OutputRoot 'legacy-unit-pins.json'
@(([IO.File]::ReadAllText($legacyInput) | ConvertFrom-Json) | ForEach-Object {$_.sha256}) | ConvertTo-Json | Set-Content -LiteralPath $legacyPins -Encoding UTF8
$analysis = Build-Module 'DataAnalysis' 'HVX.PdReview.V1.0' $analysisSources @() ($jointResources+@($legacyPins+',HVX.LegacyUnitPins'))

$creationResources = @(((Join-Path $projectRoot 'assets\xiongxin.ico') + ',XIONGXIN.Brand.ico'),
    ((Join-Path $projectRoot 'assets\xiongxin-capacitor-hd.png') + ',XIONGXIN.Capacitor.png'))
$cableVisualSource=Join-Path $projectRoot 'src\CableVisualStyle.cs'
$null = Build-Module 'ProjectCreation' 'Xiongxin.OWTS.ProjectCreation' (@(Get-ModuleSources 'ProjectCreation') + $jointSources + $cableVisualSource) @() $creationResources
$null = Build-Module 'ProjectManagement' 'Xiongxin.OWTS.ProjectManagement' (Get-ModuleSources 'ProjectManagement') @() $jointResources
$browserSources = Get-ModuleSources 'DataBrowser' @('src', 'vendor\Item', 'vendor\Calibration',
    'vendor\Calibration\Common', 'vendor\Oscillation')
$browserSources += @('LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','CalibrationPulseDisplayWindow.cs') | ForEach-Object {Join-Path $projectRoot ('src\'+$_)}
$browserSources += $jointSources
$browserSources += $cableVisualSource
$browserSources += Join-Path $projectRoot 'src\AnalysisDateText.cs'
$null = Build-Module 'DataBrowser' 'Xiongxin.OWTS.DataBrowser' $browserSources @() $jointResources
$reportSources = @(Get-ModuleSources 'ReportGenerator') + @(Join-Path $sourceRoot 'DataBrowser\src\BrowserData.cs')
$reportSources += @('LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','PdLinearHighPass.cs','PdLinearLowPass.cs') | ForEach-Object {Join-Path $projectRoot ('src\'+$_)}
$reportSources += Join-Path $projectRoot 'src\CableJointStore.cs'
$reportSources += Join-Path $projectRoot 'src\PressureFixtureJournal.cs'
$reportSources += $cableVisualSource
$reportSources += Join-Path $projectRoot 'src\AnalysisDateText.cs'
$reportSources += $conclusionSource
$reportSources += Join-Path $sourceRoot 'ReportEngine\shared\BoundedDocx.cs'
$reportSources += Join-Path $sourceRoot 'ReportEngine\shared\ReportOutputSafety.cs'
$null = Build-Module 'ReportGenerator' 'Xiongxin.OWTS.ReportGenerator' $reportSources @() $creationResources
$pressureSources = @(Get-ModuleSources 'PressureTest') + @('ZeroVoltageNoiseReference.cs','LocationCycleRecord.cs' | ForEach-Object { Join-Path $projectRoot ('src\'+$_) })
$pressureSources += Join-Path $sourceRoot 'FactoryWorker\AdjacentHardwareRangePolicy.cs'
if (-not [String]::IsNullOrWhiteSpace($HybridPinFile)) { $pressureSources += Join-Path $projectRoot 'src\HybridPhaseCore.cs' }
# Only pure settings/raw-record and range-diagnostic contracts enter the UI DLL.
# Drivers, device discovery, output adapters and the worker entry remain isolated.
$task39ManifestPath = Join-Path $sourceRoot 'Task39InputWorker\provenance\source-manifest.json'
$task39Manifest = Get-Content -LiteralPath $task39ManifestPath -Raw | ConvertFrom-Json
if ($task39Manifest.Schema -ne 'XIONGXIN.Task39.SourceImport.v1') { throw 'Invalid Task39 source manifest.' }
foreach ($name in @('BenchModels.cs','PicoCaptureConfiguration.cs','LocationRangeAdvisor.cs','LocationRangeWindow.cs','RangeDecision.cs')) {
    $source = Join-Path $sourceRoot ('PressureTest\vendor\Task39\' + $name)
    $entry = @($task39Manifest.Files | Where-Object { $_.Path -ceq ('src/' + $name) })
    if ($entry.Count -ne 1) { throw ('Missing pinned input contract: '+$name) }
    $source = & (Join-Path $PSScriptRoot 'Test-PressureTask39Contract.ps1') -Name $name -OriginalHash $entry[0].SHA256
    $pressureSources += $source
}
$null = Build-Module 'PressureTest' 'Xiongxin.OWTS.PressureTest' $pressureSources @($analysis) $jointResources
$pressureReview = Join-Path $OutputRoot 'PressureTest\HVX.PdReview.V1.0.dll'
Copy-Item -LiteralPath $analysis -Destination $pressureReview -Force
if ((Get-FileHash -LiteralPath $pressureReview -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $analysis -Algorithm SHA256).Hash) {
    throw 'PressureTest shared spectrum dependency copy failed verification.'
}

$calibrationRoot = Join-Path $sourceRoot 'CalibrationTest'
$approvedUi = Join-Path $calibrationRoot 'assets\approvedUI.png'
$brandIcon = Join-Path $calibrationRoot 'assets\brand.ico'
foreach ($resource in @($approvedUi, $brandIcon)) {
    if (-not (Test-Path -LiteralPath $resource -PathType Leaf)) { throw "Calibration resource is missing: $resource" }
}
$resources = @($approvedUi + ',HVX.CalibrationProgram.ApprovedUi.png') + @($brandIcon + ',HVX.CalibrationProgram.Xiongxin.ico')
$standaloneRoot = Join-Path $sourceRoot 'CalibrationStandalone'
$resources += @(((Join-Path $standaloneRoot 'assets\xiongxin.ico') + ',Brand.ico'),
    ((Join-Path $standaloneRoot 'assets\xiongxin-original.png') + ',Brand.png'))
$calibrationSources = @(Get-ModuleSources 'CalibrationTest') + @(
    (Join-Path $sourceRoot 'LocationABCD\ABRelease\AB\source\PeakAnchoredLeadingEdgeMath.cs'),
    (Join-Path $projectRoot 'src\PdLinearHighPass.cs'),
    (Join-Path $projectRoot 'src\LiveCalibrationRecord.cs'),
    (Join-Path $projectRoot 'src\LegacyCalibrationBinding.cs'))
$calibrationSources += Join-Path $projectRoot 'src\NoisePulseTemplates.cs'
$calibrationSources += Join-Path $projectRoot 'src\PdLinearLowPass.cs'
$calibrationSources += Join-Path $projectRoot 'src\CalibrationPulseDisplayWindow.cs'
$calibrationSources += Join-Path $projectRoot 'src\PicoCaptureCadence.cs'
$calibrationSources += @(Get-ChildItem -LiteralPath $standaloneRoot -File -Filter '*.cs' |
    Where-Object { $_.Name -notin @('Program.cs','ControlBoxTests.cs') } | Sort-Object Name | ForEach-Object FullName)
$calibrationSources += @(Get-ChildItem -LiteralPath (Join-Path $standaloneRoot 'PicoCore') -File -Filter '*.cs' |
    Sort-Object Name | ForEach-Object FullName)
$calibrationSources += $jointSources
$null = Build-Module 'CalibrationTest' 'Xiongxin.OWTS.CalibrationTest' $calibrationSources @() ($resources + $jointResources)

$manifest = [ordered]@{ Schema='HVX.UiModuleCompilation/1'; Configuration=$Configuration; BuiltUtc = [DateTime]::UtcNow.ToString('o'); CompilerSHA256 = (Get-FileHash -LiteralPath $compiler).Hash; BuildScriptSHA256=(Get-FileHash -LiteralPath $PSCommandPath).Hash; CompilerDefines=@($common|Where-Object { $_.StartsWith('/define:',[StringComparison]::Ordinal) }|ForEach-Object { $_.Substring(8).Split(';') }); ProtectedRelease = [bool]$ProtectedRelease; MaintenancePasswordEnabled = [bool]$MaintenancePassword; Modules = @($records.ToArray()) }
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputRoot 'ui-module-build.json') -Encoding UTF8
Write-Output ('Built 7 local UI modules: ' + $OutputRoot)
