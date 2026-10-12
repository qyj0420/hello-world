[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($PSVersionTable.PSEdition-ne'Desktop'){throw 'Use Windows PowerShell 5.1, matching the target .NET Framework CLR.'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$qa=[IO.Path]::GetFullPath($OutputRoot)
if(-not$qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'QA output must remain in the owned project build directory.'}
if(Test-Path -LiteralPath $qa){throw 'Use a new QA directory; existing evidence is never overwritten.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Bounded runner helper could not be parsed.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')) {
 $node=@($ast.FindAll({param($n)$n-is[Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count-ne1){throw 'Bounded runner helper missing.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$sources=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','R2RoundedButton.cs','PressureRecordPublication.cs','CableJointStore.cs','CableJointDialog.cs','CableVisualStyle.cs','LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','CalibrationPulseDisplayWindow.cs','AnalysisDateText.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
foreach($part in @('src','vendor\Item','vendor\Calibration','vendor\Calibration\Common','vendor\Oscillation')){
 $sources+=@(Get-ChildItem -LiteralPath (Join-Path $root ('modules-src\DataBrowser\'+$part)) -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
}
$brand=Join-Path $root 'assets\xiongxin.ico';$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$test=Join-Path $root 'tests\BrowserWaveformPublicationSmoke.cs'
$inputPaths=@((@($sources)+@($PSCommandPath,$helper,$brand,$csc,$test))|Sort-Object -Unique)
$inputs=@($inputPaths|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$started=[DateTime]::UtcNow.ToString('o')
$refs=@('/nologo','/noconfig','/langversion:7.3','/platform:x64','/optimize+','/deterministic+','/debug-','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
$directory=Join-Path $qa 'modules\DataBrowser';New-Item -ItemType Directory -Path $directory|Out-Null
$dll=Join-Path $directory 'Xiongxin.OWTS.DataBrowser.dll'
$compile=@(& $csc @refs /target:library ('/out:'+$dll) ('/resource:'+$brand+',XIONGXIN.Brand.ico') @sources 2>&1|ForEach-Object{$_.ToString()});$compileExit=$LASTEXITCODE
$compile|Set-Content -LiteralPath (Join-Path $qa 'module-compile.log') -Encoding UTF8
if($compileExit-ne0){throw ('Isolated browser compilation failed; log retained: '+($compile-join"`n"))}
$dllSHA=(Get-FileHash -LiteralPath $dll).Hash
$exe=Join-Path $qa 'BrowserWaveformPublicationSmoke.exe'
$compile=@(& $csc @refs /target:exe ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()});$compileExit=$LASTEXITCODE
$compile|Set-Content -LiteralPath (Join-Path $qa 'test-compile.log') -Encoding UTF8
if($compileExit-ne0){throw ('Synthetic fixture compilation failed; log retained: '+($compile-join"`n"))}
$testSHA=(Get-FileHash -LiteralPath $exe).Hash
$execution=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa,(Join-Path $qa 'synthetic')) -LogPrefix (Join-Path $qa 'browser-loading') -TimeoutMilliseconds 60000
$execution.Output|Write-Output
$mismatches=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash-cne$_.SHA256})
$runtimeUnchanged=(Get-FileHash -LiteralPath $dll).Hash-ceq$dllSHA-and(Get-FileHash -LiteralPath $exe).Hash-ceq$testSHA
$passed=$execution.ExitCode-eq0-and$mismatches.Count-eq0-and$runtimeUnchanged
[ordered]@{Schema='HVX.BrowserWaveformLoading.Offline/1';Status=$(if($passed){'PASS'}else{'FAIL'});StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Inputs=$inputs;InputMismatchCount=$mismatches.Count;SourceUnchanged=($mismatches.Count-eq0);Module=$dll;ModuleSHA256=$dllSHA;RuntimeUnchanged=$runtimeUnchanged;TestSourceSHA256=(Get-FileHash -LiteralPath $test).Hash;TestExe=$exe;TestExeSHA256=$testSHA;Execution=$execution;ControlsConstructed=$true;WindowsShown=$true;SyntheticOffscreenWindow=$true;ProductEntryExecuted=$false;AlgorithmInvoked=$false;WorkerEntryExecuted=$false;ProfilesRead=$false;SDKCalled=$false;HardwareAccessed=$false;UserProjectWritten=$false;SameFinalProductBytes=$false;Scope='Existing asynchronous browser reader with immediate detail feedback; synthetic LOC/CAL success, failure, retry, cancellation, stale generation, language, navigation and dispose. Not final protected product bytes or physical acceptance.'}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'proof.json') -Encoding UTF8
Write-Output ('BROWSER_LOADING_PROOF='+$qa+'\proof.json')
if(-not$passed){throw 'Browser loading regression failed or inputs drifted; evidence retained.'}
