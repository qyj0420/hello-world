param([Parameter(Mandatory=$true)][string]$BuildRoot)
# Executes only the individually reviewed offline test harnesses. Never invokes
# product Main, an installer or a device CLI. Read test sources before use.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$build=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
if(-not $build.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an isolated build directory.'}
$publisher=Join-Path $PSScriptRoot 'Publish-V30BatchValidation.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($publisher,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Batch publisher parse failed.'}
function Read-Names([string]$Name){
 $assign=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.AssignmentStatementAst]},$false)|Where-Object {$_.Left.Extent.Text -ceq ('$'+$Name)})
 if($assign.Count -ne 1){throw ('Missing unique literal test list: '+$Name)}
 $literal=$assign[0].Right.Extent.Text
 if($literal -cnotmatch '^@\(''\w+''(?:,''\w+'')*\)$'){throw 'Only literal test identifiers may be read from publisher.'}
 return @([regex]::Matches($literal,"'(?<name>[A-Za-z0-9]+)'" )|ForEach-Object {$_.Groups['name'].Value})
}
$ui=Read-Names 'requiredUi';$hostTests=Read-Names 'requiredHost'
foreach($name in @($ui)+@($hostTests)){if(-not(Test-Path -LiteralPath (Join-Path $root ('tests/'+$name+'.cs')) -PathType Leaf)){throw ('Test source missing: '+$name)}}
$freeze=Join-Path $build 'qa/v30-batch-freeze.json'
& $publisher -Mode Capture -BuildRoot $build -FreezeEvidence $freeze
& (Join-Path $PSScriptRoot 'Test-DeploymentPayload.ps1') -BuildRoot $build -EvidencePath (Join-Path $build 'qa/deployment.json')
# Bounded groups keep each receipt and failure focused; tests remain bound to
# exactly the same frozen bytes and cannot borrow old package pass records.
for($offset=0;$offset -lt $ui.Count;$offset+=8){
 $last=[Math]::Min($offset+7,$ui.Count-1)
 & (Join-Path $PSScriptRoot 'Test-UiRepairFocused.ps1') -BuildRoot $build -Names @($ui[$offset..$last])
}
& (Join-Path $PSScriptRoot 'Test-DeviceConnectNoPassword.ps1') -BuildRoot $build -Names $hostTests
$uiProofs=@(Get-ChildItem -LiteralPath (Join-Path $build 'qa') -Recurse -File -Filter 'verification-ui-repair.json'|ForEach-Object FullName)
$hostProofs=@(Get-ChildItem -LiteralPath (Join-Path $build 'qa') -Directory -Filter 'password-free-source-*'|ForEach-Object {Join-Path $_.FullName 'verification.json'})
& $publisher -Mode Validate -BuildRoot $build -FreezeEvidence $freeze -DeploymentProof (Join-Path $build 'qa/deployment.json') -UiProofs $uiProofs -HostProofs $hostProofs
Write-Output 'BATCH_DELIVERY_OFFLINE_CHECKS_COMPLETE; CONFIDENTIALITY_REVIEW_AND_PUBLICATION_STILL_REQUIRED'
