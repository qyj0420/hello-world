param(
    [Parameter(Mandatory=$true)][string]$ModulesRoot,
    [Parameter(Mandatory=$true)][string]$LocationFile,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$root=$project
$modules=[IO.Path]::GetFullPath($ModulesRoot)
$destination=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $destination){throw 'Use a fresh owned QA output directory.'}
if($destination.StartsWith($modules+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)-or$destination.Equals($modules,[StringComparison]::OrdinalIgnoreCase)){throw 'The integration harness must not write to tested runtime modules.'}
if(-not($destination.StartsWith($project+'\qa\',[StringComparison]::OrdinalIgnoreCase)-or$destination.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase))){throw 'Evidence must stay in project-owned qa or build.'}
$dll=Join-Path $modules 'DataAnalysis\HVX.PdReview.V1.0.dll'
$pins=Join-Path $modules 'research-pins.json'
if(-not(Test-Path -LiteralPath $pins -PathType Leaf)){$pins=Join-Path (Split-Path -Parent $modules) 'research-pins.json'}
$source=Join-Path $project 'tests\LocationEIntegrationSmoke.cs'
$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
# Extract only the existing bounded, hidden test-process helpers; do not run
# the regression builder/product entry points of the containing script.
$runner=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded regression runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $nodes=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($nodes.Count-ne 1){throw ('Missing helper '+$name)}
 . ([ScriptBlock]::Create($nodes[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $modules
Assert-RegressionUnlinkedPath $destination -AllowMissing
foreach($input in @($dll,$pins,$source,$LocationFile,$compiler,$runner)){Assert-RegressionUnlinkedPath $input;if(!(Test-Path -LiteralPath $input -PathType Leaf)){throw "Missing QA input: $input"}}
function ModuleSnapshot {
 @(Get-ChildItem -LiteralPath $modules -File -Recurse | Sort-Object FullName | ForEach-Object {Assert-RegressionUnlinkedPath $_.FullName;[ordered]@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
}
function InputSnapshot {
 @($source,$PSCommandPath,$dll,$pins,$compiler,$runner) | ForEach-Object { [ordered]@{path=[IO.Path]::GetFullPath($_);sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} }
}
$runtimeBefore=@(ModuleSnapshot);$inputsBefore=@(InputSnapshot)
New-Item -ItemType Directory -Path $destination | Out-Null
$exe=Join-Path $destination 'LocationEIntegrationSmoke.exe'
& $compiler /nologo /target:exe /platform:x64 /langversion:7.3 "/out:$exe" "/reference:$dll" /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll "/resource:$pins,HVX.Research.Pins" $source
if($LASTEXITCODE -ne 0){throw 'Pure E integration harness compilation failed.'}
# This is a test executable only. It does not load/start the desktop host or any hardware DLL/worker.
$process=Invoke-RegressionExecutable -Executable $exe -Arguments @($modules,[IO.Path]::GetFullPath($LocationFile),(Join-Path $destination 'fixtures')) -LogPrefix (Join-Path $destination 'route')
if($process.ExitCode -ne 0){$process.Output|Write-Output;throw 'Pure E integration harness failed; retained independent QA logs.'}
$proof=([IO.File]::ReadAllText($process.StdoutPath)|ConvertFrom-Json)
$inputsUnchanged=(($inputsBefore|ConvertTo-Json -Compress -Depth 4)-ceq((InputSnapshot)|ConvertTo-Json -Compress -Depth 4))
$runtimeUnchanged=(($runtimeBefore|ConvertTo-Json -Compress -Depth 4)-ceq((ModuleSnapshot)|ConvertTo-Json -Compress -Depth 4))
if(-not$inputsUnchanged-or-not$runtimeUnchanged){throw 'Test inputs/runtime changed during integration QA; no PASS receipt may be issued.'}
$proof | Add-Member -NotePropertyName inputHashes -NotePropertyValue $inputsBefore
$proof | Add-Member -NotePropertyName inputsUnchanged -NotePropertyValue $inputsUnchanged
$proof | Add-Member -NotePropertyName runtimeUnchanged -NotePropertyValue $runtimeUnchanged
$proof | Add-Member -NotePropertyName runtimeFiles -NotePropertyValue $runtimeBefore.Count
$proof | Add-Member -NotePropertyName execution -NotePropertyValue $process
$proof | Add-Member -NotePropertyName harness -NotePropertyValue ([ordered]@{path=$exe;sha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash;location='QA only; original modules resolved without copies'})
$json=$proof | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText((Join-Path $destination 'verification.json'),$json,(New-Object Text.UTF8Encoding($false)))
$json
