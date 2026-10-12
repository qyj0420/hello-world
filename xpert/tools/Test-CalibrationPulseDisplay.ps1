[CmdletBinding()]
param([string]$OutputRoot='build/qa-calibration-pulse-display-20261010')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not [IO.Path]::IsPathRooted($OutputRoot)){$OutputRoot=Join-Path $project $OutputRoot}
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $OutputRoot){throw 'Use a fresh isolated output root.'}
$null=New-Item -ItemType Directory -Path $OutputRoot
$base=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','PressureRecordPublication.cs','CableJointStore.cs','CableJointDialog.cs','LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','CalibrationPulseDisplayWindow.cs')|ForEach-Object{Join-Path $project ('src/'+$_)}
$flags=@('/nologo','/noconfig','/langversion:7.3','/optimize+','/deterministic+','/debug-','/target:library','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:Accessibility.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Microsoft.CSharp.dll')
$compiler=Join-Path $PSScriptRoot 'compiler/csc.exe'
$cal=@(Get-ChildItem -LiteralPath (Join-Path $project 'modules-src/CalibrationTest/src') -Filter '*.cs' -File |Sort-Object Name|ForEach-Object FullName)+$base+@(Join-Path $project 'modules-src/LocationABCD/ABRelease/AB/source/PeakAnchoredLeadingEdgeMath.cs')+@('NoisePulseTemplates.cs','PicoCaptureCadence.cs'|ForEach-Object{Join-Path $project ('src/'+$_)})
$standalone=Join-Path $project 'modules-src/CalibrationStandalone'
$cal+=@(Get-ChildItem -LiteralPath $standalone -Filter '*.cs' -File|Where-Object{$_.Name -notin @('Program.cs','ControlBoxTests.cs')}|Sort-Object Name|ForEach-Object FullName)+@(Get-ChildItem -LiteralPath (Join-Path $standalone 'PicoCore') -Filter '*.cs' -File|Sort-Object Name|ForEach-Object FullName)
$browser=$base+@('CableVisualStyle.cs','AnalysisDateText.cs'|ForEach-Object{Join-Path $project ('src/'+$_)})
foreach($part in @('src','vendor/Item','vendor/Calibration','vendor/Calibration/Common','vendor/Oscillation')){$browser+=@(Get-ChildItem -LiteralPath (Join-Path $project ('modules-src/DataBrowser/'+$part)) -Filter '*.cs' -File|Sort-Object Name|ForEach-Object FullName)}
$all=@($cal+$browser+(Join-Path $project 'tests/CalibrationPulseDisplaySmoke.cs')+$PSCommandPath)|Sort-Object -Unique
$before=@($all|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
function Compile([string]$name,[string]$assembly,[string[]]$sources,[string[]]$resources){
 $dir=Join-Path $OutputRoot ('modules/'+$name);$null=New-Item -ItemType Directory -Path $dir -Force
 $dest=Join-Path $dir ($assembly+'.dll');$args2=$flags+@('/out:'+$dest)+@($resources|ForEach-Object{'/resource:'+$_})
 & $compiler @args2 @sources 2>&1|Tee-Object -FilePath (Join-Path $dir 'compile.log')
 if($LASTEXITCODE-ne0){throw ($name+' compile failed')}
}
$joint=(Join-Path $project 'assets/xiongxin.ico')+',XIONGXIN.Brand.ico'
Compile 'CalibrationTest' 'Xiongxin.OWTS.CalibrationTest' $cal @($joint,((Join-Path $project 'modules-src/CalibrationTest/assets/approvedUI.png')+',HVX.CalibrationProgram.ApprovedUi.png'),((Join-Path $project 'modules-src/CalibrationTest/assets/brand.ico')+',HVX.CalibrationProgram.Xiongxin.ico'),((Join-Path $standalone 'assets/xiongxin.ico')+',Brand.ico'),((Join-Path $standalone 'assets/xiongxin-original.png')+',Brand.png'))
Compile 'DataBrowser' 'Xiongxin.OWTS.DataBrowser' $browser @($joint)
$test=Join-Path $OutputRoot 'CalibrationPulseDisplaySmoke.exe'
$testargs=@('/nologo','/langversion:7.3','/target:exe','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll',('/out:'+$test))
& $compiler @testargs (Join-Path $project 'tests/CalibrationPulseDisplaySmoke.cs') 2>&1|Tee-Object -FilePath (Join-Path $OutputRoot 'test-compile.log')
if($LASTEXITCODE-ne0){throw 'test compile failed'}
$qa=Join-Path $OutputRoot 'qa';$null=New-Item -ItemType Directory -Path $qa
$process=Start-Process -FilePath $test -ArgumentList @(('"'+$OutputRoot+'"'),('"'+$qa+'"')) -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $qa 'test.stdout.txt') -RedirectStandardError (Join-Path $qa 'test.stderr.txt')
$exit=$process.ExitCode
Get-Content -LiteralPath (Join-Path $qa 'test.stdout.txt')
Get-Content -LiteralPath (Join-Path $qa 'test.stderr.txt')
$after=@($all|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$unchanged=($before|ConvertTo-Json -Depth 4 -Compress)-ceq($after|ConvertTo-Json -Depth 4 -Compress)
[ordered]@{Schema='CalibrationPulseDisplay.SourceFake.v1';ExitCode=$exit;SourceUnchanged=$unchanged;HardwareCommands=0;OriginalProjectWrites=0;Scope='Calibration and browser local modules only; no product Main';Sources=$after;Runtime=@(Get-ChildItem -LiteralPath $OutputRoot -Recurse -File -Filter '*.dll'|ForEach-Object{[ordered]@{Path=$_.FullName;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $qa 'proof.json') -Encoding UTF8
if($exit-ne0-or-not$unchanged){throw 'Offline pulse display verification failed; proof retained.'}
