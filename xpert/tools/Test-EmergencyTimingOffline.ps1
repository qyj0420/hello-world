param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root=$project
Set-Location -LiteralPath $project
$qa=Join-Path $project ('build\emergency-timing-offline-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$tokens=$null;$errors=$null
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded test runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
New-Item -ItemType Directory -Path $qa | Out-Null
$sources=@('FactoryEmergencyReadPolicy.cs','FactoryEmergencyMonitor.cs','FactoryIdleDiRebuildPolicy.cs','FactoryEmergencyTimingContractTests.cs')|
 ForEach-Object{Join-Path $project ('modules-src\FactoryWorker\'+$_)}
$sources+=Join-Path $project 'tests\FactoryEmergencyTimingSmoke.cs'
$inputs=@($sources)+@($helper,$PSCommandPath,(Join-Path $PSScriptRoot 'compiler\csc.exe'))
$before=@($inputs|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$exe=Join-Path $qa 'FactoryEmergencyTimingSmoke.exe'
$compile=@(& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /target:exe /platform:x64 /langversion:7.3 /main:FactoryEmergencyTimingSmoke /r:System.dll /r:System.Core.dll ('/out:'+$exe) @sources 2>&1|ForEach-Object{$_.ToString()})
$compileExit=$LASTEXITCODE
$compile|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($compileExit -ne 0){throw ($compile -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @() -LogPrefix (Join-Path $qa 'emergency-timing') -TimeoutMilliseconds 30000
$after=@($inputs|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$same=($before|ConvertTo-Json -Compress) -ceq ($after|ConvertTo-Json -Compress)
[ordered]@{Schema='HVX.EmergencyTiming.Offline/1';HardwareAccessed=$false;ProductEntryExecuted=$false;PhysicalAcceptance=$false;
 Status=$(if($run.ExitCode -eq 0 -and $same){'PASS'}else{'FAIL'});SourceUnchanged=$same;Inputs=$before;
 TestSHA256=(Get-FileHash -LiteralPath $exe).Hash;Execution=$run;
 Limitations='Pure managed fake DI. Does not verify real Windows/USB response, driver cancellation, electrical shutdown or physical reconnect.'}|
 ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
$run.Output|Write-Output
Write-Output ('EMERGENCY_TIMING_OFFLINE_PROOF='+$qa)
if($run.ExitCode -ne 0 -or -not $same){throw 'Emergency timing fake test failed or sources changed.'}
