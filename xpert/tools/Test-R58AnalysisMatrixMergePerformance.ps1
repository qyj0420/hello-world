param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$qa=[IO.Path]::GetFullPath($OutputRoot)
if(-not $qa.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'QA output must be in isolated build directory.'}
New-Item -ItemType Directory -Path $qa -Force | Out-Null
$build=Join-Path $PSScriptRoot 'Build-UiModules.ps1';$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($build,[ref]$tokens,[ref]$errors)
$function=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq 'Get-ModuleSources')
if($errors.Count -ne 0 -or $function.Count -ne 1){throw 'Cannot recover exact DataAnalysis compile input generator.'}
$projectRoot=$project;$sourceRoot=Join-Path $project 'modules-src';. ([ScriptBlock]::Create($function[0].Extent.Text))
$files=@(Get-ModuleSources 'DataAnalysis' @('src','vendor\PdMap','vendor\PdMap\Common','vendor\Spectrum','vendor\Task7','vendor\Task28','vendor\LocationTree','shared'))
$files+=@('ZeroVoltageNoiseReference.cs','DefectModelCatalog.cs','AnalysisDateText.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','NoisePulseTemplates.cs','LocationCycleRecord.cs','LiveCalibrationRecord.cs','CalibrationPacketIdentityContract.cs','LegacyCalibrationBinding.cs','CableJointStore.cs','CableJointDialog.cs')|ForEach-Object{Join-Path $project ('src\'+$_)}
$files+=Join-Path $sourceRoot 'LocationABCD\ABRelease\AB\source\PeakAnchoredLeadingEdgeMath.cs'
$files+=Join-Path $sourceRoot 'ConclusionAlgorithm\EvidenceV3\HVX.Task15.Embedded.cs'
$files+=Join-Path $project 'tests\R58AnalysisMatrixMergePerformance.cs'
$before=@($files|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$exe=Join-Path $qa 'matrix-merge.exe';$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$arguments=@('/nologo','/langversion:7.3','/target:exe','/platform:x64','/optimize+','/deterministic+','/main:R58AnalysisMatrixMergePerformance',('/out:'+$exe),
 '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:Accessibility.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Microsoft.CSharp.dll')
$lines=@(& $compiler @arguments @files 2>&1 | ForEach-Object{$_.ToString()});$code=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($code -ne 0){throw ($lines -join "`n")}
$receipt=Join-Path $qa 'measurements.json'
$start=New-Object Diagnostics.ProcessStartInfo;$start.FileName=$exe;$start.Arguments='"'+$receipt+'"';$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
$process=[Diagnostics.Process]::Start($start);if(-not $process.WaitForExit(60000)){$process.Kill();throw 'Pure matrix benchmark exceeded 60 seconds.'}
$stdout=$process.StandardOutput.ReadToEnd();$stderr=$process.StandardError.ReadToEnd();$stdout|Set-Content -LiteralPath (Join-Path $qa 'stdout.txt');$stderr|Set-Content -LiteralPath (Join-Path $qa 'stderr.txt');Write-Output $stdout
if($process.ExitCode -ne 0){throw $stderr}
foreach($input in $before){if((Get-FileHash -LiteralPath $input.Path).Hash -cne $input.SHA256){throw ('Compile input changed: '+$input.Path)}}
[ordered]@{Schema='HVX.R58.AnalysisMatrixMerge.Runner/1';Inputs=$before;Measurements=$receipt;MeasurementsSHA256=(Get-FileHash -LiteralPath $receipt).Hash;
 HardwareAccessed=$false;SDKCalled=$false;ProductStarted=$false;UserProjectWritten=$false;PhysicalAcceptance=$false;FullAnalysisSpeedVerified=$false}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $qa 'proof.json') -Encoding UTF8
Write-Output ('MATRIX_MERGE_PROOF='+$receipt)
