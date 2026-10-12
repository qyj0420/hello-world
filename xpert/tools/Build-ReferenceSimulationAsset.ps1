[CmdletBinding()]
param(
    [string]$SourceProjectDirectory = 'D:\OWTSData\人大附中215-金茂府4#212\2023年06月14日-15时24分',
    [string]$OutputFile = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceRoot = [IO.Path]::GetFullPath($SourceProjectDirectory).TrimEnd('\')
if ([string]::IsNullOrWhiteSpace($OutputFile)) { $OutputFile = Join-Path $projectRoot 'assets\reference-simulation\rdfz-jmf-v1.json' }
$OutputFile = [IO.Path]::GetFullPath($OutputFile)
$assetRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'assets\reference-simulation')).TrimEnd('\') + '\'
$buildRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'build')).TrimEnd('\') + '\'
if (-not $OutputFile.StartsWith($assetRoot, [StringComparison]::OrdinalIgnoreCase) -and
    -not $OutputFile.StartsWith($buildRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be inside this project assets/reference-simulation or build directory.' }
if ($OutputFile.StartsWith($sourceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Source project must never be an output destination.' }

function Assert-NoReparse([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            $entry = Get-Item -LiteralPath $cursor -Force
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse paths are not accepted.' }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }; $cursor = $parent
    }
}
Assert-NoReparse $sourceRoot
Assert-NoReparse ([IO.Path]::GetDirectoryName($OutputFile))
if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) { throw 'Source project directory is missing.' }
$invariant = [Globalization.CultureInfo]::InvariantCulture
$encoding = [Text.Encoding]::GetEncoding(936, [Text.EncoderFallback]::ExceptionFallback, [Text.DecoderFallback]::ExceptionFallback)

function Number([string]$Text) {
    $value = 0.0
    if (-not [double]::TryParse($Text.Trim(), [Globalization.NumberStyles]::Float, $invariant, [ref]$value) -or
        [double]::IsNaN($value) -or [double]::IsInfinity($value)) { throw ('Invalid finite number: ' + $Text) }
    return $value
}
function Key([string]$Text) { return ($Text.ToLowerInvariant() -replace '[\s_\-:：=（）()]', '') }
function HeaderValue($Headers, [string]$Name) {
    $needle = Key $Name
    $matches = @($Headers.Keys | Where-Object { (Key $_).StartsWith($needle, [StringComparison]::Ordinal) })
    if ($matches.Count -ne 1) { throw ('Missing or ambiguous header: ' + $Name) }
    return [string]$Headers[$matches[0]]
}
function OptionalNumber($Headers, [string]$Name) {
    $text = HeaderValue $Headers $Name
    if ($text.Trim() -eq 'NaN' -or $text.Trim().Length -eq 0) { return $null }
    return Number $text
}
function Read-Header([IO.StreamReader]$Reader, [int]$Count) {
    $headers = [ordered]@{}
    for ($i = 0; $i -lt $Count; $i++) {
        $line = $Reader.ReadLine(); if ($null -eq $line) { throw 'Truncated header.' }
        $parts = $line.Split([char]9, 2)
        if ($parts.Length -eq 2 -and $parts[0].Trim().Length -gt 0) { $headers[$parts[0].Trim()] = $parts[1].Trim() }
    }
    return $headers
}
function Median([double[]]$Values) {
    $ordered = @($Values | Sort-Object)
    if ($ordered.Count -eq 0) { throw 'No values for median.' }
    $half = [int][Math]::Floor($ordered.Count / 2)
    if ($ordered.Count % 2 -eq 0) { return ($ordered[$half - 1] + $ordered[$half]) / 2 }
    return $ordered[$half]
}
function Stable-Period([double[]]$Values) {
    $maximum = ($Values | Measure-Object -Maximum).Maximum
    $minimum = ($Values | Measure-Object -Minimum).Minimum
    if ($Values.Length -lt 64 -or $maximum -le 0 -or $minimum -ge 0 -or [Math]::Min($maximum, -$minimum) -lt [Math]::Max($maximum, -$minimum) * .15) { throw 'No stable bipolar voltage evidence.' }
    $crossings = New-Object 'System.Collections.Generic.List[double]'; $armed = $false
    $hysteresis = [Math]::Max($maximum, -$minimum) * .05
    for ($i = 1; $i -lt $Values.Length; $i++) {
        if ($Values[$i - 1] -lt -$hysteresis) { $armed = $true }
        if ($armed -and $Values[$i - 1] -le 0 -and $Values[$i] -gt 0) {
            $crossings.Add($i - 1 + (-$Values[$i - 1]) / ($Values[$i] - $Values[$i - 1])); $armed = $false
        }
    }
    if ($crossings.Count -lt 5) { throw 'Fewer than five stable voltage periods.' }
    $periods = for ($i = 1; $i -lt $crossings.Count; $i++) { $crossings[$i] - $crossings[$i - 1] }
    $median = Median $periods
    if ($median -lt 8 -or @($periods | Where-Object { [Math]::Abs($_ - $median) -le $median * .04 }).Count -lt $periods.Count * .9) { throw 'Voltage period consistency check failed.' }
    return $median
}

$calibrationNames = @('ALL_100nC.cal', 'ALL_10nC.cal', 'ALL_1nC.cal', 'ALL_500pC.cal')
$demNames = @(
    "A_0.00kV_2023-06-14-15'27'56.dem", "A_3.69kV_2023-06-14-15'28'08.dem", "A_12.30kV_2023-06-14-15'28'46.dem", "A_20.91kV_2023-06-14-15'29'57.dem",
    "B_0.00kV_2023-06-14-15'31'42.dem", "B_3.69kV_2023-06-14-15'31'53.dem", "B_12.30kV_2023-06-14-15'32'46.dem", "B_20.91kV_2023-06-14-15'34'11.dem",
    "C_0.00kV_2023-06-14-15'35'48.dem", "C_3.69kV_2023-06-14-15'35'59.dem", "C_12.30kV_2023-06-14-15'36'38.dem", "C_20.91kV_2023-06-14-15'37'51.dem"
)
$names = @('ITEM.in', 'CABLE.in') + $calibrationNames + $demNames
$hashes = @{}
$sourceFiles = foreach ($name in $names) {
    $path = Join-Path $sourceRoot $name; Assert-NoReparse $path
    $file = Get-Item -LiteralPath $path
    if ($file.Length -le 0 -or $file.Length -gt 1MB) { throw ('Source fixture file empty or oversized: ' + $name) }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $hashes[$name] = $hash
    [ordered]@{ sourceFile = $name; sourceSha256 = $hash; sourceBytes = $file.Length }
}
$calibrations = foreach ($name in $calibrationNames) {
    $reader = [IO.StreamReader]::new((Join-Path $sourceRoot $name), $encoding, $true)
    try {
        $headers = Read-Header $reader 13
        $samples = New-Object 'System.Collections.Generic.List[double]'
        while (-not $reader.EndOfStream) {
            $line = $reader.ReadLine(); if ([string]::IsNullOrWhiteSpace($line)) { continue }
            if ($samples.Count -ge 100000) { throw 'CAL sample limit exceeded.' }
            $samples.Add((Number ($line.Split([char]9)[0])) / 1000.0)
        }
        $rate = Number (HeaderValue $headers 'SR'); $length = Number (HeaderValue $headers 'Cable length')
        $start = Number (HeaderValue $headers 'Start_point'); $end = Number (HeaderValue $headers 'Final_point')
        $charge = Number (HeaderValue $headers 'Cal.value'); $coefficient = Number (HeaderValue $headers 'NI_Calib_factor')
        if ($rate -ne 125000000 -or $length -ne 810 -or $samples.Count -lt 16 -or $start -lt 0 -or $end -le $start -or $end * $rate -ge $samples.Count) { throw ('Unexpected calibration context: ' + $name) }
        $maximumMv = ($samples.ToArray() | ForEach-Object { [Math]::Abs($_ * 1000) } | Measure-Object -Maximum).Maximum
        if ([Math]::Abs($maximumMv * $coefficient - $charge) -gt $charge * .005) { throw 'Legacy charge/peak/calibration-factor identity failed.' }
        [ordered]@{
            levelCode = ([IO.Path]::GetFileNameWithoutExtension($name) -replace '^ALL_', '')
            chargePc = $charge; sourcePhase = 'ALL'; sampleRateHz = $rate; sourceUnit = 'mV'; voltageSamples = $samples.ToArray()
            sourceIncidentSeconds = $start; sourceReflectionSeconds = $end
            sourceVelocityMPerUs = (Number (HeaderValue $headers 'V (m/usec)'))
            timingEvidence = 'Source CAL Start_point(s) and Final_point(s) headers; not re-detected'
            unitEvidence = 'Legacy millivolt convention; chargePc equals raw absolute peak multiplied by NI_Calib_factor within 0.5%; volts = source / 1000'
            sourceCoefficientPcPerMv = $coefficient; sourceFile = $name; sourceSha256 = $hashes[$name]
        }
    } finally { $reader.Dispose() }
}
$records = foreach ($name in $demNames) {
    if ($name -notmatch '^([ABC])_([0-9.]+)kV_') { throw 'Invalid selected phase/voltage filename.' }
    $phase = $Matches[1]; $nominal = Number $Matches[2]
    $reader = [IO.StreamReader]::new((Join-Path $sourceRoot $name), $encoding, $true)
    try {
        $headers = Read-Header $reader 16
        $voltage = New-Object 'System.Collections.Generic.List[double]'; $pd = New-Object 'System.Collections.Generic.List[double]'
        $voltageEnded = $false; $pdEnded = $false
        if ((HeaderValue $headers 'Voltage (kV)') -ne 'PD (pC):') { throw 'Selected DEM column labels are not the expected explicit kV/pC pair.' }
        while (-not $reader.EndOfStream) {
            $line = $reader.ReadLine(); if ([string]::IsNullOrWhiteSpace($line)) { continue }
            if ([Math]::Max($voltage.Count, $pd.Count) -ge 100000) { throw 'DEM sample limit exceeded.' }
            $cells = $line.Split([char]9)
            for ($column = 0; $column -lt 2; $column++) {
                $text = if ($cells.Length -gt $column) { $cells[$column].Trim() } else { '' }
                if ($text -eq '' -or $text -eq 'NaN') { if ($column -eq 0) { $voltageEnded = $true } else { $pdEnded = $true }; continue }
                if (($column -eq 0 -and $voltageEnded) -or ($column -eq 1 -and $pdEnded)) { throw 'Interior missing samples must not be compacted.' }
                if ($column -eq 0) { $voltage.Add((Number $text)) } else { $pd.Add((Number $text)) }
            }
        }
        $frequency = OptionalNumber $headers 'Hz'; $rate = $null
        if ($voltage.Count -lt 16 -or $pd.Count -lt 16 -or (Number (HeaderValue $headers 'Delta-x')) -ne 625) { throw 'Unexpected selected DEM layout.' }
        if ($nominal -gt 0) {
            if ($null -eq $frequency -or $frequency -lt 20 -or $frequency -gt 2000) { throw 'No reliable recorded frequency for rate estimation.' }
            $rate = (Stable-Period $voltage.ToArray()) * $frequency
        } elseif (@($voltage.ToArray() | Where-Object { [Math]::Abs($_) -gt .001 }).Count -gt 0) { throw 'Selected zero reference contains nonzero stored voltage.' }
        [ordered]@{
            id = [IO.Path]::GetFileNameWithoutExtension($name); phase = $phase; nominalVoltageKv = $nominal
            sourceFile = $name; sourceSha256 = $hashes[$name]; sampleRateHz = $rate; rateEstimated = $true
            rateEvidence = 'Estimated from stable stored voltage zero-crossing period multiplied by recorded Hz; Delta-x=625 has no declared unit'
            recordedFrequencyHz = $frequency; recordedCapacitanceUf = (OptionalNumber $headers 'C[uF]'); recordedLossPercent = (OptionalNumber $headers 'D-losses')
            voltageKv = $voltage.ToArray(); dischargePc = $pd.ToArray()
        }
    } finally { $reader.Dispose() }
}
$nonzero = @($records | Where-Object { $_.nominalVoltageKv -gt 0 })
$consensus = Median @($nonzero | ForEach-Object { $_.sampleRateHz })
if ($nonzero.Count -ne 9 -or @($nonzero | Where-Object { [Math]::Abs($_.sampleRateHz - $consensus) -gt $consensus * .05 }).Count -gt 0) { throw 'Selected DEM project timebase consensus failed.' }
foreach ($record in $records) {
    if ($record.nominalVoltageKv -eq 0) {
        $record.sampleRateHz = $consensus
        $record.rateEvidence = 'Estimated median of the nine included nonzero DEM records with matching unitless Delta-x=625; no measured ADC timebase or live voltage evidence'
    }
}
$profile = [ordered]@{
    schema = 'HVX.ReferenceSimulation/1'; id = 'rdfz-jmf-v1'; title = '人大附中215—金茂府4#212'
    sourceProjectRelativePath = '人大附中215-金茂府4#212/2023年06月14日-15时24分'
    sourceLengthM = 810.0; nominalPhaseVoltageKv = 8.7
    sourceFiles = @($sourceFiles); calibrations = @($calibrations); pressureRecords = @($records)
    locationEvents = @(); locationEvidence = 'No validated location samples included'
    limitations = @('Reference-derived simulation only; not a current measurement or device calibration',
        'CAL source amplitude converted from legacy mV to V; source markers are not labeled as first-arrival detections',
        'DEM timebase is estimated; original unequal voltage/PD sample counts are preserved',
        'Zero-voltage source records have no trusted hardware identity/range/return-to-zero evidence',
        'ITEM date differs from directory date; CABLE repeats A-phase blocks; geometry is not silently repaired',
        'No original reports, binary Data.an or large LOC bodies are included')
}
foreach ($name in $names) {
    if ((Get-FileHash -LiteralPath (Join-Path $sourceRoot $name) -Algorithm SHA256).Hash -ne $hashes[$name]) { throw ('Source changed during extraction: ' + $name) }
}
$serialized = $profile | ConvertTo-Json -Depth 9 -Compress
$directory = [IO.Path]::GetDirectoryName($OutputFile); [IO.Directory]::CreateDirectory($directory) | Out-Null
$temporary = Join-Path $directory ('.reference-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllText($temporary, $serialized, [Text.UTF8Encoding]::new($false))
    if ([IO.File]::Exists($OutputFile)) { [IO.File]::Replace($temporary, $OutputFile, $null) } else { [IO.File]::Move($temporary, $OutputFile) }
} finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
Write-Output ('REFERENCE_ASSET=' + $OutputFile)
Write-Output ('REFERENCE_SHA256=' + (Get-FileHash -LiteralPath $OutputFile -Algorithm SHA256).Hash)
Write-Output ('SOURCE_FILES_UNCHANGED=' + $names.Count)
Write-Output ('CALIBRATIONS=' + $calibrations.Count + '; PRESSURE_RECORDS=' + $records.Count + '; ZERO_RATE_ESTIMATE=' + $consensus.ToString('R', $invariant))
