param([string]$OutputRoot='',[switch]$CurrentOpticalOnly)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
if(-not $OutputRoot){$OutputRoot=Join-Path $root ('build\qa-factory-optical-current-'+[Guid]::NewGuid().ToString('N').Substring(0,10))}
$qa=[IO.Path]::GetFullPath($OutputRoot)
if(-not $qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $qa)){throw 'Use a fresh isolated project build directory.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Bounded runner parse failed.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $nodes=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($nodes.Count -ne 1){throw 'Missing bounded runner.'};. ([ScriptBlock]::Create($nodes[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$factory=Join-Path $root 'modules-src\FactoryWorker'
$sources=@(Get-ChildItem -LiteralPath $factory -Recurse -File -Filter '*.cs'|Sort-Object FullName|ForEach-Object FullName)
$test=Join-Path $root 'tests\FactoryOpticalCurrentPolicySmoke.cs';$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$gate=Join-Path $PSScriptRoot 'Test-FactoryBatchSource.ps1'
$inputs=@($sources)+@($test,$PSCommandPath,$helper,$gate,(Join-Path $root 'App.config'))+
 @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'compiler') -File|ForEach-Object FullName)+
 @(Get-ChildItem -LiteralPath $factory -File -Filter '*.json'|ForEach-Object FullName)
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o');$identity=& $gate
New-Item -ItemType Directory -Path $qa|Out-Null
$exe=Join-Path $qa 'FactoryOpticalManagedFakesOnly.exe'
$args=@('/nologo','/optimize+','/deterministic+','/langversion:latest','/platform:x64','/target:exe','/main:FactoryOpticalCurrentPolicySmoke',
 '/r:System.dll','/r:System.Core.dll','/r:System.Xml.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',('/out:'+$exe))
$lines=@(& $compiler @args @sources $test 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE
$lines|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($code -ne 0){throw ($lines -join "`n")}
Copy-Item -LiteralPath (Join-Path $root 'App.config') -Destination ($exe+'.config')
$testArguments=if($CurrentOpticalOnly){@('--current-optical-only')}else{@()}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments $testArguments -LogPrefix (Join-Path $qa 'optical') -TimeoutMilliseconds 120000
$same=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$proof=Join-Path $qa 'proof.json'
[ordered]@{Schema='HVX.Factory.OpticalCurrentPolicy.Managed/1';Status=$(if($run.ExitCode -eq 0 -and $same){'PASS'}else{'FAIL'});
 StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Inputs=$before;SourceUnchanged=$same;FactorySourceIdentity=$identity;
 TestExecutableSHA256=(Get-FileHash -LiteralPath $exe).Hash;Execution=$run;
 HardwareAccessed=$false;SDKCalled=$false;WorkerEntryExecuted=$false;ProductEntryExecuted=$false;WindowsShown=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;FullRegression=$false;
 Mode=$(if($CurrentOpticalOnly){'CurrentOpticalAndR30Partial'}else{'LegacyFiveSuiteDiagnostic'});
 ExcludedLegacySuites=$(if($CurrentOpticalOnly){@('FactoryTests','FactoryManualTests','FactoryZeroTriggerTests','FactoryFinalAi4WorkflowTests')}else{@()});
 PreservedLegacyFailureProof='build/qa-factory-optical-current-final-20261011-03/proof.json';
 Scope=$(if($CurrentOpticalOnly){'PARTIAL current-policy evidence ONLY: 25 one-second optical/1500ms archive compatibility/bridge opt-in/default checks plus existing R30 82 managed fake checks. Four legacy residual/AI/confirmation-policy suites are NOT executed in this mode; their FAIL receipt is retained separately, not claimed passed. Existing +1100 R30 injections cover post-hold WAIT; added +700 checks cover the current hold. No actual worker/product/native backend. Fake-clock times are not physical measurements.'}else{'Dedicated pure managed Main invokes existing Factory/Manual/ZeroOptical/FinalAi4/R30 fake suites. One-second hold and historical 1500 read/new-request rejection, bridge desktop opt-in/default false, abort at +700 within current hold. Existing +1100 R30 injections remain post-hold WAIT coverage. Never runs actual worker/product/native backend. Fake-clock durations are not physical timing.'})}|
 ConvertTo-Json -Depth 20|Set-Content -LiteralPath $proof -Encoding UTF8
$run.Output|Write-Output;Write-Output ('FACTORY_OPTICAL_CURRENT_PROOF='+$proof)
if($run.ExitCode -ne 0 -or -not $same){throw ('Factory optical policy managed regression failed; evidence retained: '+$qa)}
