param([Parameter(Mandatory=$true)][string]$OutputRoot,[string]$BaselineReader='',[string]$ActualLoc='',[string]$Label='candidate')
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
if($BaselineReader){$original=Join-Path $sourceRoot 'DataAnalysis\src\ReviewLocationFileReader.cs';$files=@($files|Where-Object{$_ -ne $original})+$BaselineReader}
$test=Join-Path $project 'tests\R58LocationReaderPerformance.cs';$files+=$test
$before=@($files|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$exe=Join-Path $qa ($Label+'.exe');$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$arguments=@('/nologo','/langversion:7.3','/target:exe','/platform:x64','/optimize+','/deterministic+','/main:R58LocationReaderPerformance',('/out:'+$exe),
 '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:Accessibility.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Microsoft.CSharp.dll')
$lines=@(& $compiler @arguments @files 2>&1 | ForEach-Object{$_.ToString()});$code=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa ($Label+'.compile.txt')) -Encoding UTF8
if($code -ne 0){throw ($lines -join "`n")}
$actualBefore=if($ActualLoc){(Get-FileHash -LiteralPath $ActualLoc).Hash}else{''}
$receipt=Join-Path $qa ($Label+'.measurements.json');$fixture=Join-Path $qa 'fixtures'
$start=New-Object Diagnostics.ProcessStartInfo;$start.FileName=$exe;$start.Arguments='"'+$fixture+'" "'+$receipt+'" "'+$ActualLoc+'"';$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
$process=[Diagnostics.Process]::Start($start);if(-not $process.WaitForExit(60000)){$process.Kill();throw 'Pure parser benchmark exceeded 60 seconds.'}
$stdout=$process.StandardOutput.ReadToEnd();$stderr=$process.StandardError.ReadToEnd();$stdout|Set-Content -LiteralPath (Join-Path $qa ($Label+'.stdout.txt'));$stderr|Set-Content -LiteralPath (Join-Path $qa ($Label+'.stderr.txt'));Write-Output $stdout
if($process.ExitCode -ne 0){throw $stderr}
foreach($input in $before){if((Get-FileHash -LiteralPath $input.Path).Hash -cne $input.SHA256){throw ('Compile input changed: '+$input.Path)}}
if($ActualLoc -and (Get-FileHash -LiteralPath $ActualLoc).Hash -cne $actualBefore){throw 'Read-only real LOC changed.'}
[ordered]@{Schema='HVX.R58.LocationReader.Runner/1';Label=$Label;Inputs=$before;Measurements=$receipt;MeasurementsSHA256=(Get-FileHash -LiteralPath $receipt).Hash;ActualLoc=$ActualLoc;ActualLocSHA256=$actualBefore;UserProjectWritten=$false;HardwareAccessed=$false;SDKCalled=$false;ProductStarted=$false;PhysicalAcceptance=$false}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $qa ($Label+'.proof.json')) -Encoding UTF8
if($Label -eq 'candidate' -and (Test-Path -LiteralPath (Join-Path $qa 'baseline.measurements.json'))){
 $prior=Get-Content -LiteralPath (Join-Path $qa 'baseline.measurements.json') -Raw|ConvertFrom-Json;$current=Get-Content -LiteralPath $receipt -Raw|ConvertFrom-Json
 if($prior.Checks -ne $current.Checks -or $prior.Tests.Count -ne $current.Tests.Count){throw 'Baseline/candidate parser checks differ.'}
 $comparison=@(for($i=0;$i -lt $prior.Tests.Count;$i++){
   $old=$prior.Tests[$i];$new=$current.Tests[$i]
   if($old.FileName -cne $new.FileName -or $old.Samples -ne $new.Samples -or $old.Bytes -ne $new.Bytes -or $old.SampleSHA256 -cne $new.SampleSHA256){throw 'Baseline/candidate samples differ.'}
   [ordered]@{FileName=$old.FileName;Samples=$old.Samples;SampleSHA256=$old.SampleSHA256;SamplesBitIdentical=$true;
    ReadBeforeMs=$old.ReadMedianMilliseconds;ReadAfterMs=$new.ReadMedianMilliseconds;ValidateBeforeMs=$old.ValidateMedianMilliseconds;ValidateAfterMs=$new.ValidateMedianMilliseconds;
    ReadGen0Before=$old.ReadGeneration0Collections;ReadGen0After=$new.ReadGeneration0Collections}
 })
 [ordered]@{Schema='HVX.R58.LocationReader.Comparison/1';ParserContractChecksPerBuild=$current.Checks;Measurements=$comparison;
   BaselineProofSHA256=(Get-FileHash -LiteralPath (Join-Path $qa 'baseline.proof.json')).Hash;CandidateProofSHA256=(Get-FileHash -LiteralPath (Join-Path $qa 'candidate.proof.json')).Hash;
   FullAnalysisSpeedVerified=$false;Scope='Strict LOC read/validation only; unchanged candidate algorithms, thresholds, matrix outputs and GUI; no physical acceptance.'}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $qa 'comparison.proof.json') -Encoding UTF8
}
Write-Output ('LOCATION_READER_PROOF='+$receipt)
