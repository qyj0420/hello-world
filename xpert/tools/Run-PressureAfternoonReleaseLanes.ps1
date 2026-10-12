param([Parameter(Mandatory=$true)][string]$BuildRoot,[ValidateSet('Capture','UiA','UiB','Host')][string]$Lane='Capture')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build=[IO.Path]::GetFullPath($BuildRoot)
$qa=Join-Path $build 'qa'
$publisher=Join-Path $root 'tools/Publish-V30BatchValidation.ps1'
$freeze=Join-Path $qa 'v30-batch-freeze-afternoon.json'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($publisher,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Publisher parse failure.'}
function Read-TestNames([string]$name){
 $nodes=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.AssignmentStatementAst]},$false)|Where-Object {$_.Left.Extent.Text -ceq ('$'+$name)})
 if($nodes.Count -ne 1 -or $nodes[0].Right.Extent.Text -cnotmatch '^@\(''\w+''(?:,''\w+'')*\)$'){throw 'Expected literal mandatory test list.'}
 @([regex]::Matches($nodes[0].Right.Extent.Text,"'(?<n>[A-Za-z0-9]+)'" )|ForEach-Object {$_.Groups['n'].Value})
}
if($Lane -eq 'Capture'){
 & $publisher -Mode Capture -BuildRoot $build -FreezeEvidence $freeze
 & (Join-Path $root 'tools/Test-DeploymentPayload.ps1') -BuildRoot $build -EvidencePath (Join-Path $qa 'deployment-afternoon.json')
 exit
}
if(-not(Test-Path -LiteralPath $freeze)){throw 'Capture first.'}
if($Lane -eq 'Host'){
 & (Join-Path $root 'tools/Test-DeviceConnectNoPassword.ps1') -BuildRoot $build -Names @(Read-TestNames 'requiredHost')
 exit
}
$ui=@(Read-TestNames 'requiredUi')+@('AnalysisBatchCompletionAuditSmoke','AnalysisBatchEvidenceSmoke','BrowserAverageGeometrySmoke','CalibrationInteractionRegressionSmoke','CalibrationMeasurementFeedbackSmoke','R27UnifiedWorkflowSmoke','PressurePreferencesSmoke','PressureFinishCurrentRoundSmoke','PressureStandaloneFailureSmoke','PressureSessionDebugSmoke','PressureActionFeedbackSmoke','PressureCompletedPlanSelectionSmoke','PressurePhasePlanResetSmoke','PressureOverRangeRetrySmoke','PressureWaveformMetricsSmoke','PublishedLocationCyclePhaseSmoke','StatisticsEmptyResultSmoke','PressureProgressSmoke')
$failure=New-Object 'System.Collections.Generic.List[string]'
for($offset=0;$offset -lt $ui.Count;$offset+=8){
 $group=[int]($offset/8)
 if(($group%2 -eq 0) -ne ($Lane -eq 'UiA')){continue}
 $last=[Math]::Min($offset+7,$ui.Count-1)
 try{& (Join-Path $root 'tools/Test-UiRepairFocused.ps1') -BuildRoot $build -Names @($ui[$offset..$last])}
 catch{$failure.Add($_.Exception.Message);Write-Output ('GROUP_FAILED='+$_.Exception.Message)}
}
if($failure.Count){throw ($failure -join ' | ')}
Write-Output ('LANE_COMPLETE='+$Lane)
