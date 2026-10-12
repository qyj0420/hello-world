[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$output=[IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Only the isolated r58 project build is an admissible target.'}
function Unlinked([string]$path){
    for($walk=[IO.Path]::GetFullPath($path);-not [String]::IsNullOrWhiteSpace($walk);$walk=[IO.Path]::GetDirectoryName($walk)){
        if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked verification paths are prohibited.'}
    }
}
function Within([string]$base,[string]$relative){
    if([String]::IsNullOrWhiteSpace($relative) -or $relative -match '(^[/\\]|:|\\|(^|/)\.\.?(/|$))'){throw 'Unsafe verification path.'}
    $path=[IO.Path]::GetFullPath((Join-Path $base $relative))
    if(-not $path.StartsWith($base.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Verification path escapes its root.'}
    Unlinked $path;return $path
}
function Hash([string]$path){Unlinked $path;return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
function ByteHash([byte[]]$bytes){$sha=[Security.Cryptography.SHA256]::Create();try{return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','')}finally{$sha.Dispose()}}
function Json([string]$path){Unlinked $path;if((Get-Item -LiteralPath $path).Length -gt 33554432){throw 'Oversized verification receipt.'};return Get-Content -LiteralPath $path -Raw -Encoding UTF8|ConvertFrom-Json}
Unlinked $project;Unlinked $output
$checks=New-Object 'System.Collections.Generic.List[object]'
function Check([bool]$value,[string]$name){$checks.Add([ordered]@{Name=$name;Passed=$value});if(-not $value){throw ('Static desktop verification failed: '+$name)}}
$exe=Within $output 'Xiongxin.OWTS.Desktop.exe'
$exeHash=Hash $exe
$build=Json (Within $output 'r58-desktop-test-build.json')
$moduleReceiptPath=Within $output 'r58-desktop-test-module-manifest.json'
$modules=Json $moduleReceiptPath
Check ($build.Schema -ceq 'HVX.R58.CompleteDesktopTestBuild/1' -and $build.CompleteDesktopExecutable -eq $true -and $build.CustomerInstaller -eq $false) 'Complete desktop test identity, not a customer installer'
Check ($build.ExecutableSHA256 -ceq $exeHash -and $build.ModuleManifestSHA256 -ceq (Hash $moduleReceiptPath)) 'Executable and complete module receipts bind actual bytes'
Check ($build.SourceUnchanged -eq $true -and $modules.SourceInputsUnchanged -eq $true -and $modules.FrozenInputsUnchanged -eq $true) 'Frozen source assertions are present'
Check ($build.UiSourceRoot -ceq $project -and $modules.UiSourceRoot -ceq $project -and $build.FrozenSourceRoot -ceq $modules.FrozenSourceRoot) 'Current UI and frozen hardware source owners are explicit and separate'
$frozenOwner=[IO.Path]::GetFullPath($build.FrozenSourceRoot).TrimEnd('\');Unlinked $frozenOwner
Check ([IO.Path]::GetFileName($frozenOwner) -ceq 'r58-hardware-source-20261012' -and $frozenOwner -cne $project) 'Frozen hardware owner is not relabelled as hotfix source'
$frozenCompile=Within $frozenOwner 'build/r58-hardware-compile-only-20261012-03/compile-only-manifest.json'
Check ((Hash $frozenCompile) -ceq 'BB426073103600829E6D0C598B7A8E31DC4BED7CBF998827BB02888DE5861EE1' -and $build.FrozenCompileClosureSHA256 -ceq (Hash $frozenCompile) -and $modules.CompileOnlyManifestSHA256 -ceq (Hash $frozenCompile)) 'Exact reviewed frozen compile provenance is retained'
$frozen=Json $frozenCompile
Check ($frozen.SourceRoot -ceq $frozenOwner -and $frozen.Status -ceq 'COMPILE_ONLY_PASS' -and $frozen.InputSetsAndHashesUnchanged -eq $true -and $frozen.BaseInputsUnchanged -eq $true) 'Frozen compile source owner and immutable input proof agree'
foreach($entry in $frozen.Inputs){Check ((Hash $entry.Path) -ceq $entry.SHA256) ('Frozen source input remains unchanged '+$entry.RelativePath)}
$hardwareProof=Within $output 'r58-module-evidence/hardware-build-manifest.json'
Check ((Hash $hardwareProof) -ceq '3C7BD4F5BBF4951FB850047A2075024FA810F24490713E03469CF5500DA684DF' -and $modules.HardwareManifestSHA256 -ceq (Hash $hardwareProof) -and $modules.HardwareSourceEquivalent -eq $true) 'Reviewed hardware module closure is unchanged'
$hardware=Json $hardwareProof
foreach($entry in $hardware.Inputs){Check ((Hash (Within $frozenOwner $entry.Path)) -ceq $entry.Sha256) ('Frozen hardware build input remains unchanged '+$entry.Path)}
Check ($build.ProductEntryExecuted -eq $false -and $build.SDKCalled -eq $false -and $build.HardwareAccessed -eq $false -and $build.PhysicalAcceptance -eq $false) 'Build does not claim device execution or physical acceptance'
Check ($build.ProtectedRelease -eq $true -and $build.MaintenancePasswordEnabled -eq $true -and $modules.MaintenancePasswordEnabled -eq $true) 'Host and UI require protected maintenance compilation'
Check (@($build.CompilerDefines|Where-Object{$_ -cmatch '(^|[;:])MAINTENANCE_PASSWORD_GUARD($|;)'}).Count -eq 1) 'Actual host compiler arguments include maintenance guard exactly once'
$dn='D:/PD_AI_LabVIEW2026_Project/Pulse400Hz_Extraction_DLL/tools/ConfuserEx-CLI-1.6.0/dnlib.dll'
Unlinked $dn
# Only the metadata parser is loaded into the CLR. No product assembly is loaded,
# reflected, instantiated, initialized, invoked, or started by this script.
$null=[Reflection.Assembly]::LoadFrom($dn)
$hostModule=[dnlib.DotNet.ModuleDefMD]::Load($exe)
$decoded=@{};$indexRows=New-Object 'System.Collections.Generic.List[object]'
$contracts=New-Object 'System.Collections.Generic.List[object]'
function TypeOf($module,[string]$name){return @($module.GetTypes()|Where-Object FullName -CEQ $name)}
function RequireMethod($type,[string]$name,[int]$parameters,[bool]$public=$true){
    $methods=@($type.Methods|Where-Object{$_.Name.String -ceq $name -and $_.MethodSig.Params.Count -eq $parameters -and (-not $public -or $_.IsPublic)})
    Check ($methods.Count -ge 1) ($type.FullName+'::'+$name+' metadata contract')
}
try{
    Check ($hostModule.Machine.ToString() -ceq 'AMD64' -and $hostModule.Kind.ToString() -ceq 'Windows') 'Host is an AMD64 Windows GUI executable'
    Check ($null -ne $hostModule.EntryPoint -and $hostModule.EntryPoint.FullName -ceq 'System.Int32 Xiongxin.OWTS.Desktop.Program::Main(System.String[])') 'Actual entry point is the desktop Main, not a worker or fixture'
    $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    Check ($version.FileVersion -ceq '1.1.2.0' -and $build.FileVersion -ceq $version.FileVersion) 'Actual Windows file version is V1.1.2.0'
    Check ($version.ProductVersion -match 'r58') 'Actual informational version distinguishes the r58 derived build'
    $access=@(TypeOf $hostModule 'Xiongxin.OWTS.Desktop.SettingsAccessControl')
    Check ($access.Count -eq 1) 'Host maintenance access-control type is retained'
    $enabled=@($access[0].Fields|Where-Object{$_.Name.String -ceq 'Enabled'})
    Check ($enabled.Count -eq 1 -and $enabled[0].HasConstant -and $enabled[0].Constant.Value -eq $true) 'Compiled maintenance guard is enabled, not just a receipt flag'
    RequireMethod $access[0] 'Verify' 2 $false
    RequireMethod $access[0] 'RequiresDeviceAuthorization' 1 $false
    $resources=@{}
    foreach($resource in $hostModule.Resources){$name=$resource.Name.String;Check (-not $resources.ContainsKey($name)) ('Unique managed resource '+$name);$resources.Add($name,$resource)}
    Check ($resources.ContainsKey('HVX.NeutralWindow.ico')) 'Neutral window icon resource is present'
    $iconBytes=$resources['HVX.NeutralWindow.ico'].CreateReader().ToArray()
    Check ((ByteHash $iconBytes) -ceq (Hash (Within $output 'assets/shortcuts/256.ico'))) 'Neutral window icon matches the delivered shortcut icon'
    $win32=$hostModule.Win32Resources
    Check ($null -ne $win32 -and @($win32.Root.Directories|Where-Object{$_.Name.ToString() -ceq '14'}).Count -eq 1) 'Native PE group-icon resource exists'
    Check ($resources.ContainsKey('HVX.Modules.Index.v1')) 'Embedded module index exists'
    $index=$resources['HVX.Modules.Index.v1'].CreateReader().ToArray()
    Check ($index.Length -le 4194304 -and (ByteHash $index) -ceq $build.EmbeddedPayloadHash) 'Embedded index is bounded and matches its build digest'
    $lines=[Text.Encoding]::UTF8.GetString($index).Split("`n")
    Check ($lines[0] -ceq 'XIONGXIN.Modules.v1') 'Embedded index format is current'
    $seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $moduleRoot=Within $output 'modules'
    $expanded=0L
    foreach($line in $lines){
        if(-not $line -or $line -ceq 'XIONGXIN.Modules.v1'){continue}
        $fields=$line.Split("`t");Check ($fields.Count -eq 4) 'Index row has four fields'
        $relative=$fields[0];$length=0L
        Check ([long]::TryParse($fields[1],[ref]$length) -and $length -ge 0 -and $length -le 268435456) ('Bounded indexed file '+$relative)
        Check ($fields[2] -cmatch '^[0-9A-F]{64}$' -and $fields[3] -ceq ('HVX.Modules.Blob.'+$fields[2]+'.gz')) ('Content-addressed resource '+$relative)
        Check ($seen.Add($relative)) ('Unique payload path '+$relative)
        $disk=Within $moduleRoot $relative
        Check ((Get-Item -LiteralPath $disk).Length -eq $length -and (Hash $disk) -ceq $fields[2]) ('Staged bytes match embedded index '+$relative)
        if(-not $decoded.ContainsKey($fields[3])){
            Check ($resources.ContainsKey($fields[3])) ('Compressed payload exists '+$relative)
            $compressed=$resources[$fields[3]].CreateReader().ToArray()
            Check ($compressed.Length -le 268435456) ('Bounded compressed payload '+$relative)
            $stream=New-Object IO.MemoryStream(,$compressed)
            $gzip=New-Object IO.Compression.GZipStream($stream,[IO.Compression.CompressionMode]::Decompress)
            $sha=[Security.Cryptography.SHA256]::Create();$buffer=New-Object byte[] 65536;$count=0L
            try{
                while(($read=$gzip.Read($buffer,0,$buffer.Length)) -gt 0){
                    $count+=$read;if($count -gt $length){throw ('Expanded blob exceeds indexed size: '+$relative)}
                    $null=$sha.TransformBlock($buffer,0,$read,$buffer,0)
                }
                $null=$sha.TransformFinalBlock((New-Object byte[] 0),0,0)
                $decoded.Add($fields[3],@{Bytes=$count;SHA256=[BitConverter]::ToString($sha.Hash).Replace('-','')})
            }finally{$sha.Dispose();$gzip.Dispose();$stream.Dispose()}
        }
        Check ($decoded[$fields[3]].Bytes -eq $length -and $decoded[$fields[3]].SHA256 -ceq $fields[2]) ('Decompressed bytes match exact length and SHA256 '+$relative)
        $expanded+=$length;Check ($expanded -le 1073741824 -and $indexRows.Count -lt 4096) 'Total embedded payload stays within production budgets'
        $indexRows.Add([ordered]@{Path=$relative;Bytes=$length;SHA256=$fields[2]})
    }
    $diskFiles=@(Get-ChildItem -LiteralPath $moduleRoot -Recurse -File)
    Check ($diskFiles.Count -eq $indexRows.Count -and $indexRows.Count -eq $build.EmbeddedFiles -and $indexRows.Count -eq @($modules.Modules).Count) 'Embedded index, disk and complete manifest file counts agree'
    $expected=@{}
    foreach($entry in $modules.Modules){Check (-not $expected.ContainsKey($entry.Path)) ('Unique module receipt path '+$entry.Path);$expected.Add($entry.Path,$entry)}
    foreach($row in $indexRows){Check ($expected.ContainsKey($row.Path) -and $expected[$row.Path].SHA256 -ceq $row.SHA256 -and $expected[$row.Path].Bytes -eq $row.Bytes) ('Complete manifest binds embedded file '+$row.Path)}
    foreach($file in $diskFiles){Unlinked $file.FullName;Check ($seen.Contains($file.FullName.Substring($moduleRoot.Length+1).Replace('\','/'))) 'No unindexed staged payload files'}
    Check (@($resources.Keys|Where-Object{$_ -like 'HVX.Modules.Blob.*' -and -not $decoded.ContainsKey($_)}).Count -eq 0) 'No unindexed embedded module blobs'
    $pinPath=Within $output 'research-pins.json';$pinHash=Hash $pinPath;$pins=Json $pinPath
    Check ($pinHash -ceq $build.ResearchPinsSHA256 -and $pinHash -ceq $modules.ResearchRuntime.PinsSHA256) 'Research pin manifest matches host and module receipts'
    Check ($resources.ContainsKey('HVX.Research.Pins') -and (ByteHash ($resources['HVX.Research.Pins'].CreateReader().ToArray())) -ceq $pinHash) 'Actual host embeds the same research pins'
    Check (@($pins.PSObject.Properties).Count -eq 452 -and $modules.ResearchRuntime.Files -eq 452) 'All 452 corrected research runtime files are pinned'
    foreach($pin in $pins.PSObject.Properties){$relative='r71/'+$pin.Name;Check ($expected.ContainsKey($relative) -and $expected[$relative].SHA256 -ceq $pin.Value) ('Research pin matches complete embedded payload '+$pin.Name)}
    Check ($null -ne $pins.PSObject.Properties['q86.cp312-win_amd64.pyd']) 'Corrected E+ q86 module is present'
    $runtime=$modules.ResearchRuntime
    $sessionRoot=[IO.Path]::GetFullPath($build.RuntimeSessionRoot).TrimEnd('\');Unlinked $sessionRoot
    Check ($sessionRoot.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase) -and $runtime.RuntimeSessionRoot -ceq $sessionRoot) 'Analysis runtime overlay has an explicit owned build location'
    $sessionReceipt=Within $output 'r58-module-evidence/runtime-session-receipt.json'
    $session=Json $sessionReceipt
    Check ((Hash $sessionReceipt) -ceq $build.RuntimeSessionReceiptSHA256 -and (Hash $sessionReceipt) -ceq $runtime.RuntimeSessionReceiptSHA256 -and (Hash (Within $sessionRoot 'runtime-session-receipt.json')) -ceq (Hash $sessionReceipt)) 'Runtime session receipt binds unchanged staged and original evidence'
    $oldPinsPath=Within $output 'r58-module-evidence/research-frozen-pins.json';$oldPins=Json $oldPinsPath
    Check ((Hash $oldPinsPath) -ceq $runtime.FrozenPinsSHA256 -and $runtime.FrozenPinsSHA256 -ceq $frozen.CorrectedEPlusRuntime.PinsSHA256 -and (Hash $session.FrozenPinsPath) -ceq $runtime.FrozenPinsSHA256 -and @($oldPins.PSObject.Properties).Count -eq 452) 'Old 452-file pin manifest remains the original frozen provenance'
    Check ($session.Schema -ceq 'HVX.R58.AnalysisRuntimeSession/1' -and $session.SourceRoot -ceq $project -and $session.ModulesRoot -ceq (Join-Path $sessionRoot 'modules') -and $session.Files -eq 452 -and $session.ByteIdenticalReusedFiles -eq 451 -and $session.ChangedModule -ceq 'q73.cp312-win_amd64.pyd' -and $session.PinsSHA256 -ceq $pinHash -and $session.FrozenPinsSHA256 -ceq $runtime.FrozenPinsSHA256 -and $session.ProductStarted -eq $false -and $session.SDKCalled -eq $false -and $session.HardwareUsed -eq $false -and $session.PhysicalAcceptance -eq $false -and $session.AlgorithmRulesChanged -eq $false) 'Runtime session changes adapter transport only, with no hardware or algorithm execution claim'
    Check ((Hash (Within $sessionRoot 'research-pins.json')) -ceq $pinHash -and $runtime.ByteIdenticalReusedFiles -eq 451 -and $runtime.ChangedModule -ceq 'q73.cp312-win_amd64.pyd') 'New runtime pins agree with the actual embedded host contract'
    Check (@(Get-ChildItem -LiteralPath (Within $sessionRoot 'modules/r71') -Recurse -File).Count -eq 452) 'Original overlay contains no extra unpinned runtime files'
    $changed=New-Object 'System.Collections.Generic.List[string]'
    foreach($pin in $pins.PSObject.Properties){
        Check ($null -ne $oldPins.PSObject.Properties[$pin.Name]) ('Overlay retains frozen runtime entry '+$pin.Name)
        if($pin.Value -cne $oldPins.PSObject.Properties[$pin.Name].Value){$changed.Add($pin.Name)}
        Check ((Hash (Within (Join-Path $sessionRoot 'modules/r71') $pin.Name)) -ceq $pin.Value) ('Original runtime overlay bytes match staged/payload pins '+$pin.Name)
    }
    Check ($changed.Count -eq 1 -and $changed[0] -ceq 'q73.cp312-win_amd64.pyd') 'Exactly q73 changes; the other 451 files remain byte-identical'
    $adapterReceipt=Within $output 'r58-module-evidence/research-adapter-source-receipt.json';$adapter=Json $adapterReceipt
    $adapterSource=Within $project 'modules-src/ResearchAdapter/research_entry.py';$adapterInput=Within $sessionRoot 'adapter-native/compiler-input/q73.py'
    Check ((Hash $adapterReceipt) -ceq $runtime.AdapterReceiptSHA256 -and (Hash (Within $sessionRoot 'adapter-native/source-receipt.json')) -ceq (Hash $adapterReceipt) -and ($session.AdapterReceipt|ConvertTo-Json -Depth 6 -Compress) -ceq ($adapter|ConvertTo-Json -Depth 6 -Compress)) 'Adapter compile receipt agrees with exact session evidence'
    Check ($adapter.schema -ceq 'HVX.ResearchAdapter.NativeBuild/1' -and $adapter.source -ceq $adapterSource -and $adapter.sourceSha256 -ceq (Hash $adapterSource) -and $runtime.AdapterSourceSHA256 -ceq $adapter.sourceSha256 -and $adapter.compilerInputSha256 -ceq (Hash $adapterInput) -and $runtime.CompilerInputSHA256 -ceq $adapter.compilerInputSha256 -and $adapter.algorithmSourcesChanged -eq $false -and $adapter.hardwareAccessed -eq $false) 'Current owned adapter source and compiler input have not changed'
    Check ((ByteHash ([Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText($adapterSource,[Text.Encoding]::UTF8)))) -ceq $adapter.compilerInputSha256 -and (Hash (Within $sessionRoot 'adapter-native/runtime/q73.cp312-win_amd64.pyd')) -ceq $pins.PSObject.Properties['q73.cp312-win_amd64.pyd'].Value) 'Native q73 output binds exact BOM-normalized compiler input and embedded file pin'
    $native=Within $output 'HvxAcquisitionNative.dll';$nativeHash=Hash $native
    Check ($nativeHash -ceq $build.NativeBridgeSHA256) 'Host-adjacent native bridge matches reviewed build'
    foreach($copy in $modules.NativeCopies){Check ((Hash (Within $moduleRoot $copy.Path)) -ceq $nativeHash -and $copy.SHA256 -ceq $nativeHash) ('Managed owner has identical adjacent native bridge '+$copy.Path)}
    Check (@($modules.NativeCopies).Count -eq 8) 'All eight module/process native bridge copies are present'
    $nativeBytes=[IO.File]::ReadAllBytes($native);$pe=[BitConverter]::ToInt32($nativeBytes,60)
    Check ($pe -gt 0 -and $pe -le $nativeBytes.Length-264 -and [BitConverter]::ToUInt32($nativeBytes,$pe) -eq 17744 -and [BitConverter]::ToUInt16($nativeBytes,$pe+4) -eq 34404) 'Native bridge is an AMD64 PE image (never loaded)'
    $hostProtectionPath=Within $output 'host-protection/receipt.json';$hostProtection=Json $hostProtectionPath
    Check ((Hash $hostProtectionPath) -ceq $build.HostProtection.ReceiptSHA256 -and $hostProtection.InputSHA256 -ceq $build.HostProtection.InputSHA256 -and $hostProtection.OutputSHA256 -ceq $exeHash -and $hostProtection.ExistingMetadataPreserved -eq $true -and $hostProtection.InputUnchanged -eq $true) 'Host protection receipt binds original, protected output and unchanged metadata'
    foreach($transform in $modules.OwnedAlgorithmProtection){
        $receiptPath=Within $output $transform.ReceiptPath;$receipt=Json $receiptPath
        Check ((Hash $receiptPath) -ceq $transform.ReceiptSHA256 -and $receipt.InputSHA256 -ceq $transform.InputSHA256 -and $receipt.OutputSHA256 -ceq $transform.OutputSHA256 -and (Hash (Within $output $transform.Path)) -ceq $receipt.OutputSHA256 -and $receipt.ExistingMetadataPreserved -eq $true -and $receipt.InputUnchanged -eq $true -and $receipt.HardwareAccessed -eq $false) ('Owned-module protection chain '+$transform.Path)
    }
    $uiPath=Within $output $modules.SourceUiRuntime.ManifestPath;$ui=Json $uiPath
    Check ((Hash $uiPath) -ceq $modules.SourceUiRuntime.ManifestSHA256 -and $ui.MaintenancePasswordEnabled -eq $true -and 'MAINTENANCE_PASSWORD_GUARD' -cin @($ui.CompilerDefines)) 'Actual UI compiler receipt includes maintenance guard'
    $uiContracts=@(
        @('ProjectCreation/Xiongxin.OWTS.ProjectCreation.dll','HVX.Controls.ProjectCreationControl','LoadProject',1),
        @('ProjectManagement/Xiongxin.OWTS.ProjectManagement.dll','HVX.Controls.ProjectManagementControl','LoadRoot',1),
        @('DataBrowser/Xiongxin.OWTS.DataBrowser.dll','HVX.Controls.ProjectDataBrowserControl','LoadProject',1),
        @('DataAnalysis/HVX.PdReview.V1.0.dll','HVX.PdReview.PdReviewControl','SetLocationScoreThresholds',3),
        @('ReportGenerator/Xiongxin.OWTS.ReportGenerator.dll','HVX.ReportGenerator.ReportGeneratorControl','LoadProject',1),
        @('PressureTest/Xiongxin.OWTS.PressureTest.dll','HVX.Controls.PressureTestControl','LoadProjectPlan',1),
        @('CalibrationTest/Xiongxin.OWTS.CalibrationTest.dll','HVX.Controls.CalibrationProgramControl','NotifyCalibrationPageEnteredAsync',0))
    foreach($contract in $uiContracts){
        $child=[dnlib.DotNet.ModuleDefMD]::Load((Within $moduleRoot $contract[0]))
        try{
            $type=@(TypeOf $child $contract[1]);Check ($type.Count -eq 1 -and $type[0].IsPublic) ('Exact public UI type '+$contract[1])
            RequireMethod $type[0] '.ctor' 0
            RequireMethod $type[0] $contract[2] $contract[3]
            Check (@($type[0].Properties|Where-Object{$_.Name.String -ceq 'LanguageIndex' -and $null -ne $_.SetMethod -and $_.SetMethod.IsPublic}).Count -eq 1) ('Writable shared language contract '+$contract[1])
            $contracts.Add([ordered]@{File=$contract[0];Type=$contract[1];Method=$contract[2];MetadataOnly=$true})
        }finally{$child.Dispose()}
    }
    foreach($worker in @(@('PressureRuntime/Xiongxin.Pressure.Runtime.exe','Xiongxin.FactoryTest.UnifiedWorkerEntry','Xiongxin.FactoryTest.FactoryResidentDeviceClient','ReadPublishedRoundResultAsync'),@('Task39/Xiongxin.Task39.InputWorker.exe','Xiongxin.ZeroBench.Program','Xiongxin.ZeroBench.HardwareCapture',''))){
        $child=[dnlib.DotNet.ModuleDefMD]::Load((Within $moduleRoot $worker[0]))
        try{
            Check ($child.Machine.ToString() -ceq 'AMD64' -and $null -ne $child.EntryPoint -and $child.EntryPoint.DeclaringType.FullName -ceq $worker[1]) ('Exact x64 worker entry metadata '+$worker[0])
            $type=@(TypeOf $child $worker[2]);Check ($type.Count -eq 1) ('Current worker type '+$worker[2])
            if($worker[3]){Check (@($type[0].Methods|Where-Object{$_.Name.String -ceq $worker[3]}).Count -ge 1) ('Current worker method '+$worker[3])}
        }finally{$child.Dispose()}
    }
    $profileType=@(TypeOf $hostModule 'Xiongxin.OWTS.Desktop.AppPaths')
    $profile=@($profileType[0].Fields|Where-Object{$_.Name.String -ceq 'ProfileDirectoryName'})
    Check ($profile.Count -eq 1 -and $profile[0].Constant.Value -ceq $build.ConfigurationProfile) 'Compiled configuration profile matches explicit receipt'
    # Only existence is inspected. Never read installed settings, credentials,
    # or ProgramData state contents and never edit or migrate any profile.
    $statePath=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'XIONGXIN/HVXPublicBetaNoDriver11220261011/install-state.xml'
    $footprint=[ordered]@{ParentManifestExists=(Test-Path -LiteralPath (Within $output 'private/parent-manifest.xml'));ParentHostExists=(Test-Path -LiteralPath (Within $output 'private/ParentHost.exe'));SharedProgramDataStateExists=(Test-Path -LiteralPath $statePath)}
    Check ($build.SharesExistingV112Profile -eq $true -and $build.ConfigurationProfile -ceq 'PublicBeta-NoDriver-1.1.2-20261011') 'Shared V1.1.2 profile is declared, not claimed isolated'
    Check ((Hash $exe) -ceq $exeHash -and (Hash $moduleReceiptPath) -ceq $build.ModuleManifestSHA256) 'Target bytes stayed unchanged throughout static verification'
    $proofDirectory=Join-Path $output ('qa/desktop-offline-'+[Guid]::NewGuid().ToString('N'));Unlinked $proofDirectory
    $null=New-Item -ItemType Directory -Path $proofDirectory
    $proofPath=Join-Path $proofDirectory 'proof.json'
    [ordered]@{Schema='HVX.R58.DesktopTestOffline/1';Status='STATIC_PASS';CheckedUtc=[DateTime]::UtcNow.ToString('o');ExecutableSHA256=$exeHash;ToolSHA256=(Hash $PSCommandPath);DnlibSHA256=(Hash $dn);Checks=$checks.ToArray();EmbeddedFiles=$indexRows.Count;UniqueBlobs=$decoded.Count;ExpandedBytes=$expanded;ResearchFiles=452;ModuleContracts=$contracts.ToArray();ConfigurationProfile=$build.ConfigurationProfile;SharesExistingV112Profile=$true;InstallationFootprint=$footprint;InstalledSettingsRead=$false;ProductEntryExecuted=$false;ProductAssemblyLoadedIntoCLR=$false;ProductInitializerExecuted=$false;ProductionNativeLoaded=$false;SDKCalled=$false;HardwareAccessed=$false;PhysicalAcceptance=$false;Uncovered=@('Live device/driver behavior','Actual GUI rendering and interactions','Correct credential access test','Native algorithm numerical regression','Full known-vulnerability audit/code signing','Portable behavior when an old shared ProgramData footprint exists')}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $proofPath -Encoding UTF8
    Write-Output ('R58_DESKTOP_STATIC_CHECKS='+$checks.Count)
    Write-Output ('R58_DESKTOP_STATIC_PROOF='+$proofPath)
}finally{$hostModule.Dispose()}
