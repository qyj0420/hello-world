param([string]$CandidateRoot=(Join-Path $PSScriptRoot '..\modules-src\LocationABCD\ABRelease'))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$candidate=[IO.Path]::GetFullPath($CandidateRoot).TrimEnd('\')
$manifestPath=Join-Path $candidate 'manifest.json'
$pin='953203C6268C4B55F874E86C1D4D6019648722F406F98CABA6DA17BB88DD0562'
if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -cne $pin) {throw 'Frozen ABC source manifest changed.'}
$manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$entries=@($manifest.files.PSObject.Properties)
if ($entries.Count -lt 125) {throw 'Incomplete frozen AB source set.'}
foreach($entry in $entries){
    $path=[IO.Path]::GetFullPath((Join-Path $candidate $entry.Name))
    if (-not $path.StartsWith($candidate+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Candidate path escaped its root.'}
    $walk=$path
    while($walk.Length -ge $candidate.Length){
        if((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked candidate input is prohibited.'}
        $walk=[IO.Path]::GetDirectoryName($walk)
    }
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.Value){throw ('Frozen candidate changed: '+$entry.Name)}
}
[ordered]@{Candidate='AB_INTERNAL_20261006_R02';ManifestSha256=$pin;Files=$entries.Count;Version='3.0.22.0';Root=$candidate}
