param([string]$OutputRoot = '')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $OutputRoot) { $OutputRoot = Join-Path $PSScriptRoot ('build\verify-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff')) }
$output = [IO.Path]::GetFullPath($OutputRoot)
if (-not $output.StartsWith($PSScriptRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)) {
    throw 'Use a fresh verification directory inside modules-src/ReportEngine.'
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$reference = Join-Path $project 'dependencies\Report\Runtime\HVX.Report.Engine.dll'
if ((Get-FileHash -LiteralPath $reference).Hash -ne '00561E3E4F48D51DB2817296BA9A6F52E3015AB53699BF18FB796BE01295533D') { throw 'Report runtime baseline changed.' }
$candidate = Join-Path $output 'candidate'
$baseline = Join-Path $output 'baseline'
New-Item -ItemType Directory -Path $candidate, $baseline -Force | Out-Null
$common = @('/nologo', '/platform:x64', '/checked+', '/unsafe-', '/utf8output', '/codepage:65001',
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
    '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll',
    '/reference:System.Web.Extensions.dll', '/reference:System.Xml.dll', '/reference:System.Xml.Linq.dll')
$candidateDll = Join-Path $candidate 'HVX.Report.Engine.dll'
& $compiler @common /target:library /optimize+ /debug- ('/out:' + $candidateDll) (Join-Path $PSScriptRoot 'src\AssemblyInfo.cs') (Join-Path $PSScriptRoot 'src\ReportGenerator.cs') (Join-Path $PSScriptRoot 'shared\BoundedDocx.cs') (Join-Path $PSScriptRoot 'shared\ReportOutputSafety.cs')
if ($LASTEXITCODE -ne 0) { throw 'Report candidate compilation failed.' }
$smoke = Join-Path $candidate 'HVX.Report.Engine.Smoke.exe'
& $compiler @common /target:exe /optimize+ ('/out:' + $smoke) ('/reference:' + $candidateDll) (Join-Path $PSScriptRoot 'tests\SmokeTest.cs')
if ($LASTEXITCODE -ne 0) { throw 'Report smoke compilation failed.' }
Copy-Item -LiteralPath $reference -Destination (Join-Path $baseline 'HVX.Report.Engine.dll')
Copy-Item -LiteralPath $smoke -Destination $baseline
foreach ($directory in @($candidate, $baseline)) {
    & (Join-Path $directory 'HVX.Report.Engine.Smoke.exe') (Join-Path $directory 'smoke-output')
    if ($LASTEXITCODE -ne 0) { throw ('Safe synthetic report smoke failed: ' + $directory) }
}

# The legacy compiler has no deterministic switch. Normalize only the COFF
# timestamp, PE checksum and the single GUID heap holding its generated MVID.
# Everything else (including IL, metadata, resources and imports) must match.
function Get-NormalizedPeHash([string]$Path) {
    [byte[]]$bytes = [IO.File]::ReadAllBytes($Path)
    $pe = [BitConverter]::ToInt32($bytes, 0x3c)
    $optional = $pe + 24
    $sections = $optional + [BitConverter]::ToUInt16($bytes, $pe + 20)
    $sectionCount = [BitConverter]::ToUInt16($bytes, $pe + 6)
    function Convert-Rva([int]$Rva) {
        for ($n = 0; $n -lt $sectionCount; $n++) {
            $header = $sections + $n * 40
            $start = [BitConverter]::ToInt32($bytes, $header + 12)
            $length = [BitConverter]::ToInt32($bytes, $header + 16)
            if ($Rva -ge $start -and $Rva -lt $start + $length) {
                return [BitConverter]::ToInt32($bytes, $header + 20) + $Rva - $start
            }
        }
        throw 'PE RVA outside mapped sections.'
    }
    $directoryOffset = if ([BitConverter]::ToUInt16($bytes, $optional) -eq 0x20b) { 112 } else { 96 }
    $clr = Convert-Rva ([BitConverter]::ToInt32($bytes, $optional + $directoryOffset + 14 * 8))
    $metadata = Convert-Rva ([BitConverter]::ToInt32($bytes, $clr + 8))
    $versionLength = [BitConverter]::ToInt32($bytes, $metadata + 12)
    $cursor = $metadata + 16 + $versionLength
    $streamCount = [BitConverter]::ToUInt16($bytes, $cursor + 2)
    $cursor += 4
    $guidFound = $false
    for ($n = 0; $n -lt $streamCount; $n++) {
        $offset = [BitConverter]::ToInt32($bytes, $cursor)
        $length = [BitConverter]::ToInt32($bytes, $cursor + 4)
        $start = $cursor + 8
        $end = $start
        while ($bytes[$end] -ne 0) { $end++ }
        $name = [Text.Encoding]::ASCII.GetString($bytes, $start, $end - $start)
        $cursor = ($end + 4) -band -4
        if ($name -eq '#GUID') {
            if ($length -ne 16) { throw 'Only a single MVID GUID heap may be normalized.' }
            [Array]::Clear($bytes, $metadata + $offset, 16)
            $guidFound = $true
        }
    }
    if (-not $guidFound) { throw 'MVID GUID heap was not found.' }
    [Array]::Clear($bytes, $pe + 8, 4)
    [Array]::Clear($bytes, $optional + 64, 4)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '') }
    finally { $sha.Dispose() }
}
$candidateNormalized = Get-NormalizedPeHash $candidateDll
$baselineNormalized = Get-NormalizedPeHash $reference
$result = [ordered]@{
    Schema = 'XIONGXIN.ReportEngine.SourceVerification.v1'
    CandidateSha256 = (Get-FileHash -LiteralPath $candidateDll).Hash
    BaselineSha256 = (Get-FileHash -LiteralPath $reference).Hash
    CandidateNormalizedPeSha256 = $candidateNormalized
    BaselineNormalizedPeSha256 = $baselineNormalized
    NormalizedPeEquivalent = ($candidateNormalized -eq $baselineNormalized)
    IgnoredFields = @('COFF timestamp', 'PE checksum', 'single generated module MVID')
    CandidateSyntheticSmokePassed = $true
    BaselineSyntheticSmokePassed = $true
    DeploymentPolicy = 'Keep verified Release-v1.4 baseline in this preview.'
    Sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object { [ordered]@{ File = $_.Name; Sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash } })
}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
$result | ConvertTo-Json -Depth 5
