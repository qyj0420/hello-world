[CmdletBinding()]
param([string]$ZipPath='C:\Users\qyj04\Downloads\Eplus_Corrected_Export_20261012_r01.zip')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if([IO.Path]::GetFileName($root) -cne 'r58-hardware-source-20261012'){throw 'Use only the isolated r58 source tree.'}
$target=Join-Path $root 'provenance/eplus-corrected-20261012-r01'
if(Test-Path -LiteralPath $target){throw 'Import destination already exists; never overwrite provenance.'}
foreach($path in @($root,$ZipPath,(Split-Path -Parent $target))){
    for($walk=[IO.Path]::GetFullPath($path);$walk;$walk=[IO.Path]::GetDirectoryName($walk)){
        if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked paths are not accepted.'}
    }
}
$zip=[IO.Path]::GetFullPath($ZipPath)
$zipHash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
$archive=[IO.Compression.ZipFile]::OpenRead($zip)
try{
    if($archive.Entries.Count -gt 2048){throw 'Archive entry limit exceeded.'}
    $seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $plans=New-Object 'System.Collections.Generic.List[object]';[long]$total=0
    foreach($entry in $archive.Entries){
        $name=$entry.FullName.Replace('\','/')
        if(!$name -or $name.StartsWith('/') -or $name.Contains(':') -or $name.IndexOf([char]0) -ge 0){throw 'Invalid archive path.'}
        $directory=$name.EndsWith('/');$parts=$name.TrimEnd('/').Split('/')
        if($parts[0] -cne 'package'){throw 'Unexpected archive root.'}
        foreach($part in $parts){
            if(!$part -or $part -eq '.' -or $part -eq '..' -or $part -match '[<>"|?*]' -or $part.EndsWith('.') -or $part.EndsWith(' ') -or
                $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)'){throw ('Unsafe archive component: '+$name)}
        }
        $attributes=[uint32]$entry.ExternalAttributes
        $kind=($attributes -shr 16) -band 0xF000
        if(($attributes -band 0x400) -ne 0 -or $kind -notin @(0,0x8000,0x4000)){throw 'Archive links and special files are not accepted.'}
        $relative=$name.TrimEnd('/');if(!$seen.Add($relative)){throw 'Duplicate/case-colliding archive path.'}
        $destination=[IO.Path]::GetFullPath((Join-Path $target $relative))
        if(!$destination.StartsWith($target+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Archive path escaped import directory.'}
        if($entry.Length -gt 16777216 -or $entry.Length -lt 0){throw 'Archive per-file size limit exceeded.'}
        $total+=$entry.Length;if($total -gt 134217728){throw 'Archive aggregate size limit exceeded.'}
        if($directory -and $entry.Length -ne 0){throw 'Directory entry has data.'}
        $plans.Add([ordered]@{Entry=$entry;Path=$destination;Relative=$relative;Directory=$directory})
    }
    # Validate the whole archive before creating any destination file.
    $null=New-Item -ItemType Directory -Path $target
    $records=New-Object 'System.Collections.Generic.List[object]'
    foreach($plan in $plans){
        if($plan.Directory){$null=New-Item -ItemType Directory -Path $plan.Path -Force;continue}
        $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($plan.Path)) -Force
        $input=$plan.Entry.Open();$output=New-Object IO.FileStream($plan.Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{
            $buffer=New-Object byte[] 65536;[long]$readTotal=0
            while(($count=$input.Read($buffer,0,$buffer.Length)) -gt 0){$readTotal+=$count;if($readTotal -gt $plan.Entry.Length -or $readTotal -gt 16777216){throw 'Archive actual expanded bytes exceeded budget.'};$output.Write($buffer,0,$count)}
            if($readTotal -ne $plan.Entry.Length){throw 'Archive expanded length mismatch.'}
        }finally{$output.Dispose();$input.Dispose()}
        $records.Add([ordered]@{Path=$plan.Relative;Bytes=$readTotal;SHA256=(Get-FileHash -LiteralPath $plan.Path -Algorithm SHA256).Hash})
    }
    if((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -cne $zipHash){throw 'Archive changed during import.'}
    [ordered]@{Schema='HVX.R58.EplusCorrectedImport/1';ImportedUtc=[DateTime]::UtcNow.ToString('o');ZIP=$zip;ZIPBytes=(Get-Item -LiteralPath $zip).Length;ZIPSHA256=$zipHash;EntryCount=$plans.Count;ExpandedBytes=$total;Files=@($records.ToArray());ContentIsUntrustedMaterial=$true;ProductOrSDKExecuted=$false}|
        ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $target 'import-receipt.json') -Encoding UTF8
    'SAFE_EPLUS_IMPORT_PASS '+$target+' entries='+$plans.Count+' bytes='+$total
}finally{$archive.Dispose()}
