[CmdletBinding(DefaultParameterSetName='Source')]
param(
    [Parameter(Mandatory=$true,ParameterSetName='Source')][string]$ModuleBuildRoot,
    [Parameter(Mandatory=$true,ParameterSetName='Protected')][string]$ProtectedBuildRoot,
    [switch]$IncludeDisplayChecks
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$inputIndex=@{}
$inputEvidence=New-Object 'System.Collections.Generic.List[object]'
function Get-ContainedFile([string]$Base,[string]$Relative) {
    if([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative)){throw 'Expected a relative evidence path.'}
    $basePath=[IO.Path]::GetFullPath($Base).TrimEnd('\')
    $path=[IO.Path]::GetFullPath((Join-Path $basePath $Relative))
    if(-not $path.StartsWith($basePath+'\',[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $path -PathType Leaf)){throw ('Missing or escaped evidence file: '+$Relative)}
    $cursor=Get-Item -LiteralPath $path
    while($null -ne $cursor){
        if(($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw ('Reparse point not permitted in evidence path: '+$path)}
        if($cursor.FullName.TrimEnd('\') -eq $basePath){break}
        $cursor=if($cursor -is [IO.FileInfo]){$cursor.Directory}else{$cursor.Parent}
    }
    return $path
}
function Add-Input([string]$Path,[string]$ExpectedHash='') {
    $path=[IO.Path]::GetFullPath($Path)
    $hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if($ExpectedHash -and ($ExpectedHash -notmatch '^[A-Fa-f0-9]{64}$' -or $hash -cne $ExpectedHash.ToUpperInvariant())){throw ('Evidence hash mismatch: '+$path)}
    if($inputIndex.ContainsKey($path)){
        if($inputIndex[$path] -cne $hash){throw ('Evidence changed while being read: '+$path)}
    }else{
        $inputIndex[$path]=$hash
        $inputEvidence.Add([ordered]@{Path=$path;SHA256=$hash})
    }
    return $hash
}
function Assert-ProtectionChain($Transform,[string]$ExpectedProfile,[string]$CompiledHash) {
    if($Transform.Profile -cne $ExpectedProfile -or $Transform.InputSHA256 -cne $CompiledHash){throw ('Unexpected protection profile/input: '+$ExpectedProfile)}
    $staged=Get-ContainedFile $moduleRoot $Transform.Path
    $receiptPath=Get-ContainedFile $moduleRoot $Transform.ReceiptPath
    $stagedHash=Add-Input $staged $Transform.OutputSHA256
    $receiptHash=Add-Input $receiptPath $Transform.ReceiptSHA256
    $receipt=Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8|ConvertFrom-Json
    if($receipt.Schema -cne 'HVX.OwnedAlgorithmProtection/1' -or $receipt.Profile -cne $ExpectedProfile -or
       $receipt.InputSHA256 -cne $CompiledHash -or $receipt.OutputSHA256 -cne $stagedHash -or
       $receipt.ScopeSHA256 -cne $scopeHash -or $receipt.Protection -notmatch 'names retained' -or
       $receipt.ExistingMetadataPreserved -isnot [bool] -or -not $receipt.ExistingMetadataPreserved -or
       $receipt.InputUnchanged -isnot [bool] -or -not $receipt.InputUnchanged -or
       $receipt.HardwareAccessed -isnot [bool] -or $receipt.HardwareAccessed -or
       $receipt.ProductEntryExecuted -isnot [bool] -or $receipt.ProductEntryExecuted){throw ('Invalid protection receipt: '+$ExpectedProfile)}
    # Retained files are resolved relative to the receipt, not stale absolute paths
    # inside it. They are hash-read only; no product or protection tool is run.
    $receiptFolder=Split-Path -Parent $receiptPath
    $filename=[IO.Path]::GetFileName($staged)
    $original=Get-ContainedFile $receiptFolder ('input/'+$filename)
    $protected=Get-ContainedFile $receiptFolder ('protected/'+$filename)
    $null=Add-Input $original $CompiledHash
    $null=Add-Input $protected $stagedHash
    return [ordered]@{Path=$staged;Profile=$ExpectedProfile;CompiledInput=$original;CompiledSHA256=$CompiledHash;ProtectedOutput=$protected;ProtectedSHA256=$stagedHash;StagedSHA256=$stagedHash;ReceiptPath=$receiptPath;ReceiptSHA256=$receiptHash}
}
$null=Add-Input $PSCommandPath
$boundedHelper=Get-ContainedFile $root 'tools/Test-Internal12-Calibration.ps1'
$null=Add-Input $boundedHelper
$tokens=$null;$parseErrors=$null
$helperAst=[Management.Automation.Language.Parser]::ParseFile($boundedHelper,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Bounded runner parse failure.'}
foreach($functionName in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $functionNode=@($helperAst.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $functionName)
 if($functionNode.Count -ne 1){throw 'Bounded runner helper missing.'}
 . ([ScriptBlock]::Create($functionNode[0].Extent.Text))
}
$selectedRoot=if($PSCmdlet.ParameterSetName -eq 'Protected'){$ProtectedBuildRoot}else{$ModuleBuildRoot}
$moduleRoot=[IO.Path]::GetFullPath($selectedRoot).TrimEnd('\')
if(-not $moduleRoot.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an isolated project build directory, not an installed product.'}
$protectionChains=New-Object 'System.Collections.Generic.List[object]'
if($PSCmdlet.ParameterSetName -eq 'Protected'){
    $moduleManifestPath=Get-ContainedFile $moduleRoot 'module-manifest.json'
    $buildManifestPath=Get-ContainedFile $moduleRoot 'build-manifest.json'
    $null=Add-Input $moduleManifestPath
    $null=Add-Input $buildManifestPath
    $modules=Get-Content -LiteralPath $moduleManifestPath -Raw -Encoding UTF8|ConvertFrom-Json
    $build=Get-Content -LiteralPath $buildManifestPath -Raw -Encoding UTF8|ConvertFrom-Json
    $manifestPath=Get-ContainedFile $moduleRoot $modules.SourceUiRuntime.ManifestPath
    $null=Add-Input $manifestPath $modules.SourceUiRuntime.ManifestSHA256
    $manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8|ConvertFrom-Json
    $helper=Get-ContainedFile $root 'tools/ReleaseMaintenanceEvidence.ps1'
    $null=Add-Input $helper
    $scope=Get-ContainedFile $root 'tools/owned-algorithm-protection.json'
    $scopeHash=Add-Input $scope
    # Verify the host and seven guarded UI metadata chains before calling the
    # shared pure policy. Only hashes are read; none of these entries is run.
    $protectionChains.Add((Assert-ProtectionChain $build.OwnedHostAlgorithmProtection 'HostPure' $build.HostCompilation.OutputSHA256))
    foreach($compiled in @($manifest.Modules)){
        $relative='modules/'+[string]$compiled.File
        $staged=@($modules.Modules|Where-Object{([string]$_.Path).Replace('\','/') -ceq $relative})
        if($staged.Count -ne 1){throw ('Expected one staged UI entry: '+$relative)}
        $null=Add-Input (Get-ContainedFile $moduleRoot $relative) $staged[0].Sha256
        $transform=@($modules.OwnedAlgorithmProtection|Where-Object{([string]$_.Path).Replace('\','/') -ceq $relative})
        if($transform.Count -eq 1){
            $profile=if($compiled.Module -ceq 'DataAnalysis'){'PdReview'}else{[string]$compiled.Module}
            $protectionChains.Add((Assert-ProtectionChain $transform[0] $profile $compiled.SHA256))
        }elseif($transform.Count -ne 0 -or $staged[0].Sha256 -cne $compiled.SHA256){throw ('Invalid UI compile-to-stage chain: '+$relative)}
    }
    . $helper
    Assert-ReleaseMaintenanceCompilation $build $modules $manifest
    $module=@($manifest.Modules|Where-Object{$_.Module -ceq 'PressureTest'})
    if($module.Count -ne 1){throw 'Expected exactly one PressureTest UI module.'}
    $pressurePath='modules/'+[string]$module[0].File
    $dll=Get-ContainedFile $moduleRoot $pressurePath
    $pressureChain=@($protectionChains|Where-Object{$_.Path -ceq $dll -and $_.Profile -ceq 'PressureTest'})
    if($pressureChain.Count -ne 1){throw 'Protected PressureTest chain is missing or ambiguous.'}
    # The test resolver is limited to this directory. Bind every DLL it could
    # resolve here and reject unlisted additions; do not load any of them here.
    foreach($dependency in @(Get-ChildItem -LiteralPath (Split-Path -Parent $dll) -Filter '*.dll' -File)){
        $relative='modules/PressureTest/'+$dependency.Name
        $entry=@($modules.Modules|Where-Object{([string]$_.Path).Replace('\','/') -ceq $relative})
        if($entry.Count -ne 1){throw ('Unlisted UI resolver dependency: '+$relative)}
        $null=Add-Input (Get-ContainedFile $moduleRoot $relative) $entry[0].Sha256
        $transform=@($modules.OwnedAlgorithmProtection|Where-Object{([string]$_.Path).Replace('\','/') -ceq $relative})
        if($transform.Count -eq 1 -and $relative -cne $pressurePath){
            $compiledDependency=@($manifest.Modules|Where-Object{$_.Assembly+'.dll' -ceq $dependency.Name})
            if($compiledDependency.Count -ne 1){throw 'Missing dependency compile evidence.'}
            $protectionChains.Add((Assert-ProtectionChain $transform[0] 'PdReview' $compiledDependency[0].SHA256))
        }elseif($transform.Count -gt 1){throw 'Ambiguous dependency protection.'}
    }
}else{
    $manifestPath=Get-ContainedFile $moduleRoot 'ui-module-build.json'
    $null=Add-Input $manifestPath
    $manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8|ConvertFrom-Json
    $module=@($manifest.Modules|Where-Object{$_.Module -eq 'PressureTest'})
    if($module.Count -ne 1){throw 'Expected exactly one PressureTest UI module.'}
    $dll=Get-ContainedFile $moduleRoot $module[0].File
    $null=Add-Input $dll $module[0].SHA256
}
if([IO.Path]::GetExtension($dll) -ne '.dll'){throw 'Invalid UI module path.'}
$qa=Join-Path $root ('build\PressureEmergencyUiReceiptQA-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
New-Item -ItemType Directory -Path $qa|Out-Null
$test=Get-ContainedFile $root 'tests/PressureEmergencyUiReceiptSmoke.cs'
$compiler=Get-ContainedFile $root 'tools/compiler/csc.exe'
$null=Add-Input $test
$null=Add-Input $compiler
foreach($compilerFile in @(Get-ChildItem -LiteralPath (Split-Path -Parent $compiler) -File|Where-Object{$_.Extension -eq '.dll' -or $_.Name -in @('csc.exe.config','csc.rsp')})){$null=Add-Input (Get-ContainedFile $root ('tools/compiler/'+$compilerFile.Name))}
$exe=Join-Path $qa 'PressureEmergencyUiReceiptSmoke.exe'
# Only this test source has Main. No host/worker entry point is compiled or run.
$output=@(& $compiler /nologo /langversion:7.3 /target:exe /platform:x64 /deterministic+ /main:PressureEmergencyUiReceiptSmoke /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE
$output|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($code -ne 0){throw ('Detached UI test compilation failed: '+($output -join "`n"))}
$null=Add-Input $exe
$p=Invoke-RegressionExecutable -Executable $exe -Arguments @($dll) -LogPrefix (Join-Path $qa 'receipt') -TimeoutMilliseconds 30000
$stdout=[IO.File]::ReadAllText($p.StdoutPath)
$stderr=[IO.File]::ReadAllText($p.StderrPath)
$mismatches=@($inputEvidence|Where-Object{(Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -cne $_.SHA256})
$expectedOutput='^PRESSURE_EMERGENCY_UI_RECEIPT_PASS=172;UI_SHA256='+[regex]::Escape($inputIndex[$dll])+';HardwareAccessed=False;SDKCalled=False;WorkersStarted=0;ProductEntryStarted=False;BackendConfigured=False;DetachedControl=True;UserProjectsWritten=False\s*$'
$outputVerified=$stdout -cmatch $expectedOutput -and [string]::IsNullOrWhiteSpace($stderr)
$displayResults=New-Object 'System.Collections.Generic.List[object]'
if($IncludeDisplayChecks -and $p.ExitCode -eq 0 -and $outputVerified){
 foreach($case in @(@{Name='PressureLatchedEmergencyDisplaySmoke';Expected='^PASS 13 \| HardwareAccessed=False; UserProjectsWritten=False\s*$';Checks=13},@{Name='PressureContinuousDisplayLockSmoke';Expected='^PRESSURE_CONTINUOUS_DISPLAY_PASS=18;ProductStarted=False;SDKCalled=False;ActualWorkerStarted=False;HardwareAccessed=False;BackendConfigured=False;PhysicalAcceptance=False\s*$';Checks=18})){
  $caseSource=Get-ContainedFile $root ('tests/'+$case.Name+'.cs');$null=Add-Input $caseSource
  $caseExe=Join-Path $qa ($case.Name+'.exe')
  $compile=@(& $compiler /nologo /langversion:7.3 /target:exe /platform:x64 /deterministic+ ('/main:'+$case.Name) /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:'+$caseExe) $caseSource 2>&1|ForEach-Object{$_.ToString()})
  $caseCode=$LASTEXITCODE;$compile|Set-Content -LiteralPath (Join-Path $qa ($case.Name+'.compile.txt')) -Encoding UTF8
  if($caseCode -ne 0){throw ('Actual UI display fixture compile failed: '+($compile -join "`n"))};$null=Add-Input $caseExe
  $caseProcess=Invoke-RegressionExecutable -Executable $caseExe -Arguments @($dll) -LogPrefix (Join-Path $qa $case.Name) -TimeoutMilliseconds 60000
  $caseOut=[IO.File]::ReadAllText($caseProcess.StdoutPath);$caseErr=[IO.File]::ReadAllText($caseProcess.StderrPath)
  $casePass=$caseProcess.ExitCode -eq 0 -and $caseOut -cmatch $case.Expected -and [string]::IsNullOrWhiteSpace($caseErr)
  $displayResults.Add([ordered]@{Name=$case.Name;ExitCode=$caseProcess.ExitCode;Checks=if($casePass){$case.Checks}else{0};OutputVerified=$casePass;ExactUI=$dll;UI_SHA256=$inputIndex[$dll];Output=$caseOut;Error=$caseErr;Scope='Actual managed controls and UI consumers; synthetic DI/lifecycle, no backend or device.'})
  $caseOut|Write-Output
 }
}
$mismatches=New-Object 'System.Collections.Generic.List[object]'
foreach($checkedInput in $inputEvidence){
 if((Get-FileHash -LiteralPath $checkedInput.Path -Algorithm SHA256).Hash -cne $checkedInput.SHA256){$mismatches.Add($checkedInput)}
}
$verification=[ordered]@{Schema='HVX.PressureEmergencyUiReceipt.SourceQA/1';Mode=$PSCmdlet.ParameterSetName;BuildRoot=$moduleRoot;ExitCode=$p.ExitCode;Checks=if($outputVerified){172}else{0};DisplayChecks=$displayResults.ToArray();Inputs=$inputEvidence.ToArray();InputMismatchCount=$mismatches.Count;OutputVerified=$outputVerified;ProtectionChains=$protectionChains.ToArray();ExactStagedProtectedDllTested=($PSCmdlet.ParameterSetName -eq 'Protected');Output=$stdout;Error=$stderr;HardwareAccessed=$false;SDKCalled=$false;WorkersStarted=0;ProductEntryStarted=$false;BackendConfigured=$false;DetachedControl=$true;UserProjectsWritten=$false;PhysicalAcceptance=$false}
$verification|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
Write-Output $stdout
Write-Output ('PRESSURE_EMERGENCY_UI_RECEIPT_QA='+$qa)
if($p.ExitCode -ne 0){throw ('Detached UI test failed: '+$stderr)}
if($mismatches.Count -ne 0 -or -not $outputVerified){throw 'Detached UI output or current input hash verification failed.'}
if($IncludeDisplayChecks -and ($displayResults.Count -ne 2 -or @($displayResults|Where-Object{-not $_.OutputVerified}).Count -ne 0)){throw 'Actual UI display checks failed.'}
