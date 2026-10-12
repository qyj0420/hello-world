[CmdletBinding()]
param(
    [ValidateSet('SourceAudit','RecordDeferral','Stage','Validate','Compile')][string]$Mode='Validate',
    [string]$BuildRoot='',
    [string]$PayloadDirectory='',
    [string]$FreezeEvidence='',
    [string]$MaintenancePasswordProof='',
    [string]$DeploymentProof='',
    [string]$ConfidentialityProof='',
    [ValidateSet('Required','DeferredByUser')][string]$ConfidentialityPolicy='Required',
    [string]$ConfidentialityDeferral='',
    [string]$CurrentEvidence=''
)
# SourceAudit/Validate are read-only. Stage creates a new payload only; Compile
# explicitly invokes ISCC, never setup, the product, a worker, a driver or SDK.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$profile='PublicBeta-NoDriver-1.1.2-20261011'
$define='PUBLIC_BETA_NO_DRIVER_V112_20261011'
$version='1.1.2.0'
$name='HV-DAC_Xpert_1.1.2_r57_NoDriver_20261012'
$netHash='0A3A390C47E639D0F7FC65B21195FEE6B7F65B066F80F70C60FAB191D14B7E40'
$modelHashes=@('5581BF7A60798CB8F349A3EE3A59C2F8C121D44AB98D23172453EC856D360D16','A32A19B489CD7FB0CF51F9D027C214E7636A5621D7663384D88925F2A4CEB32B')
$snapshots=New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
function Plain([string]$Path,[string]$Boundary) {
    if([string]::IsNullOrWhiteSpace($Path)){throw 'A required explicit path is missing.'}
    $full=[IO.Path]::GetFullPath($Path).TrimEnd('\');$limit=[IO.Path]::GetFullPath($Boundary).TrimEnd('\')
    if($full -ine $limit -and -not $full.StartsWith($limit+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Path escaped its approved boundary.'}
    for($walk=$full;$walk.Length -ge $limit.Length;$walk=[IO.Path]::GetDirectoryName($walk)) {
        if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked paths are prohibited.'}
        if($walk -ieq $limit){break}
    }
    return $full
}
function Relative([string]$Path) {
    if([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path) -or $Path -match '[<>:"|?*\x00-\x1f]'){throw 'Invalid relative path.'}
    $value=$Path.Replace('\','/')
    foreach($part in $value.Split('/')){if($part.Length -eq 0 -or $part -in @('.','..') -or $part -cne $part.Trim() -or $part.EndsWith('.') -or $part -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)'){throw 'Ambiguous or unsafe relative path.'}}
    return $value
}
function File([string]$Base,[string]$Path) {
    $full=Plain (Join-Path $Base (Relative $Path)) $Base
    if(-not(Test-Path -LiteralPath $full -PathType Leaf)){throw ('Required file missing: '+$Path)}
    return $full
}
function CompiledInput([string]$Path,[string]$Build) {
    $relative=Relative $Path;$direct=Plain (Join-Path $root $relative) $root
    if(Test-Path -LiteralPath $direct -PathType Leaf){return $direct}
    $prefix=(Relative $Build.Substring($root.Length+1))+'/'
    if(-not $relative.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Missing compiled input is outside the frozen build.'}
    $staged=$relative.Substring($prefix.Length)
    if($staged -cmatch '^(module-build-[a-f0-9]{32})/modules/legacy-unit-pins\.json$'){return File $Build ($Matches[1]+'/verification/legacy-unit-pins.json')}
    if($staged -cmatch '^(module-build-[a-f0-9]{32})/modules/DataAnalysis/HVX\.PdReview\.V1\.0\.dll$'){return File $Build ($Matches[1]+'/protection/PdReview/input/HVX.PdReview.V1.0.dll')}
    throw 'Missing compiled input has no approved staged relocation.'
}
function Bind([string]$Path,[string]$Expected='') {
    $sha=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if($Expected -and ($Expected -notmatch '^[A-Fa-f0-9]{64}$' -or $sha -ine $Expected)){throw ('Hash mismatch: '+$Path)}
    if($snapshots.ContainsKey($Path) -and $snapshots[$Path] -cne $sha){throw 'Input changed during verification.'}
    $snapshots[$Path]=$sha;return $sha
}
function Json([string]$Path) {
    if((Get-Item -LiteralPath $Path).Length -gt 32MB){throw 'Evidence JSON is oversized.'}
    $null=Bind $Path;return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8|ConvertFrom-Json)
}
function Bool($Object,[string]$Key,[bool]$Expected) {
    $p=$Object.PSObject.Properties[$Key]
    if($null -eq $p -or $p.Value -isnot [bool] -or $p.Value -ne $Expected){throw ('Missing or invalid boolean evidence: '+$Key)}
}
function Tree([string]$Base) {
    $null=Plain $Base $Base;$pending=New-Object 'System.Collections.Generic.Stack[string]';$pending.Push($Base);$count=0
    while($pending.Count -gt 0){foreach($item in Get-ChildItem -LiteralPath $pending.Pop() -Force){
        $null=Plain $item.FullName $Base
        if($item.PSIsContainer){$pending.Push($item.FullName)}else{$count++;if($count -gt 10000){throw 'Input inventory exceeds the file limit.'};$item.FullName}
    }}
}
function Signature([string]$Path,[string]$Publisher) {
    $s=Get-AuthenticodeSignature -LiteralPath $Path
    if($s.Status -ne 'Valid' -or $null -eq $s.SignerCertificate -or $s.SignerCertificate.Subject -notmatch $Publisher){throw 'Official tool/prerequisite signature validation failed.'}
}
function Unchanged {foreach($p in @($snapshots.Keys)){if((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -cne $snapshots[$p]){throw 'A verified input changed. Do not publish output.'}}}
function PayloadFingerprint([string]$Base) {
    return (ConvertTo-Json -InputObject @(Tree $Base|Sort-Object|ForEach-Object{[ordered]@{Path=(Relative $_.Substring($Base.Length+1));SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}}) -Compress)
}
function RuntimeInventoryHash($Rows) {
    $ordered=New-Object 'System.Collections.Generic.SortedDictionary[string,string]' ([StringComparer]::Ordinal)
    $seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach($row in $Rows){
        $path=Relative ([string]$row.Path);$sha=[string]$row.SHA256
        if($sha -cnotmatch '^[A-F0-9]{64}$' -or -not $seen.Add($path)){throw 'Invalid or duplicate runtime identity.'}
        $ordered.Add($path,$sha)
    }
    if($ordered.Count -eq 0){throw 'Empty runtime identity.'}
    $canonical=ConvertTo-Json -InputObject @($ordered.Keys|ForEach-Object{[ordered]@{Path=$_;SHA256=$ordered[$_]}}) -Compress
    $algorithm=[Security.Cryptography.SHA256]::Create()
    try{return [BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))).Replace('-','')}finally{$algorithm.Dispose()}
}
function AssertDeferredReceipt($Receipt,[string]$ExpectedBuild,[string]$ExpectedProfile,[string]$ExpectedVersion,[string]$ExpectedExe,[string]$ExpectedFreeze,[string]$ExpectedRuntime,[int]$ExpectedFiles,[datetime]$FrozenUtc) {
    if($Receipt.Schema -cne 'HVX.AlgorithmConfidentialityDeferral/1' -or $Receipt.Status -cne 'DEFERRED_BY_USER' -or
       $Receipt.Profile -cne $ExpectedProfile -or $Receipt.Version -cne $ExpectedVersion -or
       [IO.Path]::GetFullPath([string]$Receipt.BuildRoot).TrimEnd('\') -ine $ExpectedBuild -or
       $Receipt.ExecutableSHA256 -cne $ExpectedExe -or $Receipt.FreezeSHA256 -cne $ExpectedFreeze -or
       $Receipt.RuntimeInventorySHA256 -cne $ExpectedRuntime -or $Receipt.RuntimeFiles -ne $ExpectedFiles -or
       $Receipt.UserDecision -cne 'Full confidentiality review explicitly deferred by the user; source will be provided separately for further encryption.'){
        throw 'Confidentiality deferral does not bind this exact user-authorized release.'
    }
    Bool $Receipt 'FullConfidentialityAccepted' $false;Bool $Receipt 'ReviewCompleted' $false
    Bool $Receipt 'NotAConfidentialityPass' $true;Bool $Receipt 'HardwareAccessed' $false
    $recorded=[datetime]::Parse([string]$Receipt.RecordedUtc,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
    if($recorded -lt $FrozenUtc.ToUniversalTime() -or $recorded -gt [datetime]::UtcNow){throw 'Deferral is stale or dated in the future.'}
}

$installer=File $root 'installer/XIONGXIN-PublicBeta-NoDriver.iss'
$interactive=File $root 'installer/InteractiveInstall.iss'
$modelsInclude=File $root 'installer/EncryptedModels.iss'
$compiler=File $root 'tools/installer-toolchain/InnoSetup/ISCC.exe'
$null=Bind $interactive 'CED9E8DECFE8D0E4D476C7EB7ADE6A7F0DDD6B536478877D01A760EF5DC2B993'
$null=Bind $modelsInclude 'CC3DEDFC05E8AA617326507F94BC177F33372213986B1020469EE8CEAEE8925A'
$null=Bind $compiler '0A8757031B33777E4C9CBFFEE40F11A5062B36D25CBE144C1DB73B6102B80AD7'
$null=Bind (File $root 'assets/shortcuts/256.ico') '3B9F43CC1860C2C38F444A84D0AEED77A696A5C293D6DD9843A04A735A0F9285'
Signature $compiler 'CN=Pyrsys B\.V\.(?:,|$)'
$scriptText=Get-Content -LiteralPath $installer -Raw -Encoding UTF8
foreach($token in @('AppId={{2DAB4E38-ED9A-402F-AC41-663D0B14198D}','AppVersion=1.1.2.0','DefaultDirName={autopf}\HV-DAC Xpert\Public Beta 1.1.2 20261011-NoDriver',
    'UsePreviousAppDir=no','DisableDirPage=yes','PrivilegesRequired=admin','CloseApplications=no','RestartApplications=no','SetupIconFile=..\assets\shortcuts\256.ico',
    'Name: "{app}\OEMpic"; Permissions: users-modify; Flags: uninsneveruninstall','DesktopShortcut.ico','#include "EncryptedModels.iss"','#include "InteractiveInstall.iss"',
    "WizardForm.DirEdit.Text := ExpandConstant('{autopf}\HV-DAC Xpert\Public Beta 1.1.2 20261011-NoDriver');")){
    if(-not $scriptText.Contains($token)){throw ('Required isolated installer setting missing: '+$token)}
}
if($scriptText -match '(?im)^\s*\[(Run|Registry|InstallDelete|UninstallDelete)\]' -or $scriptText -match '(?i)Task41|LiveStress|HV-DAC-Xpert-Setup_' -or
    @($scriptText -split '\r?\n'|Where-Object {$_ -match '(?i)Permissions:' -and $_ -cne 'Name: "{app}\OEMpic"; Permissions: users-modify; Flags: uninsneveruninstall'}).Count){throw 'Driver handoff, deletion rules or broad application ACLs are prohibited.'}
$null=Bind $installer;$null=Bind $PSCommandPath
$copy=New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
function AddCopy([string]$Source,[string]$Target,[string]$Expected='') {
    $relative=Relative $Target;if($copy.ContainsKey($relative)){throw 'Duplicate payload destination.'}
    $copy.Add($relative,[pscustomobject]@{Source=$Source;SHA256=(Bind $Source $Expected)})
}
AddCopy (File $root 'docs/PublicBeta_NoDriver_Installation_20261011.txt') 'Installation_Readme.txt'
AddCopy (File $root 'assets/shortcuts/256.ico') 'DesktopShortcut.ico'
$manualManifestFile=File $root 'dependencies/Documentation/manual4.1-files.json'
$null=Bind $manualManifestFile 'E34629581AF8529BE97AC70B42C4BEDDA5C8B8CF5666DF32CFEA04741D1AD4A4'
$manualManifest=Json $manualManifestFile
if($manualManifest.Schema -cne 'HVX.BilingualManual.OriginalFiles/1' -or $manualManifest.DocumentVersion -cne '4.1' -or @($manualManifest.Files).Count -ne 83){throw 'Latest reviewed manual manifest is incomplete.'}
$manualSource='C:\Users\qyj04\Documents\ChatGPT\说明书\output\软件帮助文件_中英客户版_修订4.1_20261010_07\docs\manual'
foreach($entry in $manualManifest.Files){
    $relative=Relative ([string]$entry.Path)
    if($relative -ceq '说明书接入说明.txt'){continue}
    if($relative -cnotmatch '^docs/manual/[^:]+\.(html|css|js|png|pdf|docx)$'){throw 'Unapproved manual path/type.'}
    $source=File $manualSource $relative.Substring('docs/manual/'.Length)
    AddCopy $source $relative ([string]$entry.SHA256)
}
foreach($pair in @(@('软件操作使用说明书_中性中文版.pdf','HV-DAC_Xpert_Operation_Manual_ZH.pdf'),@('软件操作使用说明书_中性中文版.docx','HV-DAC_Xpert_Operation_Manual_ZH.docx'),
    @('Software_Operation_Manual_Neutral_EN.pdf','HV-DAC_Xpert_Operation_Manual_EN.pdf'),@('Software_Operation_Manual_Neutral_EN.docx','HV-DAC_Xpert_Operation_Manual_EN.docx'))){
    $key='docs/manual/'+$pair[0];AddCopy $copy[$key].Source ('说明书/'+$pair[1]) $copy[$key].SHA256
}
$glossarySource='C:\Users\qyj04\Documents\ChatGPT\说明书\output\专业术语表_中英对照_20261010'
AddCopy (File $glossarySource '电缆局部放电检测_专业术语表_中英对照.pdf') '专业术语/Electrical_Testing_Terminology_EN-ZH.pdf' 'CA7106DB9E9AE69DC1F41780EA757902A7817C3927444A86C7BF69ECAED6EA50'
AddCopy (File $root 'docs/international/electrical-glossary.html') 'docs/international/electrical-glossary.html' 'EF675BE55C03AF6DF6A03290BE98CD2105F9BD9F9CE624053A72CE6C46621DD5'
AddCopy (File $root 'docs/international/electrical-glossary.pdf') 'docs/international/electrical-glossary.pdf' 'CA7106DB9E9AE69DC1F41780EA757902A7817C3927444A86C7BF69ECAED6EA50'
if($Mode -eq 'SourceAudit'){
    Unchanged
    [ordered]@{Status='SOURCE_AUDIT_PASS';Version=$version;Profile=$profile;DefaultConfidentialityPolicy='Required';AppId='2DAB4E38-ED9A-402F-AC41-663D0B14198D';ManualVersion='4.1';DocumentationAndIconFiles=$copy.Count;
        DriverInstallerIncluded=$false;OnlyWritableApplicationSubfolder='OEMpic';InstallerCompiled=$false;InstallerExecuted=$false;ProductExecuted=$false;HardwareAccessed=$false;InputHashes=$snapshots}|ConvertTo-Json -Depth 4
    return
}

$build=Plain $BuildRoot (Join-Path $root 'build')
if($build -ieq (Join-Path $root 'build')){throw 'Use a specific new isolated build.'}
if([string]::IsNullOrWhiteSpace($PayloadDirectory)){$PayloadDirectory=Join-Path $root ('build/'+$name+'.payload')}
$payload=Plain $PayloadDirectory (Join-Path $root 'build')
if($payload -ieq $build -or $payload.StartsWith($build+'\',[StringComparison]::OrdinalIgnoreCase) -or $build.StartsWith($payload+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Payload must be independent of the frozen build.'}
$exe=File $build 'Xiongxin.OWTS.Desktop.exe';$exeHash=Bind $exe
$bm=Json (File $build 'build-manifest.json');$mm=Json (File $build 'module-manifest.json')
if($bm.Schema -cne 'XIONGXIN.UnifiedDesktop.Build.v1' -or $bm.Configuration -cne 'Release' -or $bm.FileVersion -cne $version -or $bm.ExecutableSha256 -cne $exeHash -or
    $bm.ConfigurationProfile -cne $profile -or (Get-Item -LiteralPath $exe).VersionInfo.FileVersion -cne $version -or
    $mm.Schema -cne 'XIONGXIN.UnifiedDesktop.Modules.v2' -or $mm.Configuration -cne 'Release' -or $mm.Architecture -cne 'x64'){throw 'Wrong actual build/version/profile; historical builds cannot be relabelled.'}
foreach($obj in @($bm,$mm)){Bool $obj 'ProtectedRelease' $true;Bool $obj 'MaintenancePasswordEnabled' $true}
if($bm.HostCompilation.CompilerDefines -cnotcontains 'MAINTENANCE_PASSWORD_GUARD' -or $bm.HostCompilation.CompilerDefines -cnotcontains $define -or
   $bm.HostCompilation.CompilerDefines -cnotcontains 'PUBLIC_BETA_NO_DRIVER_20261011'){throw 'Actual host compiler guard/profile defines are missing.'}
if(@($bm.Sources).Count -eq 0){throw 'Compiled host source inventory is missing.'}
foreach($entry in $bm.Sources){$null=Bind (File $root ([string]$entry.Path)) ([string]$entry.SHA256)}
$uiFile=File $build ([string]$mm.SourceUiRuntime.ManifestPath);$null=Bind $uiFile ([string]$mm.SourceUiRuntime.ManifestSHA256);$ui=Json $uiFile
if($ui.Schema -cne 'HVX.UiModuleCompilation/1' -or $ui.Configuration -cne 'Release' -or $ui.CompilerDefines -cnotcontains 'MAINTENANCE_PASSWORD_GUARD' -or @($ui.Modules).Count -ne 7){throw 'Actual guarded seven-module UI compilation is missing.'}
foreach($obj in @($ui)){Bool $obj 'ProtectedRelease' $true;Bool $obj 'MaintenancePasswordEnabled' $true}
foreach($module in $ui.Modules){Bool $module 'InputsUnchanged' $true;foreach($entry in $module.CompileInputs){$null=Bind (CompiledInput ([string]$entry.Path) $build) ([string]$entry.SHA256)}}
$freezeFile=Plain $FreezeEvidence (Join-Path $build 'qa');$freeze=Json $freezeFile
if($freeze.Schema -cne 'HVX.PublicBetaNoDriver.Freeze/1' -or [IO.Path]::GetFullPath([string]$freeze.BuildRoot).TrimEnd('\') -ine $build -or
    $freeze.ExecutableSHA256 -cne $exeHash -or $freeze.BuildManifestSHA256 -cne $snapshots[(File $build 'build-manifest.json')] -or
    $freeze.ModuleManifestSHA256 -cne $snapshots[(File $build 'module-manifest.json')]){throw 'Freeze does not bind this exact final build.'}
Bool $freeze 'HardwareAccessed' $false
$frozenUtc=[datetime]::Parse($freeze.FrozenUtc).ToUniversalTime();if($frozenUtc -gt [datetime]::UtcNow){throw 'Freeze timestamp is in the future.'}
$maintenanceFile=Plain $MaintenancePasswordProof (Join-Path $build 'qa');$maintenance=Json $maintenanceFile
if($maintenance.Schema -cne 'HVX.MaintenanceAccess.Published/1' -or $maintenance.Status -cne 'PASS' -or $maintenance.ExecutableSHA256 -cne $exeHash -or
    $maintenance.FreezeSHA256 -cne $snapshots[$freezeFile] -or [datetime]::Parse($maintenance.StartedUtc).ToUniversalTime() -lt $frozenUtc -or
    $maintenance.CompileExit -ne 0 -or $maintenance.ExitCode -ne 0 -or $maintenance.Checks -le 0){throw 'Actual maintenance negative tests are missing, stale or failed.'}
foreach($key in @('RuntimeUnchanged','SourceUnchanged','CancellationDenied','InvalidPasswordDenied','DirectEntryGuarded','RoutineEntryPasswordFree')){Bool $maintenance $key $true}
foreach($key in @('HardwareAccessed','ProductExecuted','UserSettingsWritten','CredentialMaterialIncluded')){Bool $maintenance $key $false}
foreach($entry in $maintenance.Bindings){$p=Plain ([string]$entry.Path) $root;$null=Bind $p ([string]$entry.SHA256)}
$deploymentFile=Plain $DeploymentProof (Join-Path $build 'qa');$deployment=Json $deploymentFile
if($deployment.Status -cne 'PASS' -or [IO.Path]::GetFullPath([string]$deployment.BuildRoot).TrimEnd('\') -ine $build -or [datetime]::Parse($deployment.CheckedUtc).ToUniversalTime() -lt $frozenUtc){throw 'Current final deployment proof is missing/stale.'}
Bool $deployment 'HardwareAccessed' $false
$runtime=New-Object System.Collections.Generic.List[object]
foreach($p in @(Tree (Join-Path $build 'modules'))+@(Tree (Join-Path $build 'model-payload'))+@($exe)){
    $relative=Relative $p.Substring($build.Length+1);$sha=Bind $p
    if(@($deployment.Files|Where-Object {([string]$_.Path).Replace('\','/') -ceq $relative -and $_.SHA256 -ceq $sha}).Count -eq 0){throw 'Deployment proof omits a current runtime file.'}
    $runtime.Add([pscustomobject]@{Path=$relative;SHA256=$sha})
}
foreach($entry in $deployment.Files){$null=Bind (File $build ([string]$entry.Path)) ([string]$entry.SHA256)}
$confidentialityGate=File $root 'tools/AlgorithmConfidentialityGate.ps1'
$null=Bind $confidentialityGate
$confidentialityStatus='PASS';$fullConfidentialityAccepted=$true;$reviewHash='';$deferralHash=''
if($ConfidentialityPolicy -eq 'Required'){
    if($Mode -eq 'RecordDeferral' -or -not [string]::IsNullOrWhiteSpace($ConfidentialityDeferral)){throw 'Recording/using a deferral requires explicit DeferredByUser policy.'}
    . $confidentialityGate
    $confidentiality=Assert-AlgorithmConfidentialityReview -BuildRoot $build -ReviewPath $ConfidentialityProof -RuntimeInventory $runtime.ToArray()
    $reviewHash=Bind $confidentiality.Path $confidentiality.SHA256
    foreach($p in $confidentiality.Evidence.Keys){$null=Bind $p $confidentiality.Evidence[$p]}
}else{
    if(-not [string]::IsNullOrWhiteSpace($ConfidentialityProof)){throw 'Full-review and deferred-review evidence cannot be mixed.'}
    $deferralFile=Plain $ConfidentialityDeferral (Join-Path $build 'qa')
    $runtimeHash=RuntimeInventoryHash $runtime.ToArray()
    if($Mode -eq 'RecordDeferral'){
        if(Test-Path -LiteralPath $deferralFile){throw 'Existing deferral is preserved; use a fresh receipt path.'}
        $deferral=[pscustomobject][ordered]@{Schema='HVX.AlgorithmConfidentialityDeferral/1';Status='DEFERRED_BY_USER';RecordedUtc=[datetime]::UtcNow.ToString('o');
            Version=$version;Profile=$profile;BuildRoot=$build;ExecutableSHA256=$exeHash;FreezeSHA256=$snapshots[$freezeFile];
            RuntimeInventorySHA256=$runtimeHash;RuntimeFiles=$runtime.Count;
            UserDecision='Full confidentiality review explicitly deferred by the user; source will be provided separately for further encryption.';
            FullConfidentialityAccepted=$false;ReviewCompleted=$false;NotAConfidentialityPass=$true;HardwareAccessed=$false}
        AssertDeferredReceipt $deferral $build $profile $version $exeHash $snapshots[$freezeFile] $runtimeHash $runtime.Count $frozenUtc
        Unchanged
        $receiptStream=[IO.File]::Open($deferralFile,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$receiptWriter=New-Object IO.StreamWriter($receiptStream,(New-Object Text.UTF8Encoding($false)));try{$receiptWriter.Write(($deferral|ConvertTo-Json -Depth 5))}finally{$receiptWriter.Dispose()}}finally{$receiptStream.Dispose()}
        $savedReceipt=Json $deferralFile
        AssertDeferredReceipt $savedReceipt $build $profile $version $exeHash $snapshots[$freezeFile] $runtimeHash $runtime.Count $frozenUtc
        Unchanged
        [ordered]@{Status='DEFERRED_BY_USER';FullConfidentialityAccepted=$false;Receipt=$deferralFile;ReceiptSHA256=$snapshots[$deferralFile];Version=$version;Profile=$profile;InstallerCompiled=$false;HardwareAccessed=$false}|ConvertTo-Json
        return
    }
    $deferral=Json $deferralFile
    AssertDeferredReceipt $deferral $build $profile $version $exeHash $snapshots[$freezeFile] $runtimeHash $runtime.Count $frozenUtc
    $confidentialityStatus='DEFERRED_BY_USER';$fullConfidentialityAccepted=$false;$deferralHash=$snapshots[$deferralFile]
}
$currentFile=Plain $CurrentEvidence $root;$current=Json $currentFile
if($current.Schema -cne 'HVX.ExternalStaticAudit.CurrentEvidence/1' -or @($current.Results).Count -lt 2){throw 'Current directed regression evidence is missing.'}
Bool $current 'AllCurrentHashBindingsPassed' $true
foreach($row in $current.Results){
    Bool $row 'RecordedTestPassed' $true;Bool $row 'CurrentHashBindingPassed' $true
    if($row.InputCount -le 0 -or $row.CurrentInputMismatchCount -ne 0){throw 'A directed proof has no current source bindings.'}
    $p=File $root ([string]$row.Proof);$null=Bind $p ([string]$row.ProofSHA256);$proof=Json $p
    $entries=@();$base=$root
    if($proof.PSObject.Properties['Inputs']){$entries=@($proof.Inputs)}elseif($proof.PSObject.Properties['CurrentSourceHashes']){$entries=@($proof.CurrentSourceHashes)}
    elseif($proof.PSObject.Properties['Sources']){$entries=@($proof.Sources)}elseif($proof.PSObject.Properties['SourceInventory']){$entries=@($proof.SourceInventory);$base=Join-Path $root 'modules-src/FactoryWorker'}
    if($entries.Count -ne $row.InputCount -or $entries.Count -eq 0){throw 'A directed proof source inventory changed.'}
    foreach($entry in $entries){$input=if([IO.Path]::IsPathRooted($entry.Path)){Plain ([string]$entry.Path) $root}else{File $base ([string]$entry.Path)};$null=Bind $input ([string]$entry.SHA256)}
}

AddCopy $exe 'Xiongxin.OWTS.Desktop.exe' $exeHash
AddCopy (File $build 'Xiongxin.OWTS.Desktop.exe.config') 'Xiongxin.OWTS.Desktop.exe.config'
AddCopy (File $build 'module-manifest.json') 'module-manifest.json'
foreach($p in Tree (Join-Path $build 'assets')){
    if([IO.Path]::GetExtension($p) -notin @('.png','.ico','.jpg','.jpeg','.bmp','.json','.psv','.txt')){throw 'Unexpected executable/source in application assets.'}
    AddCopy $p ('assets/'+$p.Substring((Join-Path $build 'assets').Length+1))
}
foreach($pair in @(@('modules/DataAnalysis/Task12/ONNX_RUNTIME_LICENSE.txt','ONNX_RUNTIME_LICENSE.txt'),@('modules/r71/LICENSE.txt','PYTHON_LICENSE.txt'),@('modules/r71/licenses/numpy-METADATA.txt','NUMPY_LICENSE_METADATA.txt'))){
    AddCopy (File $build $pair[0]) ('licenses/'+$pair[1])
}
AddCopy (File $root 'dependencies/Protection/ConfuserEx-LICENSE.txt') 'licenses/ConfuserEx-LICENSE.txt'
foreach($hash in $modelHashes){$relative='model-payload/'+$hash.ToLowerInvariant()+'.hxm';AddCopy (File $build $relative) $relative $hash}
if(@(Tree (Join-Path $build 'model-payload')).Count -ne 2){throw 'Unexpected separate model payload.'}
AddCopy (File $root 'build/prerequisites/NDP48-x86-x64-AllOS-ENU.exe') 'prerequisites/NDP48-x86-x64-AllOS-ENU.exe' $netHash
Signature $copy['prerequisites/NDP48-x86-x64-AllOS-ENU.exe'].Source 'CN=Microsoft Corporation(?:,|$)'
$release=[ordered]@{Schema='HVX.PublicBetaNoDriver.Release/1';Version=$version;Profile=$profile;ExecutableSHA256=$exeHash;
    ProtectedRelease=$true;MaintenancePasswordEnabled=$true;DriverInstallerIncluded=$false;DriversInstalled=$false;ModelsOutsideApplication=$true;
    HardwareAccessed=$false;PhysicalAcceptance=$false;ApplicationSignature=[string](Get-AuthenticodeSignature -LiteralPath $exe).Status;
    FreezeSHA256=$snapshots[$freezeFile];DeploymentProofSHA256=$snapshots[$deploymentFile];MaintenancePasswordProofSHA256=$snapshots[$maintenanceFile];
    ConfidentialityStatus=$confidentialityStatus;FullConfidentialityAccepted=$fullConfidentialityAccepted;
    ConfidentialityReviewSHA256=$reviewHash;ConfidentialityDeferralSHA256=$deferralHash;CurrentEvidenceSHA256=$snapshots[$currentFile];ManualVersion='4.1';
    ManualManifestSHA256=$snapshots[$manualManifestFile];InstallerCompiled=$false}
if($Mode -eq 'Stage'){
    if(Test-Path -LiteralPath $payload){throw 'Existing payload is preserved; select a new isolated payload directory.'}
    Unchanged;$null=New-Item -ItemType Directory -Path $payload
    foreach($relative in $copy.Keys){$target=Plain (Join-Path $payload $relative) $payload;$null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force;Copy-Item -LiteralPath $copy[$relative].Source -Destination $target;$null=Bind $target $copy[$relative].SHA256}
    $release|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $payload 'release-validation.json') -Encoding UTF8
    $sums=@(Tree $payload|Sort-Object|ForEach-Object{(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash+'  '+(Relative $_.Substring($payload.Length+1))})
    $sums|Set-Content -LiteralPath (Join-Path $payload 'SHA256SUMS.txt') -Encoding UTF8
    Unchanged;Write-Output ('NO_DRIVER_PAYLOAD_STAGED='+$payload);return
}
$actual=@(Tree $payload);$expectedCount=$copy.Count+2
if($actual.Count -ne $expectedCount){throw 'Payload contains missing or additional files.'}
foreach($relative in $copy.Keys){$null=Bind (File $payload $relative) $copy[$relative].SHA256}
$published=Json (File $payload 'release-validation.json')
foreach($key in $release.Keys){if($published.$key -cne $release[$key]){throw ('Staged release binding changed: '+$key)}}
$seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach($line in Get-Content -LiteralPath (File $payload 'SHA256SUMS.txt') -Encoding UTF8){
    if($line -notmatch '^([A-F0-9]{64})  (.+)$'){throw 'Malformed payload checksum manifest.'}
    $sha=$Matches[1];$relative=Relative $Matches[2]
    if($relative -ieq 'SHA256SUMS.txt' -or -not $seen.Add($relative)){throw 'Duplicate or self-referencing payload checksum.'}
    $null=Bind (File $payload $relative) $sha
}
if($seen.Count -ne $actual.Count-1){throw 'Checksum manifest does not cover every payload file.'}
Unchanged
if($Mode -eq 'Validate'){[ordered]@{Status='INSTALLER_INPUT_VALIDATED';Profile=$profile;ExecutableSHA256=$exeHash;Payload=$payload;FileCount=$actual.Count;DriverInstallerIncluded=$false;InstallerCompiled=$false;InstallerExecuted=$false;HardwareAccessed=$false}|ConvertTo-Json;return}
$payloadBeforeCompile=PayloadFingerprint $payload
$output=Plain (Join-Path $root ('deliverables/'+$name+'.Setup.exe')) (Join-Path $root 'deliverables')
if(Test-Path -LiteralPath $output){throw 'Existing installer is preserved; no overwrite is allowed.'}
& $compiler ('/DPayloadDir='+$payload) ('/DOutputDirPath='+(Join-Path $root 'deliverables')) ('/DSetupFileName='+$name+'.Setup') $installer
if($LASTEXITCODE -ne 0 -or -not(Test-Path -LiteralPath $output -PathType Leaf)){throw 'Installer compile failed; output is not accepted.'}
Unchanged
if((PayloadFingerprint $payload) -cne $payloadBeforeCompile){throw 'Payload inventory changed while compiling; do not deliver the output.'}
[ordered]@{Status='INSTALLER_COMPILED_NOT_EXECUTED';Setup=$output;SetupSHA256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash;SetupSignature=[string](Get-AuthenticodeSignature -LiteralPath $output).Status;
    ExecutableSHA256=$exeHash;Profile=$profile;DriverInstallerIncluded=$false;InstallerExecuted=$false;ProductExecuted=$false;HardwareAccessed=$false;PhysicalAcceptance=$false}|ConvertTo-Json
