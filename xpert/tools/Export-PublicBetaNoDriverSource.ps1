[CmdletBinding()]
param(
    [ValidateSet('Audit','SourceFirst','Export')][string]$Mode='Audit',
    [string]$BuildRoot='',
    [string]$FreezeEvidence='',
    [string]$ReleaseEvidence='',
    [string]$ConfidentialityDeferral='',
    [string]$SetupPath='',
    [string]$ExpectedSetupSHA256='',
    [string]$ArchivePath=''
)
# Audit inventories source paths/hashes only. Export is a separate post-package
# freeze action. This script never invokes a compiler, product, worker or SDK.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
function SafeRelative([string]$Path) {
    if([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path) -or $Path -match '[<>:"|?*\x00-\x1f]'){throw 'Unsafe source path.'}
    $p=$Path.Replace('\','/')
    foreach($s in $p.Split('/')){if($s.Length -eq 0 -or $s -in @('.','..') -or $s -cne $s.Trim() -or $s.EndsWith('.') -or $s -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)'){throw 'Ambiguous source path.'}}
    return $p
}
function IsSourceEligible([string]$Path) {
    $p=SafeRelative $Path;$ext=[IO.Path]::GetExtension($p).ToLowerInvariant();$name=[IO.Path]::GetFileName($p)
    # Path-only rejection: do not open or search credential material.
    if($p -match '(?i)(^|/)(build(?:-check)?|bin|obj|deliverables|outputs?|tmp|temp|data|testdata|smoke-output|evidence|qa|records?|logs?|OEMpic|\.git|\.codex|\.aws|\.agents|__pycache__)(/|$)' -or
       $name -match '(?i)(^\.env|credential|secret|private[-_.]?key|password[-_.]?material|maintenance[-_.]?material|device-asset-catalog)' -or
       $ext -in @('.exe','.dll','.pyd','.pdb','.pyc','.zip','.7z','.hxm','.pdm','.loc','.cal','.in','.ini','.csv','.key','.pem','.pfx','.p12','.snk','.kdbx','.db','.sqlite')){return $false}
    # UserSettings.cs/UserConfig.cs are class definitions, not an installed
    # user's saved settings. Apply this name rule only to runtime-data formats.
    if($name -match '(?i)(user[-_.]?(settings|config)|customer[-_.]?config)' -and
       $ext -in @('.json','.xml','.config','.ini','.yaml','.yml','.toml','.txt','.psv','.db','.sqlite')){return $false}
    if($p -in @('Build.ps1','Stage-Modules.ps1','app.manifest','App.config','README.md','README.txt','AGENTS.md')){return $true}
    if($p -match '^(src|hybrid-src|modules-src|tests)/'){
        if($ext -in @('.cs','.py','.cpp','.c','.h','.hpp','.csproj','.sln','.props','.targets','.resx','.ps1','.cmd','.config')){return $true}
        if($p -match '^modules-src/[^/]+/assets/[^/]+$' -and $ext -in @('.png','.ico')){return $true}
        # Only named provenance/contracts, never arbitrary JSON configurations.
        return $ext -eq '.json' -and $name -match '(?i)(manifest|provenance|adaptation|confirmed-legacy-inputs|project-input-source)'
    }
    if($p -match '^tools/[^/]+$'){return $ext -in @('.ps1','.py','.json','.md','.txt')}
    if($p -match '^tools/compiler/[^/]+$'){return $ext -in @('.props','.targets','.rsp') -or $name -match '^(LICENSE|README|NOTICE)'}
    if($p -in @('installer/XIONGXIN-PublicBeta-NoDriver.iss','installer/EncryptedModels.iss','installer/InteractiveInstall.iss')){return $true}
    if($p -match '^assets/(i18n|shortcuts)/'){return $ext -in @('.txt','.psv','.ico')}
    if($p -match '^assets/xiongxin[^/]*\.(ico|png)$'){return $true}
    # Do not sweep historical diagnostics/incident prose into a source release.
    if($p -in @('docs/PublicBeta_NoDriver_Packaging_Audit_20261011.md','docs/PublicBeta_NoDriver_Installation_20261011.txt')){return $true}
    if($p -match '^docs/international/electrical-glossary\.(html|pdf|json)$'){return $true}
    if($p -match '^dependencies/(Algorithms|Documentation|Protection|ResearchRuntime|Report)/'){
        if($name -match '^(LICENSE|NOTICE|README|.*[-_]LICENSE)' -and $ext -in @('.txt','.md')){return $true}
        return $ext -eq '.json' -and $name -match '(?i)(manifest|frozen-runtime-inputs)'
    }
    return $false
}
function LocalPath([string]$Path,[string]$Boundary) {
    if([string]::IsNullOrWhiteSpace($Path)){throw 'An explicit path is required.'}
    $p=[IO.Path]::GetFullPath($Path).TrimEnd('\');$b=[IO.Path]::GetFullPath($Boundary).TrimEnd('\')
    if($p -ine $b -and -not $p.StartsWith($b+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Source export path escaped its boundary.'}
    for($w=$p;$w.Length -ge $b.Length;$w=[IO.Path]::GetDirectoryName($w)){
        if((Test-Path -LiteralPath $w) -and ((Get-Item -LiteralPath $w -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked source/export paths are prohibited.'}
        if($w -ieq $b){break}
    };return $p
}
function ProjectFile([string]$Relative) {
    $p=LocalPath (Join-Path $project (SafeRelative $Relative)) $project
    if(-not(Test-Path -LiteralPath $p -PathType Leaf)){throw ('Missing source file: '+$Relative)};return $p
}
function SourceInventory {
    $rows=New-Object System.Collections.Generic.List[object]
    $pending=New-Object 'System.Collections.Generic.Stack[string]'
    foreach($top in @('src','hybrid-src','modules-src','tests','tools','installer','assets','docs','dependencies')){$pending.Push((LocalPath (Join-Path $project $top) $project))}
    $paths=New-Object System.Collections.Generic.List[string]
    foreach($file in @('Build.ps1','Stage-Modules.ps1','app.manifest','App.config','README.md','README.txt','AGENTS.md')){if(Test-Path -LiteralPath (Join-Path $project $file)){$paths.Add((ProjectFile $file))}}
    while($pending.Count){foreach($item in Get-ChildItem -LiteralPath $pending.Pop() -Force){
        $relative=SafeRelative $item.FullName.Substring($project.Length+1)
        if($item.PSIsContainer){
            if($relative -match '(?i)(^|/)(build(?:-check)?|bin|obj|deliverables|outputs?|tmp|temp|data|testdata|smoke-output|evidence|qa|records?|logs?|OEMpic|\.git|\.codex|\.aws|\.agents|__pycache__|installer-toolchain|HardwareConnector|Task41)(/|$)' -and $relative -notmatch '^modules-src/HardwareConnector$'){continue}
            $null=LocalPath $item.FullName $project;$pending.Push($item.FullName)
        }elseif(IsSourceEligible $relative){$paths.Add((LocalPath $item.FullName $project))}
    }}
    if($paths.Count -gt 15000){throw 'Unexpectedly large source inventory.'}
    foreach($p in $paths|Sort-Object){$info=Get-Item -LiteralPath $p;if($info.Length -gt 32MB){throw 'Oversized source input.'};$rows.Add([pscustomobject]@{Path=(SafeRelative $p.Substring($project.Length+1));Bytes=$info.Length;SHA256=(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash})}
    return $rows.ToArray()
}
function ReadProof([string]$Path,[string]$Boundary) {
    $p=LocalPath $Path $Boundary;$f=Get-Item -LiteralPath $p
    if($f.PSIsContainer -or $f.Length -gt 32MB){throw 'Oversized or invalid release proof.'}
    return [pscustomobject]@{Path=$p;SHA256=(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash;Value=(Get-Content -LiteralPath $p -Raw -Encoding UTF8|ConvertFrom-Json)}
}
function AssertConfidentialityRelease($Release) {
    $accepted=$Release.PSObject.Properties['FullConfidentialityAccepted']
    if($null -eq $accepted -or $accepted.Value -isnot [bool]){throw 'Explicit confidentiality release state is required.'}
    if($Release.ConfidentialityStatus -ceq 'PASS'){
        if(-not $accepted.Value -or $Release.ConfidentialityReviewSHA256 -cnotmatch '^[A-F0-9]{64}$' -or
           -not [string]::IsNullOrEmpty([string]$Release.ConfidentialityDeferralSHA256)){throw 'Invalid completed confidentiality release state.'}
    }elseif($Release.ConfidentialityStatus -ceq 'DEFERRED_BY_USER'){
        if($accepted.Value -or $Release.ConfidentialityDeferralSHA256 -cnotmatch '^[A-F0-9]{64}$' -or
           -not [string]::IsNullOrEmpty([string]$Release.ConfidentialityReviewSHA256)){throw 'A deferred review must not claim confidentiality acceptance.'}
    }else{throw 'Unknown confidentiality release state.'}
}
function AssertCompiledSourceRows($Inventory,$CompileInputs) {
    $byPath=@{};foreach($row in $Inventory){$byPath[(SafeRelative ([string]$row.Path))]=$row}
    $seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $CompileInputs){
        $relative=SafeRelative ([string]$entry.Path)
        if([IO.Path]::GetExtension($relative) -notin @('.cs','.py','.cpp','.c','.h','.hpp')){continue}
        if(-not $byPath.ContainsKey($relative)){throw ('Compiled source absent from source whitelist: '+$relative)}
        if([string]$entry.SHA256 -notmatch '^[A-Fa-f0-9]{64}$' -or $byPath[$relative].SHA256 -ine $entry.SHA256){throw ('Compiled source hash mismatch: '+$relative)}
        $null=$seen.Add($relative)
    }
    if($seen.Count -eq 0){throw 'No compiled source coverage was available.'}
    return [pscustomobject]@{Status='PASS';DistinctCompiledSources=$seen.Count;MissingSources=0;HashMismatches=0}
}
function CompiledSourceCoverage($Inventory,[string]$Root) {
    $buildPath=LocalPath $Root (Join-Path $project 'build')
    if($buildPath -ieq (Join-Path $project 'build')){throw 'Select a specific build for source coverage.'}
    $buildProof=ReadProof (Join-Path $buildPath 'build-manifest.json') $buildPath
    $moduleProof=ReadProof (Join-Path $buildPath 'module-manifest.json') $buildPath
    $uiProof=ReadProof (Join-Path $buildPath (SafeRelative ([string]$moduleProof.Value.SourceUiRuntime.ManifestPath))) $buildPath
    if($buildProof.Value.Schema -cne 'XIONGXIN.UnifiedDesktop.Build.v1' -or $uiProof.Value.Schema -cne 'HVX.UiModuleCompilation/1' -or
       $uiProof.SHA256 -cne $moduleProof.Value.SourceUiRuntime.ManifestSHA256){throw 'Invalid compiled source coverage manifests.'}
    $entries=@($buildProof.Value.Sources);foreach($module in $uiProof.Value.Modules){$entries+=@($module.CompileInputs)}
    $coverage=AssertCompiledSourceRows $Inventory $entries
    foreach($proof in @($buildProof,$moduleProof,$uiProof)){if((Get-FileHash -LiteralPath $proof.Path -Algorithm SHA256).Hash -cne $proof.SHA256){throw 'Compiled source manifest changed during coverage audit.'}}
    return [pscustomobject]@{Status=$coverage.Status;DistinctCompiledSources=$coverage.DistinctCompiledSources;MissingSources=0;HashMismatches=0;
      BuildManifestSHA256=$buildProof.SHA256;ModuleManifestSHA256=$moduleProof.SHA256;UiManifestSHA256=$uiProof.SHA256}
}
$inventory=@(SourceInventory)
$scriptHash=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
if($Mode -eq 'Audit'){
    $coverage=if([string]::IsNullOrWhiteSpace($BuildRoot)){[pscustomobject]@{Status='NOT_REQUESTED'}}else{CompiledSourceCoverage $inventory $BuildRoot}
    [ordered]@{Schema='HVX.PublicBetaNoDriver.SourceAudit/1';Status='WHITELIST_AUDIT_PASS';Files=$inventory.Count;Bytes=($inventory|Measure-Object Bytes -Sum).Sum;
      ExportScriptSHA256=$scriptHash;SourceCount=@($inventory|Where-Object {$_.Path -match '\.(cs|py|cpp|c|h|hpp)$'}).Count;
      CompiledSourceCoverage=$coverage;
      CredentialContentRead=$false;ArchiveCreated=$false;ProductExecuted=$false;HardwareAccessed=$false;
      Limits='Path/extension whitelist only; not a content-level secret scanner or a standalone dependency bundle.'}|ConvertTo-Json
    return
}
# Final action must use actual new build/package identities; no historical r52 pass.
$build=LocalPath $BuildRoot (Join-Path $project 'build')
if($build -ieq (Join-Path $project 'build')){throw 'Select the exact frozen build.'}
$freeze=ReadProof $FreezeEvidence (Join-Path $build 'qa');$release=ReadProof $ReleaseEvidence (Join-Path $project 'build')
$bm=ReadProof (Join-Path $build 'build-manifest.json') $build;$mm=ReadProof (Join-Path $build 'module-manifest.json') $build
$sourceFirst=$Mode -eq 'SourceFirst';$setup=''
if($sourceFirst){
    if(-not [string]::IsNullOrWhiteSpace($SetupPath) -or -not [string]::IsNullOrWhiteSpace($ExpectedSetupSHA256)){throw 'SourceFirst must not claim an installer binding.'}
}else{
    $setup=LocalPath $SetupPath (Join-Path $project 'deliverables')
    if($ExpectedSetupSHA256 -cnotmatch '^[A-F0-9]{64}$' -or (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -cne $ExpectedSetupSHA256){throw 'Explicit final installer SHA256 is missing or changed.'}
}
if($freeze.Value.Schema -cne 'HVX.PublicBetaNoDriver.Freeze/1' -or $release.Value.Schema -cne 'HVX.PublicBetaNoDriver.Release/1' -or
   [IO.Path]::GetFullPath([string]$freeze.Value.BuildRoot).TrimEnd('\') -ine $build -or $release.Value.Profile -cne 'PublicBeta-NoDriver-1.1.2-20261011' -or
   $bm.Value.ConfigurationProfile -cne 'PublicBeta-NoDriver-1.1.2-20261011' -or $release.Value.Version -cne '1.1.2.0' -or $bm.Value.FileVersion -cne '1.1.2.0' -or
   $release.Value.FreezeSHA256 -cne $freeze.SHA256 -or
   $freeze.Value.BuildManifestSHA256 -cne $bm.SHA256 -or $freeze.Value.ModuleManifestSHA256 -cne $mm.SHA256 -or
   $freeze.Value.ExecutableSHA256 -cne $bm.Value.ExecutableSha256 -or $release.Value.ExecutableSHA256 -cne $bm.Value.ExecutableSha256){throw 'Final package/build/source identity mismatch.'}
foreach($field in @('ProtectedRelease','MaintenancePasswordEnabled')){if($release.Value.$field -isnot [bool] -or -not $release.Value.$field){throw 'Protected final lane evidence is required.'}}
if($release.Value.DriverInstallerIncluded -isnot [bool] -or $release.Value.DriverInstallerIncluded){throw 'This is not the approved no-driver release.'}
AssertConfidentialityRelease $release.Value
$deferral=$null
if($release.Value.ConfidentialityStatus -ceq 'DEFERRED_BY_USER'){
    $deferral=ReadProof $ConfidentialityDeferral (Join-Path $build 'qa')
    $receipt=$deferral.Value
    if($deferral.SHA256 -cne $release.Value.ConfidentialityDeferralSHA256 -or $receipt.Schema -cne 'HVX.AlgorithmConfidentialityDeferral/1' -or
       $receipt.Status -cne 'DEFERRED_BY_USER' -or $receipt.Version -cne $release.Value.Version -or $receipt.Profile -cne $release.Value.Profile -or
       [IO.Path]::GetFullPath([string]$receipt.BuildRoot).TrimEnd('\') -ine $build -or $receipt.ExecutableSHA256 -cne $release.Value.ExecutableSHA256 -or
       $receipt.FreezeSHA256 -cne $freeze.SHA256){throw 'Source release deferral identity mismatch.'}
    foreach($field in @('FullConfidentialityAccepted','ReviewCompleted','HardwareAccessed')){
        if($receipt.$field -isnot [bool] -or $receipt.$field){throw 'Deferral cannot claim completion or hardware acceptance.'}
    }
    if($receipt.NotAConfidentialityPass -isnot [bool] -or -not $receipt.NotAConfidentialityPass){throw 'Missing explicit non-acceptance statement.'}
}elseif(-not [string]::IsNullOrWhiteSpace($ConfidentialityDeferral)){throw 'Completed-review release cannot include a deferral.'}
$hostExe=Join-Path $build 'Xiongxin.OWTS.Desktop.exe'
if((Get-Item -LiteralPath $hostExe).VersionInfo.FileVersion -cne '1.1.2.0' -or
   $bm.Value.HostCompilation.CompilerDefines -cnotcontains 'MAINTENANCE_PASSWORD_GUARD' -or
   $bm.Value.HostCompilation.CompilerDefines -cnotcontains 'PUBLIC_BETA_NO_DRIVER_V112_20261011' -or
   $bm.Value.HostCompilation.CompilerDefines -cnotcontains 'PUBLIC_BETA_NO_DRIVER_20261011'){throw 'Actual V1.1.2 protected host identity is required.'}
if((Get-FileHash -LiteralPath (LocalPath $hostExe $build) -Algorithm SHA256).Hash -cne $release.Value.ExecutableSHA256){throw 'Frozen executable changed.'}
$byPath=@{};foreach($row in $inventory){$byPath[$row.Path]=$row}
$compileInputs=@($bm.Value.Sources)
$ui=ReadProof (Join-Path $build (SafeRelative ([string]$mm.Value.SourceUiRuntime.ManifestPath))) $build
if($ui.SHA256 -cne $mm.Value.SourceUiRuntime.ManifestSHA256){throw 'UI build source proof changed.'}
foreach($module in $ui.Value.Modules){$compileInputs+=@($module.CompileInputs)}
$sourceCoverage=AssertCompiledSourceRows $inventory $compileInputs
$omittedBuildInputs=New-Object System.Collections.Generic.List[object]
foreach($entry in $compileInputs){
    $relative=SafeRelative ([string]$entry.Path)
    if($byPath.ContainsKey($relative)){if($byPath[$relative].SHA256 -cne $entry.SHA256){throw ('Compiled source changed: '+$relative)};continue}
    # Generated pins and cross-module references are bound but never copied.
    # No generated source/credential file is read under this exception.
    if($relative -notmatch '^build/' -or [IO.Path]::GetExtension($relative) -notin @('.dll','.json','.txt') -or
       [IO.Path]::GetFileName($relative) -notmatch '^(native-pin\.txt|legacy-unit-pins\.json|HVX\.[A-Za-z0-9_.-]+\.dll)$'){throw ('Compiled input absent from approved source whitelist: '+$relative)}
    $p=LocalPath (Join-Path $project $relative) $build
    if(-not(Test-Path -LiteralPath $p -PathType Leaf)){
        $prefix=(SafeRelative $build.Substring($project.Length+1))+'/'
        if(-not $relative.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Missing generated input is outside the frozen build.'}
        $staged=$relative.Substring($prefix.Length)
        if($staged -cmatch '^(module-build-[a-f0-9]{32})/modules/legacy-unit-pins\.json$'){$p=LocalPath (Join-Path $build ($Matches[1]+'/verification/legacy-unit-pins.json')) $build}
        elseif($staged -cmatch '^(module-build-[a-f0-9]{32})/modules/DataAnalysis/HVX\.PdReview\.V1\.0\.dll$'){$p=LocalPath (Join-Path $build ($Matches[1]+'/protection/PdReview/input/HVX.PdReview.V1.0.dll')) $build}
        else{throw 'Missing generated input has no approved staged relocation.'}
    }
    if((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -cne $entry.SHA256){throw 'Generated build-reference binding changed.'}
    $omittedBuildInputs.Add([pscustomobject]@{Name=[IO.Path]::GetFileName($relative);SHA256=$entry.SHA256})
}
if([string]::IsNullOrWhiteSpace($ArchivePath)){
    $archiveName=if($sourceFirst){'HV-DAC_Xpert_PublicBeta_1.1.2_NoDriver_20261011.BeforeSetup.Source.zip'}else{'HV-DAC_Xpert_PublicBeta_1.1.2_NoDriver_20261011.Source.zip'}
    $ArchivePath=Join-Path $project ('deliverables/'+$archiveName)
}
$archive=LocalPath $ArchivePath (Join-Path $project 'deliverables')
if([IO.Path]::GetExtension($archive) -ine '.zip' -or (Test-Path -LiteralPath $archive)){throw 'Use a new ZIP output; existing exports are preserved.'}
$binding=[ordered]@{Schema='HVX.PublicBetaNoDriver.SourceRelease/1';CreatedUtc=[datetime]::UtcNow.ToString('o');Profile=$release.Value.Profile;
  Version=$release.Value.Version;InstallerPending=$sourceFirst;InstallerSHA256=$ExpectedSetupSHA256;ExecutableSHA256=$release.Value.ExecutableSHA256;FreezeSHA256=$freeze.SHA256;
  ConfidentialityStatus=$release.Value.ConfidentialityStatus;FullConfidentialityAccepted=$release.Value.FullConfidentialityAccepted;
  ConfidentialityReviewSHA256=$release.Value.ConfidentialityReviewSHA256;ConfidentialityDeferralSHA256=$release.Value.ConfidentialityDeferralSHA256;
  BuildManifestSHA256=$bm.SHA256;ModuleManifestSHA256=$mm.SHA256;ReleaseEvidenceSHA256=$release.SHA256;ExportScriptSHA256=$scriptHash;
  Files=$inventory.Count;CompiledSourceCoverage=$sourceCoverage;BoundGeneratedReferencesNotIncluded=$omittedBuildInputs.ToArray();CredentialContentRead=$false;ProductExecuted=$false;HardwareAccessed=$false;
  Omitted='User/installation configuration, maintenance credentials, private keys, data, drivers, binaries, models, build outputs and local toolchain executables. Dependencies must be separately provisioned from reviewed sources. Exact Task12 algorithm source/provenance is unavailable and is not included; this is not a complete all-algorithm source release.'}
Add-Type -AssemblyName System.IO.Compression
$stream=[IO.File]::Open($archive,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
    $zip=New-Object IO.Compression.ZipArchive($stream,[IO.Compression.ZipArchiveMode]::Create,$true)
    try{
        foreach($row in $inventory){
            $source=ProjectFile $row.Path
            if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne $row.SHA256){throw 'Source changed before archiving.'}
            $entry=$zip.CreateEntry($row.Path,[IO.Compression.CompressionLevel]::Optimal);$out=$entry.Open();$input=[IO.File]::OpenRead($source)
            try{$input.CopyTo($out)}finally{$input.Dispose();$out.Dispose()}
        }
        foreach($pair in @(@('SOURCE_INDEX.json',($inventory|ConvertTo-Json -Depth 4)),@('RELEASE_BINDING.json',($binding|ConvertTo-Json -Depth 4)),
          @('BUILDING_SOURCE.md',"# Source snapshot`n`nSee Build.ps1 and the reviewed dependency manifests. Third-party binaries, encrypted models, toolchain executables and target-machine configuration are intentionally excluded. Provision approved dependencies separately; do not copy personal credentials. Exact Task12 algorithm source/provenance is unavailable and is not included; this is not a complete all-algorithm source release. A source snapshot is not a standalone build guarantee or hardware acceptance. The installer and EXE hashes and the actual completed/deferred confidentiality state are recorded in RELEASE_BINDING.json. DEFERRED_BY_USER does not mean confidentiality accepted or encryption complete.`n"))){
            $entry=$zip.CreateEntry($pair[0]);$writer=New-Object IO.StreamWriter($entry.Open(),(New-Object Text.UTF8Encoding($false)));try{$writer.Write($pair[1])}finally{$writer.Dispose()}
        }
    }finally{$zip.Dispose()}
}finally{$stream.Dispose()}
$checkStream=[IO.File]::Open($archive,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try{
    $checkZip=New-Object IO.Compression.ZipArchive($checkStream,[IO.Compression.ZipArchiveMode]::Read,$true)
    try{
        if($checkZip.Entries.Count -ne $inventory.Count+3){throw 'Source ZIP inventory mismatch.'}
        $seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach($entry in $checkZip.Entries){
            $relative=SafeRelative $entry.FullName
            if(-not $seen.Add($relative)){throw 'Duplicate source ZIP entry.'}
            if($byPath.ContainsKey($relative)){
                $input=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
                try{$digest=[BitConverter]::ToString($sha.ComputeHash($input)).Replace('-','')}finally{$sha.Dispose();$input.Dispose()}
                if($digest -cne $byPath[$relative].SHA256 -or $entry.Length -ne $byPath[$relative].Bytes){throw 'Source ZIP content hash mismatch.'}
            }elseif($relative -notin @('SOURCE_INDEX.json','RELEASE_BINDING.json','BUILDING_SOURCE.md')){throw 'Unexpected source ZIP entry.'}
        }
    }finally{$checkZip.Dispose()}
}finally{$checkStream.Dispose()}
if((@((SourceInventory)|ConvertTo-Json -Depth 4) -join '') -cne (@($inventory|ConvertTo-Json -Depth 4) -join '')){throw 'Source inventory changed during export; do not deliver the archive.'}
foreach($proof in @($freeze,$release,$bm,$mm,$ui)+@($deferral|Where-Object {$null -ne $_})){if((Get-FileHash -LiteralPath $proof.Path -Algorithm SHA256).Hash -cne $proof.SHA256){throw 'Release evidence changed during export; do not deliver.'}}
if((-not $sourceFirst -and (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -cne $ExpectedSetupSHA256) -or (Get-FileHash -LiteralPath $hostExe -Algorithm SHA256).Hash -cne $release.Value.ExecutableSHA256){throw 'Final installer or EXE changed during export.'}
[ordered]@{Status=$(if($sourceFirst){'SOURCE_EXPORTED_BEFORE_SETUP'}else{'SOURCE_EXPORTED_NOT_EXECUTED'});Archive=$archive;ArchiveSHA256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash;Files=$inventory.Count;
  InstallerPending=$sourceFirst;InstallerSHA256=$ExpectedSetupSHA256;ExecutableSHA256=$release.Value.ExecutableSHA256;ProductExecuted=$false;HardwareAccessed=$false}|ConvertTo-Json
