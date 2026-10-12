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
$baseSources=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','R2RoundedButton.cs','PressureRecordPublication.cs','CableJointStore.cs','CableJointDialog.cs','CableVisualStyle.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$creationSources=@($baseSources)+@(Get-ChildItem -LiteralPath (Join-Path $root 'modules-src\ProjectCreation\src') -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$browserSources=@($baseSources)+@('LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','CalibrationPulseDisplayWindow.cs','AnalysisDateText.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
foreach($part in @('src','vendor\Item','vendor\Calibration','vendor\Calibration\Common','vendor\Oscillation')){
 $browserSources+=@(Get-ChildItem -LiteralPath (Join-Path $root ('modules-src\DataBrowser\'+$part)) -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
}
$brand=Join-Path $root 'assets\xiongxin.ico';$capacitor=Join-Path $root 'assets\xiongxin-capacitor-hd.png';$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$test=Join-Path $root 'tests\ProjectBrowserCustomerFeedbackOfflineSmoke.cs'
$inputPaths=@((@($creationSources)+@($browserSources)+@($PSCommandPath,$helper,$brand,$capacitor,$csc,$test))|Sort-Object -Unique)
$inputs=@($inputPaths|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$started=[DateTime]::UtcNow.ToString('o');$runtime=New-Object System.Collections.Generic.List[object]
$refs=@('/nologo','/noconfig','/langversion:7.3','/platform:x64','/optimize+','/deterministic+','/debug-','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
foreach($module in @('ProjectCreation','DataBrowser')) {
 $directory=Join-Path $qa ('modules\'+$module);New-Item -ItemType Directory -Path $directory|Out-Null
 $dll=Join-Path $directory ('Xiongxin.OWTS.'+$module+'.dll')
 $sources=$(if($module-eq'ProjectCreation'){$creationSources}else{$browserSources});$resources=@('/resource:'+$brand+',XIONGXIN.Brand.ico')
 if($module-eq'ProjectCreation'){$resources+=('/resource:'+$capacitor+',XIONGXIN.Capacitor.png')}
 $compile=@(& $csc @refs /target:library ('/out:'+$dll) @resources @sources 2>&1|ForEach-Object{$_.ToString()});$compileExit=$LASTEXITCODE
 $compile|Set-Content -LiteralPath (Join-Path $directory 'compile.log') -Encoding UTF8
 if($compileExit-ne0){throw ($module+' isolated compilation failed; log retained: '+($compile-join"`n"))}
 $runtime.Add([ordered]@{Module=$module;Path=$dll;SHA256=(Get-FileHash -LiteralPath $dll).Hash})
}
$exe=Join-Path $qa 'ProjectBrowserCustomerFeedbackOfflineSmoke.exe'
$compile=@(& $csc @refs /target:exe ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()});$compileExit=$LASTEXITCODE
$compile|Set-Content -LiteralPath (Join-Path $qa 'test-compile.log') -Encoding UTF8
if($compileExit-ne0){throw ('Presentation fixture compilation failed; log retained: '+($compile-join"`n"))}
$testSHA=(Get-FileHash -LiteralPath $exe).Hash
$execution=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa,$root) -LogPrefix (Join-Path $qa 'customer-feedback') -TimeoutMilliseconds 60000
$execution.Output|Write-Output
$mismatches=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash-cne$_.SHA256})
$runtimeUnchanged=@($runtime|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash-cne$_.SHA256}).Count-eq0-and(Get-FileHash -LiteralPath $exe).Hash-ceq$testSHA
$passed=$execution.ExitCode-eq0-and$mismatches.Count-eq0-and$runtimeUnchanged
[ordered]@{Schema='HVX.ProjectBrowserCustomerFeedback.Offline/1';Status=$(if($passed){'PASS'}else{'FAIL'});StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Inputs=$inputs;InputMismatchCount=$mismatches.Count;SourceUnchanged=($mismatches.Count-eq0);Runtime=$runtime.ToArray();RuntimeUnchanged=$runtimeUnchanged;Tests=@([ordered]@{Name='ProjectBrowserCustomerFeedbackOfflineSmoke';TestSourceSHA256=(Get-FileHash -LiteralPath $test).Hash;Exe=$exe;ExeSHA256=$testSHA;Execution=$execution});ControlsConstructed=$false;WindowsShown=$false;ProductEntryExecuted=$false;AlgorithmInvoked=$false;WorkerEntryExecuted=$false;ProfilesRead=$false;SDKCalled=$false;HardwareAccessed=$false;UserProjectWritten=$false;SameFinalProductBytes=$false;Scope='Short customer presentation and retained diagnostic sinks; source-bound failure/success and explicit joint-decision wiring. Isolated module builds, not a final protected EXE or real workflow acceptance.'}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'proof.json') -Encoding UTF8
Write-Output ('PROJECT_BROWSER_CUSTOMER_FEEDBACK_PROOF='+$qa+'\proof.json')
if(-not$passed){throw 'Customer feedback regression failed or inputs drifted; evidence retained.'}
