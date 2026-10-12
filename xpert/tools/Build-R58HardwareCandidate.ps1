[CmdletBinding()]
param(
    [string]$OutputRoot='',
    [string]$BaseRoot='',
    [string]$ResearchRuntimeRoot='',
    [ValidatePattern('^r58-[A-Za-z0-9._-]+$')][string]$LocalRevision='r58-hardware-candidate-20261012'
)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if([IO.Path]::GetFileName($root) -cne 'r58-hardware-source-20261012'){throw 'This isolated builder belongs only to the r58 hardware source tree.'}
if([String]::IsNullOrWhiteSpace($BaseRoot)){
    $portableBase=Join-Path $root 'provenance/r57-source-baseline'
    $BaseRoot=if(Test-Path -LiteralPath $portableBase -PathType Container){$portableBase}else{Join-Path ([IO.Path]::GetDirectoryName($root)) 'r57-customer-source-20261012'}
}
$base=[IO.Path]::GetFullPath($BaseRoot).TrimEnd('\')
if($base -eq $root -or -not (Test-Path -LiteralPath $base -PathType Container)){throw 'A separate read-only r57 base source tree is required.'}
if([String]::IsNullOrWhiteSpace($OutputRoot)){$OutputRoot='build/'+$LocalRevision+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)}
if(-not [IO.Path]::IsPathRooted($OutputRoot)){$OutputRoot=Join-Path $root $OutputRoot}
$output=[IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if(-not $output.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Use a new, nonexisting directory strictly under this r58 source tree build directory.'}
function Assert-Unlinked([string]$Path,[switch]$AllowMissing) {
    $full=[IO.Path]::GetFullPath($Path)
    if(-not $AllowMissing -and -not (Test-Path -LiteralPath $full)){throw ('Missing input: '+$full)}
    for($walk=$full;-not [String]::IsNullOrWhiteSpace($walk);$walk=[IO.Path]::GetDirectoryName($walk)) {
        if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw ('Linked paths are not accepted: '+$walk)}
    }
}
Assert-Unlinked $root;Assert-Unlinked $base;Assert-Unlinked $output -AllowMissing
function CsFiles([string]$Tree,[string]$Relative,[switch]$Recurse) {
    $directory=Join-Path $Tree $Relative;Assert-Unlinked $directory
    $pending=New-Object 'System.Collections.Generic.Stack[string]';$pending.Push($directory)
    $files=New-Object 'System.Collections.Generic.List[string]'
    while($pending.Count) {
        $folder=$pending.Pop()
        foreach($entry in @(Get-ChildItem -LiteralPath $folder -Force)) {
            if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){throw ('Linked source entry: '+$entry.FullName)}
            if($entry.PSIsContainer){if($Recurse){$pending.Push($entry.FullName)}}
            elseif($entry.Extension -ieq '.cs'){$files.Add($entry.FullName)}
        }
    }
    return @($files.ToArray()|Sort-Object -Unique)
}
function Shared([string]$Tree,[string[]]$Names) {@($Names|ForEach-Object{Join-Path $Tree ('src/'+$_)})}
function SourceSets([string]$Tree) {
    $language=Shared $Tree @('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','R2RoundedButton.cs','PressureRecordPublication.cs')
    $joints=Shared $Tree @('CableJointStore.cs','CableJointDialog.cs')
    $analysis=@($language)+@(CsFiles $Tree 'modules-src/DataAnalysis/src')
    foreach($part in @('vendor/PdMap','vendor/PdMap/Common','vendor/Spectrum','vendor/Task7','vendor/Task28','vendor/LocationTree','shared')){$analysis+=@(CsFiles $Tree ('modules-src/DataAnalysis/'+$part))}
    $scorePolicy=Join-Path $Tree 'src/LocationScorePolicy.cs'
    # The r57 baseline predates this shared source. Require it in the current
    # tree below, while preserving an honest old/new compiled-closure diff.
    if(Test-Path -LiteralPath $scorePolicy -PathType Leaf){$analysis+=$scorePolicy}
    $analysis+=Shared $Tree @('ZeroVoltageNoiseReference.cs','DefectModelCatalog.cs','AnalysisDateText.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','NoisePulseTemplates.cs','LocationCycleRecord.cs','LiveCalibrationRecord.cs','CalibrationPacketIdentityContract.cs','LegacyCalibrationBinding.cs')
    $analysis+=$joints
    $analysis+=@(Join-Path $Tree 'modules-src/LocationABCD/ABRelease/AB/source/PeakAnchoredLeadingEdgeMath.cs')
    $analysis+=@(Join-Path $Tree 'modules-src/ConclusionAlgorithm/EvidenceV3/HVX.Task15.Embedded.cs')
    $pressure=@($language)+@(CsFiles $Tree 'modules-src/PressureTest/src')+@(CsFiles $Tree 'modules-src/PressureTest/vendor' -Recurse)
    $pressure+=Shared $Tree @('ZeroVoltageNoiseReference.cs','LocationCycleRecord.cs')
    $pressure+=Join-Path $Tree 'modules-src/FactoryWorker/AdjacentHardwareRangePolicy.cs'
    $calibration=@($language)+@(CsFiles $Tree 'modules-src/CalibrationTest/src')+$joints
    $calibration+=Shared $Tree @('PdLinearHighPass.cs','LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','NoisePulseTemplates.cs','PdLinearLowPass.cs','CalibrationPulseDisplayWindow.cs','PicoCaptureCadence.cs')
    $calibration+=Join-Path $Tree 'modules-src/LocationABCD/ABRelease/AB/source/PeakAnchoredLeadingEdgeMath.cs'
    $calibration+=@(CsFiles $Tree 'modules-src/CalibrationStandalone'|Where-Object{[IO.Path]::GetFileName($_) -notin @('Program.cs','ControlBoxTests.cs')})
    $calibration+=@(CsFiles $Tree 'modules-src/CalibrationStandalone/PicoCore')
    return [ordered]@{Host=@(CsFiles $Tree 'src');FactoryWorker=@(CsFiles $Tree 'modules-src/FactoryWorker' -Recurse);DataAnalysis=@($analysis|Sort-Object -Unique);PressureTest=@($pressure|Sort-Object -Unique);CalibrationTest=@($calibration|Sort-Object -Unique)}
}
function Hash([string]$Path) {Assert-Unlinked $Path;return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
if([String]::IsNullOrWhiteSpace($ResearchRuntimeRoot)){
    $portableRuntime=Join-Path $root 'dependencies/ResearchRuntime/r58-corrected-runtime/r71'
    $ResearchRuntimeRoot=if(Test-Path -LiteralPath $portableRuntime -PathType Container){$portableRuntime}else{Join-Path $root 'build/r58-eplus-corrected-runtime-20261012-01/modules/r71'}
}
$researchRuntime=[IO.Path]::GetFullPath($ResearchRuntimeRoot).TrimEnd('\')
if(-not $researchRuntime.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -and $researchRuntime -cne (Join-Path $root 'dependencies/ResearchRuntime/r58-corrected-runtime/r71')){throw 'Research runtime must be a separately verified r58 build or its exact portable dependency input.'}
Assert-Unlinked $researchRuntime
$researchPins=Join-Path ([IO.Path]::GetDirectoryName($researchRuntime)) 'research-pins.json'
$researchReceipt=Join-Path ([IO.Path]::GetDirectoryName($researchRuntime)) 'research-runtime-input-receipt.json'
$pins=[IO.File]::ReadAllText($researchPins)|ConvertFrom-Json
$researchFiles=@(Get-ChildItem -LiteralPath $researchRuntime -Recurse -File|Sort-Object FullName|ForEach-Object FullName)
$researchNativeNames=@($researchFiles|Where-Object{[IO.Path]::GetFileName($_) -like 'q*.pyd'}|ForEach-Object{[IO.Path]::GetFileName($_)}|Sort-Object)
$expectedNames=@(71..86|ForEach-Object{'q'+$_+'.cp312-win_amd64.pyd'}|Sort-Object)
if(($researchNativeNames|ConvertTo-Json -Compress) -cne ($expectedNames|ConvertTo-Json -Compress)){throw 'Corrected E+ requires exactly q71-q86 rebuilt native modules.'}
if(@($pins.PSObject.Properties).Count -ne $researchFiles.Count){throw 'Runtime file set does not match its pin inventory.'}
foreach($file in $researchFiles){
    $relative=$file.Substring($researchRuntime.Length+1).Replace('\','/')
    if($null -eq $pins.PSObject.Properties[$relative] -or (Hash $file) -cne $pins.$relative){throw ('Research runtime pin mismatch: '+$relative)}
}
$researchProof=[IO.File]::ReadAllText($researchReceipt)|ConvertFrom-Json
if($researchProof.eSourceReceipt.correctedEPlusZipSha256 -cne 'F69A945B5FBD67C114F64B736C747CDA51124883B5AAFC22F6F962D652884D2F'){throw 'Wrong corrected E+ provenance.'}
$algorithmSources=@(Join-Path $root 'modules-src/LocationABCD/CDRelease/manifest.json')
foreach($part in @('LocationE','LocationEPlusPacket','LocationEPlusFinalScore','ResearchAdapter','LocationABCD/CDRelease')){
    $algorithmSources+=@(Get-ChildItem -LiteralPath (Join-Path $root ('modules-src/'+$part)) -Recurse -File|Where-Object{$_.Extension -in @('.py','.json') -and $_.FullName -notmatch '[\\/]__pycache__[\\/]'}|ForEach-Object FullName)
}
$algorithmSources+=@((Join-Path $root 'provenance/eplus-corrected-20261012-r01/import-receipt.json'),
    (Join-Path $root 'provenance/eplus-corrected-20261012-r01/package/eplus_api.py'),
    (Join-Path $root 'provenance/eplus-corrected-20261012-r01/package/source/current/Eplus/profile.json'))
$algorithmSources+=@((Join-Path $root 'dependencies/ResearchRuntime/frozen-runtime-inputs.json'),
    (Join-Path $root 'dependencies/ResearchRuntime/portable-input-receipt.json'))
$portableCompilerInput=Join-Path $root 'dependencies/ResearchRuntime/r58-corrected-runtime/research-native'
$algorithmSources+=@(Get-ChildItem -LiteralPath $portableCompilerInput -Recurse -File|ForEach-Object FullName)
$researchBuildScripts=@('Build-ResearchNative.py','Build-ResearchRuntime.ps1','Stage-ResearchRuntime.py','Stage-ResearchFrozenRuntime.py','Get-CppToolchain.ps1')|ForEach-Object{Join-Path $PSScriptRoot $_}
function Pe([string]$Path) {
    Assert-Unlinked $Path;$stream=[IO.File]::OpenRead($Path)
    try {
        $reader=New-Object IO.BinaryReader($stream)
        if($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D){throw ('Invalid MZ: '+$Path)}
        $stream.Position=0x3c;$offset=$reader.ReadInt32()
        if($offset -lt 64 -or $offset -gt $stream.Length-26){throw ('Invalid PE offset: '+$Path)}
        $stream.Position=$offset
        if($reader.ReadUInt32() -ne 0x4550){throw ('Invalid PE signature: '+$Path)}
        $machine=$reader.ReadUInt16();$stream.Position=$offset+24;$optional=$reader.ReadUInt16()
        if($machine -ne 0x8664 -or $optional -ne 0x20b){throw ('Expected x64/PE32+ image: '+$Path)}
        return [ordered]@{Machine='0x8664';Architecture='AMD64';OptionalHeader='PE32+'}
    } finally {$stream.Dispose()}
}
$compiler=Join-Path $root 'tools/compiler/csc.exe'
$native=Join-Path $root 'native-hardware/out/HvxAcquisitionNative.dll'
$nativeEvidence=Join-Path $root 'native-hardware/out/build-evidence.json'
$nativeProof=[IO.File]::ReadAllText($nativeEvidence)|ConvertFrom-Json
$nativeHash=Hash $native
if($nativeProof.productionDll.sha256 -cne $nativeHash){throw 'Native DLL differs from its frozen native build receipt; rebuild/review native evidence first.'}
$nativePe=Pe $native
$nativeInputs=@($native,$nativeEvidence)
foreach($entry in $nativeProof.files) {
    $relative=[string]$entry.path
    if([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or ($relative -split '[\\/]') -contains '..'){throw 'Invalid native evidence input path.'}
    $path=Join-Path (Join-Path $root 'native-hardware') $relative
    if((Hash $path) -cne $entry.sha256){throw ('Native input changed after native build: '+$relative)}
    $nativeInputs+=$path
}
foreach($name in @('api-coverage.json','production-exports.txt','production-pe-audit.txt')){$nativeInputs+=Join-Path $root ('native-hardware/out/'+$name)}
# Bind exact .NET Framework reference files rather than implicit compiler/GAC
# search results. No framework/product assembly is loaded or executed here.
$framework=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)) 'Microsoft.NET/Framework64/v4.0.30319'
$frameworkReferences=@('mscorlib.dll','System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll','System.Xml.Linq.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll','System.Security.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','Accessibility.dll')|ForEach-Object{Join-Path $framework $_}
foreach($path in $frameworkReferences){Assert-Unlinked $path}
function CompilerInputs {@(Get-ChildItem -LiteralPath (Join-Path $root 'tools/compiler') -File|Where-Object{$_.Extension -in @('.dll','.exe','.config','.rsp')}|Sort-Object Name|ForEach-Object FullName)}
Assert-Unlinked (Join-Path $root 'src/LocationScorePolicy.cs')
$sets=SourceSets $root;$baseSets=SourceSets $base
$legacyInput=Join-Path $root 'modules-src/LocationABCD/ABRelease/confirmed-legacy-inputs.json'
$resources=@(
    (Join-Path $root 'assets/xiongxin.ico'),(Join-Path $root 'assets/xiongxin-original.png'),
    (Join-Path $root 'assets/shortcuts/256.ico'),(Join-Path $root 'app.manifest'),
    (Join-Path $root 'modules-src/CalibrationTest/assets/approvedUI.png'),(Join-Path $root 'modules-src/CalibrationTest/assets/brand.ico'),
    (Join-Path $root 'modules-src/CalibrationStandalone/assets/xiongxin.ico'),(Join-Path $root 'modules-src/CalibrationStandalone/assets/xiongxin-original.png'))
$config=Join-Path $root 'App.config'
function Inventory {
    $currentSets=SourceSets $root
    $records=New-Object 'System.Collections.Generic.List[object]'
    $groups=[ordered]@{Source=@($currentSets.Values|ForEach-Object{$_}|Sort-Object -Unique);Resource=$resources;NativeFrozenInput=$nativeInputs;Compiler=@(CompilerInputs);FrameworkReference=$frameworkReferences;BuildScript=@($PSCommandPath)+$researchBuildScripts;RuntimeConfig=@($config);LegacyResourceSource=@($legacyInput);
        AlgorithmSource=$algorithmSources;ResearchRuntime=@($researchFiles)+@($researchPins,$researchReceipt)}
    foreach($kind in $groups.Keys) {
        foreach($path in @($groups[$kind]|Sort-Object -Unique)) {
            $full=[IO.Path]::GetFullPath($path)
            $relative=if($full.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){$full.Substring($root.Length+1).Replace('\','/')}else{$null}
            $records.Add([ordered]@{Kind=$kind;Path=$full;RelativePath=$relative;SHA256=(Hash $full)})
        }
    }
    return @($records.ToArray())
}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
$plannedSources=@($sets.Values|ForEach-Object{$_}|Sort-Object -Unique)
$inventoriedSources=@($before|Where-Object Kind -eq 'Source'|ForEach-Object{$_.Path}|Sort-Object -Unique)
if(($plannedSources|ConvertTo-Json -Compress) -cne ($inventoriedSources|ConvertTo-Json -Compress)){throw 'Managed source closure changed while build inputs were being selected.'}
$basePaths=@($baseSets.Values|ForEach-Object{$_}|Sort-Object -Unique)
$currentRelative=@($before|Where-Object{$null -ne $_.RelativePath}|ForEach-Object{$_.RelativePath}|Sort-Object -Unique)
$baseRelative=@($basePaths|ForEach-Object{$_.Substring($base.Length+1).Replace('\','/')})
$differenceRecords=New-Object 'System.Collections.Generic.List[object]'
$baseInventory=New-Object 'System.Collections.Generic.List[object]'
foreach($relative in @($currentRelative+$baseRelative|Sort-Object -Unique)) {
    $currentPath=Join-Path $root $relative;$basePath=Join-Path $base $relative
    $currentExists=Test-Path -LiteralPath $currentPath -PathType Leaf;$baseExists=Test-Path -LiteralPath $basePath -PathType Leaf
    $currentHash=if($currentExists){Hash $currentPath}else{$null};$baseHash=if($baseExists){Hash $basePath}else{$null}
    if($baseExists){$baseInventory.Add([ordered]@{Path=$basePath;SHA256=$baseHash})}
    if($currentHash -cne $baseHash){$differenceRecords.Add([ordered]@{Path=$relative;Change=$(if(-not $baseExists){'Added'}elseif(-not $currentExists){'Removed'}else{'Modified'});R57SHA256=$baseHash;R58SHA256=$currentHash})}
}
$null=New-Item -ItemType Directory -Path $output
$derivedResources=Join-Path $output 'build-inputs';$null=New-Item -ItemType Directory -Path $derivedResources
$legacyPins=Join-Path $derivedResources 'legacy-unit-pins.json'
@(([IO.File]::ReadAllText($legacyInput)|ConvertFrom-Json)|ForEach-Object{$_.sha256})|ConvertTo-Json|Set-Content -LiteralPath $legacyPins -Encoding UTF8
$derivedHash=Hash $legacyPins
$common=@('/nologo','/nostdlib+','/langversion:latest','/deterministic+','/optimize+','/debug-','/platform:x64','/define:R58_HARDWARE_CANDIDATE')+@($frameworkReferences|ForEach-Object{'/r:'+$_})
$products=New-Object 'System.Collections.Generic.List[object]'
function Build([string]$Name,[string]$Filename,[string]$Target,[string[]]$Sources,[string[]]$Extra,[string[]]$Embedded,[string[]]$References=@()) {
    $directory=Join-Path $output $Name;$null=New-Item -ItemType Directory -Path $directory
    $destination=Join-Path $directory $Filename
    $arguments=$common+@(('/target:'+$Target),('/out:'+$destination))+$Extra+@($Embedded|ForEach-Object{'/resource:'+$_})+@($References|ForEach-Object{'/r:'+$_})
    $rsp=Join-Path $directory 'compile.rsp'
    @(@($arguments)+@($Sources)|ForEach-Object{'"'+$_.Replace('"','\"')+'"'})|Set-Content -LiteralPath $rsp -Encoding UTF8
    # /noconfig is a compiler-command-line option, not a response-file option.
    # Otherwise csc silently imports its default response-file references.
    $lines=@(& $compiler /noconfig ('@'+$rsp) 2>&1|ForEach-Object{$_.ToString()});$exit=$LASTEXITCODE
    $lines|Set-Content -LiteralPath (Join-Path $directory 'compile.log') -Encoding UTF8
    if($exit -ne 0){throw ($Name+' compilation failed: '+($lines -join "`n"))}
    $pe=Pe $destination
    $identity=[Reflection.AssemblyName]::GetAssemblyName($destination)
    $nativeCopy=Join-Path $directory 'HvxAcquisitionNative.dll'
    Copy-Item -LiteralPath $native -Destination $nativeCopy
    if((Hash $nativeCopy) -cne $nativeHash){throw ('Native copy hash mismatch: '+$Name)}
    $embeddedRecords=@($Embedded|ForEach-Object{$parts=$_.Split(',');[ordered]@{Path=$parts[0];LogicalName=$parts[1];SHA256=(Hash $parts[0])}})
    $referenceRecords=@($frameworkReferences+$References|ForEach-Object{[ordered]@{Path=$_;SHA256=(Hash $_)}})
    $products.Add([ordered]@{Name=$Name;File=$Name+'/'+$Filename;SHA256=(Hash $destination);PE=$pe;AssemblyName=$identity.Name;AssemblyVersion=$identity.Version.ToString();Target=$Target;
        Sources=@($Sources|ForEach-Object{[ordered]@{Path=$_.Substring($root.Length+1).Replace('\','/');SHA256=(Hash $_)}});Resources=$embeddedRecords;References=$referenceRecords;
        Arguments=@('/noconfig')+$arguments;ResponseFileSHA256=(Hash $rsp);CompileLogSHA256=(Hash (Join-Path $directory 'compile.log'));WarningCount=@($lines|Where-Object{$_ -match '\bwarning CS\d+'}).Count;
        NativeDLL=[ordered]@{File=$Name+'/HvxAcquisitionNative.dll';SHA256=(Hash $nativeCopy);PE=(Pe $nativeCopy)}})
    Write-Host ('COMPILED_ONLY '+$Name+' sources='+$Sources.Count)
    return $destination
}
$brand=(Join-Path $root 'assets/xiongxin.ico')+',XIONGXIN.Brand.ico'
$neutral=(Join-Path $root 'assets/shortcuts/256.ico')+',HVX.NeutralWindow.ico'
$analysis=Build 'DataAnalysis' 'HVX.PdReview.V1.0.dll' 'library' $sets.DataAnalysis @() @($brand,$neutral,($legacyPins+',HVX.LegacyUnitPins'))
# Only the final emitted path is returned; progress output is kept on the host.
$null=Build 'PressureTest' 'Xiongxin.OWTS.PressureTest.dll' 'library' $sets.PressureTest @() @($brand,$neutral) @($analysis)
$analysisCopy=Join-Path $output 'PressureTest/HVX.PdReview.V1.0.dll';Copy-Item -LiteralPath $analysis -Destination $analysisCopy
if((Hash $analysisCopy) -cne (Hash $analysis)){throw 'PressureTest spectrum dependency copy mismatch.'}
$calibrationResources=@($brand,$neutral,
    ((Join-Path $root 'modules-src/CalibrationTest/assets/approvedUI.png')+',HVX.CalibrationProgram.ApprovedUi.png'),
    ((Join-Path $root 'modules-src/CalibrationTest/assets/brand.ico')+',HVX.CalibrationProgram.Xiongxin.ico'),
    ((Join-Path $root 'modules-src/CalibrationStandalone/assets/xiongxin.ico')+',Brand.ico'),
    ((Join-Path $root 'modules-src/CalibrationStandalone/assets/xiongxin-original.png')+',Brand.png'))
$null=Build 'CalibrationTest' 'Xiongxin.OWTS.CalibrationTest.dll' 'library' $sets.CalibrationTest @() $calibrationResources
$hostExtra=@(('/win32icon:'+(Join-Path $root 'assets/shortcuts/256.ico')),('/win32manifest:'+(Join-Path $root 'app.manifest')))
$hostResources=@($neutral,((Join-Path $root 'assets/xiongxin.ico')+',Brand.ico'),((Join-Path $root 'assets/xiongxin-original.png')+',Brand.png'),($researchPins+',HVX.Research.Pins'))
$null=Build 'Host' 'HostCompileOnly.dll' 'library' $sets.Host $hostExtra $hostResources
$workerExtra=@('/main:Xiongxin.FactoryTest.UnifiedWorkerEntry',('/win32icon:'+(Join-Path $root 'assets/xiongxin.ico')))
$workerResources=@(((Join-Path $root 'assets/xiongxin.ico')+',Factory.Brand.ico'),((Join-Path $root 'assets/xiongxin-original.png')+',Factory.Brand.Logo'),($researchPins+',HVX.Research.Pins'))
$null=Build 'PressureRuntime' 'Xiongxin.Pressure.Runtime.exe' 'winexe' $sets.FactoryWorker $workerExtra $workerResources
$runtimeConfig=Join-Path $output 'PressureRuntime/Xiongxin.Pressure.Runtime.exe.config';Copy-Item -LiteralPath $config -Destination $runtimeConfig
if((Hash $runtimeConfig) -cne (Hash $config)){throw 'Runtime config copy mismatch.'}
$stagedResearch=Join-Path $output 'r71';$null=New-Item -ItemType Directory -Path $stagedResearch
foreach($file in $researchFiles){
    $relative=$file.Substring($researchRuntime.Length+1)
    $destination=Join-Path $stagedResearch $relative;$parent=[IO.Path]::GetDirectoryName($destination)
    if(-not (Test-Path -LiteralPath $parent)){$null=New-Item -ItemType Directory -Path $parent}
    Copy-Item -LiteralPath $file -Destination $destination
    if((Hash $destination) -cne (Hash $file)){throw ('Research runtime copy changed: '+$relative)}
}
Copy-Item -LiteralPath $researchPins -Destination (Join-Path $output 'research-pins.json')
Copy-Item -LiteralPath $researchReceipt -Destination (Join-Path $output 'research-runtime-input-receipt.json')
$after=@(Inventory)
$unchanged=(($before|ConvertTo-Json -Depth 8 -Compress) -ceq ($after|ConvertTo-Json -Depth 8 -Compress)) -and (Hash $legacyPins) -ceq $derivedHash
$baseAfterSets=SourceSets $base
$baseAfterPaths=@($baseAfterSets.Values|ForEach-Object{$_}|Sort-Object -Unique)
$baseUnchanged=(($basePaths|ConvertTo-Json -Compress) -ceq ($baseAfterPaths|ConvertTo-Json -Compress))
foreach($record in $baseInventory){if((Hash $record.Path) -cne $record.SHA256){$baseUnchanged=$false}}
$differenceFile=Join-Path $output 'r58-vs-r57-base-differences.json'
[ordered]@{Schema='HVX.R58.LocalRevisionDifferences/1';LocalRevision=$LocalRevision;R58Root=$root;R57BaseRoot=$base;BaseReadOnly=$true;BaseInputsUnchanged=$baseUnchanged;
    Scope='Actual compiled managed closure plus resources, toolchain and frozen native inputs; no claim of a complete unrelated-project diff.';Differences=@($differenceRecords.ToArray())}|
    ConvertTo-Json -Depth 8|Set-Content -LiteralPath $differenceFile -Encoding UTF8
$readme=Join-Path $output 'README-CANDIDATE.txt'
@"
HVX $LocalRevision - COMPILE-ONLY HARDWARE CANDIDATE

NOT A CUSTOMER INSTALLER. NOT A DROP-IN PATCH. DO NOT SEND AS AN ACCEPTED RELEASE.
No compiled product, native production DLL, SDK or device was run by this script.
No real-device, calibration, high-voltage, emergency-stop timing, USB or driver
acceptance has been performed. Do not operate equipment with this directory.

Contents: complete current FactoryWorker closure (UnifiedWorkerEntry), the full
desktop source compiled as HostCompileOnly.dll, CalibrationTest, PressureTest,
and its separately compiled DataAnalysis spectrum dependency. The frozen x64
HvxAcquisitionNative.dll is copied and SHA256-verified in each product directory.
The corrected E+ q71-q86 runtime is staged as r71 with exact verified pins, embedded
in HostCompileOnly.dll and the worker entry. This is NOT complete application
deployment: the source module paths and all app assets still require packaging.
Vendor drivers, driver installers and complete application deployment assets are
not staged here. HostCompileOnly.dll is not the desktop application executable.

C++ stage: API concentration and handle ownership only. C# still owns high-level
workflow, channel allocation, buffers, callback generations, timeouts and safety
decisions. This does not assert a complete unified native hardware backend.

The local r58 sources intentionally differ from the r57 base and older upstream
pins. The legacy Build-FactoryWorker verifier was NOT invoked or bypassed, and
this build does NOT claim that old frozen FactoryWorker hashes still match.
Review compile-only-manifest.json and r58-vs-r57-base-differences.json; packaging,
protection, complete deployment and physical acceptance remain separate gates.

For portable source exports the default baseline is provenance/r57-source-baseline
when present. Otherwise the builder uses the sibling r57-customer-source-20261012
tree. An explicit -BaseRoot remains supported. The selected baseline is read only.
"@|Set-Content -LiteralPath $readme -Encoding UTF8
$manifest=Join-Path $output 'compile-only-manifest.json'
[ordered]@{Schema='HVX.R58.HardwareCandidate.CompileOnly/1';LocalRevision=$LocalRevision;StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Status=$(if($unchanged -and $baseUnchanged){'COMPILE_ONLY_PASS'}else{'FAIL_INPUT_CHANGED'});
    SourceRoot=$root;R57BaseRoot=$base;OutputRoot=$output;InputSetsAndHashesUnchanged=$unchanged;BaseInputsUnchanged=$baseUnchanged;Inputs=$before;CompilerSHA256=(Hash $compiler);BuildScriptSHA256=(Hash $PSCommandPath);
    FrozenNativeDLL=[ordered]@{Path=$native;SHA256=$nativeHash;PE=$nativePe;BuildEvidenceSHA256=(Hash $nativeEvidence)};
    CorrectedEPlusRuntime=[ordered]@{Source=$researchRuntime;StagedDirectory='r71';Files=$researchFiles.Count;PinsSHA256=(Hash $researchPins);ReceiptSHA256=(Hash $researchReceipt);ZipSHA256=$researchProof.eSourceReceipt.correctedEPlusZipSha256;Entry='q73 -> q86 corrected composition';ProductEntryExecuted=$false};
    DerivedResources=@([ordered]@{Path='build-inputs/legacy-unit-pins.json';SHA256=$derivedHash;InputSource=$legacyInput;SourceSHA256=(Hash $legacyInput)});
    Products=@($products.ToArray());CopiedDependency=[ordered]@{File='PressureTest/HVX.PdReview.V1.0.dll';SHA256=(Hash $analysisCopy)};RuntimeConfig=[ordered]@{File='PressureRuntime/Xiongxin.Pressure.Runtime.exe.config';SHA256=(Hash $runtimeConfig)};
    DifferenceFileSHA256=(Hash $differenceFile);ReadmeSHA256=(Hash $readme);LegacyFactoryPinsReverified=$false;CustomerInstaller=$false;DropInPatch=$false;HardwareAccessed=$false;SDKCalled=$false;ProductEntryExecuted=$false;ProductionNativeLoaded=$false;PhysicalAcceptance=$false;
    NativeMigrationStage='API concentration and handle ownership; high-level C# control remains';Uncovered=@('Complete application/deployment/protection packaging','Actual SDK, ART, Pico, USB and driver compatibility','Calibration and high-voltage operation','Physical emergency-stop timing and fault recovery')}|
    ConvertTo-Json -Depth 16|Set-Content -LiteralPath $manifest -Encoding UTF8
if(-not $unchanged -or -not $baseUnchanged){throw ('Inputs changed while compiling; candidate invalid: '+$manifest)}
Write-Output ('R58_COMPILE_ONLY_MANIFEST='+$manifest)
Write-Output ('R58_COMPILE_ONLY_MANIFEST_SHA256='+(Hash $manifest))
