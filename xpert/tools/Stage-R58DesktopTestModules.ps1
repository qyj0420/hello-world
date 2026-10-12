[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [Parameter(Mandatory=$true)][string]$CompileRoot,
    [Parameter(Mandatory=$true)][string]$HardwareRoot,
    [Parameter(Mandatory=$true)][string]$FrozenSourceRoot,
    [Parameter(Mandatory=$true)][string]$RuntimeSessionRoot,
    [Parameter(Mandatory=$true)][string]$HybridPinFile,
    [Parameter(Mandatory=$true)][string]$HybridNativeDirectory
)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if([IO.Path]::GetFileName($project) -cne 'r58-analysis-hotfix-20261012'){throw 'This builder belongs to the isolated r58 analysis hotfix source tree.'}
$frozenOwner=[IO.Path]::GetFullPath($FrozenSourceRoot).TrimEnd('\')
if([IO.Path]::GetFileName($frozenOwner) -cne 'r58-hardware-source-20261012'){throw 'The reviewed frozen r58 hardware source owner is required.'}
function Assert-Unlinked([string]$Path) {
    for($walk=[IO.Path]::GetFullPath($Path);$walk;$walk=[IO.Path]::GetDirectoryName($walk)) {
        if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw ('Linked build path rejected: '+$walk)}
    }
}
function Build-Path([string]$Path,[string]$Owner=$project) {
    if(-not [IO.Path]::IsPathRooted($Path)){$Path=Join-Path $Owner $Path}
    $full=[IO.Path]::GetFullPath($Path).TrimEnd('\')
    if(-not $full.StartsWith($Owner+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Desktop build paths must remain under their exact source owner build directory.'}
    Assert-Unlinked $full; return $full
}
function Contained-Path([string]$Root,[string]$Relative) {
    if([String]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative)){throw 'Invalid relative artifact path.'}
    $full=[IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if(-not $full.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase)){throw ('Artifact escaped its root: '+$Relative)}
    Assert-Unlinked $full; return $full
}
function Hash([string]$Path) { Assert-Unlinked $Path; return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Files([string]$Root) {
    Assert-Unlinked $Root
    $found=New-Object 'System.Collections.Generic.List[string]';$pending=New-Object 'System.Collections.Generic.Stack[string]';$pending.Push($Root)
    while($pending.Count) {
        foreach($entry in @(Get-ChildItem -LiteralPath $pending.Pop() -Force)) {
            if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){throw ('Linked artifact rejected: '+$entry.FullName)}
            if($entry.PSIsContainer){$pending.Push($entry.FullName)}else{$found.Add($entry.FullName)}
        }
    }
    return @($found.ToArray()|Sort-Object)
}
function Copy-Checked([string]$Source,[string]$Target,[string]$Expected='') {
    Assert-Unlinked $Source;Assert-Unlinked $Target
    $before=Hash $Source
    if($Expected -and $before -cne $Expected){throw ('Copy input differs from reviewed hash: '+$Source)}
    $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Target)) -Force
    Copy-Item -LiteralPath $Source -Destination $Target -Force
    if((Hash $Source) -cne $before -or (Hash $Target) -cne $before){throw ('Copy verification failed: '+$Source)}
}
function Assert-ExpectedFiles([string]$Root,[System.Collections.IDictionary]$Expected) {
    $actual=@(Files $Root)
    if($actual.Count -ne $Expected.Count){throw ('Artifact file set differs from manifest: '+$Root)}
    foreach($file in $actual) {
        $relative=$file.Substring($Root.Length+1).Replace('\','/')
        if(-not $Expected.Contains($relative) -or (Hash $file) -cne $Expected[$relative]){throw ('Unexpected or modified artifact: '+$relative)}
    }
}
function Invoke-Builder([string]$Name,[hashtable]$Arguments) {
    Write-Host ('R58_STAGE_STEP='+$Name)
    $global:LASTEXITCODE=0
    & (Join-Path $PSScriptRoot $Name) @Arguments | Out-Host
    if($LASTEXITCODE -ne 0){throw ('Module build step failed: '+$Name)}
}
function Source-Inventory {
    $paths=New-Object 'System.Collections.Generic.List[string]'
    foreach($tree in @('src','modules-src')) {
        foreach($file in @(Files (Join-Path $project $tree))|Where-Object{[IO.Path]::GetExtension($_) -ceq '.cs'}){$paths.Add($file)}
    }
    foreach($name in @('Build-R58UiModules.ps1','Build-ReportEngine.ps1','Build-LocationAlgorithm.ps1','Test-LocationCandidate.ps1','Stage-AlgorithmDependencies.ps1','Stage-DefectModels.ps1','Protect-StagedAlgorithms.ps1','Protect-OwnedAlgorithms.ps1','Protect-LocationAlgorithm.ps1','owned-algorithm-protection.json','Stage-R58DesktopTestModules.ps1')){$paths.Add((Join-Path $PSScriptRoot $name))}
    foreach($name in @('modules-src/CalibrationStandalone/Build.ps1','App.config','native-hardware/out/HvxAcquisitionNative.dll')){$paths.Add((Join-Path $project $name))}
    return @($paths.ToArray()|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_.Substring($project.Length+1).Replace('\','/');SHA256=(Hash $_)}})
}
$output=Build-Path $OutputRoot;$compile=Build-Path $CompileRoot $frozenOwner;$hardware=Build-Path $HardwareRoot $frozenOwner
$pin=Build-Path $HybridPinFile;$hybrid=Build-Path $HybridNativeDirectory
$sessionRoot=Build-Path $RuntimeSessionRoot
$modules=Join-Path $output 'modules';$evidence=Join-Path $output 'r58-module-evidence'
foreach($path in @($modules,$evidence)){if(Test-Path -LiteralPath $path){throw 'Use fresh modules and module-evidence directories.'}}
$compileManifestPath=Join-Path $compile 'compile-only-manifest.json'
$compileManifestHash=Hash $compileManifestPath
if($compileManifestHash -cne 'BB426073103600829E6D0C598B7A8E31DC4BED7CBF998827BB02888DE5861EE1'){throw 'Wrong reviewed r58 compile closure.'}
$compiled=Get-Content -LiteralPath $compileManifestPath -Raw|ConvertFrom-Json
if($compiled.Schema -cne 'HVX.R58.HardwareCandidate.CompileOnly/1' -or $compiled.Status -cne 'COMPILE_ONLY_PASS' -or
   -not $compiled.InputSetsAndHashesUnchanged -or -not $compiled.BaseInputsUnchanged -or
   $compiled.SourceRoot -cne $frozenOwner -or $compiled.HardwareAccessed -or $compiled.SDKCalled -or $compiled.ProductEntryExecuted){throw 'Expected stable, offline r58 compile-only provenance owned by the frozen source.'}
function Assert-FrozenInputs {
    if((Hash $compileManifestPath) -cne $compileManifestHash){throw 'The frozen compile manifest changed.'}
    foreach($entry in $compiled.Inputs){if((Hash $entry.Path) -cne $entry.SHA256){throw ('Frozen compile input changed: '+$entry.Path)}}
}
Assert-FrozenInputs
$compileExpected=@{
    'compile-only-manifest.json'=$compileManifestHash
    'r58-vs-r57-base-differences.json'=$compiled.DifferenceFileSHA256
    'README-CANDIDATE.txt'=$compiled.ReadmeSHA256
}
foreach($entry in $compiled.Products) {
    $compileExpected.Add($entry.File,$entry.SHA256);$compileExpected.Add($entry.NativeDLL.File,$entry.NativeDLL.SHA256)
    $compileExpected.Add($entry.Name+'/compile.rsp',$entry.ResponseFileSHA256);$compileExpected.Add($entry.Name+'/compile.log',$entry.CompileLogSHA256)
}
foreach($name in @('CopiedDependency','RuntimeConfig')){$entry=$compiled.$name;$compileExpected.Add($entry.File,$entry.SHA256)}
foreach($entry in $compiled.DerivedResources){$compileExpected.Add($entry.Path,$entry.SHA256)}
$runtime=$compiled.CorrectedEPlusRuntime
if($runtime.Files -ne 452 -or $runtime.ZipSHA256 -cne 'F69A945B5FBD67C114F64B736C747CDA51124883B5AAFC22F6F962D652884D2F'){throw 'Unexpected corrected E+ runtime identity.'}
$pinsPath=Join-Path $compile 'research-pins.json';$runtimeReceipt=Join-Path $compile 'research-runtime-input-receipt.json'
$compileExpected.Add('research-pins.json',$runtime.PinsSHA256);$compileExpected.Add('research-runtime-input-receipt.json',$runtime.ReceiptSHA256)
$pins=Get-Content -LiteralPath $pinsPath -Raw|ConvertFrom-Json
if(@($pins.PSObject.Properties).Count -ne 452){throw 'Corrected runtime pin count differs.'}
foreach($entry in $pins.PSObject.Properties){$relative=$runtime.StagedDirectory+'/'+$entry.Name;$null=Contained-Path $compile $relative;$compileExpected.Add($relative,$entry.Value)}
Assert-ExpectedFiles $compile $compileExpected
$hardwareManifestPath=Join-Path $hardware 'hardware-build-manifest.json';$hardwareManifestHash=Hash $hardwareManifestPath
if($hardwareManifestHash -cne '3C7BD4F5BBF4951FB850047A2075024FA810F24490713E03469CF5500DA684DF'){throw 'Wrong reviewed r58 hardware module closure.'}
$hw=Get-Content -LiteralPath $hardwareManifestPath -Raw|ConvertFrom-Json
if($hw.Schema -cne 'XIONGXIN.UnifiedDesktop.HardwareBuild.v1' -or $hw.SourceMode -cne 'LocalUnifiedProject' -or $hw.Architecture -cne 'x64'){throw 'Unexpected hardware build provenance.'}
$hardwareExpected=@{'hardware-build-manifest.json'=$hardwareManifestHash}
foreach($entry in $hw.Binaries){$hardwareExpected.Add($entry.Path,$entry.Sha256)}
foreach($entry in $hw.Inputs) {
    if((Hash (Contained-Path $frozenOwner $entry.Path)) -cne $entry.Sha256){throw ('Hardware build input changed: '+$entry.Path)}
    if($entry.Path.StartsWith('dependencies/HardwareConnector/runtime/',[StringComparison]::Ordinal)){$hardwareExpected.Add('HardwareIntegration/runtime/'+$entry.Path.Substring('dependencies/HardwareConnector/runtime/'.Length),$entry.Sha256)}
    elseif($entry.Path.StartsWith('dependencies/Task6/',[StringComparison]::Ordinal)){$hardwareExpected.Add('Task38/Task6/'+$entry.Path.Substring('dependencies/Task6/'.Length),$entry.Sha256)}
    elseif($entry.Path -cin @('assets/xiongxin.ico','assets/xiongxin-original.png')){$hardwareExpected.Add('HardwareIntegration/'+$entry.Path,$entry.Sha256)}
}
Assert-ExpectedFiles $hardware $hardwareExpected
# The hotfix owns the new UI. Hardware workers retain the reviewed frozen source
# closure even where the original pipeline recompiles/protects build copies.
function Assert-HardwareSourceEquivalence {
    foreach($tree in @('modules-src/CalibrationStandalone','modules-src/Task39InputWorker')) {
        $left=Join-Path $project $tree;$right=Join-Path $frozenOwner $tree
        $relative=@(Files $left|ForEach-Object{$_.Substring($left.Length+1).Replace('\','/')}|Where-Object{$_ -notmatch '(^|/)(build|__pycache__)(/|$)'})
        $frozenRelative=@(Files $right|ForEach-Object{$_.Substring($right.Length+1).Replace('\','/')}|Where-Object{$_ -notmatch '(^|/)(build|__pycache__)(/|$)'})
        if(($relative|ConvertTo-Json -Compress) -cne ($frozenRelative|ConvertTo-Json -Compress)){throw ('Hardware source file sets differ: '+$tree)}
        foreach($name in $relative){if((Hash (Contained-Path $left $name)) -cne (Hash (Contained-Path $right $name))){throw ('Hotfix hardware source differs from frozen r58: '+$tree+'/'+$name)}}
    }
    foreach($name in @('PdLinearHighPass.cs','PdLinearLowPass.cs','DialogPlacement.cs','ProjectMeasurementMode.cs','PicoCaptureCadence.cs','OemUiBranding.cs','R2RoundedButton.cs')) {
        if((Hash (Contained-Path $project ('src/'+$name))) -cne (Hash (Contained-Path $frozenOwner ('src/'+$name)))){throw ('Shared calibration hardware source differs from frozen r58: '+$name)}
    }
}
Assert-HardwareSourceEquivalence
# Keep the old corrected runtime provenance intact. The new bounded transport
# adapter is a separate overlay, never a rewritten old compile manifest.
$sessionReceiptPath=Join-Path $sessionRoot 'runtime-session-receipt.json'
$sessionReceiptHash=Hash $sessionReceiptPath
$session=Get-Content -LiteralPath $sessionReceiptPath -Raw -Encoding UTF8|ConvertFrom-Json
$sessionPinsPath=Join-Path $sessionRoot 'research-pins.json';$sessionPinsHash=Hash $sessionPinsPath
$sessionModules=Join-Path $sessionRoot 'modules/r71'
$adapterReceiptPath=Join-Path $sessionRoot 'adapter-native/source-receipt.json';$adapterReceiptHash=Hash $adapterReceiptPath
$adapter=Get-Content -LiteralPath $adapterReceiptPath -Raw -Encoding UTF8|ConvertFrom-Json
$adapterSource=Join-Path $project 'modules-src/ResearchAdapter/research_entry.py'
$adapterInput=Join-Path $sessionRoot 'adapter-native/compiler-input/q73.py'
if($session.Schema -cne 'HVX.R58.AnalysisRuntimeSession/1' -or $session.SourceRoot -cne $project -or
   $session.ModulesRoot -cne (Join-Path $sessionRoot 'modules') -or $session.Files -ne 452 -or
   $session.ByteIdenticalReusedFiles -ne 451 -or $session.ChangedModule -cne 'q73.cp312-win_amd64.pyd' -or
   $session.PinsSHA256 -cne $sessionPinsHash -or $session.FrozenPinsSHA256 -cne $runtime.PinsSHA256 -or
   (Hash $session.FrozenPinsPath) -cne $runtime.PinsSHA256 -or $session.ProductStarted -ne $false -or
   $session.SDKCalled -ne $false -or $session.HardwareUsed -ne $false -or $session.PhysicalAcceptance -ne $false -or $session.AlgorithmRulesChanged -ne $false){throw 'Invalid analysis runtime session overlay provenance.'}
if($adapter.schema -cne 'HVX.ResearchAdapter.NativeBuild/1' -or $adapter.source -cne $adapterSource -or
   $adapter.sourceSha256 -cne (Hash $adapterSource) -or $adapter.compilerInputSha256 -cne (Hash $adapterInput) -or
   $adapter.algorithmSourcesChanged -ne $false -or $adapter.hardwareAccessed -ne $false -or
   ($session.AdapterReceipt|ConvertTo-Json -Depth 6 -Compress) -cne ($adapter|ConvertTo-Json -Depth 6 -Compress)){throw 'Runtime adapter source/compile receipt differs from the current owned source.'}
$sourceBytes=[Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText($adapterSource,[Text.Encoding]::UTF8))
$sourceSha=[Security.Cryptography.SHA256]::Create()
try{$normalizedSourceHash=[BitConverter]::ToString($sourceSha.ComputeHash($sourceBytes)).Replace('-','')}finally{$sourceSha.Dispose()}
if($normalizedSourceHash -cne $adapter.compilerInputSha256){throw 'Adapter compiler input is not the exact UTF-8 source with only its BOM removed.'}
$sessionPins=Get-Content -LiteralPath $sessionPinsPath -Raw -Encoding UTF8|ConvertFrom-Json
$sessionExpected=@{};$changed=New-Object 'System.Collections.Generic.List[string]'
if(@($sessionPins.PSObject.Properties).Count -ne 452){throw 'Expected all 452 analysis runtime pins.'}
foreach($entry in $sessionPins.PSObject.Properties) {
    if($entry.Name -match '(^[/\\]|:|\\|(^|/)\.\.?(/|$))' -or $null -eq $pins.PSObject.Properties[$entry.Name] -or $entry.Value -cnotmatch '^[A-F0-9]{64}$'){throw 'Unexpected runtime overlay entry.'}
    $sessionExpected.Add($entry.Name,$entry.Value)
    if($entry.Value -cne $pins.PSObject.Properties[$entry.Name].Value){$changed.Add($entry.Name)}
}
if($changed.Count -ne 1 -or $changed[0] -cne 'q73.cp312-win_amd64.pyd'){throw 'Only the owned q73 transport adapter may change.'}
if((Hash (Join-Path $sessionRoot 'adapter-native/runtime/q73.cp312-win_amd64.pyd')) -cne $sessionExpected['q73.cp312-win_amd64.pyd']){throw 'Staged q73 differs from its native adapter output.'}
function Assert-RuntimeSession {
    if((Hash $sessionReceiptPath) -cne $sessionReceiptHash -or (Hash $sessionPinsPath) -cne $sessionPinsHash -or
       (Hash $adapterReceiptPath) -cne $adapterReceiptHash -or (Hash $adapterSource) -cne $adapter.sourceSha256 -or
       (Hash $adapterInput) -cne $adapter.compilerInputSha256 -or
       (Hash (Join-Path $sessionRoot 'adapter-native/runtime/q73.cp312-win_amd64.pyd')) -cne $sessionExpected['q73.cp312-win_amd64.pyd']){throw 'Runtime overlay input changed during staging.'}
    Assert-ExpectedFiles $sessionModules $sessionExpected
}
Assert-RuntimeSession
$native=Join-Path $project 'native-hardware/out/HvxAcquisitionNative.dll';$nativeHash=Hash $native
if($nativeHash -cne $compiled.FrozenNativeDLL.SHA256){throw 'The production native bridge differs from the frozen candidate.'}
$hybridPinHash=Hash $pin;$hybridLibrary=Join-Path $hybrid 'HvxHybridCore.dll';$hybridHash=(Get-Content -LiteralPath $pin -Raw).Trim()
if($hybridHash -cnotmatch '^[A-F0-9]{64}$' -or (Hash $hybridLibrary) -cne $hybridHash){throw 'Hybrid library and pin differ.'}
$before=@(Source-Inventory)
$null=New-Item -ItemType Directory -Path $modules,$evidence
# Old r57/v30 manifests are retained as upstream evidence, not rewritten or
# falsely certified. UI contracts keep their existing independent validators.
Invoke-Builder 'Build-R58UiModules.ps1' @{OutputRoot=$modules;HybridPinFile=$pin;MaintenancePassword=$true;ProtectedRelease=$true;Configuration='Release'}
$uiManifestPath=Join-Path $modules 'ui-module-build.json';$ui=Get-Content -LiteralPath $uiManifestPath -Raw|ConvertFrom-Json
if(-not $ui.MaintenancePasswordEnabled -or -not $ui.ProtectedRelease -or $ui.Configuration -cne 'Release' -or
   'MAINTENANCE_PASSWORD_GUARD' -cnotin @($ui.CompilerDefines)){throw 'UI modules lack the protected maintenance profile.'}
foreach($file in @(Files $hardware)) {
    $relative=$file.Substring($hardware.Length+1).Replace('\','/')
    if($relative -ceq 'hardware-build-manifest.json'){continue}
    Copy-Checked $file (Contained-Path $modules $relative) $hardwareExpected[$relative]
}
Copy-Checked $hardwareManifestPath (Join-Path $evidence 'hardware-build-manifest.json') $hardwareManifestHash
Invoke-Builder 'Stage-AlgorithmDependencies.ps1' @{OutputRoot=$modules}
Invoke-Builder 'Build-ReportEngine.ps1' @{OutputDirectory=(Join-Path $modules 'ReportGenerator/runtime')}
$calibrationBuild=Join-Path $evidence 'calibration-source-build'
$global:LASTEXITCODE=0
& (Join-Path $project 'modules-src/CalibrationStandalone/Build.ps1') -OutputDirectory $calibrationBuild | Out-Host
if($LASTEXITCODE -ne 0){throw 'Calibration acquisition compilation failed.'}
foreach($name in @('XIONGXIN.Calibration.Verification.exe','XIONGXIN.Calibration.Verification.exe.config')){Copy-Checked (Join-Path $calibrationBuild $name) (Join-Path $modules ('CalibrationAcquisition/'+$name))}
Invoke-Builder 'Build-LocationAlgorithm.ps1' @{OutputDirectory=(Join-Path $evidence 'location-source-build');HybridPinFile=$pin;ProtectedRelease=$true}
$locationName='fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll';$location=Join-Path $evidence ('location-source-build/'+$locationName);$locationHash=Hash $location
foreach($consumer in @('DataAnalysis','PressureTest')){Copy-Checked $location (Join-Path $modules ($consumer+'/'+$locationName)) $locationHash}
# Reuse the independently compiled complete 225-source worker, then apply the
# same existing pure-method protection to this candidate copy below.
$workers=@($compiled.Products|Where-Object Name -CEQ 'PressureRuntime')
if($workers.Count -ne 1 -or @($workers[0].Sources).Count -ne 225){throw 'Expected the complete frozen r58 pressure worker closure.'}
$worker=$workers[0]
Copy-Checked (Contained-Path $compile $worker.File) (Join-Path $modules 'PressureRuntime/Xiongxin.Pressure.Runtime.exe') $worker.SHA256
Copy-Checked (Contained-Path $compile $compiled.RuntimeConfig.File) (Join-Path $modules 'PressureRuntime/Xiongxin.Pressure.Runtime.exe.config') $compiled.RuntimeConfig.SHA256
[ordered]@{Schema='HVX.R58.PressureRuntimeReuse/1';LocalRevision='r58-hardware-desktop-test-20261012';SourceCount=225;Sources=$worker.Sources;CompileManifestSHA256=$compileManifestHash;BinarySHA256=$worker.SHA256;OldUpstreamPinsClaimedMatching=$false;ProductEntryExecuted=$false;HardwareAccessed=$false}|
    ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $evidence 'r58-pressure-source-manifest.json') -Encoding UTF8
# New explicit Task39 closure; the unchanged historical source manifest remains
# an origin record and is never silently updated to authorize these adaptations.
$task39Root=Join-Path $project 'modules-src/Task39InputWorker'
$task39Sources=@(foreach($part in @('src','vendor/Task38')){Get-ChildItem -LiteralPath (Join-Path $task39Root $part) -File -Filter '*.cs'|ForEach-Object FullName})|Sort-Object
if($task39Sources.Count -ne 56){throw 'Task39 current source closure must contain exactly 56 files.'}
$captureText=Get-Content -LiteralPath (Join-Path $task39Root 'src/HardwareCapture.cs') -Raw
if($captureText -match 'ArtDAQ_(Write|CreateAO|CreateDO|ResetDevice)' -or $captureText -match '\.(WriteAoVolts|SetDigitalOutputs|ForceSourceOff)\('){throw 'Task39 input capture may not directly drive output ports.'}
$triggerText=Get-Content -LiteralPath (Join-Path $task39Root 'src/ArtSharedTrigger.cs') -Raw
if($triggerText -match 'ArtDAQ_(CreateAO|WriteAnalog|ResetDevice)' -or $triggerText -notmatch 'settings.ArtDevice \+ "/port0/line7"' -or $triggerText -notmatch 'ArtDAQ_WriteDigitalLines'){throw 'Task39 trigger must remain a fixed P0.7-only adapter.'}
$task39Inputs=@($task39Sources|ForEach-Object{[ordered]@{Path=$_.Substring($project.Length+1).Replace('\','/');SHA256=(Hash $_)}})
$task39Stage=Join-Path $modules 'Task39';$null=New-Item -ItemType Directory -Path $task39Stage
$task39Exe=Join-Path $task39Stage 'Xiongxin.Task39.InputWorker.exe'
$task39Icon=Join-Path $task39Root 'assets/xiongxin.ico';$task39Logo=Join-Path $task39Root 'assets/xiongxin-original.png'
$task39Args=@('/nologo','/noconfig','/langversion:7.3','/target:winexe','/platform:x64','/optimize+','/deterministic+','/debug-',
    '/main:Xiongxin.ZeroBench.Program','/r:System.dll','/r:System.Core.dll','/r:System.Xml.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll',
    ('/win32icon:'+$task39Icon),('/resource:'+$task39Icon+',ZeroBench.Brand.ico'),('/resource:'+$task39Logo+',ZeroBench.Brand.Logo'),('/out:'+$task39Exe))
$task39Rsp=Join-Path $evidence 'task39-compile.rsp'
@(@($task39Args)+@($task39Sources)|ForEach-Object{'"'+$_.Replace('"','\"')+'"'})|Set-Content -LiteralPath $task39Rsp -Encoding UTF8
$compiler=Join-Path $PSScriptRoot 'compiler/csc.exe'
$task39Lines=@(& $compiler ('@'+$task39Rsp) 2>&1|ForEach-Object{$_.ToString()});$task39Code=$LASTEXITCODE
$task39Lines|Set-Content -LiteralPath (Join-Path $evidence 'task39-compile.log') -Encoding UTF8
if($task39Code -ne 0){$task39Lines|Out-Host;throw 'r58 Task39 compilation failed.'}
Copy-Checked (Join-Path $project 'App.config') ($task39Exe+'.config')
$oldTask39Manifest=Join-Path $task39Root 'provenance/source-manifest.json'
[ordered]@{Schema='HVX.R58.Task39SourceBuild/1';LocalRevision='r58-hardware-desktop-test-20261012';SourceCount=$task39Sources.Count;Sources=$task39Inputs;CompilerSHA256=(Hash $compiler);OriginManifestSHA256=(Hash $oldTask39Manifest);OldUpstreamPinsClaimedMatching=$false;BinarySHA256=(Hash $task39Exe);NativeSHA256=$nativeHash;ProductEntryExecuted=$false;HardwareAccessed=$false}|
    ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $evidence 'r58-task39-source-manifest.json') -Encoding UTF8
# Native bridge must be adjacent to every owning managed assembly/process, not
# inferred from an SDK search path. The host's own copy is staged by the root builder.
$nativeCopies=@()
foreach($folder in @('DataAnalysis','PressureTest','CalibrationTest','CalibrationAcquisition','PressureRuntime','Task39','Task38','HardwareIntegration')) {
    $target=Join-Path $modules ($folder+'/HvxAcquisitionNative.dll');Copy-Checked $native $target $nativeHash
    $nativeCopies+=[ordered]@{Path=$folder+'/HvxAcquisitionNative.dll';SHA256=$nativeHash}
}
# Frozen CPython/NumPy/q86 bytes are retained; only the independently compiled
# q73 transport adapter is overlaid. No product, Python entry, or SDK is run.
foreach($entry in $sessionPins.PSObject.Properties){Copy-Checked (Contained-Path $sessionModules $entry.Name) (Contained-Path $modules ('r71/'+$entry.Name)) $entry.Value}
$researchPins=Join-Path $output 'research-pins.json';Copy-Checked $sessionPinsPath $researchPins $sessionPinsHash
Copy-Checked $runtimeReceipt (Join-Path $evidence 'research-runtime-input-receipt.json') $runtime.ReceiptSHA256
Copy-Checked $pinsPath (Join-Path $evidence 'research-frozen-pins.json') $runtime.PinsSHA256
Copy-Checked $sessionReceiptPath (Join-Path $evidence 'runtime-session-receipt.json') $sessionReceiptHash
Copy-Checked $adapterReceiptPath (Join-Path $evidence 'research-adapter-source-receipt.json') $adapterReceiptHash
$runtimeExpected=$sessionExpected
Assert-ExpectedFiles (Join-Path $modules 'r71') $runtimeExpected
$hybridStage=Join-Path $modules 'Hybrid';Copy-Checked $hybridLibrary (Join-Path $hybridStage 'HvxHybridCore.dll') $hybridHash
[ordered]@{schema='HVX.HybridNative/1';abiVersion=1;architecture='x64';libraryFile='HvxHybridCore.dll';librarySha256=$hybridHash}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $hybridStage 'native-manifest.json') -Encoding UTF8
$modelPayload=Join-Path $output 'model-payload';Invoke-Builder 'Stage-DefectModels.ps1' @{OutputDirectory=$modelPayload}
$protection=@(& (Join-Path $PSScriptRoot 'Protect-StagedAlgorithms.ps1') -ModulesRoot $modules -BuildRoot $output -EvidenceRoot (Join-Path $evidence 'protection'))
# Build-only metadata stays outside the embedded runtime.
foreach($name in @('ui-module-build.json','legacy-unit-pins.json')){Copy-Checked (Join-Path $modules $name) (Join-Path $evidence $name);Remove-Item -LiteralPath (Join-Path $modules $name)}
foreach($log in @(Files $modules)|Where-Object{[IO.Path]::GetFileName($_) -ceq 'compile.log'}) {
    $relative=$log.Substring($modules.Length+1);Copy-Checked $log (Join-Path $evidence ('ui-logs/'+$relative));Remove-Item -LiteralPath $log
}
Assert-FrozenInputs
Assert-ExpectedFiles $compile $compileExpected;Assert-ExpectedFiles $hardware $hardwareExpected
foreach($entry in $hw.Inputs){if((Hash (Contained-Path $frozenOwner $entry.Path)) -cne $entry.Sha256){throw ('Hardware input changed during staging: '+$entry.Path)}}
Assert-HardwareSourceEquivalence;Assert-RuntimeSession
if((Hash $pin) -cne $hybridPinHash -or (Hash $hybridLibrary) -cne $hybridHash){throw 'Hybrid inputs changed during staging.'}
$after=@(Source-Inventory)
if(($before|ConvertTo-Json -Depth 4 -Compress) -cne ($after|ConvertTo-Json -Depth 4 -Compress)){throw 'Source files or module builders changed during staging.'}
Assert-ExpectedFiles (Join-Path $modules 'r71') $runtimeExpected
foreach($copy in $nativeCopies){if((Hash (Contained-Path $modules $copy.Path)) -cne $copy.SHA256){throw 'A native bridge copy changed during protection.'}}
$moduleFiles=@(Files $modules|ForEach-Object{[ordered]@{Path=$_.Substring($modules.Length+1).Replace('\','/');Bytes=(Get-Item -LiteralPath $_).Length;SHA256=(Hash $_)}})
$moduleManifest=Join-Path $output 'r58-desktop-test-module-manifest.json'
[ordered]@{Schema='HVX.R58.DesktopTestModules/1';LocalRevision='r58-hardware-desktop-test-20261012';CompletedUtc=[DateTime]::UtcNow.ToString('o');Modules=$moduleFiles;SourceInputs=$before;
    CompileOnlyManifestSHA256=$compileManifestHash;HardwareManifestSHA256=$hardwareManifestHash;FrozenSourceRoot=$frozenOwner;UiSourceRoot=$project;SourceInputsUnchanged=$true;FrozenInputsUnchanged=$true;HardwareSourceEquivalent=$true;
    MaintenancePasswordEnabled=$true;ProtectedRelease=$true;OwnedAlgorithmProtection=$protection;NativeCopies=$nativeCopies;
    SourceUiRuntime=[ordered]@{ManifestPath='r58-module-evidence/ui-module-build.json';ManifestSHA256=(Hash (Join-Path $evidence 'ui-module-build.json'))};
    ResearchRuntime=[ordered]@{Files=452;PinsSHA256=$sessionPinsHash;ReceiptSHA256=$runtime.ReceiptSHA256;CorrectedEPlusZipSHA256=$runtime.ZipSHA256;Entry='q73 retained transport -> q86 unchanged corrected composition';RuntimeSessionRoot=$sessionRoot;RuntimeSessionReceiptSHA256=$sessionReceiptHash;AdapterReceiptSHA256=$adapterReceiptHash;AdapterSourceSHA256=$adapter.sourceSha256;CompilerInputSHA256=$adapter.compilerInputSha256;FrozenPinsSHA256=$runtime.PinsSHA256;ByteIdenticalReusedFiles=451;ChangedModule='q73.cp312-win_amd64.pyd'};
    Task39LocalRevisionManifestSHA256=(Hash (Join-Path $evidence 'r58-task39-source-manifest.json'));PressureLocalRevisionManifestSHA256=(Hash (Join-Path $evidence 'r58-pressure-source-manifest.json'));
    LocationAlgorithmSHA256=$locationHash;HybridSHA256=$hybridHash;ProductEntryExecuted=$false;SDKCalled=$false;HardwareAccessed=$false;CustomerInstaller=$false;PhysicalAcceptance=$false}|
    ConvertTo-Json -Depth 10|Set-Content -LiteralPath $moduleManifest -Encoding UTF8
[pscustomobject]@{ModulesRoot=$modules;ResearchPins=$researchPins;RuntimeSessionReceiptSHA256=$sessionReceiptHash;ModuleManifest=$moduleManifest;ModuleManifestSHA256=(Hash $moduleManifest);ModelPayloadRoot=$modelPayload;EvidenceRoot=$evidence;Files=$moduleFiles.Count;MaintenancePasswordEnabled=$true;ProtectedRelease=$true;HardwareAccessed=$false;SDKCalled=$false;ProductEntryExecuted=$false}
