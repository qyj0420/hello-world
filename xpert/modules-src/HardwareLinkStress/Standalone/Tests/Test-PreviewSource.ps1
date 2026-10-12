[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$standaloneRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$formPath = Join-Path $standaloneRoot 'StressStandaloneForm.cs'
$registrationPath = Join-Path $standaloneRoot 'HostRegistration.Preview.cs'
$programPath = Join-Path $standaloneRoot 'Program.cs'
$buildPath = Join-Path $standaloneRoot 'Build-Preview.ps1'
$manifestPath = Join-Path $standaloneRoot 'app.manifest'
$iconPath = Join-Path $standaloneRoot 'assets\xiongxin.ico'
$logoPath = Join-Path $standaloneRoot 'assets\xiongxin-original.png'

foreach ($required in @($formPath, $registrationPath, $programPath, $buildPath,
        $manifestPath, $iconPath, $logoPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Preview source missing: $required"
    }
}

$form = Get-Content -LiteralPath $formPath -Raw
$registration = Get-Content -LiteralPath $registrationPath -Raw
$program = Get-Content -LiteralPath $programPath -Raw
$build = Get-Content -LiteralPath $buildPath -Raw
$manifest = Get-Content -LiteralPath $manifestPath -Raw

function Require-Match([string]$value, [string]$pattern, [string]$reason) {
    if ($value -notmatch $pattern) { throw $reason }
}

function Forbid-Match([string]$value, [string]$pattern, [string]$reason) {
    if ($value -match $pattern) { throw $reason }
}

Require-Match $registration 'CreateVerifiedHost\s*\(\s*\)\s*\{\s*return\s+null\s*;' 'Preview host is no longer inert; review the hardware boundary.'
Require-Match $program 'new\s+StressStandaloneForm\s*\(\s*HostRegistration\.CreateVerifiedHost\s*\(\s*\)\s*\)' 'Entry point bypasses controlled host registration.'
Require-Match $form 'verifiedHost\s*==\s*null\s*\?\s*CreatePreviewPanel' 'Preview panel is not selected for a null host.'
Require-Match $form 'var\s+start\s*=\s*new\s+Button\s*\{[^}]*Enabled\s*=\s*false' 'Preview hardware start button is not disabled.'
Require-Match $form 'var\s+reconnect\s*=\s*new\s+CheckBox\s*\{[^}]*Enabled\s*=\s*false' 'Preview auto-reconnect checkbox is not disabled.'
Require-Match $form 'Text\s*=\s*hasHost\s*\?' 'Window does not distinguish a live host from preview state.'
Require-Match $build "'/target:winexe'" 'Preview build must target a GUI application.'
Require-Match $build "'/platform:x64'" 'Preview build must target x64.'
Require-Match $build '/win32icon:' 'Preview build has no executable icon.'
Require-Match $build 'Brand\.ico' 'Preview build does not embed the window icon.'
Require-Match $build 'Brand\.png' 'Preview build does not embed the full logo.'
Require-Match $manifest 'requestedExecutionLevel\s+level="asInvoker"' 'Preview must not require elevation.'

$standaloneCode = $form + "`n" + $registration + "`n" + $program
Forbid-Match $standaloneCode '(?m)^\s*\[DllImport\(' 'Preview source contains native calls.'
Forbid-Match $standaloneCode '\b(?:Process\.Start|SxConnectDev|ExactCardStressEndpointFactory|ArtAiOnlyStressEndpoint|PicoInputOnlyStressEndpoint)\b' 'Preview source contains a hardware call.'
Forbid-Match $build 'Adapters\\' 'Preview build includes real hardware adapters.'

foreach ($asset in @($iconPath, $logoPath)) {
    if ((Get-Item -LiteralPath $asset).Length -le 0) { throw "Brand asset is empty: $asset" }
}

Write-Output 'PREVIEW_SOURCE_SMOKE=PASS; static source checks only; no EXE built and no hardware accessed.'
