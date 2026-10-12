[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$BuildRoot,[Parameter(Mandatory=$true)][string]$PriorAudit)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$build=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
$priorPath=[IO.Path]::GetFullPath($PriorAudit)
if(-not $build.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or -not $priorPath.StartsWith($build+'\qa\',[StringComparison]::OrdinalIgnoreCase)){throw 'Only final-build QA evidence may be retried.'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded execution helpers.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
    $nodes=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
    if($nodes.Count -ne 1){throw 'Missing/ambiguous bounded execution helper.'};. ([ScriptBlock]::Create($nodes[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $build;Assert-RegressionUnlinkedPath $priorPath
$priorHash=(Get-FileHash -LiteralPath $priorPath).Hash
$prior=Get-Content -LiteralPath $priorPath -Raw -Encoding UTF8|ConvertFrom-Json
if($prior.Schema -cne 'HVX.FinalProtectionAudit/1' -or $prior.BuildRoot -cne $build -or $prior.Status -cne 'FAILED' -or
   $prior.Failure -cne 'EmbeddedModulePayloadSmoke failed; original diagnostics retained.' -or $prior.RuntimeUnchanged -ne $true -or
   $prior.HardwareAccessed -ne $false -or $prior.ProductEntryExecuted -ne $false){throw 'Retry requires the original cache-fixture failure.'}
$rows=@($prior.Steps|Where-Object Name -ceq 'EmbeddedModulePayloadSmoke')
$exe=Join-Path ([IO.Path]::GetDirectoryName($priorPath)) 'EmbeddedModulePayloadSmoke.exe'
if($rows.Count -ne 1 -or (Get-FileHash -LiteralPath $exe).Hash -cne $rows[0].TestSHA256){throw 'Retry executable differs from the failed fixture.'}
foreach($entry in $prior.Runtime){$p=Join-Path $build $entry.Path;Assert-RegressionUnlinkedPath $p;if((Get-FileHash -LiteralPath $p).Hash -cne $entry.SHA256){throw 'Final runtime changed before cache retry.'}}
$qa=Join-Path $build ('qa/cache-fixture-retry-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
New-Item -ItemType Directory -Path $qa|Out-Null
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($build,(Join-Path $qa 'fixture')) -LogPrefix (Join-Path $qa 'cache-retry')
if($run.ExitCode -ne 0 -or $run.TimedOut -or -not $run.ProcessExitConfirmed -or $run.ExecutionFailure){throw 'Exact-byte cache retry failed. Retain all original diagnostics.'}
$match=[regex]::Match(($run.Output -join "`n"),'EMBEDDED_MODULE_PAYLOAD_PASS=(\d+)')
if(-not $match.Success -or [int]$match.Groups[1].Value -le 0){throw 'Cache check count missing.'}
foreach($entry in $prior.Runtime){if((Get-FileHash -LiteralPath (Join-Path $build $entry.Path)).Hash -cne $entry.SHA256){throw 'Final runtime changed during cache retry.'}}
if((Get-FileHash -LiteralPath $priorPath).Hash -cne $priorHash){throw 'Original failure evidence changed.'}
[ordered]@{Schema='HVX.FinalCacheFixtureRetry/1';BuildRoot=$build;CompletedUtc=[DateTime]::UtcNow.ToString('o');Status='PASS';Checks=[int]$match.Groups[1].Value;
    PriorAudit=$priorPath;PriorAuditSHA256=$priorHash;OriginalFailurePreserved=$true;ExactTestSHA256=$rows[0].TestSHA256;Execution=$run;
    RuntimeUnchanged=$true;HardwareAccessed=$false;ProductEntryExecuted=$false;WorkerStarted=$false;ActualUserCacheWritten=$false;
    Limitation='Elevated sandbox filesystem permission for test-owned hardlink/junction creation only. Symlink privilege may remain unavailable; equivalent junction rejection is tested. No product entry or hardware execution.'}|
    ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
Write-Output ('FINAL_CACHE_RETRY_PROOF='+(Join-Path $qa 'verification.json'))
