param(
 [Parameter(Mandatory=$true)][string]$BuildRoot,
 [Parameter(Mandatory=$true)][string]$FreezeEvidence
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$BuildRoot=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
$FreezeEvidence=[IO.Path]::GetFullPath($FreezeEvidence)
$bounded=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($bounded,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded path verification.'}
$node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq 'Assert-RegressionUnlinkedPath')
if($node.Count -ne 1){throw 'Missing bounded path verification.'}
. ([ScriptBlock]::Create($node[0].Extent.Text))
Assert-RegressionUnlinkedPath $BuildRoot
Assert-RegressionUnlinkedPath $FreezeEvidence
if(-not $BuildRoot.StartsWith((Join-Path $root 'build')+'\',[StringComparison]::OrdinalIgnoreCase) -or
 -not $FreezeEvidence.StartsWith((Join-Path $BuildRoot 'qa')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an isolated build and its own QA freeze.'}
$manifestPath=Join-Path $BuildRoot 'build-manifest.json'
$modulePath=Join-Path $BuildRoot 'module-manifest.json'
$manifest=Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath|ConvertFrom-Json
$module=Get-Content -Raw -Encoding UTF8 -LiteralPath $modulePath|ConvertFrom-Json
$freeze=Get-Content -Raw -Encoding UTF8 -LiteralPath $FreezeEvidence|ConvertFrom-Json
if(-not $manifest.MaintenancePasswordEnabled -or -not $module.MaintenancePasswordEnabled -or -not $manifest.ProtectedRelease){throw 'This test requires an actual protected maintenance-password build.'}
$frozenUtc=[datetime]::Parse($freeze.FrozenUtc).ToUniversalTime()
if([datetime]::UtcNow -lt $frozenUtc){throw 'Freeze timestamp is in the future.'}
$sourceInventory=@($manifest.Sources)
if($sourceInventory.Count -eq 0){throw 'Host source inventory is missing.'}
function CurrentSources {
 @($sourceInventory|ForEach-Object {
  $relative=([string]$_.Path).Replace('/','\')
  if([IO.Path]::IsPathRooted($relative) -or $relative.Split('\') -contains '..'){throw 'Invalid compiled-source path.'}
  $file=[IO.Path]::GetFullPath((Join-Path $root $relative))
  if(-not $file.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Compiled-source path escaped project.'}
  Assert-RegressionUnlinkedPath $file
  [ordered]@{Path=$_.Path;Sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash}
 })
}
function Canonical($items) { ConvertTo-Json -InputObject @($items|ForEach-Object{[ordered]@{Path=$_.Path;Sha256=([string]$_.Sha256).ToUpperInvariant()}}|Sort-Object {$_.Path}) -Depth 8 -Compress }
$sourcesBefore=@(CurrentSources)
if((Canonical $sourcesBefore) -cne (Canonical $sourceInventory)){throw 'Current host sources differ from this compiled build.'}
$bindingPaths=@($manifestPath,$modulePath,$FreezeEvidence,$PSCommandPath,(Join-Path $PSScriptRoot 'Test-UiRepairFocused.ps1'),$bounded,(Join-Path $root 'tests/MaintenanceAdvancedAccessPublishedSmoke.cs'))
function Bindings { @($bindingPaths|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}}) }
$bindingsBefore=@(Bindings)
$freezeHash=(Get-FileHash -LiteralPath $FreezeEvidence -Algorithm SHA256).Hash
$exeHash=(Get-FileHash -LiteralPath (Join-Path $BuildRoot 'Xiongxin.OWTS.Desktop.exe') -Algorithm SHA256).Hash
if($exeHash -cne $manifest.ExecutableSha256){throw 'Protected executable does not match build manifest.'}
$qaRoot=Join-Path $BuildRoot 'qa'
$previous=@(Get-ChildItem -LiteralPath $qaRoot -Directory -Filter 'ui-repair-*'|ForEach-Object FullName)
$runError=$null;$output=@()
try {$output=@(& (Join-Path $PSScriptRoot 'Test-UiRepairFocused.ps1') -BuildRoot $BuildRoot -Names @('MaintenanceAdvancedAccessPublishedSmoke'))}
catch {$runError=$_.Exception.Message}
$created=@(Get-ChildItem -LiteralPath $qaRoot -Directory -Filter 'ui-repair-*'|Where-Object {
 if($_.FullName -in $previous){return $false}
 $receipt=Join-Path $_.FullName 'verification-ui-repair.json'
 if(-not(Test-Path -LiteralPath $receipt -PathType Leaf)){return $false}
 $candidate=Get-Content -Raw -Encoding UTF8 -LiteralPath $receipt|ConvertFrom-Json
 return @($candidate.Tests|Where-Object Name -ceq 'MaintenanceAdvancedAccessPublishedSmoke').Count -eq 1
})
if($created.Count -ne 1){throw 'Cannot uniquely bind maintenance test receipt.'}
$qa=$created[0].FullName
$uiPath=Join-Path $qa 'verification-ui-repair.json'
if(-not(Test-Path -LiteralPath $uiPath -PathType Leaf)){throw ('Maintenance test produced no receipt: '+$qa)}
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath $uiPath|ConvertFrom-Json
$tests=@($ui.Tests)
if($tests.Count -ne 1 -or $tests[0].Name -cne 'MaintenanceAdvancedAccessPublishedSmoke'){throw 'Unexpected test closure.'}
$test=$tests[0]
$observed=([string]::Join("`n",@($test.Output)))
function ObservedBoolean([string]$name) {
 $matches=[regex]::Matches($observed,'(?<![A-Za-z])'+[regex]::Escape($name)+'=(True|False)(?![A-Za-z])')
 if($matches.Count -ne 1){return $null}
 return [bool]::Parse($matches[0].Groups[1].Value)
}
$match=[regex]::Match($observed,'MAINTENANCE_ADVANCED_ACCESS_PASS=([1-9][0-9]*)')
$checks=if($match.Success){[int]$match.Groups[1].Value}else{0}
$sourceUnchanged=((Canonical $sourcesBefore) -ceq (Canonical @(CurrentSources))) -and
 (($bindingsBefore|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Bindings)|ConvertTo-Json -Depth 5 -Compress))
$fields=@{}
foreach($name in @('CancellationDenied','InvalidPasswordDenied','DirectEntryGuarded','RoutineEntryPasswordFree','HardwareAccessed','ProductExecuted','UserSettingsWritten','CredentialMaterialIncluded','KnownCorrectCredentialEntered')){$fields[$name]=ObservedBoolean $name}
$valid=$null -eq $runError -and $test.CompileExit -eq 0 -and $test.ExitCode -eq 0 -and
 $test.TimedOut -eq $false -and $test.ProcessExitConfirmed -eq $true -and $checks -gt 0 -and
 $ui.RuntimeUnchanged -eq $true -and $sourceUnchanged -and
 [datetime]::Parse($ui.StartedUtc).ToUniversalTime() -ge $frozenUtc
foreach($name in @('CancellationDenied','InvalidPasswordDenied','DirectEntryGuarded','RoutineEntryPasswordFree')){$valid=$valid -and $fields[$name] -eq $true}
foreach($name in @('HardwareAccessed','ProductExecuted','UserSettingsWritten','CredentialMaterialIncluded')){$valid=$valid -and $fields[$name] -eq $false}
$proof=Join-Path $qa 'verification-maintenance-published.json'
[ordered]@{
 Schema='HVX.MaintenanceAccess.Published/1';Status=$(if($valid){'PASS'}else{'FAIL'});BuildRoot=$BuildRoot;
 ExecutableSHA256=$exeHash;FreezeSHA256=$freezeHash;StartedUtc=$ui.StartedUtc;CompletedUtc=[DateTime]::UtcNow.ToString('o');
 CompileExit=$test.CompileExit;ExitCode=$test.ExitCode;Checks=$checks;RuntimeUnchanged=$ui.RuntimeUnchanged;SourceUnchanged=$sourceUnchanged;
 Runtime=$ui.Runtime;SourceInventory=$sourceInventory;Tests=$tests;Bindings=$bindingsBefore;
 CancellationDenied=$fields.CancellationDenied;InvalidPasswordDenied=$fields.InvalidPasswordDenied;DirectEntryGuarded=$fields.DirectEntryGuarded;RoutineEntryPasswordFree=$fields.RoutineEntryPasswordFree;
 HardwareAccessed=$fields.HardwareAccessed;ProductExecuted=$fields.ProductExecuted;UserSettingsWritten=$fields.UserSettingsWritten;CredentialMaterialIncluded=$fields.CredentialMaterialIncluded;
 KnownCorrectCredentialEntered=$fields.KnownCorrectCredentialEntered;PhysicalAcceptance=$false;FullRegression=$false;UiProofSHA256=(Get-FileHash -LiteralPath $uiPath).Hash;RunError=$runError
}|ConvertTo-Json -Depth 16|Set-Content -LiteralPath $proof -Encoding UTF8
$output|Write-Output
Write-Output ('MAINTENANCE_PUBLISHED_PROOF='+$proof)
if(-not $valid){throw ('Maintenance published checks failed; preserved evidence '+$proof)}
