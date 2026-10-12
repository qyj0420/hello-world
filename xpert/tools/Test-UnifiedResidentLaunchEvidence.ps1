param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$qa=Join-Path $root ('build\qa-resident-launch-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
New-Item -ItemType Directory -Path $qa|Out-Null
$sources=@('modules-src\FactoryWorker\UnifiedWorkerLaunchEvidence.cs','tests\UnifiedResidentLaunchEvidenceSmoke.cs'|ForEach-Object{Join-Path $root $_})
$inputs=$sources+@(Join-Path $root 'modules-src\FactoryWorker\FactoryResidentDevices.cs')+@(Join-Path $root 'modules-src\FactoryWorker\UnifiedWorkerEntry.cs')
function Inventory {@($inputs|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
$exe=Join-Path $qa 'UnifiedResidentLaunchEvidenceSmoke.exe'
$output=@(& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /target:exe /langversion:7.3 /r:System.dll /r:System.Core.dll /r:System.Xml.dll ('/out:'+$exe) @sources 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$output|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($code -ne 0){throw ($output -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa,$root) -LogPrefix (Join-Path $qa 'result') -TimeoutMilliseconds 30000
$unchanged=(($before|ConvertTo-Json -Compress) -ceq (@(Inventory)|ConvertTo-Json -Compress))
[ordered]@{Schema='HVX.ResidentLaunchProof.Offline/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');HardwareAccessed=$false;NativeCalls=$false;ProductStarted=$false;WorkersStarted=0;SourceUnchanged=$unchanged;FullRegression=$false;Inputs=$before;Tests=@([ordered]@{Name='UnifiedResidentLaunchEvidenceSmoke';ExitCode=$run.ExitCode;Output=$run.Output;TimedOut=$run.TimedOut;ProcessExitConfirmed=$run.ProcessExitConfirmed;Milliseconds=$run.ElapsedMilliseconds})}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
$run.Output|Write-Output
if($run.ExitCode -ne 0 -or -not $unchanged){throw ('Resident launch proof offline regression failed: '+$qa)}
Write-Output ('RESIDENT_LAUNCH_PROOF='+$qa)
