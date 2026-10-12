param([Parameter(Mandatory=$true)][string]$CandidateDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$candidate=[IO.Path]::GetFullPath($CandidateDirectory)
$manifestPath=Join-Path $candidate 'candidate-manifest.json'
if((Get-FileHash -LiteralPath $manifestPath).Hash -cne '27BF37B8322A7D255F3B87FC3E110839B463F75BE4A84C25D8EF762CFD138B7B'){throw 'Unreviewed candidate manifest.'}
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
if($manifest.Version -cne '1.0.30.9' -or -not $manifest.SourceUnchangedDuringBuildAndTests){throw 'Candidate source not frozen.'}
$freeze=Join-Path $candidate 'source-freeze'
$source=Join-Path $project 'modules-src\FactoryWorker'
$proof=Join-Path $project 'verification\batch-bridge-20261008'
$backup=Join-Path $proof ('worker-before-import-'+[Guid]::NewGuid().ToString('N'))
$entries=@($manifest.Sources|Where-Object {$_.RelativePath.EndsWith('.cs',[StringComparison]::OrdinalIgnoreCase)})
foreach($entry in $entries){
 $relative=[string]$entry.RelativePath
 if([IO.Path]::IsPathRooted($relative)-or $relative.Contains('..')-or $relative.Contains(':')){throw 'Invalid relative source.'}
 $path=Join-Path $freeze $relative
 if((Get-FileHash -LiteralPath $path).Hash -cne $entry.SHA256){throw ('Frozen source changed: '+$relative)}
}
$existing=@(Get-ChildItem -LiteralPath $source -Recurse -File -Filter '*.cs')
$names=@($entries|ForEach-Object {$_.RelativePath.Replace('/','\')})
foreach($file in $existing){
 $relative=$file.FullName.Substring($source.Length+1)
 if($relative -notin $names -and $relative -notin @('UnifiedPressureBridge.cs','UnifiedWorkerEntry.cs')){throw ('Unreviewed local source: '+$relative)}
}
New-Item -ItemType Directory -Path $backup|Out-Null
foreach($file in $existing){
 $destination=Join-Path $backup $file.FullName.Substring($source.Length+1)
 New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force|Out-Null
 Copy-Item -LiteralPath $file.FullName -Destination $destination
}
# Mechanical import from the reviewed freeze; preserve both local adapter files.
foreach($entry in $entries){
 $destination=Join-Path $source $entry.RelativePath
 New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force|Out-Null
 Copy-Item -LiteralPath (Join-Path $freeze $entry.RelativePath) -Destination $destination
 if((Get-FileHash -LiteralPath $destination).Hash -cne $entry.SHA256){throw 'Imported source hash mismatch.'}
}
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $source 'v30-batch-upstream-manifest.json')
[ordered]@{Schema='HVX.Pressure.BatchSourceImport/1';SourceVersion='1.0.30.9';SourceCandidate=$candidate;
 SourceManifestSHA256=(Get-FileHash -LiteralPath $manifestPath).Hash;
 SourceExecutableSHA256='181A121A5AAE4E8F10D3BDFB99B3BBF16AED325267DB940E496F80831505FD41';
 ImportedUtc=[DateTime]::UtcNow.ToString('o');Backup=$backup;HardwareAccessed=$false;PhysicalAcceptance=$false;
 Files=@($entries|ForEach-Object{[ordered]@{Path=$_.RelativePath;FrozenUpstreamSHA256=$_.SHA256;IntegratedSHA256=(Get-FileHash -LiteralPath (Join-Path $source $_.RelativePath)).Hash;ExactUpstream=$true}});
 LocalAdapters=@('UnifiedPressureBridge.cs','UnifiedWorkerEntry.cs')
}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $source 'v30-batch-integration-provenance.json') -Encoding UTF8
Write-Output ('IMPORTED='+$entries.Count+'; BACKUP='+$backup)
