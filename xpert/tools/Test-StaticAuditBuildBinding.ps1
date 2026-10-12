param([Parameter(Mandatory=$true)][string]$BuildRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$build=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
if(-not $build.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an isolated project build, not an installed product.'}
$inputs=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$checks=0
function Check([bool]$ok,[string]$name){$script:checks++;if(-not $ok){throw ('Build binding failed: '+$name)}}
function Inside([string]$base,[string]$relative){
 if([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative.Replace('/','\').Split('\') -contains '..'){throw 'Invalid evidence relative path.'}
 $path=[IO.Path]::GetFullPath((Join-Path $base $relative))
 if(-not $path.StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence path escaped its owner.'}
 for($walk=$path;$walk.Length -ge $root.Length;$walk=[IO.Path]::GetDirectoryName($walk)){
  if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked evidence paths are not allowed.'}
 }
 return $path
}
function Bound([string]$path,[string]$expected){
 Check ($expected -match '^[A-Fa-f0-9]{64}$') 'valid expected file hash'
 Check ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ceq $expected) 'actual file matches its evidence'
 $null=$inputs.Add($path)
}
function Read-Json([string]$path){
 if((Get-Item -LiteralPath $path).Length -gt 16*1024*1024){throw 'Build evidence JSON exceeds its budget.'}
 $null=$inputs.Add($path)
 return Get-Content -LiteralPath $path -Raw -Encoding UTF8|ConvertFrom-Json
}
$hostPath=Inside $build 'build-manifest.json';$modulesPath=Inside $build 'module-manifest.json'
$auditHostManifest=Read-Json $hostPath;$modules=Read-Json $modulesPath
Check ($auditHostManifest.Schema -ceq 'XIONGXIN.UnifiedDesktop.Build.v1' -and $modules.Schema -ceq 'XIONGXIN.UnifiedDesktop.Modules.v2') 'supported build manifests'
$uiPath=Inside $build $modules.SourceUiRuntime.ManifestPath
Bound $uiPath $modules.SourceUiRuntime.ManifestSHA256
$ui=Read-Json $uiPath
$policy=Inside $root 'tools\ReleaseMaintenanceEvidence.ps1';$null=$inputs.Add($policy);. $policy
Assert-ReleaseMaintenanceCompilation $auditHostManifest $modules $ui
$checks++
Check ($ui.PSObject.Properties['BuildScriptSHA256'] -ne $null) 'UI builder records its actual script'
Bound (Inside $root 'tools\Build-UiModules.ps1') $ui.BuildScriptSHA256
$uiSourceBindings=0
foreach($compiledModule in @($ui.Modules)){
 Check ($compiledModule.PSObject.Properties['CompileInputs'] -ne $null -and $compiledModule.InputsUnchanged -eq $true) 'UI compile records stable input inventory'
 $sourceInputs=@($compiledModule.CompileInputs|Where-Object {$_.Kind -ceq 'Source'})
 Check ($sourceInputs.Count -gt 0 -and $sourceInputs.Count -le $compiledModule.SourceCount) 'UI source inventory is nonempty and consistent'
 foreach($sourceInput in $sourceInputs){
  Check ($sourceInput.Path -notmatch '^modules-src/(?:LocationABC|LocationAlgorithm)/') 'historical location copies are outside current UI compilation'
  Bound (Inside $root $sourceInput.Path) $sourceInput.SHA256
  $uiSourceBindings++
 }
 foreach($resourceInput in @($compiledModule.CompileInputs|Where-Object {$_.Kind -ceq 'Resource'})){
  $resourcePath=Inside $root $resourceInput.Path
  if(-not (Test-Path -LiteralPath $resourcePath -PathType Leaf)){
   # Stage-Modules deliberately moves this exact generated compile resource
   # into verification before moving the runtime tree. Keep checking its
   # original bytes; do not accept an arbitrary missing resource or a new pin.
   $verificationRoot=Split-Path -Parent $uiPath
   $originalResource=Join-Path (Join-Path (Split-Path -Parent $verificationRoot) 'modules') 'legacy-unit-pins.json'
   Check ([string]::Equals($resourcePath,$originalResource,[StringComparison]::OrdinalIgnoreCase)) 'only the known staged pin resource may have moved'
   $resourcePath=Inside $verificationRoot 'legacy-unit-pins.json'
  }
  Bound $resourcePath $resourceInput.SHA256
 }
}
$exe=Inside $build 'Xiongxin.OWTS.Desktop.exe';Bound $exe $auditHostManifest.ExecutableSha256
foreach($entry in @($auditHostManifest.Sources)){Bound (Inside $root $entry.Path) $entry.Sha256}
$paths=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach($entry in @($modules.Modules)){
 Check ($paths.Add([string]$entry.Path)) 'unique staged module path'
 $path=Inside $build $entry.Path
 Check ((Get-Item -LiteralPath $path).Length -eq $entry.Bytes) 'staged module byte count'
 Bound $path $entry.Sha256
}
$transforms=@($modules.OwnedAlgorithmProtection)+@($auditHostManifest.OwnedHostAlgorithmProtection)
foreach($transform in $transforms){
 $receiptPath=Inside $build $transform.ReceiptPath;Bound $receiptPath $transform.ReceiptSHA256
 $receipt=Read-Json $receiptPath
 Check ($receipt.InputSHA256 -ceq $transform.InputSHA256 -and $receipt.OutputSHA256 -ceq $transform.OutputSHA256) 'actual protection receipt hash chain'
 Check ($receipt.InputUnchanged -eq $true -and $receipt.ExistingMetadataPreserved -eq $true) 'protection preserves metadata and original input'
 Bound (Inside $build $transform.Path) $transform.OutputSHA256
}
$workerManifestPath=Inside $build $modules.SourcePressureRuntime.ManifestPath
Bound $workerManifestPath $modules.SourcePressureRuntime.ManifestSHA256
$worker=Read-Json $workerManifestPath
foreach($entry in @($worker.Files)){Bound (Inside (Join-Path $root 'modules-src\FactoryWorker') $entry.Path) $entry.SHA256}
Check ($worker.HardwareAccessed -eq $false -and $worker.PhysicalAcceptance -eq $false) 'worker compile evidence is not physical acceptance'
$resourceDirectories=@(Get-ChildItem -LiteralPath $build -Directory -Filter 'embedded-resources-*')
Check ($resourceDirectories.Count -eq 1) 'one owned embedded payload index'
$indexPath=Inside $resourceDirectories[0].FullName 'modules.index';Bound $indexPath $auditHostManifest.EmbeddedModules.BundleHash
$lines=@(Get-Content -LiteralPath $indexPath -Encoding UTF8)
Check ($lines.Count -eq @($modules.Modules).Count+1 -and $lines[0] -ceq 'XIONGXIN.Modules.v1') 'complete embedded index'
foreach($line in @($lines|Select-Object -Skip 1)){
 $row=$line.Split("`t");Check ($row.Count -eq 4) 'index row structure'
 $matches=@($modules.Modules|Where-Object{([string]$_.Path).Replace('\','/') -ceq ('modules/'+$row[0])})
 Check ($matches.Count -eq 1 -and [long]$matches[0].Bytes -eq [long]$row[1] -and $matches[0].Sha256 -ceq $row[2]) 'embedded index agrees with staged files'
}
$null=$inputs.Add($PSCommandPath)
$qa=Join-Path $build ('qa\static-audit-build-binding-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
New-Item -ItemType Directory -Path $qa|Out-Null
$proof=Join-Path $qa 'verification.json'
[ordered]@{Schema='HVX.StaticAudit.BuildBinding/1';CheckedUtc=[DateTime]::UtcNow.ToString('o');ExitCode=0;Checks=$checks;BuildRoot=$build;ExecutableSHA256=$auditHostManifest.ExecutableSha256;ExecutableFileVersion=$auditHostManifest.FileVersion;GuardedCompilationChainPassed=$true;HostCurrentSources=@($auditHostManifest.Sources).Count;WorkerCurrentSources=@($worker.Files).Count;StagedFiles=@($modules.Modules).Count;EmbeddedIndexCompared=$true;UiSourceInventoryProvidedByBuilder=$true;UiSourceBindings=$uiSourceBindings;ActualProductExecuted=$false;ProductEntryStarted=$false;SDKCalled=$false;WorkersStarted=0;InstallerBuilt=$false;HardwareAccessed=$false;PhysicalAcceptance=$false;Inputs=@($inputs|Sort-Object|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output ('STATIC_AUDIT_BUILD_BINDING_PASS='+$checks+';Files='+@($modules.Modules).Count+';HardwareAccessed=false;ProductEntryStarted=false;SDKCalled=false')
Write-Output ('STATIC_AUDIT_BUILD_BINDING_PROOF='+$proof)
