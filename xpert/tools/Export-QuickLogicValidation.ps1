param(
 [ValidateSet('Validate','Export')][string]$Mode='Validate',
 [Parameter(Mandatory=$true)][string]$BuildRoot,
 [Parameter(Mandatory=$true)][string[]]$Proofs,
 [Parameter(Mandatory=$true)][string[]]$RequiredTests,
 [Parameter(Mandatory=$true)][string]$DeploymentProof,
 [Parameter(Mandatory=$true)][string]$EmbeddedProof,
 [string]$OutputName='HV-DAC_Xpert_QuickLogic_1.0.30.9_20261009_01',
 [string[]]$FixedItems=@(),
 [string[]]$PendingItems=@()
)
# The user-authorized quick logic EXE lane is separate from the existing
# V30Batch and formal publisher gates. It does not produce an installer,
# execute the product or access hardware. Receipts describe scoped evidence.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$build=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
$snapshots=@{}
function Assert-QuickPath([string]$Path,[string]$Boundary) {
 $full=[IO.Path]::GetFullPath($Path);$limit=[IO.Path]::GetFullPath($Boundary).TrimEnd('\')
 if($full -ine $limit -and -not $full.StartsWith($limit+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Path escapes its permitted root.'}
 for($walk=$full;$walk.Length -ge $limit.Length;$walk=[IO.Path]::GetDirectoryName($walk)){
  if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw ('Linked path is prohibited: '+$walk)}
  if($walk -ieq $limit){break}
 }
 return $full
}
function Get-QuickFile([string]$Base,[string]$Relative) {
 if([String]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')){throw 'Invalid relative file name.'}
 $file=Assert-QuickPath (Join-Path $Base $Relative) $Base
 if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw ('Required file missing: '+$Relative)}
 return $file
}
function Get-QuickTree([string]$Base,[string]$Relative) {
 $directory=Assert-QuickPath (Join-Path $Base $Relative) $Base
 if(-not(Test-Path -LiteralPath $directory -PathType Container)){throw ('Required directory missing: '+$Relative)}
 $pending=New-Object 'System.Collections.Generic.Stack[string]';$pending.Push($directory)
 while($pending.Count){foreach($entry in Get-ChildItem -LiteralPath $pending.Pop() -Force){
  $null=Assert-QuickPath $entry.FullName $Base
  if($entry.PSIsContainer){$pending.Push($entry.FullName)}else{$entry.FullName}
 }}
}
function Get-QuickInventory([string]$Base,[string[]]$Trees,[string[]]$Files) {
 $paths=@($Files|ForEach-Object {Get-QuickFile $Base $_})
 foreach($tree in $Trees){$paths+=@(Get-QuickTree $Base $tree)}
 @($paths|Sort-Object -Unique|ForEach-Object {[ordered]@{Path=$_.Substring($Base.TrimEnd('\').Length+1).Replace('\','/');SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})
}
function Convert-QuickInventory($Entries) {
 $rows=@($Entries|ForEach-Object {
  $relative=([string]$_.Path).Replace('\','/')
  if([String]::IsNullOrWhiteSpace($relative) -or $_.SHA256 -notmatch '^[A-Fa-f0-9]{64}$'){throw 'Malformed inventory row.'}
  [ordered]@{Path=$relative;SHA256=([string]$_.SHA256).ToUpperInvariant()}
 }|Sort-Object { $_.Path })
 if(@($rows|Group-Object { $_.Path.ToUpperInvariant() }|Where-Object Count -ne 1).Count){throw 'Duplicate inventory path.'}
 ConvertTo-Json -InputObject $rows -Depth 4 -Compress
}
function Assert-QuickInventory($Actual,$Expected,[string]$Description) {
 if((Convert-QuickInventory $Actual) -cne (Convert-QuickInventory $Expected)){throw ($Description+' changed or incomplete.')}
}
function Assert-QuickHash([string]$Base,[string]$Relative,[string]$Expected) {
 $file=Get-QuickFile $Base $Relative
 if($Expected -notmatch '^[A-Fa-f0-9]{64}$' -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $Expected){throw ('File changed: '+$Relative)}
 return $file
}
function Read-QuickJson([string]$Path,[bool]$Proof=$false) {
 $file=if($Proof){Assert-QuickPath ([IO.Path]::GetFullPath($Path)) (Join-Path $build 'qa')}else{Assert-QuickPath $Path $build}
 if(-not(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -gt 32MB){throw 'Evidence is missing or exceeds the size limit.'}
 $script:snapshots[$file]=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
 Get-Content -LiteralPath $file -Raw -Encoding UTF8|ConvertFrom-Json
}
function Assert-QuickBoolean($Value,[bool]$Expected,[string]$Description) {
 if($Value -isnot [bool] -or $Value -ne $Expected){throw ('Invalid '+$Description)}
}
function Assert-QuickEvidenceStable {
 foreach($file in $script:snapshots.Keys){if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne $script:snapshots[$file]){throw 'Evidence changed during export.'}}
}

$null=Assert-QuickPath $build (Join-Path $project 'build')
if($build -ieq (Join-Path $project 'build')){throw 'Select one dedicated candidate build.'}
if(-not $RequiredTests.Count -or @($RequiredTests|Where-Object {$_ -notmatch '^[A-Za-z0-9]+$'}).Count -or @($RequiredTests|Group-Object|Where-Object Count -ne 1).Count){throw 'An explicit, unique list of scoped tests is required.'}
$bm=Read-QuickJson (Get-QuickFile $build 'build-manifest.json')
$mm=Read-QuickJson (Get-QuickFile $build 'module-manifest.json')
if($bm.Schema -cne 'XIONGXIN.UnifiedDesktop.Build.v1' -or $mm.Schema -cne 'XIONGXIN.UnifiedDesktop.Modules.v2' -or $bm.ConfigurationProfile -cne 'Validation-V30-Batch-20261008' -or $bm.FileVersion -cne '1.0.30.9'){throw 'Unexpected candidate profile or version.'}
Assert-QuickBoolean $bm.V30BatchValidationProfile $true 'V30Batch profile'
Assert-QuickBoolean $bm.ProtectedRelease $true 'protected candidate build'
Assert-QuickBoolean $bm.PressureStandalone $false 'main software scope'
Assert-QuickBoolean $bm.HybridArchitecture.Included $true 'hybrid runtime'
$exe=Assert-QuickHash $build 'Xiongxin.OWTS.Desktop.exe' $bm.ExecutableSha256
if([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -cne '1.0.30.9' -or $bm.EmbeddedModules.ResourceName -cne 'HVX.Modules.Index.v1'){throw 'Wrong executable identity or missing embedded payload.'}
$builtUtc=[datetime]::Parse($bm.BuiltUtc).ToUniversalTime()
$runtime=Get-QuickInventory $build @('modules') @('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config')
Assert-QuickInventory @($runtime|Where-Object {$_.Path.StartsWith('modules/')}) $mm.Modules 'Module manifest'
$payload=Get-QuickInventory $build @('assets','model-payload') @('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config','module-manifest.json')
$source=Get-QuickInventory $project @('src') @()
Assert-QuickInventory @($source|Where-Object {$_.Path -like '*.cs'}) $bm.Sources 'Compiled shell sources'

$deployment=Read-QuickJson $DeploymentProof $true
if($deployment.Status -cne 'PASS' -or [IO.Path]::GetFullPath([string]$deployment.BuildRoot).TrimEnd('\') -ine $build -or [datetime]::Parse($deployment.CheckedUtc).ToUniversalTime() -lt $builtUtc){throw 'Deployment evidence is stale or belongs to a different build.'}
Assert-QuickBoolean $deployment.HardwareAccessed $false 'deployment hardware boundary'
$deploymentMap=@{}
foreach($entry in $deployment.Files){$relative=([string]$entry.Path).Replace('\','/');$null=Assert-QuickHash $build $relative $entry.SHA256;$deploymentMap[$relative]=[string]$entry.SHA256}
foreach($entry in @($runtime|Where-Object {$_.Path.StartsWith('modules/') -or $_.Path -ceq 'Xiongxin.OWTS.Desktop.exe'})+@($payload|Where-Object {$_.Path.StartsWith('model-payload/')})){
 if(-not $deploymentMap.ContainsKey($entry.Path) -or $deploymentMap[$entry.Path] -ine $entry.SHA256){throw ('Deployment evidence omits '+$entry.Path)}
}
$embedded=Read-QuickJson $EmbeddedProof $true
if($embedded.Schema -cne 'HVX.NativeModelStaticReview/1' -or $embedded.Status -cne 'PASS_SCOPED_STATIC_CHECKS' -or [IO.Path]::GetFullPath([string]$embedded.BuildRoot).TrimEnd('\') -ine $build -or $embedded.ExecutableSHA256 -cne $bm.ExecutableSha256 -or [datetime]::Parse($embedded.Utc).ToUniversalTime() -lt $builtUtc){throw 'Final PE embedded evidence is stale or belongs to a different build.'}
foreach($flag in @('HardwareAccessed','ProductExecuted','ActualUserCacheWritten','DirectReadableProprietarySourceArtifactFound')){Assert-QuickBoolean $embedded.$flag $false $flag}
Assert-QuickBoolean $embedded.CachePayloadComparedFromFinalPe $true 'final executable payload comparison'
if($embedded.EmbeddedIndexSHA256 -cne $bm.EmbeddedModules.BundleHash -or $embedded.CacheEntries -ne $bm.EmbeddedModules.FileCount -or $embedded.UniqueCompressedResources -ne $bm.EmbeddedModules.UniqueResources -or $embedded.ExpandedBytes -ne $bm.EmbeddedModules.ExpandedBytes){throw 'Final embedded payload counts differ from the build.'}
Assert-QuickInventory @($runtime|Where-Object {$_.Path.StartsWith('modules/')}|ForEach-Object {[ordered]@{Path=$_.Path.Substring(8);SHA256=$_.SHA256}}) $embedded.CacheInventory 'Final executable cache bytes'
Assert-QuickInventory @($payload|Where-Object {$_.Path.StartsWith('model-payload/')}) $embedded.Models 'Model payload bytes'

$tested=@{};$proofSummaries=New-Object 'System.Collections.Generic.List[object]'
foreach($path in $Proofs){
 $proof=Read-QuickJson $path $true;$file=[IO.Path]::GetFullPath($path)
 if($proof.Schema -cnotin @('HVX.UiRepair.Focused/1','HVX.PasswordFree.Source/2') -or [IO.Path]::GetFullPath([string]$proof.BuildRoot).TrimEnd('\') -ine $build){throw 'A test proof belongs to a different build or unsupported schema.'}
 Assert-QuickBoolean $proof.HardwareAccessed $false 'test hardware boundary'
 Assert-QuickBoolean $proof.RuntimeUnchanged $true 'unchanged tested runtime'
 if($proof.Schema -ceq 'HVX.UiRepair.Focused/1'){Assert-QuickBoolean $proof.UserProjectsWritten $false 'isolated test projects'}else{
  Assert-QuickBoolean $proof.UserSettingsWritten $false 'isolated host settings'
  Assert-QuickBoolean $proof.SourceUnchanged $true 'unchanged tested host sources'
  Assert-QuickInventory $bm.Sources $proof.SourceInventory 'Tested host source inventory'
 }
 if([datetime]::Parse($proof.StartedUtc).ToUniversalTime() -lt $builtUtc -or [datetime]::Parse($proof.CompletedUtc).ToUniversalTime() -lt [datetime]::Parse($proof.StartedUtc).ToUniversalTime()){throw 'Tests must run after the final candidate build.'}
 Assert-QuickInventory $runtime $proof.Runtime 'Tested runtime inventory'
 if(-not @($proof.Tests).Count -or @($proof.Tests|Group-Object Name|Where-Object Count -ne 1).Count){throw 'Missing or duplicate tests in a proof.'}
 foreach($test in $proof.Tests){
  if($test.Name -notmatch '^[A-Za-z0-9]+$' -or $test.CompileExit -ne 0 -or $null -eq $test.ExitCode -or $test.ExitCode -ne 0){throw 'Failed or malformed scoped test.'}
  Assert-QuickBoolean $test.TimedOut $false 'test timeout'
  Assert-QuickBoolean $test.ProcessExitConfirmed $true 'test process completion'
  $testSource='tests/'+$test.Name+'.cs'
  if($test.Name -ceq 'PressureManualZeroHostProgressSmoke'){$testSource='tools/PressureManualZeroHostProgressSmoke.cs'}
  $null=Assert-QuickHash $project $testSource $test.SourceSHA256
  $null=Assert-QuickHash ([IO.Path]::GetDirectoryName($file)) ($test.Name+'.exe') $test.TestSHA256
  $tested[$test.Name]=$true
 }
 $proofSummaries.Add([ordered]@{SHA256=$snapshots[$file];Schema=$proof.Schema;Tests=@($proof.Tests.Name)})
}
foreach($name in $RequiredTests){if(-not $tested.ContainsKey($name)){throw ('Required scoped test missing: '+$name)}}
Assert-QuickEvidenceStable
if($Mode -ceq 'Validate'){Write-Output ('QUICK_LOGIC_READY; TESTS='+$tested.Count+'; FULL_REGRESSION=false; EXE='+$bm.ExecutableSha256);return}
if($OutputName -notmatch '^HV-DAC_Xpert_QuickLogic_1\.0\.30\.9_[0-9]{8}_[0-9]{2}$'){throw 'Use a distinct QuickLogic delivery name.'}
$deliverables=Assert-QuickPath (Join-Path $project 'deliverables') $project
$delivery=Assert-QuickPath (Join-Path $deliverables $OutputName) $deliverables
if(Test-Path -LiteralPath $delivery){throw 'Never overwrite an existing delivery.'}
$candidate=Join-Path $project ('build/quick-logic-delivery-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $candidate
foreach($entry in $payload){
 $sourceFile=Assert-QuickHash $build $entry.Path $entry.SHA256
 $target=Assert-QuickPath (Join-Path $candidate $entry.Path) $candidate
 $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
 Copy-Item -LiteralPath $sourceFile -Destination $target
 $null=Assert-QuickHash $candidate $entry.Path $entry.SHA256
}
$null=New-Item -ItemType Directory -Path (Join-Path $candidate 'licenses')
foreach($entry in @(@('modules/DataAnalysis/Task12/ONNX_RUNTIME_LICENSE.txt','ONNX_RUNTIME_LICENSE.txt'),@('modules/r71/LICENSE.txt','PYTHON_LICENSE.txt'),@('modules/r71/licenses/numpy-METADATA.txt','NUMPY_LICENSE_METADATA.txt'))){Copy-Item -LiteralPath (Get-QuickFile $build $entry[0]) -Destination (Join-Path $candidate ('licenses/'+$entry[1]))}
Copy-Item -LiteralPath (Get-QuickFile $project 'dependencies/Protection/ConfuserEx-LICENSE.txt') -Destination (Join-Path $candidate 'licenses/ConfuserEx-LICENSE.txt')
Assert-QuickInventory (Get-QuickInventory $build @('modules') @('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config')) $runtime 'Tested runtime after copy'
Assert-QuickInventory (Get-QuickInventory $build @('assets','model-payload') @('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config','module-manifest.json')) $payload 'Runtime payload after copy'
Assert-QuickEvidenceStable
$receipt=[ordered]@{Schema='HVX.QuickLogicValidationRelease/1';Version='1.0.30.9';GeneratedUtc=[DateTime]::UtcNow.ToString('o');ExecutableSHA256=$bm.ExecutableSha256;UserAuthorizedScope='Personal quick logic validation EXE; calibration, pressure operation and report loading; GUI follow-up handled separately';RequiredTests=@($RequiredTests);TestsObserved=@($tested.Keys|Sort-Object);Proofs=$proofSummaries.ToArray();DeploymentProofSHA256=$snapshots[[IO.Path]::GetFullPath($DeploymentProof)];EmbeddedProofSHA256=$snapshots[[IO.Path]::GetFullPath($EmbeddedProof)];FixedItems=@($FixedItems);PendingItems=@($PendingItems);RuntimePayload=$payload;HardwareAccessed=$false;ProductExecuted=$false;InstallerProduced=$false;FullRegression=$false;PhysicalAcceptance=$false;FullConfidentialityReview=$false;ProtectionTransformationVerified=$true;FinalEmbeddedPayloadVerified=$true;FormalPublishGateUnchanged=$true;ApplicationSignature=[string](Get-AuthenticodeSignature -LiteralPath $exe).Status;DriversInstalled=$false}
$receipt|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $candidate 'release-scoped.json') -Encoding UTF8
$usage=@('HV-DAC Xpert 快速逻辑修正版','请保留整个文件夹，双击 Xiongxin.OWTS.Desktop.exe 启动。','运行环境：Windows 64位，.NET Framework 4.8；实机仍使用已安装的设备驱动。','本包为本人测试的可运行程序，未生成安装包，也未执行设备操作。','本次只执行所改逻辑的定向离线检查，实机采集和加压效果由现场测试确认。','本次修正：')+@($FixedItems|ForEach-Object {'- '+$_})+@('仍待处理或验证：')+@($PendingItems|ForEach-Object {'- '+$_})
[IO.File]::WriteAllLines((Join-Path $candidate '使用说明.txt'),$usage,(New-Object Text.UTF8Encoding($false)))
$sums=@(Get-ChildItem -LiteralPath $candidate -Recurse -File -Force|Sort-Object FullName|ForEach-Object {(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash+'  '+$_.FullName.Substring($candidate.Length+1).Replace('\','/')})
[IO.File]::WriteAllLines((Join-Path $candidate 'SHA256SUMS.txt'),$sums,(New-Object Text.UTF8Encoding($false)))
$null=New-Item -ItemType Directory -Path $deliverables -Force
[IO.Directory]::Move($candidate,$delivery)
Write-Output ('QUICK_LOGIC_PAYLOAD='+$delivery)
Write-Output ('EXECUTABLE_SHA256='+$bm.ExecutableSha256)
