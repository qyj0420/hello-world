param(
    [ValidateSet('SourceAudit','Validate','Compile')][string]$Mode='Validate',
    [string]$PayloadDirectory='',
    [string]$ExpectedExecutableSHA256='',
    [ValidateSet('06','08')][string]$DeliveryBatch='06',
    [string]$MaintenancePasswordProof=''
)
# Independent 20261010-06/08 lanes. Validate is read-only; only explicit Compile
# invokes the compiler. This does not run setup, the product, a worker or a SDK.
$ErrorActionPreference='Stop'
if($Mode -eq 'Compile' -and $DeliveryBatch -ne '08'){throw 'New password-free historical installer compilation is disabled. SourceAudit/Validate remain available without changing old packages.'}
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$deliveryName='HV-DAC_Xpert_Validation_V30Batch_1.0.30.9_20261010_'+$DeliveryBatch
$expectedPayload=Join-Path $root ('deliverables\'+$deliveryName)
$setupName=$deliveryName+'.Setup'
$output=Join-Path $root ('deliverables\'+$setupName+'.exe')
$netHash='0A3A390C47E639D0F7FC65B21195FEE6B7F65B066F80F70C60FAB191D14B7E40'
$modelHashes=@('5581BF7A60798CB8F349A3EE3A59C2F8C121D44AB98D23172453EC856D360D16',
               'A32A19B489CD7FB0CF51F9D027C214E7636A5621D7663384D88925F2A4CEB32B')
$manualPayloadHashes=New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
$manualSourceFiles=@()

function Assert-SetupPlainPath([string]$Path,[string]$Boundary) {
    $full=[IO.Path]::GetFullPath($Path).TrimEnd('\')
    $limit=[IO.Path]::GetFullPath($Boundary).TrimEnd('\')
    if($full -ine $limit -and -not $full.StartsWith($limit+'\',[StringComparison]::OrdinalIgnoreCase)){
        throw 'Path escapes the approved boundary.'
    }
    for($walk=$full;$walk.Length -ge $limit.Length;$walk=[IO.Path]::GetDirectoryName($walk)){
        if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){
            throw ('Reparse point is prohibited: '+$walk)
        }
        if($walk -ieq $limit){break}
    }
    return $full
}
function Convert-SetupRelativePath([string]$Relative) {
    if([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative -match '[<>:"|?*\x00-\x1f]'){
        throw 'Invalid relative payload path.'
    }
    $normalized=$Relative.Replace('\','/')
    foreach($segment in $normalized.Split('/')){
        if($segment.Length -eq 0 -or $segment -in @('.','..') -or $segment -cne $segment.Trim() -or $segment.EndsWith('.')){
            throw 'Ambiguous or traversing payload path.'
        }
    }
    return $normalized
}
function Get-SetupFile([string]$Base,[string]$Relative) {
    $relativePath=Convert-SetupRelativePath $Relative
    $file=Assert-SetupPlainPath (Join-Path $Base $relativePath) $Base
    if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw ('Required file missing: '+$relativePath)}
    return $file
}
function Get-SetupTreeFiles([string]$Base) {
    $directory=Assert-SetupPlainPath $Base $root
    if(-not(Test-Path -LiteralPath $directory -PathType Container)){throw 'Payload directory is missing.'}
    $pending=New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($directory)
    while($pending.Count -gt 0){
        foreach($item in Get-ChildItem -LiteralPath $pending.Pop() -Force){
            $null=Assert-SetupPlainPath $item.FullName $Base
            if($item.PSIsContainer){$pending.Push($item.FullName)}else{$item.FullName}
        }
    }
}
function Assert-SetupHash([string]$File,[string]$Expected) {
    if($Expected -notmatch '^[A-Fa-f0-9]{64}$' -or (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash -ine $Expected){
        throw ('Integrity mismatch: '+$File)
    }
}
function Assert-SetupSignature([string]$File,[string]$Subject) {
    $signature=Get-AuthenticodeSignature -LiteralPath $File
    if($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Subject -notmatch $Subject){
        throw ('Official dependency signature invalid: '+$File)
    }
    return [ordered]@{Path=$File;SHA256=(Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash;
        Status=[string]$signature.Status;Signer=$signature.SignerCertificate.Subject}
}
function Read-SetupJson([string]$File) {
    if((Get-Item -LiteralPath $File).Length -gt 32MB){throw 'Release JSON exceeds the size limit.'}
    return (Get-Content -LiteralPath $File -Raw -Encoding UTF8|ConvertFrom-Json)
}
function Assert-SetupBoolean($Object,[string]$Name,[bool]$Expected) {
    $property=$Object.PSObject.Properties[$Name]
    if($null -eq $property -or $property.Value -isnot [bool] -or $property.Value -ne $Expected){throw ('Invalid release field: '+$Name)}
}
function Get-SetupPayloadInventory([string]$Payload) {
    $sums=Get-SetupFile $Payload 'SHA256SUMS.txt'
    if((Get-Item -LiteralPath $sums).Length -gt 4MB){throw 'Checksum manifest exceeds the size limit.'}
    $expected=New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach($line in Get-Content -LiteralPath $sums -Encoding UTF8){
        if($line -notmatch '^([a-fA-F0-9]{64})  (.+)$'){throw 'Malformed checksum line.'}
        $hash=$Matches[1].ToUpperInvariant();$relative=Convert-SetupRelativePath $Matches[2]
        if($relative -ieq 'SHA256SUMS.txt' -or $expected.ContainsKey($relative)){throw 'Duplicate or self-referential checksum entry.'}
        $expected.Add($relative,$hash)
    }
    if($expected.Count -eq 0 -or $expected.Count -gt 5000){throw 'Invalid checksum entry count.'}
    $actual=New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach($file in Get-SetupTreeFiles $Payload){
        $relative=Convert-SetupRelativePath $file.Substring($Payload.Length+1)
        if($relative -ieq 'SHA256SUMS.txt'){continue}
        if($actual.ContainsKey($relative)){throw 'Duplicate filesystem payload path.'}
        $maintenanceFile=$DeliveryBatch -eq '08' -and ($relative -cmatch '^(docs/international/electrical-glossary\.(html|pdf)|prerequisites/HV-DAC-Xpert-Setup_1\.4\.8_LiveStress_Neutral\.exe)$' -or $manualPayloadHashes.ContainsKey($relative))
        if(-not $maintenanceFile -and $relative -notmatch '^(Xiongxin\.OWTS\.Desktop\.exe(\.config)?|module-manifest\.json|release-validation\.json|使用说明\.txt|assets/[^:]+|licenses/[^:]+|model-payload/[a-fA-F0-9]{64}\.hxm|prerequisites/NDP48-x86-x64-AllOS-ENU\.exe)$'){
            throw ('Non-publisher file is not allowed in this lane: '+$relative)
        }
        if(-not $expected.ContainsKey($relative)){throw ('Unlisted payload file: '+$relative)}
        Assert-SetupHash $file $expected[$relative]
        $actual.Add($relative,$expected[$relative])
    }
    if($actual.Count -ne $expected.Count){throw 'Checksum manifest contains missing files.'}
    foreach($required in @('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config','module-manifest.json','release-validation.json','使用说明.txt',
                          'assets/xiongxin.ico','assets/shortcuts/256.ico','assets/shortcuts/512.ico',
                          'licenses/ONNX_RUNTIME_LICENSE.txt','licenses/PYTHON_LICENSE.txt','licenses/NUMPY_LICENSE_METADATA.txt','licenses/ConfuserEx-LICENSE.txt',
                          'prerequisites/NDP48-x86-x64-AllOS-ENU.exe')){
        if(-not $actual.ContainsKey($required)){throw ('Publisher payload is incomplete: '+$required)}
    }
    foreach($hash in $modelHashes){
        $relative='model-payload/'+$hash.ToLowerInvariant()+'.hxm'
        if(-not $actual.ContainsKey($relative) -or $actual[$relative] -cne $hash){throw 'Encrypted model does not have the approved exact content.'}
    }
    if(@($actual.Keys|Where-Object {$_ -like 'model-payload/*'}).Count -ne 2){throw 'Unexpected model payload.'}
    foreach($relative in $manualPayloadHashes.Keys){
        if(-not $actual.ContainsKey($relative) -or $actual[$relative] -cne $manualPayloadHashes[$relative]){throw ('Original manual file missing or changed: '+$relative)}
    }
    # The manifest itself is included in the before/after snapshot, not in its own entries.
    $actual.Add('SHA256SUMS.txt',(Get-FileHash -LiteralPath $sums -Algorithm SHA256).Hash)
    return $actual
}
function Convert-SetupInventory($Inventory) {
    return (ConvertTo-Json -InputObject @($Inventory.Keys|Sort-Object|ForEach-Object {[ordered]@{Path=$_;SHA256=$Inventory[$_]}}) -Compress)
}

$installer=Get-SetupFile $root $(if($DeliveryBatch -eq '06'){'installer/XIONGXIN-V30Batch-20261010.iss'}else{'installer/XIONGXIN-V30Batch-20261010-08.iss'})
$interactive=Get-SetupFile $root 'installer/InteractiveInstall.iss'
$modelsInclude=Get-SetupFile $root 'installer/EncryptedModels.iss'
$information=Get-SetupFile $root ('docs/20261010修正版'+$DeliveryBatch+'安装提示.txt')
$setupIcon=Get-SetupFile $root 'assets/xiongxin.ico'
$compiler=Get-SetupFile $root 'tools/installer-toolchain/InnoSetup/ISCC.exe'
# These existing shared includes are deliberately not edited by this lane.
Assert-SetupHash $interactive 'CED9E8DECFE8D0E4D476C7EB7ADE6A7F0DDD6B536478877D01A760EF5DC2B993'
Assert-SetupHash $modelsInclude 'CC3DEDFC05E8AA617326507F94BC177F33372213986B1020469EE8CEAEE8925A'
Assert-SetupHash $setupIcon '8C1FA1541C93DC83C64161AF51065F2752DA40C50C071ECF445CEEDCEE099E77'
Assert-SetupHash $compiler '0A8757031B33777E4C9CBFFEE40F11A5062B36D25CBE144C1DB73B6102B80AD7'
$compilerSignature=Assert-SetupSignature $compiler 'CN=Pyrsys B\.V\.(?:,|$)'
if($DeliveryBatch -eq '08'){
    $manualManifestFile=Get-SetupFile $root 'dependencies/Documentation/manual4.1-files.json'
    Assert-SetupHash $manualManifestFile 'E34629581AF8529BE97AC70B42C4BEDDA5C8B8CF5666DF32CFEA04741D1AD4A4'
    $manualManifest=Read-SetupJson $manualManifestFile
    if($manualManifest.Schema -cne 'HVX.BilingualManual.OriginalFiles/1' -or $manualManifest.DocumentVersion -cne '4.1' -or $manualManifest.ReferenceDelivery -cne '20261010_07' -or $manualManifest.FileCount -ne 83 -or @($manualManifest.Files).Count -ne 83){throw 'Unexpected original bilingual manual manifest.'}
    $manualSourceFiles+=@($manualManifestFile)
    foreach($entry in $manualManifest.Files){
        $relative=Convert-SetupRelativePath ([string]$entry.Path)
        if($relative -cne '说明书接入说明.txt' -and $relative -cnotmatch '^docs/manual/[^:]+\.(html|css|js|png|pdf|docx)$'){throw 'Unapproved manual file type or path.'}
        $source=Get-SetupFile $root ('dependencies/Documentation/manual4.1/'+$relative)
        Assert-SetupHash $source $entry.SHA256
        $manualSourceFiles+=@($source)
        $target=if($relative -ceq '说明书接入说明.txt'){'docs/说明书接入说明_4.1.txt'}else{$relative}
        if($manualPayloadHashes.ContainsKey($target)){throw 'Duplicate manual path.'}
        $manualPayloadHashes.Add($target,[string]$entry.SHA256)
    }
}
$scriptText=Get-Content -LiteralPath $installer -Raw -Encoding UTF8
foreach($token in @(('AppId={{EE583CD3-84B4-45DD-8E37-7A0DA0E30B'+$DeliveryBatch+'}'),
    ('DefaultDirName={autopf}\HV-DAC Xpert\Validation 20261010-'+$DeliveryBatch),'UsePreviousAppDir=no','DisableDirPage=yes',
    'PrivilegesRequired=admin','CloseApplications=no','RestartApplications=no','SetupIconFile=..\assets\xiongxin.ico',
    ('InfoBeforeFile=..\docs\20261010修正版'+$DeliveryBatch+'安装提示.txt'),'#include "EncryptedModels.iss"','#include "InteractiveInstall.iss"',
    ("WizardForm.DirEdit.Text := ExpandConstant('{autopf}\HV-DAC Xpert\Validation 20261010-"+$DeliveryBatch+"');"))){
    if(-not $scriptText.Contains($token)){throw ('Installer isolation or interactive policy missing: '+$token)}
}
$checkedScript=$scriptText
if($DeliveryBatch -eq '08'){
    foreach($requiredManualEntry in @('Source: "{#PayloadDir}\docs\manual\*"; DestDir: "{app}\docs\manual"; Flags: ignoreversion recursesubdirs createallsubdirs',
        'Source: "{#PayloadDir}\docs\说明书接入说明_4.1.txt"; DestDir: "{app}\docs"; Flags: ignoreversion',
        'Name: "{group}\中英软件操作说明书"; Filename: "{app}\docs\manual\index.html"')){
        if(-not $scriptText.Contains($requiredManualEntry)){throw 'Installer omits the reviewed manual deployment or entry.'}
    }
    $approvedRun='Filename: "{app}\prerequisites\HV-DAC-Xpert-Setup_1.4.8_LiveStress_Neutral.exe"; Description: "继续安装原厂驱动与设备管理软件（按原安装器提示操作）"; Flags: postinstall waituntilterminated skipifsilent'
    $runs=[regex]::Matches($scriptText,'(?ms)^\[Run\]\r?\n(.*?)(?=^\[|\z)')
    if($runs.Count -ne 1){throw 'Expected one explicitly reviewed Task41 interactive handoff.'}
    $runLines=@($runs[0].Groups[1].Value -split '\r?\n'|Where-Object {$_ -notmatch '^\s*(;|$)'})
    if($runLines.Count -ne 1 -or $runLines[0] -cne $approvedRun){throw 'Unreviewed executable or arguments in installer handoff.'}
    $checkedScript=$scriptText.Replace($runs[0].Value,'')
}
if($checkedScript -match '(?im)^\s*\[(Run|UninstallRun|Registry|InstallDelete|UninstallDelete)\]' -or
   $scriptText -match '(?im)^\s*(Exec|ShellExec|ExecAsOriginalUser)\s*\(' -or
   @([regex]::Matches($scriptText,'IconFilename: "\{app\}\\assets\\shortcuts\\256\.ico"')).Count -ne 2){
    throw 'Unexpected application execution, mutation or shortcut policy.'
}
$sourceSnapshot=@{}
foreach($file in @($PSCommandPath,$installer,$interactive,$modelsInclude,$information,$setupIcon,$compiler)+$manualSourceFiles){
    $sourceSnapshot[$file]=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
}
if($Mode -eq 'SourceAudit'){
    $runtime=Get-SetupFile $root 'build/prerequisites/NDP48-x86-x64-AllOS-ENU.exe'
    Assert-SetupHash $runtime $netHash
    $runtimeSignature=Assert-SetupSignature $runtime 'CN=Microsoft Corporation(?:,|$)'
    [ordered]@{Status='SOURCE_AUDIT_ONLY';PayloadValidated=$false;InstallerCompiled=$false;
        ExpectedPayload=$expectedPayload;Compiler=$compilerSignature;DotNet=$runtimeSignature;
        SourceSHA256=$sourceSnapshot;HardwareAccessed=$false;InstallerExecuted=$false}|ConvertTo-Json -Depth 5
    return
}
if([string]::IsNullOrWhiteSpace($PayloadDirectory)){$PayloadDirectory=$expectedPayload}
$payload=Assert-SetupPlainPath ([IO.Path]::GetFullPath($PayloadDirectory)) $root
if($payload -ine $expectedPayload){throw 'Only the exact independent publisher output for the selected batch is accepted.'}
$before=Get-SetupPayloadInventory $payload
$release=Read-SetupJson (Get-SetupFile $payload 'release-validation.json')
if($release.Schema -cne 'HVX.V30Batch.ValidationRelease/1' -or $release.Version -cne '1.0.30.9' -or
   $release.Profile -cne 'Validation-V30-Batch-20261008'){
    throw 'Wrong release-validation schema, version or actual configuration profile.'
}
foreach($field in @('PressureStandalone','HardwareAccessed','PhysicalAcceptance','FullRegression','SafetyMaterialsApproved',
                     'WholeSuitePhysicalAcceptance','DriversInstalled')){
    Assert-SetupBoolean $release $field $false
}
Assert-SetupBoolean $release 'ModelsOutsideApplication' $true
Assert-SetupBoolean $release 'Task41CompleteSuiteIncluded' ($DeliveryBatch -eq '08')
if($DeliveryBatch -eq '08'){
    $task41Hash='45AD475BEA28DAACDA7DFF44CB9C3D2DE9D70DAB06879AA23F85CB2112B91F02'
    if($release.Task41InstallerSHA256 -cne $task41Hash){throw 'Task41 original installer identity differs.'}
    Assert-SetupHash (Get-SetupFile $payload 'prerequisites/HV-DAC-Xpert-Setup_1.4.8_LiveStress_Neutral.exe') $task41Hash
    Assert-SetupHash (Get-SetupFile $payload 'docs/international/electrical-glossary.html') 'EF675BE55C03AF6DF6A03290BE98CD2105F9BD9F9CE624053A72CE6C46621DD5'
    Assert-SetupHash (Get-SetupFile $payload 'docs/international/electrical-glossary.pdf') 'CA7106DB9E9AE69DC1F41780EA757902A7817C3927444A86C7BF69ECAED6EA50'
    if($release.ManualVersion -cne '4.1' -or $release.ManualReferenceDelivery -cne '20261010_07' -or $release.ManualManifestSHA256 -cne 'E34629581AF8529BE97AC70B42C4BEDDA5C8B8CF5666DF32CFEA04741D1AD4A4'){throw 'Publisher manual provenance is missing or changed.'}
}
Assert-SetupBoolean $release 'MaintenancePasswordIncluded' ($DeliveryBatch -eq '08')
if($DeliveryBatch -eq '08'){
    # The publisher must bind real protected-program negative tests. A flag or
    # password-looking dialog alone is not sufficient release evidence.
    if([string]::IsNullOrWhiteSpace($MaintenancePasswordProof)){throw 'The maintenance batch requires its actual access-control proof.'}
    $maintenanceFile=Assert-SetupPlainPath ([IO.Path]::GetFullPath($MaintenancePasswordProof)) (Join-Path $root 'build')
    if(-not(Test-Path -LiteralPath $maintenanceFile -PathType Leaf)){throw 'Maintenance proof is missing.'}
    if(-not $release.PSObject.Properties['MaintenancePasswordProofSHA256']){throw 'Publisher omitted its maintenance proof binding.'}
    Assert-SetupHash $maintenanceFile ([string]$release.MaintenancePasswordProofSHA256)
    $maintenance=Read-SetupJson $maintenanceFile
    if($maintenance.Schema -cne 'HVX.MaintenanceAccess.Published/1' -or $maintenance.Status -cne 'PASS' -or
       $maintenance.ExecutableSHA256 -cne $release.ExecutableSHA256 -or $maintenance.FreezeSHA256 -cne $release.FreezeSHA256){
        throw 'Maintenance proof belongs to a different program or freeze.'
    }
    foreach($field in @('RuntimeUnchanged','SourceUnchanged','CancellationDenied','InvalidPasswordDenied','DirectEntryGuarded','RoutineEntryPasswordFree')){
        Assert-SetupBoolean $maintenance $field $true
    }
    foreach($field in @('HardwareAccessed','ProductExecuted','UserSettingsWritten','CredentialMaterialIncluded')){
        Assert-SetupBoolean $maintenance $field $false
    }
    if($null -eq $maintenance.CompileExit -or $maintenance.CompileExit -ne 0 -or $null -eq $maintenance.ExitCode -or $maintenance.ExitCode -ne 0 -or $null -eq $maintenance.Checks -or $maintenance.Checks -le 0){throw 'Maintenance tests did not complete successfully.'}
    $sourceSnapshot[$maintenanceFile]=(Get-FileHash -LiteralPath $maintenanceFile -Algorithm SHA256).Hash
}
foreach($field in @('ExecutableSHA256','FreezeSHA256','DeploymentProofSHA256','LocalAdaptationSHA256')){
    if([string]$release.$field -notmatch '^[A-Fa-f0-9]{64}$'){throw ('Missing publisher evidence hash: '+$field)}
}
$proofs=@($release.Proofs)
foreach($kind in @('UI','Host','Confidentiality')){
    if(@($proofs|Where-Object {$_.Kind -ceq $kind -and [string]$_.SHA256 -match '^[A-Fa-f0-9]{64}$'}).Count -lt 1){
        throw ('Publisher proof kind missing: '+$kind)
    }
}
$allowedProofKinds=@('UI','Host','Confidentiality')
if($DeliveryBatch -eq '08'){
    $allowedProofKinds+='Maintenance'
    if(@($proofs|Where-Object {$_.Kind -ceq 'Maintenance' -and $_.SHA256 -ceq $release.MaintenancePasswordProofSHA256}).Count -ne 1){throw 'Maintenance publisher proof reference is missing or ambiguous.'}
}
foreach($proof in $proofs){
    if($proof.Kind -notin $allowedProofKinds -or [string]$proof.SHA256 -notmatch '^[A-Fa-f0-9]{64}$'){
        throw 'Malformed publisher proof reference.'
    }
}
$exe=Get-SetupFile $payload 'Xiongxin.OWTS.Desktop.exe'
Assert-SetupHash $exe ([string]$release.ExecutableSHA256)
if(-not [string]::IsNullOrWhiteSpace($ExpectedExecutableSHA256)){Assert-SetupHash $exe $ExpectedExecutableSHA256}
if((Get-Item -LiteralPath $exe).VersionInfo.FileVersion -cne '1.0.30.9'){throw 'EXE FileVersion does not match this lane.'}
if([string](Get-AuthenticodeSignature -LiteralPath $exe).Status -cne [string]$release.ApplicationSignature){
    throw 'EXE signature status does not match the publisher statement.'
}
$manifest=Read-SetupJson (Get-SetupFile $payload 'module-manifest.json')
if($Mode -eq 'Compile'){
    # Only newly guarded releases can create another installer. Historical 08
    # validation remains read-only; old evidence is never relabelled as new.
    if($null -eq $release.PSObject.Properties['MaintenanceCompilation'] -or
       $release.MaintenanceCompilation.Schema -cne 'HVX.ReleaseMaintenanceCompilation/1' -or
       $release.MaintenanceCompilation.Guard -cne 'MAINTENANCE_PASSWORD_GUARD' -or
       $release.MaintenanceCompilation.ProtectedHostSHA256 -cne $release.ExecutableSHA256 -or
       $release.MaintenanceCompilation.UiCompilationSHA256 -notmatch '^[A-Fa-f0-9]{64}$' -or
       $release.MaintenanceCompilation.HostCompilationSHA256 -notmatch '^[A-Fa-f0-9]{64}$' -or
       $release.MaintenanceCompilation.UiModuleCount -ne 7 -or
       $manifest.MaintenancePasswordEnabled -isnot [bool] -or $manifest.MaintenancePasswordEnabled -ne $true -or
       $manifest.ProtectedRelease -isnot [bool] -or $manifest.ProtectedRelease -ne $true -or
       $manifest.Configuration -cne 'Release' -or
       $manifest.SourceUiRuntime.ManifestSHA256 -cne $release.MaintenanceCompilation.UiCompilationSHA256){throw 'New installer compilation requires the guarded host/UI compilation chain and maintenance proof; historical receipts are audit-only.'}
}
if($manifest.Schema -cne 'XIONGXIN.UnifiedDesktop.Modules.v2' -or $manifest.Architecture -cne 'x64'){
    throw 'Incorrect module manifest.'
}
Assert-SetupHash (Get-SetupFile $payload 'assets/xiongxin.ico') $sourceSnapshot[$setupIcon]
Assert-SetupHash (Get-SetupFile $payload 'assets/shortcuts/256.ico') '3B9F43CC1860C2C38F444A84D0AEED77A696A5C293D6DD9843A04A735A0F9285'
Assert-SetupHash (Get-SetupFile $payload 'assets/shortcuts/512.ico') 'BAF373C7BEEF9EEFFFF87109BA903BDD560DC6D1DC8C334EC98348A5A7AB3995'
$runtime=Get-SetupFile $payload 'prerequisites/NDP48-x86-x64-AllOS-ENU.exe'
Assert-SetupHash $runtime $netHash
$runtimeSignature=Assert-SetupSignature $runtime 'CN=Microsoft Corporation(?:,|$)'
$null=Assert-SetupPlainPath $output $root
if(Test-Path -LiteralPath $output){throw 'Existing setup must not be overwritten.'}
if($Mode -eq 'Validate'){
    [ordered]@{Status='INSTALLER_INPUT_VALIDATED';Payload=$payload;FileCount=$before.Count;
        ExecutableSHA256=[string]$release.ExecutableSHA256;Profile=[string]$release.Profile;
        SourceSHA256=$sourceSnapshot;Compiler=$compilerSignature;DotNet=$runtimeSignature;
        InstallerCompiled=$false;InstallerExecuted=$false;HardwareAccessed=$false;PhysicalAcceptance=$false}|ConvertTo-Json -Depth 5
    return
}
# No older publishing gate or receipt is changed. All publisher tests and
# confidentiality review must have completed before this explicit invocation.
if((Convert-SetupInventory (Get-SetupPayloadInventory $payload)) -cne (Convert-SetupInventory $before)){
    throw 'Payload changed during installer input checks.'
}
foreach($file in $sourceSnapshot.Keys){Assert-SetupHash $file $sourceSnapshot[$file]}
& $compiler ('/DPayloadDir='+$payload) ('/DOutputDirPath='+(Join-Path $root 'deliverables')) ('/DSetupFileName='+$setupName) $installer
if($LASTEXITCODE -ne 0 -or -not(Test-Path -LiteralPath $output -PathType Leaf)){throw 'Installer compilation failed; no accepted setup output.'}
foreach($file in $sourceSnapshot.Keys){Assert-SetupHash $file $sourceSnapshot[$file]}
if((Convert-SetupInventory (Get-SetupPayloadInventory $payload)) -cne (Convert-SetupInventory $before)){
    throw 'Payload changed while compiling. Do not deliver the output.'
}
$null=Assert-SetupPlainPath $output $root
[ordered]@{Status='INSTALLER_COMPILED_NOT_EXECUTED';Setup=$output;
    SetupSHA256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash;
    SetupSignature=[string](Get-AuthenticodeSignature -LiteralPath $output).Status;
    Payload=$payload;FileCount=$before.Count;ExecutableSHA256=[string]$release.ExecutableSHA256;
    Profile=[string]$release.Profile;SourceSHA256=$sourceSnapshot;
    InstallerExecuted=$false;ProductExecuted=$false;HardwareAccessed=$false;PhysicalAcceptance=$false}|ConvertTo-Json -Depth 5
