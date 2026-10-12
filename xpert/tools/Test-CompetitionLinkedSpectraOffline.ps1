[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($PSVersionTable.PSEdition-ne'Desktop'){throw 'Use Windows PowerShell 5.1 for the target CLR.'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$qa=[IO.Path]::GetFullPath($OutputRoot)
if(!$qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $qa)){throw 'Use fresh project-owned QA output.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Bounded helper parse failed.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){$nodes=@($ast.FindAll({param($n)$n-is[Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name);if($nodes.Count-ne1){throw 'Bounded helper missing.'};. ([ScriptBlock]::Create($nodes[0].Extent.Text))}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$sources=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','R2RoundedButton.cs','PressureRecordPublication.cs','ZeroVoltageNoiseReference.cs','DefectModelCatalog.cs','AnalysisDateText.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','NoisePulseTemplates.cs','LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','CableJointStore.cs','CableJointDialog.cs','CalibrationPacketIdentityContract.cs','LocationCycleRecord.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$sources+=Join-Path $root 'src\LocationScorePolicy.cs'
foreach($part in @('src','vendor\PdMap','vendor\PdMap\Common','vendor\Spectrum','vendor\Task7','vendor\Task28','vendor\LocationTree','shared')){$sources+=@(Get-ChildItem -LiteralPath (Join-Path $root ('modules-src\DataAnalysis\'+$part)) -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)}
$sources+=@('modules-src\LocationABCD\ABRelease\AB\source\PeakAnchoredLeadingEdgeMath.cs','modules-src\ConclusionAlgorithm\EvidenceV3\HVX.Task15.Embedded.cs'|ForEach-Object{Join-Path $root $_})
$names=@('CompetitionLinkedSpectraOfflineSmoke','CompetitionSharedMenuOfflineSmoke','CompetitionSpectrumViewsSmoke','OrdinarySpectrumBridgeSmoke')
$tests=@($names|ForEach-Object{Join-Path $root ('tests\'+$_+'.cs')});$tests+=Join-Path $root 'modules-src\DataAnalysis\vendor\Spectrum\tests\ReviewBridgeRegression.cs'
$legacy=Join-Path $root 'modules-src\LocationABCD\ABRelease\confirmed-legacy-inputs.json';$brand=Join-Path $root 'assets\xiongxin.ico';$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=@($sources+$tests+@($PSCommandPath,$helper,$legacy,$brand,$csc)|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$started=[DateTime]::UtcNow.ToString('o');New-Item -ItemType Directory -Path (Join-Path $qa 'modules\DataAnalysis')|Out-Null
$pins=Join-Path $qa 'legacy-unit-pins.json';@((Get-Content -LiteralPath $legacy -Raw -Encoding UTF8|ConvertFrom-Json)|ForEach-Object{$_.sha256})|ConvertTo-Json|Set-Content -LiteralPath $pins -Encoding UTF8
$refs=@('/nologo','/noconfig','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
$dll=Join-Path $qa 'modules\DataAnalysis\HVX.PdReview.V1.0.dll'
$out=@(& $csc @refs /target:library ('/out:'+$dll) ('/resource:'+$brand+',XIONGXIN.Brand.ico') ('/resource:'+$pins+',HVX.LegacyUnitPins') @sources 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE;$out|Set-Content -LiteralPath (Join-Path $qa 'module-compile.log') -Encoding UTF8;if($code-ne0){throw ($out-join"`n")}
$runs=New-Object System.Collections.Generic.List[object]
foreach($name in $names){$exe=Join-Path $qa ($name+'.exe');$testSources=@(Join-Path $root ('tests\'+$name+'.cs'));$extra=@();if($name-eq'OrdinarySpectrumBridgeSmoke'){$testSources+=Join-Path $root 'modules-src\DataAnalysis\vendor\Spectrum\tests\ReviewBridgeRegression.cs';$extra+=('/r:'+$dll)}
 $out=@(& $csc @refs @extra /target:exe ('/main:'+$name) ('/out:'+$exe) @testSources 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE;$out|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.log')) -Encoding UTF8;if($code-ne0){throw ($out-join"`n")}
 $arguments=if($name-eq'CompetitionLinkedSpectraOfflineSmoke'){@($dll,(Join-Path $qa 'linked-fixture'))}elseif($name-eq'CompetitionSharedMenuOfflineSmoke'){@($dll,(Join-Path $qa 'menu-fixture'))}else{@($qa,(Join-Path $qa ($name+'-fixture')))}
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments $arguments -LogPrefix (Join-Path $qa $name) -TimeoutMilliseconds 120000;$runs.Add([ordered]@{Name=$name;Execution=$run});$run.Output|Write-Output
}
$changed=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash-cne$_.SHA256});$pass=$changed.Count-eq0-and@($runs|Where-Object{$_.Execution.ExitCode-ne0}).Count-eq0
[ordered]@{Schema='HVX.CompetitionLinkedSpectra.Offline/1';Status=$(if($pass){'PASS'}else{'FAIL'});StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Inputs=$inputs;SourceUnchanged=($changed.Count-eq0);InputMismatchCount=$changed.Count;Runtime=@{Path=$dll;SHA256=(Get-FileHash -LiteralPath $dll).Hash};Tests=$runs.ToArray();ProductStarted=$false;WorkersStarted=0;HardwareAccessed=$false;SDKCalled=$false;InferenceInvoked=$false;LearningInvoked=$false;UserProjectsWritten=$false;Scope='Current-source UI twin spectra, pending mouse selection followed by explicit statistics, shared menus/phases and supplied-score parser only; synthetic read-only archive. 1x/~1.5x/2x font metrics at widths 760/1400 and Simplified/English/Traditional Chinese; not a physical multi-monitor DPI test. Existing ordinary and six-mode read-only profiles regression. No actual model inference or hardware.'}|ConvertTo-Json -Depth 14|Set-Content -LiteralPath (Join-Path $qa 'proof.json') -Encoding UTF8
Write-Output ('LINKED_SPECTRA_PROOF='+$qa+'\proof.json');if(!$pass){throw 'Offline linked-spectrum regression failed; evidence retained.'}
