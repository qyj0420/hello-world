param([string]$OutputRoot='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
if(-not $OutputRoot){$OutputRoot=Join-Path $root ('build\qa-pressure-desktop-dataflow-'+[Guid]::NewGuid().ToString('N').Substring(0,10))}
$qa=[IO.Path]::GetFullPath($OutputRoot)
if(-not $qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $qa)){throw 'Use a fresh isolated project build directory.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Bounded runner helper parse failed.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$factory=@(Get-ChildItem -LiteralPath (Join-Path $root 'modules-src\FactoryWorker') -Recurse -File -Filter '*.cs'|Sort-Object FullName|ForEach-Object FullName)
$pair=Join-Path $root 'modules-src\PressureTest\src\PressureRoundPairPreparation.cs'
$test=Join-Path $root 'tests\PressureDesktopDataflowSmoke.cs'
$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=@($factory)+@($pair,$test,$PSCommandPath,$helper,$compiler,(Join-Path $root 'App.config'),
 (Join-Path $root 'modules-src\PressureTest\src\PressureRoundRecordSaving.cs'),
 (Join-Path $root 'modules-src\PressureTest\src\PressureTestControl.ZeroVoltageHardware.cs'))
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
New-Item -ItemType Directory -Path $qa|Out-Null
$exe=Join-Path $qa 'PressureDesktopDataflowOffline.exe'
$common=@('/nologo','/optimize+','/deterministic+','/langversion:latest','/platform:x64','/target:exe','/main:PressureDesktopDataflowSmoke',
 '/r:System.dll','/r:System.Core.dll','/r:System.Xml.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',('/out:'+$exe))
$lines=@(& $compiler @common @factory $pair $test 2>&1|ForEach-Object{$_.ToString()})
$compile=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($compile -ne 0){throw ($lines -join "`n")}
Copy-Item -LiteralPath (Join-Path $root 'App.config') -Destination ($exe+'.config')
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa) -LogPrefix (Join-Path $qa 'dataflow') -TimeoutMilliseconds 60000
$same=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$proof=Join-Path $qa 'proof.json'
[ordered]@{Schema='HVX.Pressure.DesktopDataflow.Focused/1';Status=$(if($run.ExitCode -eq 0 -and $same){'PASS'}else{'FAIL'});
 StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Inputs=$before;SourceUnchanged=$same;CompileExit=$compile;Execution=$run;
 TestExecutableSHA256=(Get-FileHash -LiteralPath $exe).Hash;HardwareAccessed=$false;SDKCalled=$false;WorkerEntryExecuted=$false;ProductEntryExecuted=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;FullRegression=$false;
 Scope='Exact FactoryRunner/zero runner with managed fakes, explicit default/deferred metric behavior, original ADC/range quality, actual fake stop/error handling; derived LOC/DEM task overlap/drain/false/fault/cancel join. Pressure UI glue is source-bound but needs separate managed UI regression; no promised field latency or physical acceptance.'}|
 ConvertTo-Json -Depth 15|Set-Content -LiteralPath $proof -Encoding UTF8
$run.Output|Write-Output;Write-Output ('PRESSURE_DESKTOP_DATAFLOW_PROOF='+$proof)
if($run.ExitCode -ne 0 -or -not $same){throw ('Desktop dataflow regression failed; evidence retained: '+$qa)}
