[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OutputRoot, [switch]$RunOfflineTests)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if (-not [IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot = Join-Path $project $OutputRoot }
$output = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if (-not $output.StartsWith($project + '\build\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Task39 output must be a subdirectory of the unified project build directory.'
}
function Assert-NoReparse([string]$Path) {
    $walk = [IO.Path]::GetFullPath($Path)
    while (-not [String]::IsNullOrEmpty($walk)) {
        if ((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw ('Linked build input/output is prohibited: ' + $walk)
        }
        $walk = [IO.Path]::GetDirectoryName($walk)
    }
}
Assert-NoReparse $output
$worker = Join-Path $project 'modules-src\Task39InputWorker'
$manifestPath = Join-Path $worker 'provenance\source-manifest.json'
$compiler = Join-Path $PSScriptRoot 'compiler\csc.exe'
foreach ($path in @($compiler, $manifestPath)) {
    Assert-NoReparse $path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Required Task39 input missing: ' + $path) }
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.Schema -ne 'XIONGXIN.Task39.SourceImport.v1') { throw 'Unsupported Task39 source manifest.' }
$known = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$sourcePaths = New-Object 'System.Collections.Generic.List[string]'
foreach ($entry in $manifest.Files) {
    $relative = [string]$entry.Path
    if ($relative -notmatch '^(src/[^/]+\.cs|vendor/Task38/[^/]+\.cs|assets/xiongxin(?:-original\.png|\.ico))$' -or
        -not $known.Add($relative)) { throw ('Invalid/duplicate Task39 manifest entry: ' + $relative) }
    $path = [IO.Path]::GetFullPath((Join-Path $worker $relative))
    if (-not $path.StartsWith($worker + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Task39 source escaped snapshot.' }
    Assert-NoReparse $path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $entry.Bytes -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.SHA256) { throw ('Task39 source snapshot changed: ' + $relative) }
    if ($relative.EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase)) { $sourcePaths.Add($path) }
}
$actual = @(foreach ($part in @('src', 'vendor\Task38', 'assets')) {
    Get-ChildItem -LiteralPath (Join-Path $worker $part) -File | ForEach-Object { $_.FullName.Substring($worker.Length + 1).Replace('\', '/') }
})
if ($known.Count -ne 58 -or $sourcePaths.Count -ne 56 -or $actual.Count -ne $known.Count) { throw 'Task39 snapshot file set is incomplete or contains unreviewed files.' }
foreach ($relative in $actual) { if (-not $known.Contains($relative)) { throw ('Unmanifested Task39 source: ' + $relative) } }

# Equivalent to the upstream build guards: input acquisition cannot drive AO/HV;
# the separately isolated trigger adapter can own only P0.7. Compilation executes neither.
$captureText = Get-Content -LiteralPath (Join-Path $worker 'src\HardwareCapture.cs') -Raw
if ($captureText -match 'ArtDAQ_(Write|CreateAO|CreateDO|ResetDevice)' -or $captureText -match '\.(WriteAoVolts|SetDigitalOutputs|ForceSourceOff)\(') {
    throw 'HardwareCapture may not write ports directly; only the isolated P0.7 adapter is allowed.'
}
$triggerText = Get-Content -LiteralPath (Join-Path $worker 'src\ArtSharedTrigger.cs') -Raw
if ($triggerText -match 'ArtDAQ_(CreateAO|WriteAnalog|ResetDevice)' -or $triggerText -notmatch 'settings.ArtDevice \+ "/port0/line7"' -or $triggerText -notmatch 'ArtDAQ_WriteDigitalLines') {
    throw 'TRG backend must remain a fixed P0.7-only digital adapter without AO or resets.'
}
$target = Join-Path $output 'Task39'
New-Item -ItemType Directory -Path $target -Force | Out-Null
$program = Join-Path $target 'Xiongxin.Task39.InputWorker.exe'
$icon = Join-Path $worker 'assets\xiongxin.ico'
$logo = Join-Path $worker 'assets\xiongxin-original.png'
$arguments = @('/nologo', '/noconfig', '/langversion:7.3', '/target:winexe', '/platform:x64', '/optimize+', '/deterministic+', '/debug-',
    '/main:Xiongxin.ZeroBench.Program', '/r:System.dll', '/r:System.Core.dll', '/r:System.Xml.dll',
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', ('/win32icon:' + $icon),
    ('/resource:' + $icon + ',ZeroBench.Brand.ico'), ('/resource:' + $logo + ',ZeroBench.Brand.Logo'), ('/out:' + $program))
$lines = @(& $compiler @arguments @($sourcePaths.ToArray() | Sort-Object) 2>&1 | ForEach-Object { $_.ToString() })
$compileCode = $LASTEXITCODE
$lines | Set-Content -LiteralPath (Join-Path $target 'compile.log') -Encoding UTF8
foreach ($line in $lines) { Write-Host $line }
if ($compileCode -ne 0 -or -not (Test-Path -LiteralPath $program -PathType Leaf)) { throw 'Task39 input worker compilation failed.' }
$identity = [Reflection.AssemblyName]::GetAssemblyName($program)
$peStream = [IO.File]::OpenRead($program)
try {
    $pe = New-Object IO.BinaryReader($peStream)
    try {
        if ($peStream.Length -lt 64 -or $pe.ReadUInt16() -ne 0x5A4D) { throw 'Invalid Task39 PE header.' }
        $peStream.Position = 0x3C
        $peOffset = $pe.ReadInt32()
        if ($peOffset -lt 64 -or $peOffset -gt $peStream.Length - 26) { throw 'Invalid Task39 PE offset.' }
        $peStream.Position = $peOffset
        if ($pe.ReadUInt32() -ne 0x4550 -or $pe.ReadUInt16() -ne 0x8664) { throw 'Task39 must be an x64 executable.' }
    } finally { $pe.Dispose() }
} finally { $peStream.Dispose() }
if ($identity.Name -ne 'Xiongxin.Task39.InputWorker') { throw 'Unexpected Task39 executable identity.' }
$config = '<?xml version="1.0" encoding="utf-8"?><configuration><startup useLegacyV2RuntimeActivationPolicy="true"><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" /></startup></configuration>'
[IO.File]::WriteAllText(($program + '.config'), $config, (New-Object Text.UTF8Encoding($false)))
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $target 'source-manifest.json') -Force
# Recheck after compiling, so a concurrent edit cannot produce an unpinned payload.
foreach ($entry in $manifest.Files) {
    if ((Get-FileHash -LiteralPath (Join-Path $worker $entry.Path) -Algorithm SHA256).Hash -ne $entry.SHA256) { throw ('Task39 input changed during compilation: ' + $entry.Path) }
}

$testReport = $null
if ($RunOfflineTests) {
    # The upstream self-test writes synthetic captures beside its executable. Use
    # an isolated verification directory; those files must never enter the payload.
    $qa = Join-Path (Split-Path -Parent $output) ('task39-self-test-' + [Guid]::NewGuid().ToString('N'))
    Assert-NoReparse $qa
    New-Item -ItemType Directory -Path $qa | Out-Null
    $testExe = Join-Path $qa 'Xiongxin.Task39.InputWorker.exe'
    Copy-Item -LiteralPath $program -Destination $testExe
    Copy-Item -LiteralPath ($program + '.config') -Destination ($testExe + '.config')
    $testReport = Join-Path $qa 'self-test.txt'
    $process = Start-Process -FilePath $testExe -ArgumentList @('--self-test', ('"' + $testReport + '"')) -PassThru -WindowStyle Hidden
    try {
        if (-not $process.WaitForExit(60000)) {
            # Only this known offline test process; it never opens a device.
            $process.Kill()
            throw ('Task39 offline self-test timed out: ' + $qa)
        }
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $testReport -PathType Leaf)) { throw ('Task39 offline self-test failed: ' + $testReport) }
    } finally { $process.Dispose() }
    Get-Content -LiteralPath $testReport | ForEach-Object { Write-Host $_ }
}
$evidence = [ordered]@{
    Schema = 'XIONGXIN.Task39.InputWorkerBuild.v1'; BuiltUtc = [DateTime]::UtcNow.ToString('o')
    Assembly = $identity.FullName; File = 'Task39/Xiongxin.Task39.InputWorker.exe'; Architecture = 'x64'
    SHA256 = (Get-FileHash -LiteralPath $program -Algorithm SHA256).Hash
    SourceManifestSHA256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    CompilerSHA256 = (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash
    SourceCount = $sourcePaths.Count; OfflineSelfTest = $testReport
    Validation = 'Pinned project-local source, input-only static guards and x64 metadata verified; no device discovery or hardware command executed.'
}
$evidence | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'task39-build-manifest.json') -Encoding UTF8
Write-Output ('Built isolated Task39 input worker: ' + $program)
