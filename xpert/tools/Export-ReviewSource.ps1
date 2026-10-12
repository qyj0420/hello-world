param(
    [string]$ReviewRoot = 'D:\codex\HVXUnifiedDesktop_SourceReview',
    [string]$Version = '1.1.3-Internal05_20260928',
    [string]$BuildFolder = 'Preview-1.1.3-Internal05',
    [string]$QaFolder = 'qa-internal05'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ($Version -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Invalid version folder name.' }
$destination = Join-Path ([IO.Path]::GetFullPath($ReviewRoot)) $Version
if (Test-Path -LiteralPath $destination) { throw 'Review snapshot already exists. Existing annotations are never overwritten.' }
$folders = @('src','modules-src','assets','dependencies','tools','tests','docs','installer')
$files = @(Get-ChildItem -LiteralPath $root -File)
foreach ($folder in $folders) { $files += @(Get-ChildItem -LiteralPath (Join-Path $root $folder) -Recurse -File) }
$sensitive = @($files | Where-Object { $_.Extension -in '.snk','.pfx','.pem','.key' -or $_.Name -like '.env*' })
if ($sensitive.Count -gt 0) { throw 'Private material requires manual review before export.' }
New-Item -ItemType Directory -Path $destination | Out-Null
$index = foreach ($file in $files) {
    $relative = $file.FullName.Substring($root.Length).TrimStart('\')
    $target = Join-Path $destination $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hash) { throw "Export mismatch: $relative" }
    [pscustomobject]@{ Path=$relative; Bytes=$file.Length; SHA256=$hash; Category=$(if ($file.Extension -in '.cs','.ps1','.csproj') {'Source / Build'} elseif ($file.Extension -in '.dll','.exe') {'Binary dependency / tool'} else {'Asset / Config / Documentation'}) }
}
$index | Sort-Object Path | Export-Csv -LiteralPath (Join-Path $destination 'SOURCE_INDEX.csv') -NoTypeInformation -Encoding UTF8
$evidence = Join-Path $destination 'release-evidence'
New-Item -ItemType Directory -Path $evidence | Out-Null
foreach ($name in @('build-manifest.json','module-manifest.json')) { Copy-Item -LiteralPath (Join-Path $root ('build\' + $BuildFolder + '\' + $name)) -Destination $evidence }
Copy-Item -LiteralPath (Join-Path $root ('build\' + $QaFolder + '\verification.json')) -Destination $evidence
foreach ($item in Get-ChildItem -LiteralPath (Join-Path $root ('build\'+$QaFolder)) -File | Where-Object { $_.Name -eq 'verification-internal07.json' -or $_.Name -like '*.stdout.txt' -or $_.Name -like '*.stderr.txt' }) {
    Copy-Item -LiteralPath $item.FullName -Destination $evidence
}
Write-Output "REVIEW_SOURCE=$destination"
Write-Output "VERIFIED_FILES=$($index.Count)"
Write-Output "CS_FILES=$(@($files | Where-Object Extension -eq '.cs').Count)"
