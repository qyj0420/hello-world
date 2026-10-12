[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [string]$CompileRoot='',
    [string]$HardwareRoot='',
    [string]$FrozenSourceRoot='',
    [Parameter(Mandatory=$true)][string]$RuntimeSessionRoot
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$output=[IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if([String]::IsNullOrWhiteSpace($FrozenSourceRoot)){$FrozenSourceRoot=Join-Path (Split-Path -Parent $root) 'r58-hardware-source-20261012'}
$FrozenSourceRoot=[IO.Path]::GetFullPath($FrozenSourceRoot).TrimEnd('\')
if([String]::IsNullOrWhiteSpace($CompileRoot)){$CompileRoot=Join-Path $FrozenSourceRoot 'build/r58-hardware-compile-only-20261012-03'}
if([String]::IsNullOrWhiteSpace($HardwareRoot)){$HardwareRoot=Join-Path $FrozenSourceRoot 'build/r58-hardware-modules-03'}
$RuntimeSessionRoot=[IO.Path]::GetFullPath($RuntimeSessionRoot).TrimEnd('\')
if(-not $RuntimeSessionRoot.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Runtime session must be an explicit owned hotfix build.'}
if(-not $output.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Use a fresh project build directory.'}
function Unlinked([string]$path){
    for($walk=[IO.Path]::GetFullPath($path);-not [String]::IsNullOrWhiteSpace($walk);$walk=[IO.Path]::GetDirectoryName($walk)){
        if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw ('Linked build input/output prohibited: '+$walk)}
    }
}
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
Unlinked $root;Unlinked $output;Unlinked $CompileRoot;Unlinked $HardwareRoot;Unlinked $FrozenSourceRoot;Unlinked $RuntimeSessionRoot
$frozenManifest=Join-Path $CompileRoot 'compile-only-manifest.json'
if((Hash $frozenManifest) -cne 'BB426073103600829E6D0C598B7A8E31DC4BED7CBF998827BB02888DE5861EE1'){throw 'Wrong reviewed r58 compile closure.'}
$frozen=Get-Content -LiteralPath $frozenManifest -Raw|ConvertFrom-Json
if($frozen.SourceRoot -cne $FrozenSourceRoot -or $frozen.Status -cne 'COMPILE_ONLY_PASS' -or -not $frozen.InputSetsAndHashesUnchanged -or -not $frozen.BaseInputsUnchanged){throw 'Frozen r58 input proof or source owner is not passing.'}
foreach($inputFile in $frozen.Inputs){Unlinked $inputFile.Path;if((Hash $inputFile.Path) -cne $inputFile.SHA256){throw ('Changed reviewed input: '+$inputFile.RelativePath)}}
# This is a new local revision. Do not overwrite old v30/r57 provenance or
# assert that its obsolete frozen hashes match current r58 hardware sources.
function Inventory {
    $paths=New-Object 'System.Collections.Generic.List[string]'
    foreach($directory in @('src','modules-src','hybrid-src','tools','dependencies','assets','native-hardware')){
        foreach($file in @(Get-ChildItem -LiteralPath (Join-Path $root $directory) -Recurse -File)){
            # The source root itself lives under build; only its relative path
            # is used to reject generated nested build output.
            $relative=$file.FullName.Substring($root.Length+1)
            if($relative -match '(^|\\)(build|__pycache__)(\\|$)'){continue}
            # Independent post-build validators/exporters are not compiler or
            # staging inputs; their own hashes are recorded by their receipts.
            if($relative -in @('tools\Test-R58DesktopTestOffline.ps1','tools\Export-R58DesktopTest.ps1')){continue}
            Unlinked $file.FullName;$paths.Add($file.FullName)
        }
    }
    foreach($name in @('App.config','app.manifest')){$paths.Add((Join-Path $root $name))}
    @($paths|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_.Substring($root.Length+1).Replace('\','/');Bytes=(Get-Item -LiteralPath $_).Length;SHA256=(Hash $_)}})
}
$before=@(Inventory)
New-Item -ItemType Directory -Path $output|Out-Null
$evidence=Join-Path $output 'r58-desktop-evidence';New-Item -ItemType Directory -Path $evidence|Out-Null
$started=[DateTime]::UtcNow.ToString('o')
$before|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $evidence 'source-inputs-before.json') -Encoding UTF8
$hybrid=& (Join-Path $root 'tools/Build-HybridNative.ps1') -OutputDirectory (Join-Path $output 'hybrid-build')
$stage=& (Join-Path $root 'tools/Stage-R58DesktopTestModules.ps1') -OutputRoot $output -CompileRoot $CompileRoot -HardwareRoot $HardwareRoot -FrozenSourceRoot $FrozenSourceRoot -RuntimeSessionRoot $RuntimeSessionRoot -HybridPinFile $hybrid.PinFile -HybridNativeDirectory $hybrid.Directory
if($null -eq $stage -or -not (Test-Path -LiteralPath $stage.ModulesRoot -PathType Container)){throw 'Complete module staging failed.'}
$embedded=& (Join-Path $root 'tools/Build-EmbeddedPayload.ps1') -ModulesRoot $stage.ModulesRoot -OutputDirectory (Join-Path $output 'embedded-resources')
$exe=Join-Path $output 'Xiongxin.OWTS.Desktop.exe'
$compiler=Join-Path $root 'tools/compiler/csc.exe'
$framework=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$refs=@('mscorlib.dll','System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll','System.Xml.Linq.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll','System.Security.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','Accessibility.dll')
$arguments=@('/nologo','/noconfig','/nostdlib+','/langversion:7.3','/deterministic+','/target:winexe','/platform:x64','/optimize+','/debug-',
    '/main:Xiongxin.OWTS.Desktop.Program',
    '/define:HYBRID_CORE;HYBRID_ROOT;V30_BATCH_VALIDATION_20261008;PUBLIC_BETA_NO_DRIVER_20261011;PUBLIC_BETA_NO_DRIVER_V112_20261011;MAINTENANCE_PASSWORD_GUARD',
    ('/win32icon:'+(Join-Path $root 'assets/shortcuts/256.ico')),
    ('/win32manifest:'+(Join-Path $root 'app.manifest')),
    ('/resource:'+(Join-Path $root 'assets/shortcuts/256.ico')+',HVX.NeutralWindow.ico'),
    ('/resource:'+(Join-Path $root 'assets/xiongxin.ico')+',Brand.ico'),
    ('/resource:'+(Join-Path $root 'assets/xiongxin-original.png')+',Brand.png'),
    ('/resource:'+$stage.ResearchPins+',HVX.Research.Pins'),
    ('/resource:'+$hybrid.PinFile+',HVX.HybridNative.Pin'),('/out:'+$exe))
foreach($name in $refs){$reference=Join-Path $framework $name;Unlinked $reference;$arguments+='/r:'+$reference}
$arguments+=@($embedded.ResourceArguments)
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -File -Filter '*.cs'|Where-Object{$_.Name -cne 'AssemblyInfo.cs'}|Sort-Object FullName|ForEach-Object FullName)
# Derive metadata only, retaining the frozen original and all business inputs.
# This avoids labelling the new r58 test executable as the old r57 installer.
$originalAssemblyInfo=Join-Path $root 'src/AssemblyInfo.cs'
$metadata=[IO.File]::ReadAllText($originalAssemblyInfo)
$oldInfo='V1.1.2 r57; isolated customer no-driver installer; bounded project and report inputs; explicit AI region statistics; Eplus-final-pair-linear-score-ownership-r01; physical acceptance and dependency maintenance pending'
if($metadata.Split(@($oldInfo),[StringSplitOptions]::None).Length -ne 2){throw 'Unexpected frozen assembly metadata.'}
$metadata=$metadata.Replace($oldInfo,'V1.1.2 r58 desktop test 20261012; native hardware API ownership; corrected Eplus composition; ordered location thresholds 30/45/50; physical acceptance pending')
$derivedMetadata=Join-Path $evidence 'AssemblyInfo.R58.cs'
[IO.File]::WriteAllText($derivedMetadata,$metadata,(New-Object Text.UTF8Encoding($false)))
$sources+=@($derivedMetadata)
$response=Join-Path $evidence 'desktop-compile.rsp'
@(@($arguments)+@($sources)|ForEach-Object{'"'+$_.Replace('"','\"')+'"'})|Set-Content -LiteralPath $response -Encoding UTF8
$compileLines=@(& $compiler ('@'+$response) 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE
$compileLines|Set-Content -LiteralPath (Join-Path $evidence 'desktop-compile.log') -Encoding UTF8
if($code -ne 0 -or -not (Test-Path -LiteralPath $exe -PathType Leaf)){throw ('Desktop compilation failed; see '+$evidence)}
$unprotectedHash=Hash $exe
$protected=& (Join-Path $root 'tools/Protect-OwnedAlgorithms.ps1') -InputAssembly $exe -OutputDirectory (Join-Path $output 'host-protection') -Profile HostPure
$receiptPath=Join-Path $output 'host-protection/receipt.json';$receipt=Get-Content -LiteralPath $receiptPath -Raw|ConvertFrom-Json
if($receipt.InputSHA256 -cne $unprotectedHash -or $receipt.OutputSHA256 -cne (Hash $protected) -or -not $receipt.ExistingMetadataPreserved -or -not $receipt.InputUnchanged){throw 'Protected host evidence mismatch.'}
Copy-Item -LiteralPath $protected -Destination $exe -Force
Copy-Item -LiteralPath (Join-Path $root 'App.config') -Destination ($exe+'.config')
$native=Join-Path $root 'native-hardware/out/HvxAcquisitionNative.dll'
if((Hash $native) -cne '9598652183EDA702E90A672AB17A81D1DA9B24BBE31192C1ACFCACFB1B79C4CC'){throw 'Native bridge changed.'}
Copy-Item -LiteralPath $native -Destination (Join-Path $output 'HvxAcquisitionNative.dll')
foreach($directory in @('assets','assets/shortcuts','assets/reference-simulation','OEMpic')){New-Item -ItemType Directory -Path (Join-Path $output $directory) -Force|Out-Null}
foreach($relative in @('assets/device-asset-catalog.v1.json','assets/xiongxin.ico','assets/xiongxin-original.png','assets/shortcuts/256.ico','assets/shortcuts/512.ico','assets/reference-simulation/rdfz-jmf-v1.json')){
    Copy-Item -LiteralPath (Join-Path $root $relative) -Destination (Join-Path $output $relative)
}
if(Test-Path -LiteralPath (Join-Path $root 'docs/international')){
    New-Item -ItemType Directory -Path (Join-Path $output 'docs') -Force|Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'docs/international') -Destination (Join-Path $output 'docs/international') -Recurse
}
if ([IO.Path]::GetFullPath($stage.ResearchPins) -cne [IO.Path]::GetFullPath((Join-Path $output 'research-pins.json'))) {
    Copy-Item -LiteralPath $stage.ResearchPins -Destination (Join-Path $output 'research-pins.json')
}
$after=@(Inventory)
if(($before|ConvertTo-Json -Depth 5 -Compress) -cne ($after|ConvertTo-Json -Depth 5 -Compress)){throw 'Build inputs changed; output is not publishable.'}
foreach($inputFile in $frozen.Inputs){if((Hash $inputFile.Path) -cne $inputFile.SHA256){throw ('Frozen input changed: '+$inputFile.RelativePath)}}
$version=[Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
if($version -cne '1.1.2.0'){throw 'Wrong desktop version.'}
[ordered]@{
    Schema='HVX.R58.CompleteDesktopTestBuild/1';Revision='r58-desktop-test-20261012';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o')
    CompleteDesktopExecutable=$true;CustomerInstaller=$false;DriverInstallerIncluded=$false;FileVersion=$version;Architecture='AMD64';Framework='.NET Framework 4.8'
    Executable='Xiongxin.OWTS.Desktop.exe';ExecutableSHA256=(Hash $exe);CompilerSHA256=(Hash $compiler);SourceInputCount=$before.Count;SourceUnchanged=$true
    FrozenCompileClosureSHA256=(Hash $frozenManifest);FrozenSourceRoot=$FrozenSourceRoot;UiSourceRoot=$root;ModuleManifestSHA256=(Hash $stage.ModuleManifest);EmbeddedPayloadHash=$embedded.BundleHash;EmbeddedFiles=$embedded.FileCount
    RuntimeSessionRoot=$RuntimeSessionRoot;RuntimeSessionReceiptSHA256=$stage.RuntimeSessionReceiptSHA256
    ResearchPinsSHA256=(Hash $stage.ResearchPins);NativeBridgeSHA256=(Hash $native);HybridPin=$hybrid.Sha256;ProtectedRelease=$true;MaintenancePasswordEnabled=$true
    HostProtection=[ordered]@{InputSHA256=$receipt.InputSHA256;OutputSHA256=$receipt.OutputSHA256;ReceiptSHA256=(Hash $receiptPath)}
    DerivedAssemblyMetadata=[ordered]@{OriginalPath='src/AssemblyInfo.cs';OriginalSHA256=(Hash $originalAssemblyInfo);DerivedPath='r58-desktop-evidence/AssemblyInfo.R58.cs';DerivedSHA256=(Hash $derivedMetadata);ChangeScope='Informational version string only; no business-code changes'}
    CompilerDefines=@($arguments|Where-Object{$_ -like '/define:*'});WarningCount=@($compileLines|Where-Object{$_ -match 'warning CS[0-9]+'}).Count
    ConfigurationProfile='PublicBeta-NoDriver-1.1.2-20261011';SharesExistingV112Profile=$true;LegacyFrozenFactoryPinsReverified=$false
    ProductEntryExecuted=$false;ProductionNativeLoaded=$false;SDKCalled=$false;HardwareAccessed=$false;PhysicalAcceptance=$false;Signed=$false
    Uncovered=@('User real-device acceptance','Full native high-level service migration','Complete historical GUI regression','Known-vulnerability release audit and code signing')
}|ConvertTo-Json -Depth 9|Set-Content -LiteralPath (Join-Path $output 'r58-desktop-test-build.json') -Encoding UTF8
Write-Output ('R58_COMPLETE_DESKTOP_EXE='+$exe)
