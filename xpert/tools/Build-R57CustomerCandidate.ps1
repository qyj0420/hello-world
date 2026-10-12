[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SourceArchive,
    [string]$OutputRoot=(Join-Path $PSScriptRoot '../build/candidate-r57')
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$output=[IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
$archive=[IO.Path]::GetFullPath($SourceArchive)
if(-not $output.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Use a new isolated build directory.'}
if(-not $archive.StartsWith($root+'\deliverables\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use the explicit source snapshot in deliverables.'}
foreach($path in @($output,$archive)){
    for($walk=$path;$walk.Length -ge $root.Length;$walk=[IO.Path]::GetDirectoryName($walk)){
        if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked build inputs are prohibited.'}
        if($walk -ieq $root){break}
    }
}
Add-Type -AssemblyName System.IO.Compression
$project=$root
$inventoryTool=Join-Path $PSScriptRoot 'Export-PublicBetaNoDriverSource.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($inventoryTool,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'The source whitelist does not parse.'}
foreach($name in @('SafeRelative','IsSourceEligible','LocalPath','ProjectFile','SourceInventory')){
    $nodes=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -ceq $name)
    if($nodes.Count -ne 1){throw 'Ambiguous source inventory helper.'}
    . ([ScriptBlock]::Create($nodes[0].Extent.Text))
}
$archiveHash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
$stream=[IO.File]::OpenRead($archive)
try{
    $zip=New-Object IO.Compression.ZipArchive($stream,[IO.Compression.ZipArchiveMode]::Read,$true)
    try{
        $indexEntry=$zip.GetEntry('SOURCE_INDEX.json');$bindingEntry=$zip.GetEntry('SNAPSHOT_BINDING.json')
        if($null -eq $indexEntry -or $null -eq $bindingEntry -or $indexEntry.Length -gt 16MB -or $bindingEntry.Length -gt 1MB){throw 'Invalid source snapshot metadata.'}
        $reader=New-Object IO.StreamReader($indexEntry.Open());try{$inputs=@($reader.ReadToEnd()|ConvertFrom-Json)}finally{$reader.Dispose()}
        $reader=New-Object IO.StreamReader($bindingEntry.Open());try{$binding=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
        if($binding.Schema -cne 'HVX.PreBuildSourceSnapshot/1' -or $binding.ExecutableBuilt -isnot [bool] -or $binding.ExecutableBuilt -ne $false -or
            $binding.Revision -cne 'r57' -or $binding.Files -ne $inputs.Count -or $inputs.Count -le 100 -or
            $binding.ExporterSHA256 -cne (Get-FileHash -LiteralPath $inventoryTool -Algorithm SHA256).Hash){throw 'Wrong snapshot identity.'}
        $indexed=@{}
        foreach($input in $inputs){
            $relative=SafeRelative ([string]$input.Path)
            if(-not(IsSourceEligible $relative) -or $indexed.ContainsKey($relative) -or $input.SHA256 -cnotmatch '^[A-F0-9]{64}$' -or $input.Bytes -lt 0 -or $input.Bytes -gt 32MB){throw 'Invalid source index.'}
            $indexed[$relative]=$input
        }
        if($zip.Entries.Count -ne $inputs.Count+3){throw 'Source ZIP entry count mismatch.'}
        $seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach($entry in $zip.Entries){
            $relative=SafeRelative $entry.FullName
            if(-not $seen.Add($relative)){throw 'Duplicate source ZIP entry.'}
            if($indexed.ContainsKey($relative)){
                if($entry.Length -ne $indexed[$relative].Bytes){throw 'Source ZIP length mismatch.'}
                $inputStream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
                try{$digest=[BitConverter]::ToString($sha.ComputeHash($inputStream)).Replace('-','')}finally{$sha.Dispose();$inputStream.Dispose()}
                if($digest -cne $indexed[$relative].SHA256){throw 'Source ZIP hash mismatch.'}
            }elseif($relative -notin @('SOURCE_INDEX.json','SNAPSHOT_BINDING.json','BUILDING_SOURCE.md')){throw 'Unexpected source ZIP entry.'}
        }
    }finally{$zip.Dispose()}
}finally{$stream.Dispose()}
$byPath=@{}
function Assert-SnapshotCurrent {
    foreach($entry in $inputs){
        $relative=SafeRelative ([string]$entry.Path)
        $path=ProjectFile $relative
        if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $entry.SHA256){throw ('Source differs from exported snapshot: '+$relative)}
    }
    if((@((SourceInventory)|ConvertTo-Json -Depth 4) -join '') -cne (@($inputs|ConvertTo-Json -Depth 4) -join '')){throw 'Current source inventory differs from snapshot.'}
    if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -cne $archiveHash){throw 'Source archive changed.'}
}
foreach($entry in $inputs){if($byPath.ContainsKey($entry.Path)){throw 'Duplicate snapshot source.'};$byPath[$entry.Path]=$entry}
Assert-SnapshotCurrent
New-Item -ItemType Directory -Path (Join-Path $output 'qa')|Out-Null
$started=[datetime]::UtcNow.ToString('o')
[ordered]@{Schema='HVX.R57.SourceFreeze/1';StartedUtc=$started;SourceArchive=$archive;SourceArchiveSHA256=$archiveHash;Inputs=$inputs;ProductExecuted=$false;HardwareAccessed=$false}|
    ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'qa/source-freeze-initial.json') -Encoding UTF8
& (Join-Path $root 'Build.ps1') -Configuration Release -Hybrid -V30BatchValidation -ProtectedRelease -MaintenancePassword -PublicBetaNoDriver -OutputDirectory $output
Assert-SnapshotCurrent
$manifest=Get-Content -LiteralPath (Join-Path $output 'build-manifest.json') -Raw|ConvertFrom-Json
$modules=Get-Content -LiteralPath (Join-Path $output 'module-manifest.json') -Raw|ConvertFrom-Json
$ui=Get-Content -LiteralPath (Join-Path $output $modules.SourceUiRuntime.ManifestPath) -Raw|ConvertFrom-Json
$compiled=@($manifest.Sources);foreach($module in $ui.Modules){$compiled+=@($module.CompileInputs)}
$checked=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach($entry in $compiled){
    $relative=([string]$entry.Path).Replace('\','/')
    if([IO.Path]::GetExtension($relative) -notin @('.cs','.py','.cpp','.c','.h','.hpp')){continue}
    if(-not $byPath.ContainsKey($relative) -or $byPath[$relative].SHA256 -ine $entry.SHA256){throw ('Compiled source not bound to pre-build snapshot: '+$relative)}
    $null=$checked.Add($relative)
}
if($checked.Count -eq 0){throw 'No compiled source coverage.'}
$exe=Join-Path $output 'Xiongxin.OWTS.Desktop.exe'
if($manifest.FileVersion -cne '1.1.2.0' -or $manifest.ConfigurationProfile -cne 'PublicBeta-NoDriver-1.1.2-20261011' -or
    -not $manifest.ProtectedRelease -or -not $manifest.MaintenancePasswordEnabled -or $manifest.ExecutableSha256 -cne (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash){throw 'Protected EXE identity mismatch.'}
[ordered]@{Schema='HVX.R57.SourceFreezeBuild/1';Revision='r57';StartedUtc=$started;CompletedUtc=[datetime]::UtcNow.ToString('o');Passed=$true
    SourceUnchanged=$true;SourceArchive=$archive;SourceArchiveSHA256=$archiveHash;DistinctCompiledSourceFiles=$checked.Count
    ExecutableSHA256=$manifest.ExecutableSha256;BuildManifestSHA256=(Get-FileHash -LiteralPath (Join-Path $output 'build-manifest.json') -Algorithm SHA256).Hash
    ProductExecuted=$false;SDKCalled=$false;HardwareAccessed=$false;InstallerBuilt=$false;PhysicalAcceptance=$false}|
    ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'qa/source-freeze-build.json') -Encoding UTF8
Write-Output ('R57_FROZEN_BUILD='+$output)
