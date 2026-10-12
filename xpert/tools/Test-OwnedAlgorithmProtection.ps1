[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ProtectionDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$directory=[IO.Path]::GetFullPath($ProtectionDirectory).TrimEnd('\')
if(-not $directory.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Only isolated project build receipts may be tested.'}
$receipt=Get-Content -LiteralPath (Join-Path $directory 'receipt.json') -Raw|ConvertFrom-Json
if($receipt.Schema -cne 'HVX.OwnedAlgorithmProtection/1' -or $receipt.HardwareAccessed -ne $false){throw 'Wrong receipt boundary.'}
$assemblyPaths=@{Input=(Join-Path $directory ('input\'+[IO.Path]::GetFileName($receipt.Input)));Output=$receipt.Output}
# Staging replaces receipt.Input after conversion. Compare the immutable local
# snapshot against InputSHA256, not the already-protected staged consumer.
foreach($side in @('Input','Output')){if((Get-FileHash -LiteralPath $assemblyPaths[$side] -Algorithm SHA256).Hash -cne $receipt.($side+'SHA256')){throw 'Input snapshot/output changed after protection.'}}
$probe=Join-Path $directory 'OwnedAlgorithmBodyProbe.exe'
& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /target:exe /platform:x64 /r:System.dll /r:System.Core.dll ('/out:'+$probe) (Join-Path $root 'tests\OwnedAlgorithmBodyProbe.cs')
if($LASTEXITCODE -ne 0){throw 'Pure probe compile failed.'}
$results=@()
$helperTokens=$null;$helperErrors=$null
$helperAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$helperTokens,[ref]$helperErrors)
if($helperErrors.Count){throw 'Cannot parse bounded execution helpers.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
    $nodes=@($helperAst.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
    if($nodes.Count -ne 1){throw 'Missing/ambiguous bounded execution helper.'}
    . ([ScriptBlock]::Create($nodes[0].Extent.Text))
}
foreach($side in @('Input','Output')){
    $run=Invoke-RegressionExecutable -Executable $probe -Arguments @($assemblyPaths[$side]) -LogPrefix (Join-Path $directory ('numeric-'+$side)) -TimeoutMilliseconds 45000
    $stdout=$run.StdoutPath;$stderr=$run.StderrPath
    if($run.ExitCode -ne 0 -or $run.TimedOut -or -not $run.ProcessExitConfirmed -or $run.ExecutionFailure){throw ('Pure comparison failed: '+$side+'; '+(Get-Content -LiteralPath $stderr -Raw))}
    $text=Get-Content -LiteralPath $stdout -Raw
    $matches=[regex]::Match($text,'CHECKS=(\d+); HARDWARE=False; PRODUCT_ENTRY=False; MODEL_INFERENCE=False')
    if(-not $matches.Success){throw 'Probe boundary/check count missing.'}
    $results+=@{Side=$side;Checks=[int]$matches.Groups[1].Value;OutputSHA256=(Get-FileHash -LiteralPath $stdout).Hash}
}
$same=$results[0].OutputSHA256 -ceq $results[1].OutputSHA256
[ordered]@{Schema='HVX.OwnedAlgorithmBodyRegression/1';Profile=$receipt.Profile;InputSHA256=$receipt.InputSHA256;ProtectedSHA256=$receipt.OutputSHA256;ExactNumericOutputsEqual=$same;Cases=$results;HardwareAccessed=$false;ProductEntryExecuted=$false;ModelInference=$false;FullCompatibilityAccepted=$false;Scope='Synthetic shared filters, calibration averaging/quality/timing, range diagnostics; not the complete application.'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $directory 'numeric-verification.json') -Encoding UTF8
if(-not $same){throw 'Protected numerical outputs differ.'}
Write-Output ('PURE_BODY_PARITY_PASS='+$receipt.Profile+';CHECKS='+$results[1].Checks)
