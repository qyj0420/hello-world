[CmdletBinding()]
param([string]$SourceProjectDirectory = 'D:\OWTSData\人大附中215-金茂府4#212\2023年06月14日-15时24分')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$asset = Join-Path $root 'assets\reference-simulation\rdfz-jmf-v1.json'
$profile = Get-Content -LiteralPath $asset -Raw -Encoding UTF8 | ConvertFrom-Json
$script:checks = 0
function Check([bool]$Condition, [string]$Message) { $script:checks++; if (-not $Condition) { throw $Message } }
Check ($profile.schema -eq 'HVX.ReferenceSimulation/1') 'Schema mismatch.'
Check ($profile.calibrations.Count -eq 4 -and $profile.pressureRecords.Count -eq 12) 'Unexpected waveform selection.'
Check ($profile.locationEvents.Count -eq 0 -and $profile.locationEvidence -eq 'No validated location samples included') 'Unvalidated location evidence was introduced.'
Check ($profile.sourceFiles.Count -eq 18) 'Missing source provenance.'
$before = @{}
foreach ($entry in $profile.sourceFiles) {
    Check ([IO.Path]::GetFileName($entry.sourceFile) -eq $entry.sourceFile) 'Source name must not traverse directories.'
    $path = Join-Path $SourceProjectDirectory $entry.sourceFile
    $before[$entry.sourceFile] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    Check ($before[$entry.sourceFile] -eq $entry.sourceSha256) 'Asset/source checksum differs.'
}
foreach ($cal in $profile.calibrations) {
    Check ($cal.voltageSamples.Count -eq 2475 -and $cal.sampleRateHz -eq 125000000) 'CAL samples/timebase mismatch.'
    Check ($cal.sourcePhase -eq 'ALL' -and $cal.sourceUnit -eq 'mV') 'CAL provenance is not ALL/mV.'
    $peakVolts = ($cal.voltageSamples | ForEach-Object { [Math]::Abs($_) } | Measure-Object -Maximum).Maximum
    Check ([Math]::Abs($peakVolts * 1000 * $cal.sourceCoefficientPcPerMv - $cal.chargePc) -lt $cal.chargePc * .005) 'mV-to-V scaling was lost.'
    Check ($cal.sourceIncidentSeconds -ge 0 -and $cal.sourceReflectionSeconds -gt $cal.sourceIncidentSeconds -and $cal.timingEvidence -match 'headers; not re-detected') 'Original timing marker provenance was lost.'
}
foreach ($phase in @('A', 'B', 'C')) {
    $records = @($profile.pressureRecords | Where-Object { $_.phase -eq $phase })
    Check ($records.Count -eq 4) 'Each phase needs four voltage cases.'
    Check ((@($records.nominalVoltageKv | Sort-Object) -join ',') -eq '0,3.69,12.3,20.91') 'Selected voltage cases differ.'
    foreach ($record in $records) {
        Check ($record.voltageKv.Count -eq 3200 -and $record.dischargePc.Count -eq 3201) 'Original unequal column lengths were changed.'
        Check ($record.rateEstimated -and $record.sampleRateHz -gt 79000 -and $record.sampleRateHz -lt 81000) 'Estimated timebase missing or implausible.'
        Check ($record.sourceFile -match '_2023-06-14-' -and $record.sourceFile -notmatch '[/\\]') 'Unexpected source or absolute legacy path.'
        if ($record.nominalVoltageKv -eq 0) {
            Check (@($record.voltageKv | Where-Object { [Math]::Abs($_) -gt .001 }).Count -eq 0) 'Source zero-voltage waveform is not zero.'
            Check (@($record.dischargePc | Where-Object { $_ -ne 0 }).Count -gt 0) 'Noise reference was replaced by an invented flat trace.'
            Check ($record.rateEvidence -match 'no measured ADC timebase or live voltage evidence') 'SIM reference evidence caveat missing.'
        }
    }
}
$qa = Join-Path $root ('build\qa-reference-simulation-asset\' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($qa) | Out-Null
$copy = Join-Path $qa 'rdfz-jmf-v1.json'
& (Join-Path $root 'tools\Build-ReferenceSimulationAsset.ps1') -SourceProjectDirectory $SourceProjectDirectory -OutputFile $copy
Check ((Get-FileHash -LiteralPath $copy).Hash -eq (Get-FileHash -LiteralPath $asset).Hash) 'Extraction is not byte-reproducible.'
foreach ($name in $before.Keys) {
    Check ((Get-FileHash -LiteralPath (Join-Path $SourceProjectDirectory $name)).Hash -eq $before[$name]) 'Read-only extraction changed source bytes.'
}
Write-Output ('REFERENCE_SIMULATION_ASSET_PASS=' + $checks + '; SOURCE_FILES_UNCHANGED=' + $before.Count + '; HARDWARE_COMMANDS=0')
