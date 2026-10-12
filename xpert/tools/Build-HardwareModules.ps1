[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OutputRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPrefix = $projectRoot.TrimEnd('\') + '\'
$output = [IO.Path]::GetFullPath($OutputRoot)
if (-not $output.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Hardware output must be a subdirectory of the unified project: $output"
}
$compiler = Join-Path $projectRoot 'tools\compiler\csc.exe'
$controlRoot = Join-Path $projectRoot 'modules-src\HardwareControl'
$connectorRoot = Join-Path $projectRoot 'modules-src\HardwareConnector'
$dependencyRoot = Join-Path $projectRoot 'dependencies\HardwareConnector'
$task6Root = Join-Path $projectRoot 'dependencies\Task6'
$icon = Join-Path $projectRoot 'assets\xiongxin.ico'
$logo = Join-Path $projectRoot 'assets\xiongxin-original.png'
$nativeBackend = Join-Path $projectRoot 'native-hardware\out\HvxAcquisitionNative.dll'
$nativeBackendSource = Join-Path $projectRoot 'native-hardware\HvxAcquisitionNative.cpp'

function Assert-File([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required hardware build input missing: $Path" }
}
function Assert-Hash([string]$Path, [string]$Sha256) {
    Assert-File $Path
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sha256) {
        throw "Hardware dependency does not match the verified local baseline: $Path"
    }
}
function Get-PeMachine([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        try {
            if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { throw "Invalid PE file: $Path" }
            $stream.Position = 0x3C
            $offset = $reader.ReadInt32()
            if ($offset -lt 64 -or $offset -gt ($stream.Length - 26)) { throw "Invalid PE header: $Path" }
            $stream.Position = $offset
            if ($reader.ReadUInt32() -ne 0x4550) { throw "Invalid PE signature: $Path" }
            return $reader.ReadUInt16()
        } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}
function Invoke-Compile([string]$Name, [string[]]$Arguments) {
    & $compiler @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Hardware build failed: $Name (exit $LASTEXITCODE)" }
}
function Copy-Verified([string]$Source, [string]$Destination) {
    Assert-File $Source
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
    if ((Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash) {
        throw "Hardware dependency copy failed hash verification: $Destination"
    }
}

foreach ($required in @($compiler, $icon, $logo, $nativeBackend, $nativeBackendSource,
    (Join-Path $connectorRoot 'app.manifest'), (Join-Path $connectorRoot 'integration.manifest'))) { Assert-File $required }

# Preserve the verified Task38 build's source guards. These inspect text only;
# compiling this script never opens, discovers, resets or drives a device.
$artSource = Join-Path $controlRoot 'src\ArtUsb3135Device.cs'
$controllerSource = Join-Path $controlRoot 'src\Task38Controller.cs'
$picoSource = Join-Path $controlRoot 'src\PicoScope5242DBlockDevice.cs'
foreach ($required in @($artSource, $controllerSource, $picoSource)) { Assert-File $required }
$artText = Get-Content -LiteralPath $artSource -Raw
$controllerText = Get-Content -LiteralPath $controllerSource -Raw
$picoText = Get-Content -LiteralPath $picoSource -Raw
$nativeText = Get-Content -LiteralPath $nativeBackendSource -Raw
if ($picoText.IndexOf('Pico5000aNative.SuppressStartupWindow();') -lt 0 -or
    $picoText.IndexOf('Pico5000aNative.SuppressStartupWindow();') -gt $picoText.IndexOf('Pico5000aNative.OpenUnit(') -or
    $picoText -notmatch 'ConfigureAndMaybeArm\(configuration\.PicoRangeIndex, false\)' -or
    $picoText -notmatch 'if \(!startCapture\) return;[\s\S]*Pico5000aNative\.RunBlock' -or
    $picoText -notmatch 'CheckHardwareBridge\(HvxSuppressPicoStartupWindow\(\)' -or
    $nativeText -notmatch 'apply\(485331304,4582\)' -or
    $nativeText -notmatch 'void\s+RawApply\(void\(HVX_CALL\*\s*apply\)\(int32_t,int16_t\)') {
    throw 'Pico initialization must suppress startup UI, verify capture settings, and not start RunBlock.'
}
if ($artText -notmatch 'ConfigureFiniteDividerCapture\(false\)' -or
    $artText -notmatch 'ArtDaqNative\.TaskControl\(finiteInputTask, 2\)' -or
    $artText -notmatch 'if \(startCapture\)[\s\S]*ArtDaqNative\.StartTask\(finiteInputTask\)') {
    throw 'ART initialization must verify finite acquisition without starting its trigger wait.'
}
if ($artText -notmatch 'private const double SinglePointIoTimeoutSeconds\s*=\s*0\.020') {
    throw 'ART single-point I/O timeout guard is missing or no longer 20 ms.'
}
if ($artText -notmatch 'lock \(safetyInputSync\)' -or $artText -notmatch 'lock \(digitalOutputSync\)') {
    throw 'ART P0.4 and digital-output task lock isolation guard failed.'
}
$forceStart = $artText.IndexOf('public void ForceSourceOff()')
$forceEnd = $artText.IndexOf('public void StopMonitoringInputs()', $forceStart)
if ($forceStart -lt 0 -or $forceEnd -le $forceStart) { throw 'Cannot locate ForceSourceOff for safety-order validation.' }
$forceBody = $artText.Substring($forceStart, $forceEnd - $forceStart)
$forceDo = $forceBody.IndexOf('WriteDigitalOutputsCore()')
$forceAo = $forceBody.IndexOf('WriteAoCore(0.0)')
if ($forceDo -lt 0 -or $forceAo -lt 0 -or $forceDo -ge $forceAo) {
    throw 'ForceSourceOff must attempt HV/lamp digital shutdown before AO=0.'
}
if ($forceBody -notmatch 'try\s*\{\s*WriteDigitalOutputsCore\(\);\s*\}[\s\S]*catch[\s\S]*try\s*\{\s*WriteAoCore\(0\.0\);\s*\}') {
    throw 'ForceSourceOff must attempt AO=0 even when the HV/lamp digital write fails.'
}
$clearStart = $artText.IndexOf('private static void ClearTask(')
$clearEnd = $artText.IndexOf('private void SafeClearAllTasks()', $clearStart)
if ($clearStart -lt 0 -or $clearEnd -le $clearStart) { throw 'Cannot locate ClearTask for handle-retention validation.' }
$clearBody = $artText.Substring($clearStart, $clearEnd - $clearStart)
$nativeClear = $clearBody.IndexOf('ArtDaqNative.ClearTask(handle)')
$releaseHandle = $clearBody.IndexOf('task = IntPtr.Zero')
if ($clearBody -notmatch 'int stopCode\s*=\s*ArtDaqNative\.StopTask' -or
    $clearBody -notmatch 'int clearCode\s*=\s*ArtDaqNative\.ClearTask' -or
    $nativeClear -lt 0 -or $releaseHandle -le $nativeClear) {
    throw 'ClearTask must check Stop/Clear results and release a handle only after native ClearTask succeeds.'
}
$armStart = $artText.IndexOf('public void ArmFiniteDividerCapture()')
$armEnd = $artText.IndexOf('public double[] ReadFiniteDividerCapture', $armStart)
if ($armStart -lt 0 -or $armEnd -le $armStart) { throw 'Cannot locate finite-capture setup for transition validation.' }
$armBody = $artText.Substring($armStart, $armEnd - $armStart)
if ($armBody.IndexOf('ClearTask(ref monitoringInputTask') -lt 0 -or
    $armBody.IndexOf('ClearTask(ref monitoringInputTask') -ge $armBody.IndexOf('ArtDaqNative.CreateTask')) {
    throw 'Monitoring-to-finite transition must finish monitored-task cleanup before creating the finite task.'
}
if ($artText -notmatch 'Math\.Max\(0\.001, timeoutMilliseconds / 1000\.0\)') {
    throw 'Finite-segment read timeout must remain independent of the 20 ms single-point I/O timeout.'
}
if ($controllerText -notmatch 'periodMilliseconds\s*-\s*\(int\)pollPeriod\.ElapsedMilliseconds') {
    throw 'SafetyPollLoop must subtract P0.4 read/handling time from the 50 ms poll period.'
}
$inspectionSource = Join-Path $controlRoot 'src\ConnectionInspection.cs'
Assert-File $inspectionSource
if ((Get-Content -LiteralPath $inspectionSource -Raw) -match '\.(Initialize|OpenUnit|CreateTask|ResetDevice|SetDigitalOutputs|WriteAoVolts|Remove)\s*\(') {
    throw 'Connection inspection must remain discovery-only without device outputs or record removal.'
}

$verifiedDependencies = [ordered]@{
    'dependencies\Task6\05f21d64e56010f23ed61cc9.V1.0.6.dll' = '006F73F20EE9353CB64B14FBFC2A9C74558C0D4414E3BEF7DF00D9FBC2C6380E'
    'dependencies\Task6\7e4a9c2d6f1b8053a7c4e912.V1.0.6.dll' = '1F4A6E59EDA06AEB1583F33AB1B58983F284B052983277FD522A851DA3EACC4E'
    'dependencies\Task6\2c8a82000748444a44aa0ad8.V1.0.6.dll' = '1F3F18F640A94051E4A6C94E9A08347BE6BCEEB6739B11748330F120A2CF0A37'
    'dependencies\HardwareConnector\runtime\Sxuptp.dll' = 'EF901229E4DCE25A39FA1E42B2220DB675783A68272E00334B73AB221F7A9DDE'
    'dependencies\HardwareConnector\runtime\SxConnectDev.exe' = 'A99A056EC07EEC26EC07B99EDFF7A1FA557FE10105C646B72A9811EFE17BF286'
}
foreach ($relative in $verifiedDependencies.Keys) { Assert-Hash (Join-Path $projectRoot $relative) $verifiedDependencies[$relative] }

$controlSources = @(Get-ChildItem -LiteralPath (Join-Path $controlRoot 'src') -Filter '*.cs' -File | Sort-Object Name | ForEach-Object FullName)
$uiSources = @(Get-ChildItem -LiteralPath (Join-Path $controlRoot 'ui') -Filter '*.cs' -File | Sort-Object Name | ForEach-Object FullName)
$uiSources += Join-Path $projectRoot 'src\DialogPlacement.cs'
if ($controlSources.Count -lt 10 -or $uiSources.Count -lt 5) { throw 'Hardware control source set is incomplete.' }
$connectorNames = @('Program.cs','Display.cs','DeviceNames.cs','ConnectionHealth.cs','Workflow.cs','Localization.cs','LocaleCatalog.cs','ArtAliases.cs')
$connectorSources = @($connectorNames | ForEach-Object { Join-Path $connectorRoot $_ })
$connectorSources += Join-Path $projectRoot 'src\DialogPlacement.cs'
$connectorSources += @('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs') | ForEach-Object { Join-Path $projectRoot ('src\'+$_) }
$integrationSources = @($connectorSources + @((Join-Path $connectorRoot 'ModuleIdentity.cs'), (Join-Path $connectorRoot 'Integration.cs'), (Join-Path $connectorRoot 'Integration.Discovery.cs')))
$integrationSources += Join-Path $connectorRoot 'Integration.LocalInspection.cs'
$uninstallerSource = Join-Path $connectorRoot 'UninstallHardware.cs'
foreach ($source in @($integrationSources + @($uninstallerSource))) { Assert-File $source }
# Reject only developer-checkout fallbacks; customer-selected directories and installed
# ART/Pico/vendor driver locations intentionally remain supported.
foreach ($source in @($controlSources + $uiSources + $integrationSources + @($uninstallerSource))) {
    if ((Get-Content -LiteralPath $source -Raw) -match '(?i)[A-Z]:\\(?:PD_AI_LabVIEW2026_Project|DS600|codex|Users\\qyj04)(?:\\|\b)') {
        throw "Developer-machine path remains in a hardware runtime source: $source"
    }
}

$task38Output = Join-Path $output 'Task38'
$task6Output = Join-Path $task38Output 'Task6'
$connectorOutput = Join-Path $output 'HardwareIntegration'
$runtimeOutput = Join-Path $connectorOutput 'runtime'
$assetsOutput = Join-Path $connectorOutput 'assets'
New-Item -ItemType Directory -Path $task38Output,$task6Output,$connectorOutput,$runtimeOutput,$assetsOutput -Force | Out-Null
$dll = Join-Path $task38Output 'HVX.OWTS.Task38.V1.0.38.dll'
$controlExe = Join-Path $task38Output 'Task38.ControlPanel.exe'
$integrationExe = Join-Path $connectorOutput 'HardwareIntegration.exe'
$connectorExe = Join-Path $connectorOutput 'HV-DAC-Xpert.exe'
$uninstallerExe = Join-Path $connectorOutput 'UninstallHardware.exe'
$common = @('/nologo','/langversion:7.3','/platform:x64','/optimize+','/debug-','/deterministic+')
Invoke-Compile 'Task38 controller' ($common + @('/target:library', ('/out:' + $dll)) + $controlSources)
Invoke-Compile 'Task38 control panel' ($common + @('/target:winexe', ('/reference:' + $dll),
    '/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll', ('/win32icon:' + $icon),
    ('/resource:' + $icon + ',Task38.Brand.ico'), ('/resource:' + $logo + ',Task38.Brand.Logo'),
    ('/out:' + $controlExe)) + $uiSources)
$connectorReferences = @('/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll',
    '/reference:System.ServiceProcess.dll','/reference:System.Core.dll','/reference:System.Xml.Linq.dll')
$connectorCommon = @($common + $connectorReferences + @('/target:winexe', ('/win32icon:' + $icon)))
Invoke-Compile 'Hardware connector' ($connectorCommon + @(
    ('/win32manifest:' + (Join-Path $connectorRoot 'app.manifest')), ('/out:' + $connectorExe)) + $connectorSources)
Invoke-Compile 'Hardware integration worker' ($connectorCommon + @('/main:Xiongxin.HvDacXpert.Integration',
    ('/win32manifest:' + (Join-Path $connectorRoot 'integration.manifest')), ('/out:' + $integrationExe)) + $integrationSources)
Invoke-Compile 'Hardware maintenance uninstaller' ($connectorCommon + @(
    ('/win32manifest:' + (Join-Path $connectorRoot 'app.manifest')), ('/out:' + $uninstallerExe), $uninstallerSource,
    (Join-Path $projectRoot 'src\DialogPlacement.cs')))
foreach ($name in @('05f21d64e56010f23ed61cc9.V1.0.6.dll','7e4a9c2d6f1b8053a7c4e912.V1.0.6.dll','2c8a82000748444a44aa0ad8.V1.0.6.dll')) {
    Copy-Verified (Join-Path $task6Root $name) (Join-Path $task6Output $name)
}
foreach ($name in @('Sxuptp.dll','SxConnectDev.exe')) {
    Copy-Verified (Join-Path $dependencyRoot ('runtime\' + $name)) (Join-Path $runtimeOutput $name)
}
foreach ($directory in @($task38Output,$connectorOutput)) {
    Copy-Verified $nativeBackend (Join-Path $directory 'HvxAcquisitionNative.dll')
}
# Always apply the unified project's current complete XIONGXIN logo.
Copy-Verified $icon (Join-Path $assetsOutput 'xiongxin.ico')
Copy-Verified $logo (Join-Path $assetsOutput 'xiongxin-original.png')

$binaries = @($dll,$controlExe,$integrationExe,$connectorExe,$uninstallerExe,
    (Join-Path $task38Output 'HvxAcquisitionNative.dll'),(Join-Path $connectorOutput 'HvxAcquisitionNative.dll'))
foreach ($binary in $binaries) {
    if ((Get-PeMachine $binary) -ne 0x8664) { throw "Expected x64 hardware output: $binary" }
}
if ([Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString() -ne '1.0.38.0') {
    throw 'Task38 controller assembly version changed unexpectedly.'
}
$inputs = @($controlSources + $uiSources + $integrationSources + @($uninstallerSource,$icon,$logo,$compiler,$PSCommandPath,$nativeBackend,$nativeBackendSource,
    (Join-Path $connectorRoot 'app.manifest'), (Join-Path $connectorRoot 'integration.manifest')) +
    @($verifiedDependencies.Keys | ForEach-Object { Join-Path $projectRoot $_ }))
$manifest = [ordered]@{
    Schema = 'XIONGXIN.UnifiedDesktop.HardwareBuild.v1'
    BuiltUtc = [DateTime]::UtcNow.ToString('o')
    Architecture = 'x64'
    SourceMode = 'LocalUnifiedProject'
    Validation = 'Compiled only; static Task38 safety guards, pinned dependency hashes and x64 PE checks passed. No hardware process was launched.'
    Inputs = @($inputs | Sort-Object -Unique | ForEach-Object {
        [ordered]@{ Path = $_.Substring($projectPrefix.Length).Replace('\','/'); Sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
    })
    Binaries = @($binaries | ForEach-Object {
        [ordered]@{ Path = $_.Substring($output.Length).TrimStart('\').Replace('\','/'); Sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
    })
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'hardware-build-manifest.json') -Encoding UTF8
Write-Output "Hardware modules built locally: $output"
Write-Output 'HARDWARE_COMPILE_ONLY=PASS'
