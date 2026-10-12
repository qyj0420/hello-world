[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ArchivePath)
# A source-only snapshot, deliberately separate from the final release exporter.
# It cannot assert an EXE/installer identity or bypass their protected-build gates.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$exporter=Join-Path $PSScriptRoot 'Export-PublicBetaNoDriverSource.ps1'
$exporterHash=(Get-FileHash -LiteralPath $exporter -Algorithm SHA256).Hash
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($exporter,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'The reviewed source whitelist does not parse.'}
# Import only these exact pure inventory helpers; never evaluate the exporter entry.
foreach($name in @('SafeRelative','IsSourceEligible','LocalPath','ProjectFile','SourceInventory')){
    $nodes=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -ceq $name)
    if($nodes.Count -ne 1){throw 'Ambiguous inventory helper.'}
    . ([ScriptBlock]::Create($nodes[0].Extent.Text))
}
$archive=LocalPath $ArchivePath (Join-Path $project 'deliverables')
if([IO.Path]::GetExtension($archive) -ine '.zip' -or (Test-Path -LiteralPath $archive)){throw 'Use a new source ZIP; existing deliveries are preserved.'}
$inventory=@(SourceInventory)
if($inventory.Count -le 100){throw 'Unexpectedly small source inventory.'}
$byPath=@{};foreach($row in $inventory){
    if($byPath.ContainsKey($row.Path)){throw 'Duplicate inventory path.'}
    $byPath[$row.Path]=$row
}
$binding=[ordered]@{
    Schema='HVX.PreBuildSourceSnapshot/1';Version='1.1.2.0';Revision='r57';CreatedUtc=[datetime]::UtcNow.ToString('o')
    Files=$inventory.Count;SourceBytes=($inventory|Measure-Object Bytes -Sum).Sum
    ExporterSHA256=$exporterHash;SnapshotToolSHA256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    ExecutableBuilt=$false;ExecutableSHA256='';InstallerBuilt=$false;PhysicalAcceptance=$false
    CredentialContentRead=$false;ProductExecuted=$false;SDKCalled=$false;HardwareAccessed=$false
    Omitted='Credentials, user configuration/data, binaries, drivers, models, toolchain executables and build outputs. Exact Task12 proprietary source is unavailable. This is not a complete all-algorithm or standalone dependency bundle.'
}
$readme=@'
# V1.1.2 r57 source snapshot before EXE compilation

This archive contains the reviewed source whitelist and a SHA256 index. It was
exported before the new EXE. No installer/EXE hash, encryption completion,
all-algorithm completeness or hardware acceptance is claimed by this snapshot.

Build.ps1 remains the protected Release entry. The complete Release profile uses
Hybrid, V30BatchValidation, ProtectedRelease, MaintenancePassword and
PublicBetaNoDriver. It requires the approved local dependencies/toolchain and
installation provisioning; personal credentials must not be copied.

This is the verified r55 delivered source baseline plus the latest offline-tested
AI shared phase selector, linked spectra selection and context-menu fixes.
Concurrent unverified host, hardware and version changes are deliberately not
included. Offline proof does not replace real-device tests. Previously reported
security risks have not been repaired by this debug build.
P0.5 connection-lamp lifecycle work is explicitly deferred to the next phase.

SOURCE_INDEX.json binds every included file. SNAPSHOT_BINDING.json describes this
pre-build artifact. Verify later compile manifests against this index before
claiming that an EXE was compiled from these exact sources. Any source change
requires a new snapshot. Neither this tool nor this archive starts the product.
'@
Add-Type -AssemblyName System.IO.Compression
$stream=[IO.File]::Open($archive,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
    $zip=New-Object IO.Compression.ZipArchive($stream,[IO.Compression.ZipArchiveMode]::Create,$true)
    try{
        foreach($row in $inventory){
            $path=ProjectFile $row.Path
            if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $row.SHA256){throw 'Source changed before snapshot.'}
            $entry=$zip.CreateEntry($row.Path,[IO.Compression.CompressionLevel]::Optimal)
            $input=[IO.File]::OpenRead($path);$output=$entry.Open()
            try{$input.CopyTo($output)}finally{$input.Dispose();$output.Dispose()}
        }
        foreach($pair in @(@('SOURCE_INDEX.json',($inventory|ConvertTo-Json -Depth 4)),@('SNAPSHOT_BINDING.json',($binding|ConvertTo-Json -Depth 5)),@('BUILDING_SOURCE.md',$readme))){
            $entry=$zip.CreateEntry($pair[0]);$writer=New-Object IO.StreamWriter($entry.Open(),(New-Object Text.UTF8Encoding($false)))
            try{$writer.Write($pair[1])}finally{$writer.Dispose()}
        }
    }finally{$zip.Dispose()}
}finally{$stream.Dispose()}
$stream=[IO.File]::OpenRead($archive)
try{
    $zip=New-Object IO.Compression.ZipArchive($stream,[IO.Compression.ZipArchiveMode]::Read,$true)
    try{
        if($zip.Entries.Count -ne $inventory.Count+3){throw 'ZIP count mismatch.'}
        $seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach($entry in $zip.Entries){
            $relative=SafeRelative $entry.FullName
            if(-not $seen.Add($relative)){throw 'Duplicate ZIP path.'}
            if($byPath.ContainsKey($relative)){
                $input=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
                try{$digest=[BitConverter]::ToString($sha.ComputeHash($input)).Replace('-','')}finally{$sha.Dispose();$input.Dispose()}
                if($digest -cne $byPath[$relative].SHA256 -or $entry.Length -ne $byPath[$relative].Bytes){throw 'ZIP byte verification failed.'}
            }elseif($relative -notin @('SOURCE_INDEX.json','SNAPSHOT_BINDING.json','BUILDING_SOURCE.md')){throw 'Unexpected ZIP entry.'}
        }
    }finally{$zip.Dispose()}
}finally{$stream.Dispose()}
if((@((SourceInventory)|ConvertTo-Json -Depth 4) -join '') -cne (@($inventory|ConvertTo-Json -Depth 4) -join '')){throw 'Source changed during snapshot; do not deliver.'}
if((Get-FileHash -LiteralPath $exporter -Algorithm SHA256).Hash -cne $exporterHash){throw 'Whitelist changed during export.'}
[ordered]@{Status='PREBUILD_SOURCE_SNAPSHOT_VERIFIED';Archive=$archive;ArchiveSHA256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    Files=$inventory.Count;Bytes=(Get-Item -LiteralPath $archive).Length;ExecutableBuilt=$false;InstallerBuilt=$false;HardwareAccessed=$false}|ConvertTo-Json
