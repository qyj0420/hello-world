param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$qa=[IO.Path]::GetFullPath($OutputRoot)
if(-not $qa.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $qa)){throw 'Use a new QA directory under isolated build.'}
New-Item -ItemType Directory -Path $qa | Out-Null
$build=Join-Path $PSScriptRoot 'Build-UiModules.ps1';$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($build,[ref]$tokens,[ref]$errors)
$function=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq 'Get-ModuleSources')
if($errors.Count -ne 0 -or $function.Count -ne 1){throw 'Cannot recover exact DataAnalysis source generator.'}
$projectRoot=$project;$sourceRoot=Join-Path $project 'modules-src';. ([ScriptBlock]::Create($function[0].Extent.Text))
$analysis=@(Get-ModuleSources 'DataAnalysis' @('src','vendor\PdMap','vendor\PdMap\Common','vendor\Spectrum','vendor\Task7','vendor\Task28','vendor\LocationTree','shared'))
$analysis+=@('ZeroVoltageNoiseReference.cs','DefectModelCatalog.cs','AnalysisDateText.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','NoisePulseTemplates.cs','LocationCycleRecord.cs','LiveCalibrationRecord.cs','CalibrationPacketIdentityContract.cs','LegacyCalibrationBinding.cs','CableJointStore.cs','CableJointDialog.cs')|ForEach-Object{Join-Path $project ('src\'+$_)}
$analysis+=Join-Path $sourceRoot 'LocationABCD\ABRelease\AB\source\PeakAnchoredLeadingEdgeMath.cs'
$analysis+=Join-Path $sourceRoot 'ConclusionAlgorithm\EvidenceV3\HVX.Task15.Embedded.cs'
$hostSources=@(Get-ChildItem -LiteralPath (Join-Path $project 'src') -File -Filter '*.cs' | ForEach-Object FullName)
$test=Join-Path $project 'tests/R58LocationScorePolicyOffline.cs';$brand=Join-Path $project 'assets/xiongxin.ico';$compiler=Join-Path $PSScriptRoot 'compiler/csc.exe'
$inputs=@($analysis+$hostSources+@($test,$brand,$compiler,$PSCommandPath,$build)|Sort-Object -Unique)
$before=@($inputs|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$common=@('/nologo','/langversion:7.3','/platform:x64','/optimize+','/deterministic+','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:Accessibility.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll')
function Compile([string]$Output,[string[]]$Arguments,[string[]]$Files){
 $result=@(& $compiler @common @Arguments ('/out:'+$Output) @Files 2>&1|ForEach-Object{$_.ToString()});$exit=$LASTEXITCODE
 $result|Set-Content -LiteralPath ($Output+'.compile.txt') -Encoding UTF8
 if($exit -ne 0){throw ($result -join "`n")}
}
$dll=Join-Path $qa 'HVX.PdReview.V1.0.dll';Compile $dll @('/target:library') @($analysis|Sort-Object -Unique)
$exe=Join-Path $qa 'score-policy.exe';Compile $exe @('/target:exe','/main:R58LocationScorePolicyOffline',('/r:'+$dll),('/resource:'+$brand+',Brand.ico')) @($hostSources+@($test))
$start=New-Object Diagnostics.ProcessStartInfo;$start.FileName=$exe;$start.Arguments='"'+(Join-Path $qa 'fixtures')+'"';$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
$process=[Diagnostics.Process]::Start($start);if(-not $process.WaitForExit(60000)){$process.Kill();throw 'Pure score policy tests exceeded 60 seconds.'}
$stdout=$process.StandardOutput.ReadToEnd();$stderr=$process.StandardError.ReadToEnd();$stdout|Set-Content -LiteralPath (Join-Path $qa 'stdout.txt');$stderr|Set-Content -LiteralPath (Join-Path $qa 'stderr.txt');Write-Output $stdout
if($process.ExitCode -ne 0){throw $stderr}
foreach($input in $before){if((Get-FileHash -LiteralPath $input.Path).Hash -cne $input.SHA256){throw ('Compile input changed: '+$input.Path)}}
[ordered]@{Schema='HVX.R58.LocationScorePolicy.Offline/1';Status='PASS';InputsUnchanged=$true;Inputs=$before;HardwareAccessed=$false;SDKCalled=$false;ProductStarted=$false;AlgorithmWorkerStarted=$false;PhysicalAcceptance=$false;Scope='30/45/50 boundaries, ordered configuration, persistence/old malformed fallback, linked General UI, EN text, actual manual Accept, cross-module request snapshot and cache identity; capacitor non-location exception retained.'}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $qa 'proof.json') -Encoding UTF8
Write-Output ('LOCATION_SCORE_POLICY_PROOF='+$qa)
