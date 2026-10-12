[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputRoot,
      [Parameter(Mandatory=$true)][string]$OldBuildRoot,
      [Parameter(Mandatory=$true)][string]$CalibrationSnapshot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not [IO.Path]::IsPathRooted($OutputRoot)){$OutputRoot=Join-Path $project $OutputRoot}
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
if(-not $OutputRoot.StartsWith($project+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'QA output must be inside this workspace.'}
if(Test-Path -LiteralPath $OutputRoot){throw 'Use a new isolated QA root.'}
$null=New-Item -ItemType Directory -Path $OutputRoot
$sources=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','R2RoundedButton.cs','PressureRecordPublication.cs','LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','CalibrationPulseDisplayWindow.cs','CableJointStore.cs','CableJointDialog.cs','CableVisualStyle.cs','AnalysisDateText.cs')|ForEach-Object{Join-Path $project ('src/'+$_)}
foreach($part in @('src','vendor/Item','vendor/Calibration','vendor/Calibration/Common','vendor/Oscillation')){$sources+=@(Get-ChildItem -LiteralPath (Join-Path $project ('modules-src/DataBrowser/'+$part)) -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)}
$testSource=Join-Path $project 'tests/BrowserArchivedDecaySmoke.cs'
$regressionSource=Join-Path $project 'tests/CalibrationDecayFeedbackSmoke.cs'
$inputs=@($sources+$testSource+$regressionSource+$PSCommandPath)|Sort-Object -Unique
$before=@($inputs|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$compiler=Join-Path $PSScriptRoot 'compiler/csc.exe'
$refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:Accessibility.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Microsoft.CSharp.dll')
$test=Join-Path $OutputRoot 'BrowserArchivedDecaySmoke.exe'
& $compiler /nologo /langversion:7.3 /target:exe /platform:x64 @refs ('/out:'+$test) $testSource 2>&1|Tee-Object -FilePath (Join-Path $OutputRoot 'test-compile.log')
if($LASTEXITCODE-ne0){throw 'Dedicated test compile failed.'}
function Run-Test([string]$exe,[string]$tag,[string[]]$arguments){
 $qa=Join-Path $OutputRoot $tag;$null=New-Item -ItemType Directory -Path $qa
 $p=Start-Process -FilePath $exe -ArgumentList @($arguments|ForEach-Object{'"'+$_+'"'}) -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $qa 'stdout.txt') -RedirectStandardError (Join-Path $qa 'stderr.txt')
 Get-Content -LiteralPath (Join-Path $qa 'stdout.txt');Get-Content -LiteralPath (Join-Path $qa 'stderr.txt')
 if($p.ExitCode-ne0){throw ($tag+' failed; evidence retained.')}
}
# Actual old protected 07 bytes reproduce before the current source build.
Run-Test $test 'old07' @([IO.Path]::GetFullPath($OldBuildRoot),(Join-Path $OutputRoot 'old07'),[IO.Path]::GetFullPath($CalibrationSnapshot),'old07')
$dir=Join-Path $OutputRoot 'modules/DataBrowser';$null=New-Item -ItemType Directory -Path $dir
$dll=Join-Path $dir 'Xiongxin.OWTS.DataBrowser.dll'
& $compiler /nologo /noconfig /langversion:7.3 /optimize+ /deterministic+ /debug- /target:library /platform:x64 @refs ('/out:'+$dll) ('/resource:'+(Join-Path $project 'assets/xiongxin.ico')+',XIONGXIN.Brand.ico') @sources 2>&1|Tee-Object -FilePath (Join-Path $dir 'compile.log')
if($LASTEXITCODE-ne0){throw 'Local Browser compile failed.'}
Run-Test $test 'new' @($OutputRoot,(Join-Path $OutputRoot 'new'),[IO.Path]::GetFullPath($CalibrationSnapshot),'new')
$regression=Join-Path $OutputRoot 'CalibrationDecayFeedbackSmoke.exe'
& $compiler /nologo /langversion:7.3 /target:exe /platform:x64 @refs ('/out:'+$regression) $regressionSource 2>&1|Tee-Object -FilePath (Join-Path $OutputRoot 'regression-compile.log')
if($LASTEXITCODE-ne0){throw 'Existing regression compile failed.'}
Run-Test $regression 'legacy-regression' @($OutputRoot,(Join-Path $OutputRoot 'legacy-regression'))
$after=@($inputs|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$unchanged=($before|ConvertTo-Json -Depth 4 -Compress)-ceq($after|ConvertTo-Json -Depth 4 -Compress)
[ordered]@{Schema='BrowserArchivedDecay.SourceFake.v1';SourceUnchanged=$unchanged;HardwareCommands=0;OriginalProjectWrites=0;Scope='Source-isolated Browser local display only; old protected 07 negative reproduction; not final protected release';Sources=$after;Runtime=@{Path=$dll;SHA256=(Get-FileHash -LiteralPath $dll).Hash};TestSHA256=(Get-FileHash -LiteralPath $test).Hash;CalibrationSnapshotSHA256=(Get-FileHash -LiteralPath (Join-Path $CalibrationSnapshot 'ALL_5nC.cal')).Hash}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $OutputRoot 'proof.json') -Encoding UTF8
if(-not $unchanged){throw 'Source changed during test; evidence retained.'}
