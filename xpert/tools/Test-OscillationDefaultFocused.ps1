param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$qa=[IO.Path]::GetFullPath($OutputRoot)
if(!$qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $qa)){throw 'Use a new isolated project build directory.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Invalid bounded runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$runtime=@(Get-ChildItem -LiteralPath 'modules-src\FactoryWorker' -Recurse -Filter '*.cs' -File|Sort-Object FullName|ForEach-Object FullName)
$phase=Join-Path $root 'modules-src\FactoryWorker\FactoryRangePhase.cs'
$tests=@('FactoryOscillationDefaultSmoke','FactoryRangePhaseReasonSmoke')
$hostFiles=@('src\AppSettings.cs','src\SettingsForm.PressurePreferences.cs','src\ModuleHost.FactoryPressure.cs')|ForEach-Object{Join-Path $root $_}
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=@($runtime)+@($hostFiles)+@($tests|ForEach-Object{Join-Path $root ('tests\'+$_+'.cs')})+@($helper,$csc,$PSCommandPath)
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=[IO.Path]::GetFullPath($_);SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
New-Item -ItemType Directory -Path $qa|Out-Null
$common=@('/nologo','/langversion:latest','/platform:x64','/optimize+','/deterministic+','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll')
$runs=New-Object 'System.Collections.Generic.List[object]'
foreach($name in $tests){
 $sources=@(Join-Path $root ('tests\'+$name+'.cs'))
 if($name -eq 'FactoryOscillationDefaultSmoke'){$sources+=@($runtime)}else{$sources+=@($phase)}
 $exe=Join-Path $qa ($name+'.exe')
 $compile=@(& $csc @common /target:exe ('/main:'+$name) ('/out:'+$exe) @sources 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE
 $compile|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ($compile -join "`n")}
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa) -LogPrefix (Join-Path $qa $name) -TimeoutMilliseconds 120000
 $runs.Add([ordered]@{Name=$name;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash;Execution=$run});$run.Output|Write-Output
}
$hostSource=[IO.File]::ReadAllText($hostFiles[2]);$settings=[IO.File]::ReadAllText($hostFiles[1]);$app=[IO.File]::ReadAllText($hostFiles[0])
$static=@(
 [ordered]@{Name='Legacy setting defaults to diagnostic enabled';Pass=$app.Contains('PressureOscillationCircuitConfirmed = true;')},
 [ordered]@{Name='Customer checkbox removed';Pass=(!$settings.Contains('pressureCircuitConfirmed') -and !$settings.Contains('已核对振荡回路连接'))},
 [ordered]@{Name='Host enables diagnostic regardless of old false';Pass=$hostSource.Contains('set("OscillationCircuitConnected",true);')},
 [ordered]@{Name='Obsolete preference is not a session validity gate';Pass=(!$hostSource.Contains('PressureOscillationCircuitConfirmed!=app.PressureOscillationCircuitConfirmed'))}
)
$same=($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress)
$passed=@($runs|Where-Object {$_.Execution.ExitCode -ne 0}).Count -eq 0 -and @($static|Where-Object {!$_.Pass}).Count -eq 0
$proof=Join-Path $qa 'proof.json'
[ordered]@{Schema='HVX.Factory.OscillationDefault.Focused/1';Status=$(if($same -and $passed){'PASS'}else{'FAIL'});StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');
 SourceUnchanged=$same;Inputs=$before;Runs=$runs.ToArray();HostStaticChecks=$static;HardwareAccessed=$false;SDKCalled=$false;WorkersStarted=0;ProductStarted=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;FullRegression=$false;
 Scope='Only named managed synthetic phase/window/metrics/sequence fixtures execute. Old false metadata remains unchanged; genuine raw/timebase/window rejection and separate physical safety assertions retained. Host checkbox removal is a source contract check; final host compilation and field hardware remain separate.'}|
 ConvertTo-Json -Depth 15|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output ('OSCILLATION_DEFAULT_PROOF='+$proof)
if(!$same -or !$passed){throw 'Oscillation default focused regression failed; evidence retained.'}
