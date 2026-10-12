param([string]$FieldProjectPath='')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-legacy-calibration-candidate-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')) {
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Bounded runner helper missing.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing;New-Item -ItemType Directory -Path $qa|Out-Null
$sources=@('src/LiveCalibrationRecord.cs','src/LegacyCalibrationBinding.cs','src/PressureCalibrationSelection.cs',
 'src/ProjectMeasurementMode.cs','src/CableJointStore.cs','src/PdLinearHighPass.cs','src/PdLinearLowPass.cs','tests/LegacyCalibrationCandidateSelectionSmoke.cs')
$paths=@($sources)+@($PSCommandPath,$helper,'tools/compiler/csc.exe')
$inputs=@($paths|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=[IO.Path]::GetFullPath($_);SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$arguments=@((Join-Path $qa 'synthetic'))
$fieldInputs=@()
if($FieldProjectPath) {
 $field=[IO.Path]::GetFullPath($FieldProjectPath);Assert-RegressionUnlinkedPath $field
 $fieldInputs=@(@('ITEM.in','CABLE.in','ALL_5nC.cal')|ForEach-Object{$path=Join-Path $field $_;Assert-RegressionUnlinkedPath $path;[ordered]@{Path=$path;SHA256=(Get-FileHash -LiteralPath $path).Hash}})
 $arguments+= $field
}
$exe=Join-Path $qa 'LegacyCalibrationCandidateFakeOnly.exe'
$compiled=@(& (Join-Path $PSScriptRoot 'compiler/csc.exe') /nologo /target:exe /platform:x64 /langversion:7.3 /r:System.dll /r:System.Core.dll /r:System.Xml.dll ('/out:'+$exe) @sources 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments $arguments -LogPrefix (Join-Path $qa 'selection') -TimeoutMilliseconds 60000
$changed=@(@($inputs)+@($fieldInputs)|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count
$proof=Join-Path $qa 'proof.json'
[ordered]@{Schema='HVX.LegacyCalibration.CandidateSelection.Offline/1';UTC=[DateTime]::UtcNow.ToString('o');
 ProductStarted=$false;WorkerStarted=$false;SDKCalled=$false;HardwareAccessed=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;FullRegression=$false;
 SourceAndFieldInputsUnchanged=($changed -eq 0);InputMismatchCount=$changed;Inputs=$inputs;FieldReadOnlyInputs=$fieldInputs;Result=$run;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash}|
 ConvertTo-Json -Depth 10|Set-Content -LiteralPath $proof -Encoding UTF8
$run.Output|Write-Output;Write-Output ('LEGACY_CALIBRATION_CANDIDATE_PROOF='+$proof)
if($run.ExitCode -ne 0 -or $changed -ne 0){throw 'Legacy calibration candidate regression failed.'}
