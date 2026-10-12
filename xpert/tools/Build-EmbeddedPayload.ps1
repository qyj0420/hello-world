param(
    [Parameter(Mandatory=$true)][string]$ModulesRoot,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$modules=[IO.Path]::GetFullPath($ModulesRoot).TrimEnd('\')
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
foreach($path in @($modules,$output)) {
    if(-not $path.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Embedded module inputs and generated resources must be inside project build.'}
    $walk=$path
    while($walk.Length -ge $project.Length){
        if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw ('Linked payload paths are prohibited: '+$walk)}
        $walk=[IO.Path]::GetDirectoryName($walk)
    }
}
if(-not (Test-Path -LiteralPath $modules -PathType Container)){throw 'Staged modules directory does not exist.'}
if($output.Equals($modules,[StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($modules+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Generated resources must not be inside modules.'}
if(Test-Path -LiteralPath $output){throw 'Use a new, empty output directory for embedded resources.'}
$files=New-Object System.Collections.Generic.List[object]
$pending=New-Object System.Collections.Generic.Stack[string]
$pending.Push($modules)
$paths=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$total=0L
while($pending.Count -gt 0){
    $directory=$pending.Pop()
    foreach($item in @(Get-ChildItem -LiteralPath $directory -Force)){
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw ('Linked payload entry is prohibited: '+$item.FullName)}
        if($item.PSIsContainer){$pending.Push($item.FullName);continue}
        $relative=$item.FullName.Substring($modules.Length+1).Replace('\','/')
        if($relative.Length -gt 220 -or $relative.IndexOf('/') -lt 1 -or -not $paths.Add($relative)){throw ('Invalid or duplicate payload path: '+$relative)}
        foreach($segment in $relative.Split('/')){
            if([String]::IsNullOrWhiteSpace($segment) -or $segment -in @('.','..') -or $segment.EndsWith('.') -or $segment.EndsWith(' ') -or $segment.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $segment -match '^(?i:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)'){throw ('Invalid payload path component: '+$relative)}
        }
        if($item.Length -gt 268435456){throw ('Payload file exceeds 256 MiB: '+$relative)}
        $total+=$item.Length
        if($total -gt 1073741824){throw 'Payload exceeds 1 GiB expanded.'}
        $hash=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
        $files.Add([pscustomobject]@{Relative=$relative;Path=$item.FullName;Length=$item.Length;Hash=$hash;Resource=('HVX.Modules.Blob.'+$hash+'.gz')})
    }
}
if($files.Count -lt 1 -or $files.Count -gt 4096){throw 'Invalid module payload file count.'}
New-Item -ItemType Directory -Path $output | Out-Null
$arguments=New-Object System.Collections.Generic.List[string]
$seen=@{}
foreach($entry in @($files | Sort-Object Relative)){
    if($seen.ContainsKey($entry.Hash)){
        if($seen[$entry.Hash] -ne $entry.Length){throw 'One resource hash has conflicting lengths.'}
        continue
    }
    $blob=Join-Path $output ($entry.Hash+'.gz')
    $input=[IO.File]::Open($entry.Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if($input.Length -ne $entry.Length){throw ('Payload changed while packaging: '+$entry.Relative)}
        $sha=[Security.Cryptography.SHA256]::Create()
        try{$now=[BitConverter]::ToString($sha.ComputeHash($input)).Replace('-','')}finally{$sha.Dispose()}
        if($now -ne $entry.Hash){throw ('Payload changed while packaging: '+$entry.Relative)}
        $input.Position=0
        $destination=[IO.File]::Open($blob,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try {
            $gzip=New-Object IO.Compression.GZipStream($destination,[IO.Compression.CompressionLevel]::Optimal,$true)
            try{$input.CopyTo($gzip)}finally{$gzip.Dispose()}
            $destination.Flush($true)
        } finally {$destination.Dispose()}
    } finally {$input.Dispose()}
    $seen[$entry.Hash]=$entry.Length
    $arguments.Add('/resource:'+$blob+','+$entry.Resource)
}
# Verify duplicate sources as well; deduplication must not hide a concurrently
# replaced consumer DLL even if another file has the same original hash.
foreach($entry in $files){
    if((Get-Item -LiteralPath $entry.Path).Length -ne $entry.Length -or (Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -ne $entry.Hash){throw ('Payload changed before index publication: '+$entry.Relative)}
}
$lines=New-Object System.Collections.Generic.List[string]
$lines.Add('XIONGXIN.Modules.v1')
foreach($entry in @($files | Sort-Object Relative)){$lines.Add($entry.Relative+"`t"+$entry.Length.ToString([Globalization.CultureInfo]::InvariantCulture)+"`t"+$entry.Hash+"`t"+$entry.Resource)}
$indexPath=Join-Path $output 'modules.index'
[IO.File]::WriteAllText($indexPath,([String]::Join("`n",$lines)+"`n"),(New-Object Text.UTF8Encoding($false)))
$arguments.Add('/resource:'+$indexPath+',HVX.Modules.Index.v1')
$bundle=(Get-FileHash -LiteralPath $indexPath -Algorithm SHA256).Hash
$manifest=[ordered]@{Schema='XIONGXIN.EmbeddedModulePayload.v1';BundleHash=$bundle;FileCount=$files.Count;UniqueResources=$seen.Count;ExpandedBytes=$total;IndexPath=$indexPath;ResourceArguments=$arguments.ToArray();NotEncryption=$true}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'embedded-payload.json') -Encoding UTF8
[pscustomobject]$manifest
