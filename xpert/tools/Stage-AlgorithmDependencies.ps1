param([Parameter(Mandatory = $true)][string]$OutputRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$dependencies = Join-Path $project 'dependencies'
$output = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if (-not $output.StartsWith($project + '\', [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($output) -ne 'modules') {
    throw 'OutputRoot must be a modules directory inside this project.'
}
foreach ($reserved in @('dependencies', 'modules-src', 'src', 'tools', 'assets')) {
    if ($output.StartsWith((Join-Path $project $reserved) + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Module staging cannot overwrite source or dependency inputs.'
    }
}

function Resolve-ContainedPath([string]$Root, [string]$Relative) {
    if ([IO.Path]::IsPathRooted($Relative)) { throw 'Absolute dependency paths are prohibited.' }
    $full = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $full.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Dependency path escapes its declared root.'
    }
    $walk = $full
    while ($walk.Length -ge $project.Length) {
        if ((Test-Path -LiteralPath $walk) -and
            ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw ('Linked dependency path is prohibited: ' + $walk)
        }
        $walk = [IO.Path]::GetDirectoryName($walk)
    }
    return $full
}

$manifest = Get-Content -LiteralPath (Join-Path $dependencies 'Algorithms\runtime-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.Schema -ne 'XIONGXIN.LocalAlgorithmDependencies.v1' -or @($manifest.Files).Count -ne 12) {
    throw 'Unexpected algorithm dependency manifest.'
}
$copies = @()
$destinations = @{}
foreach ($entry in $manifest.Files) {
    $source = Resolve-ContainedPath $dependencies $entry.Source
    if ($entry.Sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
        -not (Test-Path -LiteralPath $source -PathType Leaf) -or
        (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.Sha256) {
        throw ('Verified dependency is missing or changed: ' + $entry.Source)
    }
    foreach ($relative in $entry.Targets) {
        if ($relative -notmatch '^(DataAnalysis|PressureTest|ReportGenerator)/' -or
            [IO.Path]::GetExtension($relative) -notin @('.dll', '.pdm', '.txt', '.docx')) {
            throw 'Unsupported algorithm staging target.'
        }
        $destination = Resolve-ContainedPath $output $relative
        if ($destinations.ContainsKey($destination)) { throw 'Duplicate dependency target.' }
        $destinations[$destination] = $true
        $copies += [pscustomobject]@{ Source = $source; Destination = $destination; Relative = $relative; Sha256 = $entry.Sha256 }
    }
}
# All inputs and target paths must pass before the first output mutation.
foreach ($copy in $copies) {
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($copy.Destination)) -Force | Out-Null
    Copy-Item -LiteralPath $copy.Source -Destination $copy.Destination -Force
    if ((Get-FileHash -LiteralPath $copy.Destination -Algorithm SHA256).Hash -ne $copy.Sha256) {
        throw ('Staged dependency failed verification: ' + $copy.Relative)
    }
}
Write-Output ('ALGORITHM_DEPENDENCIES_STAGED=' + $copies.Count + '; VERIFIED_INPUTS=' + @($manifest.Files).Count)
