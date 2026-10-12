[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputRoot,[switch]$Audit)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($PSVersionTable.PSEdition-ne'Desktop'){throw 'Use Windows PowerShell 5.1 for the target CLR.'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$qa=[IO.Path]::GetFullPath($OutputRoot)
if(-not$qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an owned project build subdirectory.'}
if(Test-Path -LiteralPath $qa){throw 'Use a new output directory.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Runner helper could not be parsed.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')) {
 $node=@($ast.FindAll({param($n)$n-is[Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count-ne1){throw 'Runner helper missing.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$relative=@('src\ProjectMeasurementMode.cs','src\CableJointStore.cs','src\LiveCalibrationRecord.cs','src\PdLinearHighPass.cs','src\PdLinearLowPass.cs',
 'modules-src\ProjectCreation\src\ProjectCreationModels.cs','modules-src\ProjectCreation\src\ProjectCreationStorage.cs',
 'modules-src\ProjectCreation\src\ProjectCreationStorageTransaction.cs','modules-src\ProjectCreation\src\ProjectCreationSettings.cs',
 'tests\ProjectCalibrationScopeSaveSmoke.cs')
$sources=@($relative|ForEach-Object{Join-Path $root $_});$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=@((@($sources)+@($PSCommandPath,$helper,$csc))|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
New-Item -ItemType Directory -Path $qa|Out-Null
$exe=Join-Path $qa 'ProjectCalibrationScopeSaveSmoke.exe'
$compile=@(& $csc /nologo /noconfig /langversion:7.3 /platform:x64 /optimize+ /deterministic+ /debug- /target:exe ('/out:'+$exe) /r:System.dll /r:System.Core.dll /r:System.Xml.dll @sources 2>&1|ForEach-Object{$_.ToString()})
$compileExit=$LASTEXITCODE;$compile|Set-Content -LiteralPath (Join-Path $qa 'compile.log') -Encoding UTF8
if($compileExit-ne0){throw ('Compilation failed: '+($compile-join"`n"))}
$testHash=(Get-FileHash -LiteralPath $exe).Hash
$arguments=@((Join-Path $qa 'synthetic'));if($Audit){$arguments+='audit'}
$execution=Invoke-RegressionExecutable -Executable $exe -Arguments $arguments -LogPrefix (Join-Path $qa 'scope') -TimeoutMilliseconds 60000
$execution.Output|Write-Output
$drift=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash-cne$_.SHA256})
$passed=$execution.ExitCode-eq0-and$drift.Count-eq0-and(Get-FileHash -LiteralPath $exe).Hash-ceq$testHash
[ordered]@{Schema='HVX.ProjectCalibrationScopeSave.Offline/1';Audit=[bool]$Audit;Status=$(if($passed){'PASS'}else{'FAIL'});CompletedUtc=[DateTime]::UtcNow.ToString('o');Inputs=$inputs;SourceUnchanged=($drift.Count-eq0);InputMismatchCount=$drift.Count;Executable=$exe;ExecutableSHA256=$testHash;Execution=$execution;ControlsConstructed=$false;WindowsShown=$false;ProductEntryExecuted=$false;ProfilesRead=$false;SDKCalled=$false;HardwareAccessed=$false;UserProjectWritten=$false;SameFinalProductBytes=$false}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'proof.json') -Encoding UTF8
Write-Output ('PROJECT_CALIBRATION_SCOPE_PROOF='+$qa+'\proof.json')
if(-not$passed){throw 'Project calibration scope regression failed or inputs drifted; evidence retained.'}
