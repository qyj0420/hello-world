[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputRoot,[string]$ReadOnlyLocationFile='')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($PSVersionTable.PSEdition-ne'Desktop'){throw 'Use Windows PowerShell 5.1 for the target .NET Framework CLR.'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$qa=[IO.Path]::GetFullPath($OutputRoot)
if(-not $qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $qa)){throw 'A fresh project-owned build QA directory is required.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded test helper.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $nodes=@($ast.FindAll({param($n)$n-is[Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($nodes.Count-ne1){throw 'Required bounded helper missing.'};. ([ScriptBlock]::Create($nodes[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$sources=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','R2RoundedButton.cs','PressureRecordPublication.cs','ZeroVoltageNoiseReference.cs','DefectModelCatalog.cs','AnalysisDateText.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','NoisePulseTemplates.cs','LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','CableJointStore.cs','CableJointDialog.cs','CalibrationPacketIdentityContract.cs','LocationCycleRecord.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$sources+=Join-Path $root 'src\LocationScorePolicy.cs'
foreach($part in @('src','vendor\PdMap','vendor\PdMap\Common','vendor\Spectrum','vendor\Task7','vendor\Task28','vendor\LocationTree','shared')){
 $sources+=@(Get-ChildItem -LiteralPath (Join-Path $root ('modules-src\DataAnalysis\'+$part)) -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
}
$sources+=@('modules-src\LocationABCD\ABRelease\AB\source\PeakAnchoredLeadingEdgeMath.cs','modules-src\ConclusionAlgorithm\EvidenceV3\HVX.Task15.Embedded.cs'|ForEach-Object{Join-Path $root $_})
$legacy=Join-Path $root 'modules-src\LocationABCD\ABRelease\confirmed-legacy-inputs.json';$brand=Join-Path $root 'assets\xiongxin.ico';$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$test=Join-Path $root 'tests\LocationESingleReadOfflineSmoke.cs';$engine=Join-Path $root 'modules-src\DataAnalysis\src\ReviewAnalysisEngine.cs'
$inputPaths=@($sources)+@($PSCommandPath,$helper,$legacy,$brand,$csc,$test)
if($ReadOnlyLocationFile){Assert-RegressionUnlinkedPath $ReadOnlyLocationFile;$inputPaths+=([IO.Path]::GetFullPath($ReadOnlyLocationFile))}
$inputPaths=@($inputPaths|Sort-Object -Unique);$inputs=@($inputPaths|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
New-Item -ItemType Directory -Path (Join-Path $qa 'modules\DataAnalysis')|Out-Null
$pins=Join-Path $qa 'legacy-unit-pins.json';@((Get-Content -LiteralPath $legacy -Raw -Encoding UTF8|ConvertFrom-Json)|ForEach-Object{$_.sha256})|ConvertTo-Json|Set-Content -LiteralPath $pins -Encoding UTF8
$refs=@('/nologo','/noconfig','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
$dll=Join-Path $qa 'modules\DataAnalysis\HVX.PdReview.V1.0.dll'
$compile=@(& $csc @refs /target:library ('/out:'+$dll) ('/resource:'+$brand+',XIONGXIN.Brand.ico') ('/resource:'+$pins+',HVX.LegacyUnitPins') @sources 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE
$compile|Set-Content -LiteralPath (Join-Path $qa 'module-compile.log') -Encoding UTF8
if($code-ne0){throw ('Module compile failed; retained evidence. '+($compile-join"`n"))}
$exe=Join-Path $qa 'LocationESingleReadOfflineSmoke.exe'
$compile=@(& $csc @refs /target:exe ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE
$compile|Set-Content -LiteralPath (Join-Path $qa 'harness-compile.log') -Encoding UTF8
if($code-ne0){throw 'Pure offline harness compile failed.'}
$arguments=@($dll,$engine,(Join-Path $qa 'synthetic'));if($ReadOnlyLocationFile){$arguments+=([IO.Path]::GetFullPath($ReadOnlyLocationFile))}
$execution=Invoke-RegressionExecutable -Executable $exe -Arguments $arguments -LogPrefix (Join-Path $qa 'single-read') -TimeoutMilliseconds 60000
$execution.Output|Write-Output
$mismatches=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash-cne$_.SHA256})
$passed=$execution.ExitCode-eq0-and$mismatches.Count-eq0
[ordered]@{Schema='HVX.LocationE.SingleRead.Offline/1';Status=$(if($passed){'PASS'}else{'FAIL'});Inputs=$inputs;InputMismatchCount=$mismatches.Count;Runtime=@{Path=$dll;SHA256=(Get-FileHash -LiteralPath $dll).Hash};Test=@{Path=$exe;SHA256=(Get-FileHash -LiteralPath $exe).Hash};Execution=$execution;Scope='Strict input reader equivalence and malformed input rejection only; no algorithm process, product, UI, SDK or hardware. Benchmarks exclude E+ compute and result publication.'}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
if(-not$passed){throw 'Single-read offline regression failed or inputs changed; retained evidence.'}
