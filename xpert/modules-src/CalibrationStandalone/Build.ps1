param([string]$OutputDirectory = '', [string]$Compiler = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sourceRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot '..\..'))
$compiler = if (-not [string]::IsNullOrWhiteSpace($Compiler)) { [IO.Path]::GetFullPath($Compiler) } else { Join-Path $projectRoot 'tools\compiler\csc.exe' }
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Provide the .NET Framework 4.8 Roslyn csc.exe via -Compiler.' }
$out = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $sourceRoot 'build' } else { [IO.Path]::GetFullPath($OutputDirectory) }
if (-not $out.StartsWith((Join-Path $sourceRoot 'build'),[StringComparison]::OrdinalIgnoreCase) -and
    -not $out.StartsWith((Join-Path $projectRoot 'build\'),[StringComparison]::OrdinalIgnoreCase)) { throw 'Build output must stay beneath project or standalone build.' }
New-Item -ItemType Directory -Path $out -Force | Out-Null
$exe = Join-Path $out 'XIONGXIN.Calibration.Verification.exe'
$sources = @(
    (Join-Path $sourceRoot 'ControlBox.cs'),
    (Join-Path $sourceRoot 'CalibrationEmergencyMonitor.cs'),
    (Join-Path $sourceRoot 'CalibrationForm.cs'),
    (Join-Path $sourceRoot 'CalibrationAcquisitionFlow.cs'),
    (Join-Path $sourceRoot 'CalibrationSelfCheckWindow.cs'),
    (Join-Path $sourceRoot 'Program.cs'),
    (Join-Path $sourceRoot 'CalibrationWindowing.cs'),
    (Join-Path $sourceRoot 'AcquisitionPlanning.cs'),
    (Join-Path $sourceRoot 'CalibrationCaptureResult.cs'),
    (Join-Path $sourceRoot 'CalibrationProjectAudit.cs'),
    (Join-Path $projectRoot 'src\PdLinearHighPass.cs'),
    (Join-Path $projectRoot 'src\PdLinearLowPass.cs'),
    (Join-Path $projectRoot 'src\DialogPlacement.cs'),
    (Join-Path $projectRoot 'src\ProjectMeasurementMode.cs'),
    (Join-Path $projectRoot 'src\PicoCaptureCadence.cs'),
    (Join-Path $projectRoot 'src\OemUiBranding.cs'),
    (Join-Path $projectRoot 'src\R2RoundedButton.cs'),
    (Join-Path $sourceRoot 'CalibrationIncidentQuality.cs'),
    (Join-Path $sourceRoot 'ProjectDefaults.cs'),
    (Join-Path $sourceRoot 'SignalQuality.cs'),
    (Join-Path $sourceRoot 'TdrCorrelation.cs')
)
$sources += @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'PicoCore') -Filter '*.cs' -File | ForEach-Object FullName)
$args = @('/nologo','/target:winexe','/platform:x64','/optimize+','/langversion:7.3',
    '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll',
    ('/out:'+$exe),
    ('/win32icon:'+(Join-Path $sourceRoot 'assets\xiongxin.ico')),
    ('/resource:'+(Join-Path $sourceRoot 'assets\xiongxin.ico')+',Brand.ico'),
    ('/resource:'+(Join-Path $sourceRoot 'assets\xiongxin-original.png')+',Brand.png'))
& $compiler @args @sources
if ($LASTEXITCODE -ne 0) { throw 'Calibration verification compilation failed.' }
Copy-Item -LiteralPath (Join-Path $sourceRoot 'App.config') -Destination ($exe+'.config') -Force
Get-FileHash -LiteralPath $exe -Algorithm SHA256 | Select-Object Path,Hash
