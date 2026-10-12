[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($PSVersionTable.PSEdition-ne'Desktop'){throw 'Use Windows PowerShell 5.1, matching the target .NET Framework CLR.'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$qa=[IO.Path]::GetFullPath($OutputRoot)
if(-not $qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'QA output must remain in the owned project build directory.'}
if(Test-Path -LiteralPath $qa){throw 'Use a new QA directory; existing evidence is never overwritten.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Bounded runner helper could not be parsed.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n-is[Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count-ne1){throw 'Bounded runner helper missing.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$sources=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','R2RoundedButton.cs','PressureRecordPublication.cs','ZeroVoltageNoiseReference.cs','DefectModelCatalog.cs','AnalysisDateText.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','NoisePulseTemplates.cs','LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','CableJointStore.cs','CableJointDialog.cs','CalibrationPacketIdentityContract.cs','LocationCycleRecord.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$sources+=Join-Path $root 'src\LocationScorePolicy.cs'
foreach($part in @('src','vendor\PdMap','vendor\PdMap\Common','vendor\Spectrum','vendor\Task7','vendor\Task28','vendor\LocationTree','shared')){
 $sources+=@(Get-ChildItem -LiteralPath (Join-Path $root ('modules-src\DataAnalysis\'+$part)) -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
}
$sources+=@('modules-src\LocationABCD\ABRelease\AB\source\PeakAnchoredLeadingEdgeMath.cs','modules-src\ConclusionAlgorithm\EvidenceV3\HVX.Task15.Embedded.cs'|ForEach-Object{Join-Path $root $_})
$reportSources=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','CableJointStore.cs','CableVisualStyle.cs','AnalysisDateText.cs','PressureFixtureJournal.cs','PressureRecordPublication.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$reportSources+=@(Get-ChildItem -LiteralPath (Join-Path $root 'modules-src\ReportGenerator\src') -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$reportSources+=@('modules-src\DataBrowser\src\BrowserData.cs','modules-src\ConclusionAlgorithm\EvidenceV3\HVX.Task15.Embedded.cs','modules-src\ReportEngine\shared\BoundedDocx.cs','modules-src\ReportEngine\shared\ReportOutputSafety.cs'|ForEach-Object{Join-Path $root $_})
$names=@('CapacitorDemRawAnalysisSmoke','ReviewAcquisitionQualityOfflineSmoke','ReportAcquisitionQualityOfflineSmoke','CustomerModalTextOfflineSmoke','LocationELedgerBudgetOfflineSmoke','ReviewAcceptancePresentationOfflineSmoke','LocalAiSuggestionFeedbackOfflineSmoke')
$legacy=Join-Path $root 'modules-src\LocationABCD\ABRelease\confirmed-legacy-inputs.json';$brand=Join-Path $root 'assets\xiongxin.ico';$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputPaths=@($sources)+@($reportSources)+@($PSCommandPath,$helper,$legacy,$brand,$csc)+@($names|ForEach-Object{Join-Path $root ('tests\'+$_+'.cs')})
$inputPaths=@($inputPaths|Sort-Object -Unique);$inputs=@($inputPaths|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$started=[DateTime]::UtcNow.ToString('o');$records=New-Object System.Collections.Generic.List[object]
New-Item -ItemType Directory -Path (Join-Path $qa 'modules\DataAnalysis')|Out-Null
$refs=@('/nologo','/noconfig','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
if((Get-FileHash -LiteralPath $legacy).Hash-cne'5BE1895AA9BF2D9449A4875112C1A3D976F0119D23F624E976141FE955C6D37D'){throw 'Legacy pin input changed.'}
$pins=Join-Path $qa 'legacy-unit-pins.json';@((Get-Content -LiteralPath $legacy -Raw -Encoding UTF8|ConvertFrom-Json)|ForEach-Object{$_.sha256})|ConvertTo-Json|Set-Content -LiteralPath $pins -Encoding UTF8
$dll=Join-Path $qa 'modules\DataAnalysis\HVX.PdReview.V1.0.dll'
$compile=@(& $csc @refs /target:library ('/out:'+$dll) ('/resource:'+$brand+',XIONGXIN.Brand.ico') ('/resource:'+$pins+',HVX.LegacyUnitPins') @sources 2>&1|ForEach-Object{$_.ToString()});$compileExit=$LASTEXITCODE
$compile|Set-Content -LiteralPath (Join-Path $qa 'module-compile.log') -Encoding UTF8
if($compileExit-ne0){throw ('Source-isolated module compilation failed; evidence retained. '+($compile-join"`n"))}
New-Item -ItemType Directory -Path (Join-Path $qa 'modules\ReportGenerator')|Out-Null
$reportDll=Join-Path $qa 'modules\ReportGenerator\Xiongxin.OWTS.ReportGenerator.dll'
$compile=@(& $csc @refs /target:library ('/out:'+$reportDll) ('/resource:'+$brand+',XIONGXIN.Brand.ico') @reportSources 2>&1|ForEach-Object{$_.ToString()});$compileExit=$LASTEXITCODE
$compile|Set-Content -LiteralPath (Join-Path $qa 'report-module-compile.log') -Encoding UTF8
if($compileExit-ne0){throw ('Source-isolated report compilation failed; evidence retained. '+($compile-join"`n"))}
$runtimeStartSHA=(Get-FileHash -LiteralPath $dll).Hash;$reportRuntimeStartSHA=(Get-FileHash -LiteralPath $reportDll).Hash
foreach($name in $names){
 $exe=Join-Path $qa ($name+'.exe');$test=Join-Path $root ('tests\'+$name+'.cs')
 $compile=@(& $csc @refs /target:exe ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()});$compileExit=$LASTEXITCODE
 $compile|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.log')) -Encoding UTF8
 if($compileExit-ne0){throw ($name+' compilation failed: '+($compile-join"`n"))}
 $testArguments=@($dll)
 if($name-in@('CapacitorDemRawAnalysisSmoke','ReviewAcquisitionQualityOfflineSmoke','ReportAcquisitionQualityOfflineSmoke','CustomerModalTextOfflineSmoke')){$testArguments=@($qa,(Join-Path $qa ($name+'-synthetic')))}
 elseif($name-eq'LocationELedgerBudgetOfflineSmoke'){$testArguments+=Join-Path $qa 'synthetic-ledgers'}
 $execution=Invoke-RegressionExecutable -Executable $exe -Arguments $testArguments -LogPrefix (Join-Path $qa $name) -TimeoutMilliseconds 60000
 $execution.Output|Write-Output
 $records.Add([ordered]@{Name=$name;TestSourceSHA256=(Get-FileHash -LiteralPath $test).Hash;Exe=$exe;ExeSHA256=(Get-FileHash -LiteralPath $exe).Hash;Execution=$execution})
 # Independent regressions all run; any failure remains a FAIL in the proof.
}
$mismatches=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash-cne$_.SHA256})
$runtimeUnchanged=(Get-FileHash -LiteralPath $dll).Hash-ceq$runtimeStartSHA-and(Get-FileHash -LiteralPath $reportDll).Hash-ceq$reportRuntimeStartSHA
$passed=$records.Count-eq$names.Count-and@($records|Where-Object{$_.Execution.ExitCode-ne0}).Count-eq0-and$mismatches.Count-eq0-and$runtimeUnchanged
[ordered]@{Schema='HVX.ReviewAcquisitionQuality.Offline/1';Status=$(if($passed){'PASS'}else{'FAIL'});StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Inputs=$inputs;InputMismatchCount=$mismatches.Count;SourceUnchanged=($mismatches.Count-eq0);Runtime=@{Path=$dll;SHA256=$runtimeStartSHA};ReportRuntime=@{Path=$reportDll;SHA256=$reportRuntimeStartSHA};RuntimeUnchanged=$runtimeUnchanged;Tests=$records.ToArray();UIControlConstructed=$true;ProductEntryExecuted=$false;LocationAlgorithmInvoked=$false;WorkerEntryExecuted=$false;SDKCalled=$false;HardwareAccessed=$false;UserProjectWritten=$false;SameFinalProductBytes=$false;Scope='Synthetic LOC/DEM/raw/CAL only; quality parsing, reference analysis, batch/session/named-result/report and learning gates; old ledger/acceptance/AI feedback regressions. No cable location algorithm or real acquisition.'}|ConvertTo-Json -Depth 14|Set-Content -LiteralPath (Join-Path $qa 'proof.json') -Encoding UTF8
Write-Output ('ACQUISITION_QUALITY_PROOF='+$qa+'\proof.json')
if(-not$passed){throw 'Quality regression failed or source binding changed; evidence retained.'}
