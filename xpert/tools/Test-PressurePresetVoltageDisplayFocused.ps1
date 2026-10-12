param([string]$DependencyBuild='build\main-test-20261009-r38b',[string]$AlgorithmDependency='',
 [ValidateSet('PresetVoltageDisplay','CustomVoltageMinimum')][string]$Regression='PresetVoltageDisplay')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$dependency=[IO.Path]::GetFullPath((Join-Path $root $DependencyBuild))
$qa=Join-Path $root ('build\qa-pressure-'+$Regression.ToLowerInvariant()+'-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$module=Join-Path $qa 'modules\PressureTest'
$analysis=Join-Path $dependency 'modules\PressureTest\HVX.PdReview.V1.0.dll'
$algorithm=Join-Path $dependency 'modules\PressureTest\fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'
if(-not [String]::IsNullOrWhiteSpace($AlgorithmDependency)){$algorithm=[IO.Path]::GetFullPath($AlgorithmDependency)}
Assert-RegressionUnlinkedPath $analysis;Assert-RegressionUnlinkedPath $algorithm
$sources=@(Get-ChildItem -LiteralPath 'modules-src\PressureTest\src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$sources+=Join-Path $root 'modules-src\FactoryWorker\AdjacentHardwareRangePolicy.cs'
$sources+=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','ZeroVoltageNoiseReference.cs','LocationCycleRecord.cs','PressureRecordPublication.cs','R2RoundedButton.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$contract=Join-Path $PSScriptRoot 'Test-PressureTask39Contract.ps1'
$manifestPath=Join-Path $root 'modules-src\Task39InputWorker\provenance\source-manifest.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
foreach($name in @('BenchModels.cs','PicoCaptureConfiguration.cs','LocationRangeAdvisor.cs','LocationRangeWindow.cs','RangeDecision.cs')){
 $entry=@($manifest.Files|Where-Object {$_.Path -ceq ('src/'+$name)})
 if($entry.Count -ne 1){throw 'Pinned pure input contract missing.'}
 $sources+=& $contract -Name $name -OriginalHash $entry[0].SHA256
}
$entryPoint='Pressure'+$Regression+'Smoke'
$test=Join-Path $root ('tests\'+$entryPoint+'.cs')
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=@($sources)+@($analysis,$algorithm,$test,$PSCommandPath,$helper,$contract,$manifestPath,$csc)
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
New-Item -ItemType Directory -Path $module|Out-Null
$common=@('/nologo','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
$dll=Join-Path $module 'Xiongxin.OWTS.PressureTest.dll'
$compiled=@(& $csc @common /target:library ('/r:'+$analysis) ('/out:'+$dll) @sources 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'module.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
Copy-Item -LiteralPath $analysis -Destination (Join-Path $module 'HVX.PdReview.V1.0.dll')
Copy-Item -LiteralPath $algorithm -Destination (Join-Path $module 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll')
$exe=Join-Path $qa ($entryPoint+'.exe')
$compiled=@(& $csc @common /target:exe ('/main:'+$entryPoint) ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'test.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa) -LogPrefix (Join-Path $qa $entryPoint) -TimeoutMilliseconds 30000
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
[ordered]@{Schema=$(if($Regression -eq 'PresetVoltageDisplay'){'HVX.Pressure.PresetVoltage.Focused/1'}else{'HVX.Pressure.CustomVoltageMinimum.Focused/1'});StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');
 Status=$(if($run.ExitCode -eq 0 -and $unchanged){'PASS'}else{'FAIL'});HardwareAccessed=$false;SDKCalled=$false;ProductEntryExecuted=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;
 SourceUnchanged=$unchanged;DependencyBuild=$dependency;FullRegression=$false;Inputs=$before;ModuleSHA256=(Get-FileHash -LiteralPath $dll).Hash;
 TestSHA256=(Get-FileHash -LiteralPath $exe).Hash;Execution=$run;
 Superseded=$(if($Regression -eq 'PresetVoltageDisplay'){'Only historical primary-button live voltage/title assertions; feedback/result/permission contracts remain.'}else{'None; positive custom target floor only. Explicit zero-reference and real stop outputs remain unchanged.'})}|
 ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa $(if($Regression -eq 'PresetVoltageDisplay'){'verification-preset-voltage.json'}else{'verification-custom-voltage-minimum.json'})) -Encoding UTF8
$run.Output|Write-Output
if($Regression -eq 'PresetVoltageDisplay'){Write-Output ('PRESET_VOLTAGE_PROOF='+(Join-Path $qa 'verification-preset-voltage.json'))}
else{Write-Output ('CUSTOM_VOLTAGE_MINIMUM_PROOF='+(Join-Path $qa 'verification-custom-voltage-minimum.json'))}
if($run.ExitCode -ne 0 -or -not $unchanged){throw ('Preset voltage focused regression failed: '+$qa)}
