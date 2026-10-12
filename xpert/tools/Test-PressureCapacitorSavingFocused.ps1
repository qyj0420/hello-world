param([string]$DependencyBuild='build\pressure-firsttest-20261010-r43',[string]$AnalysisLibrary='',[string]$AlgorithmLibrary='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$dependency=[IO.Path]::GetFullPath((Join-Path $root $DependencyBuild))
$qa=Join-Path $root ('build\qa-capacitor-save-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Bounded offline runner could not parse.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded regression helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$module=Join-Path $qa 'modules\PressureTest'
$analysis=if($AnalysisLibrary){[IO.Path]::GetFullPath((Join-Path $root $AnalysisLibrary))}else{Join-Path $dependency 'modules\DataAnalysis\HVX.PdReview.V1.0.dll'}
$algorithm=if($AlgorithmLibrary){[IO.Path]::GetFullPath((Join-Path $root $AlgorithmLibrary))}else{Join-Path $dependency 'modules\DataAnalysis\fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'}
$sources=@(Get-ChildItem -LiteralPath 'modules-src\PressureTest\src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$sources+=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','R2RoundedButton.cs','ZeroVoltageNoiseReference.cs','LocationCycleRecord.cs','PressureRecordPublication.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$sources+=Join-Path $root 'modules-src\FactoryWorker\AdjacentHardwareRangePolicy.cs'
$manifestPath=Join-Path $root 'modules-src\Task39InputWorker\provenance\source-manifest.json'
$contract=Join-Path $PSScriptRoot 'Test-PressureTask39Contract.ps1'
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
foreach($name in @('BenchModels.cs','PicoCaptureConfiguration.cs','LocationRangeAdvisor.cs','LocationRangeWindow.cs','RangeDecision.cs')){
 $entry=@($manifest.Files|Where-Object {$_.Path -ceq ('src/'+$name)})
 if($entry.Count -ne 1){throw 'Pinned pure capture contract missing.'}
 $sources+=& $contract -Name $name -OriginalHash $entry[0].SHA256
}
$trial=@('PressureAnalysisTrial.cs','PressureBackgroundAnalysisQueue.cs','ProjectMeasurementMode.cs','PressureRecordPublication.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$test=Join-Path $root 'tests\PressureCapacitorSavingSmoke.cs';$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=@($sources)+$trial+@($test,$csc,$analysis,$algorithm,$contract,$manifestPath,$helper,$PSCommandPath)
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
New-Item -ItemType Directory -Path $module|Out-Null
$common=@('/nologo','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
$dll=Join-Path $module 'Xiongxin.OWTS.PressureTest.dll'
$lines=@(& $csc @common /target:library ('/r:'+$analysis) ('/out:'+$dll) @sources 2>&1|ForEach-Object{$_.ToString()})
$compile=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa 'module.compile.txt') -Encoding UTF8
if($compile -ne 0){throw ($lines -join "`n")}
Copy-Item -LiteralPath $analysis -Destination (Join-Path $module 'HVX.PdReview.V1.0.dll')
Copy-Item -LiteralPath $algorithm -Destination (Join-Path $module 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll')
$trialDll=Join-Path $qa 'PressureTrialContract.dll'
$lines=@(& $csc @common /target:library ('/out:'+$trialDll) @trial 2>&1|ForEach-Object{$_.ToString()})
$trialCompile=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa 'trial.compile.txt') -Encoding UTF8
if($trialCompile -ne 0){throw ($lines -join "`n")}
$exe=Join-Path $qa 'PressureCapacitorSavingSmoke.exe'
$lines=@(& $csc @common /target:exe /main:PressureCapacitorSavingSmoke ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()})
$testCompile=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa 'test.compile.txt') -Encoding UTF8
if($testCompile -ne 0){throw ($lines -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa) -LogPrefix (Join-Path $qa 'PressureCapacitorSavingSmoke') -TimeoutMilliseconds 45000
$same=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$proof=Join-Path $qa 'verification-capacitor-save.json'
[ordered]@{Schema='HVX.CapacitorSave.Focused/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');
 Status=$(if($run.ExitCode -eq 0 -and $same){'PASS'}else{'FAIL'});SourceUnchanged=$same;Inputs=$before;
 CompileExit=$compile;TrialCompileExit=$trialCompile;TestCompileExit=$testCompile;Run=$run;ModuleSHA256=(Get-FileHash -LiteralPath $dll).Hash;TrialSHA256=(Get-FileHash -LiteralPath $trialDll).Hash;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash;
 HardwareAccessed=$false;SDKCalled=$false;ProductExecuted=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;ProtectedPackageVerified=$false;
 Scope='Actual pressure control saving plus production host trial contract, synthetic owned ADC/settings; capacitor DEM-only transaction, raw preserved, Cable pair retained, exact IDs and real save ACK before managed auto-next.'}|
 ConvertTo-Json -Depth 10|Set-Content -LiteralPath $proof -Encoding UTF8
$run.Output|Write-Output;Write-Output ('CAPACITOR_SAVE_PROOF='+$proof)
if($run.ExitCode -ne 0 -or -not $same){throw ('Capacitor save regression failed: '+$qa)}
