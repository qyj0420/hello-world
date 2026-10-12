[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not [IO.Path]::IsPathRooted($OutputRoot)){$OutputRoot=Join-Path $root $OutputRoot}
$qa=[IO.Path]::GetFullPath($OutputRoot)
if(-not $qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'QA output must remain in build.'}
if(Test-Path -LiteralPath $qa){throw 'Use a new QA directory.'}
for($parent=Split-Path $qa;$parent;$parent=Split-Path $parent){if((Get-Item -LiteralPath $parent).Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Linked QA ancestors are not permitted.'}}
$sources=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','R2RoundedButton.cs','PressureRecordPublication.cs','ZeroVoltageNoiseReference.cs','DefectModelCatalog.cs','AnalysisDateText.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','NoisePulseTemplates.cs','LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','CableJointStore.cs','CableJointDialog.cs','CalibrationPacketIdentityContract.cs','LocationCycleRecord.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$sources+=Join-Path $root 'src\LocationScorePolicy.cs'
foreach($part in @('src','vendor\PdMap','vendor\PdMap\Common','vendor\Spectrum','vendor\Task7','vendor\Task28','vendor\LocationTree','shared')){
 $sources+=@(Get-ChildItem -LiteralPath (Join-Path $root ('modules-src\DataAnalysis\'+$part)) -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
}
$sources+=@('modules-src\LocationABCD\ABRelease\AB\source\PeakAnchoredLeadingEdgeMath.cs','modules-src\ConclusionAlgorithm\EvidenceV3\HVX.Task15.Embedded.cs'|ForEach-Object{Join-Path $root $_})
$test=Join-Path $root 'tests\CapacitorDemRawAnalysisSmoke.cs';$legacy=Join-Path $root 'modules-src\LocationABCD\ABRelease\confirmed-legacy-inputs.json'
$brand=Join-Path $root 'assets\xiongxin.ico';$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=@($sources+$test+$PSCommandPath+$legacy+$brand+$csc)|Sort-Object -Unique
function Inventory{@($inputs|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
$null=New-Item -ItemType Directory -Path (Join-Path $qa 'modules\DataAnalysis')
$refs=@('/nologo','/noconfig','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
if((Get-FileHash -LiteralPath $legacy).Hash-cne'5BE1895AA9BF2D9449A4875112C1A3D976F0119D23F624E976141FE955C6D37D'){throw 'Legacy pin source changed.'}
$pins=Join-Path $qa 'legacy-unit-pins.json';@((Get-Content -LiteralPath $legacy -Raw -Encoding UTF8|ConvertFrom-Json)|ForEach-Object{$_.sha256})|ConvertTo-Json|Set-Content -LiteralPath $pins -Encoding UTF8
$dll=Join-Path $qa 'modules\DataAnalysis\HVX.PdReview.V1.0.dll'
& $csc @refs /target:library ('/out:'+$dll) ('/resource:'+$brand+',XIONGXIN.Brand.ico') ('/resource:'+$pins+',HVX.LegacyUnitPins') @sources 2>&1|Tee-Object -FilePath (Join-Path $qa 'analysis-compile.log')
if($LASTEXITCODE-ne0){throw 'Source-isolated DataAnalysis compilation failed; evidence retained.'}
$exe=Join-Path $qa 'CapacitorDemRawAnalysisSmoke.exe'
& $csc @refs /target:exe ('/out:'+$exe) $test 2>&1|Tee-Object -FilePath (Join-Path $qa 'test-compile.log')
if($LASTEXITCODE-ne0){throw 'Focused test compilation failed; evidence retained.'}
$p=Start-Process -FilePath $exe -ArgumentList @('"'+$qa+'"','"'+(Join-Path $qa 'fixture')+'"') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $qa 'stdout.txt') -RedirectStandardError (Join-Path $qa 'stderr.txt')
if(-not $p.WaitForExit(90000)){$p.Kill();throw 'Focused test timeout; evidence retained.'};$p.WaitForExit()
Get-Content -LiteralPath (Join-Path $qa 'stdout.txt');Get-Content -LiteralPath (Join-Path $qa 'stderr.txt')
$unchanged=($before|ConvertTo-Json -Depth 5 -Compress)-ceq(@(Inventory)|ConvertTo-Json -Depth 5 -Compress)
[ordered]@{Schema='HVX.CapacitorDemRaw.SourceFocused/1';Status=$(if($p.ExitCode-eq0-and$unchanged){'PASS'}else{'FAIL'});StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Inputs=$before;SourceUnchanged=$unchanged;Runtime=@{Path=$dll;SHA256=(Get-FileHash -LiteralPath $dll).Hash};Test=@{Path=$exe;SHA256=(Get-FileHash -LiteralPath $exe).Hash;ExitCode=$p.ExitCode};HardwareAccessed=$false;SDKCalled=$false;ProductEntryExecuted=$false;WorkerEntryExecuted=$false;UserProjectWritten=$false;SameFinalProductBytes=$false;SourceIsolatedModule=$true;Scope='Synthetic owned DEM/raw/settings/Capacitor CAL; actual analysis engine/tree/background batch/session; no cable localization runtime installed.'}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'proof.json') -Encoding UTF8
Write-Output ('CAPACITOR_DEM_PROOF='+$qa+'\proof.json')
if($p.ExitCode-ne0-or-not$unchanged){throw 'Focused validation failed; original evidence retained.'}
