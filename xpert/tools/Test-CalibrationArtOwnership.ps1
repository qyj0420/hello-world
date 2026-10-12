param(
    [string]$OutputRoot,
    [switch]$Baseline
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo ('build/qa-calibration-art-' + [Guid]::NewGuid().ToString('N').Substring(0, 10))
}
$output = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $output) { throw 'Use a new, empty proof directory.' }
New-Item -ItemType Directory -Path $output | Out-Null
$control = Join-Path $repo 'modules-src/CalibrationStandalone/ControlBox.cs'
$test = if ($Baseline) { Join-Path $repo 'tests/CalibrationArtOwnershipBaselineSmoke.cs' } else { Join-Path $repo 'modules-src/CalibrationStandalone/ControlBoxTests.cs' }
$main = if ($Baseline) { 'CalibrationArtOwnershipBaselineSmoke' } else { 'CalibrationArtNativeOwnershipSmoke' }
$sources = @($control, (Join-Path $repo 'modules-src/CalibrationStandalone/CalibrationEmergencyMonitor.cs'), $test)
if (!$Baseline) { $sources += Join-Path $repo 'tests/CalibrationArtNativeOwnershipSmoke.cs' }
$inputs = @($sources) + @($PSCommandPath) | ForEach-Object {
    [pscustomobject]@{ Path = $_; SHA256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
}
$exe = Join-Path $output 'CalibrationArtOwnershipFakeOnly.exe'
& (Join-Path $repo 'tools/compiler/csc.exe') /nologo /target:exe /platform:x64 /langversion:7.3 /r:System.dll /r:System.Core.dll ('/main:' + $main) ('/out:' + $exe) @sources
if ($LASTEXITCODE -ne 0) { throw 'Offline fixture compilation failed.' }
$stdout = Join-Path $output 'stdout.txt'
$stderr = Join-Path $output 'stderr.txt'
$process = Start-Process -FilePath $exe -WorkingDirectory $output -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
if (!$process.WaitForExit(15000)) { $process.Kill(); throw 'Offline fixture timed out.' }
$process.Refresh()
$stable = @($inputs | Where-Object { (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.SHA256 }).Count -eq 0
$proof = [ordered]@{
    Schema = 'HVX.CalibrationArtOwnership.Offline/1'
    UTC = [DateTime]::UtcNow.ToString('o')
    BaselineReproduction = [bool]$Baseline
    NativeDriverLoaded = $false
    HardwareAccessed = $false
    TestEntryPoint = $main
    Inputs = $inputs
    SourceStable = $stable
    ExecutableSHA256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    ExitCode = $process.ExitCode
    Output = [IO.File]::ReadAllText($stdout)
    Error = [IO.File]::ReadAllText($stderr)
}
$proof | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'proof.json') -Encoding UTF8
Write-Output $proof.Output
Write-Output ('Proof: ' + (Join-Path $output 'proof.json'))
if (!$stable) { throw 'Source changed during test; receipt is not accepted.' }
if ($process.ExitCode -ne 0) { throw ('Offline fixture failed: ' + $proof.Error) }
