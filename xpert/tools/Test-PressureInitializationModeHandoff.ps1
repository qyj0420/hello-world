param([string]$OutputRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if(-not $OutputRoot){$OutputRoot=Join-Path $root ('build/r50-pressure-init-handoff-'+[Guid]::NewGuid().ToString('N').Substring(0,10))}
$output=[IO.Path]::GetFullPath($OutputRoot)
if(-not $output.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Use a new isolated project build directory.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Invalid bounded runner helper.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $output -AllowMissing
$workerFiles=@(Get-ChildItem -LiteralPath (Join-Path $root 'modules-src/FactoryWorker') -Recurse -File -Filter '*.cs'|Sort-Object FullName)
$hostFiles=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -File -Filter '*.cs'|Sort-Object FullName)
$test=Join-Path $root 'tests/PressureInitializationModeHandoffSmoke.cs'
$fake=Join-Path $root 'tests/PressureInitializationResidentFakeClient.cs'
$compiler=Join-Path $PSScriptRoot 'compiler/csc.exe'
$all=@($workerFiles.FullName)+@($hostFiles.FullName)+@($test,$fake,$compiler,$helper,$PSCommandPath)+@(Get-ChildItem -LiteralPath (Split-Path $compiler) -File|Where-Object Extension -in @('.dll','.config','.rsp')|ForEach-Object FullName)
$inputs=@($all|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
New-Item -ItemType Directory -Path $output|Out-Null
# Mechanical bounded extraction replaces ONLY the process/native client class.
# The production resident gate plus all trailing outcome/receipt types remain.
$resident=Join-Path $root 'modules-src/FactoryWorker/FactoryResidentDevices.cs'
$source=Get-Content -LiteralPath $resident -Raw -Encoding UTF8
$start=$source.IndexOf('    internal sealed class FactoryResidentDeviceClient')
$end=$source.IndexOf('    // This exception changes diagnostic facts only.',$start)
if($start -lt 0 -or $end -le $start -or $source.IndexOf('    internal sealed class FactoryResidentDeviceClient',$start+1) -ge 0){throw 'Resident process boundary changed; review extraction.'}
$managed=Join-Path $output 'FactoryResidentDevices.ManagedGate.cs'
[IO.File]::WriteAllText($managed,$source.Substring(0,$start)+$source.Substring($end),(New-Object Text.UTF8Encoding($false)))
$workerSources=@($workerFiles|Where-Object FullName -cne $resident|ForEach-Object FullName)+@($managed,$fake)
$worker=Join-Path $output 'Initialization.FakeProcessBoundary.dll'
$exe=Join-Path $output 'Initialization.Handoff.ManagedTests.exe'
$common=@('/nologo','/noconfig','/target:library','/langversion:latest','/platform:x64','/optimize+','/deterministic+',
 '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll',
 '/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
$lines=@(& $compiler @common ('/out:'+$worker) @workerSources 2>&1|ForEach-Object{$_.ToString()});$workerExit=$LASTEXITCODE
$lines|Set-Content -LiteralPath (Join-Path $output 'worker.compile.txt') -Encoding UTF8
if($workerExit -ne 0){throw ($lines -join "`n")}
$lines=@(& $compiler @common /target:exe /main:PressureInitializationModeHandoffSmoke ('/out:'+$exe) @($hostFiles.FullName) $test 2>&1|ForEach-Object{$_.ToString()});$compile=$LASTEXITCODE
$lines|Set-Content -LiteralPath (Join-Path $output 'host.compile.txt') -Encoding UTF8
if($compile -ne 0){throw ($lines -join "`n")}
foreach($generated in @($managed,$worker,$exe)){$inputs+=[ordered]@{Path=$generated;SHA256=(Get-FileHash -LiteralPath $generated).Hash}}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @((Join-Path $output 'fixtures'),$worker) -LogPrefix (Join-Path $output 'test') -TimeoutMilliseconds 45000
$mismatches=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count
$passed=($run.Output -join "`n") -match 'PRESSURE_INITIALIZATION_HANDOFF_PASS=(\d+);ProductionBridge=True;ProductionResidentGate=True;ProductionModePolicy=True;'
$checks=if($passed){[int]$Matches[1]}else{0}
$proof=Join-Path $output 'verification.json'
[ordered]@{Schema='HVX.PressureInitialization.ModeHandoff.Offline/1';UTC=[DateTime]::UtcNow.ToString('o');Checks=$checks;Inputs=$inputs;InputMismatchCount=$mismatches;CompileExit=$compile;WorkerCompileExit=$workerExit;Result=$run;OutputVerified=$passed;
 ProductionBridgeAndRouting=$true;ProductionResidentGateAndPolicy=$true;FakeBoundary='FactoryResidentDeviceClient only, process/native boundary replaced';ProductStarted=$false;SDKCalled=$false;ActualWorkerStarted=$false;HardwareAccessed=$false;OutputTasksCreated=0;PhysicalAcceptance=$false}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $proof -Encoding UTF8
$run.Output|Write-Output;Write-Output ('PRESSURE_INITIALIZATION_HANDOFF_PROOF='+$proof)
if($run.ExitCode -ne 0 -or -not $passed -or $mismatches -ne 0){throw 'Initialization production-mode fake-boundary regression failed.'}
