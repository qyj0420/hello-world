param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-disconnect-targeted-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')) {
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
New-Item -ItemType Directory -Path $qa | Out-Null
$inputs=@('src/HardwareStatusAdapter.cs','modules-src/HardwareConnector/Integration.cs','modules-src/HardwareConnector/Integration.Discovery.cs','tests/HardwareDisconnectSmoke.cs','tests/HardwareArchitectureDiscoverySmoke.cs')
function Inventory { @($inputs|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath (Join-Path $root $_)).Hash}}) }
$before=@(Inventory);$results=@();$started=[DateTime]::UtcNow.ToString('o')
foreach($name in @('HardwareDisconnectSmoke','HardwareArchitectureDiscoverySmoke')) {
 $sources=@(('tests/'+$name+'.cs'),'modules-src/HardwareConnector/Integration.cs','modules-src/HardwareConnector/Integration.Discovery.cs')
 if($name -eq 'HardwareDisconnectSmoke'){$sources+='src/HardwareStatusAdapter.cs'}
 $exe=Join-Path $qa ($name+'.exe')
 $compile=@(& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /target:exe /platform:x64 /langversion:7.3 /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Xml.Linq.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ('/main:'+$name) ('/out:'+$exe) @sources 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$compile|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ($compile -join "`n")}
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa) -LogPrefix (Join-Path $qa $name) -TimeoutMilliseconds 60000
 $results+=@([ordered]@{Name=$name;ExitCode=$run.ExitCode;Output=$run.Output;TimedOut=$run.TimedOut;ProcessExitConfirmed=$run.ProcessExitConfirmed;Milliseconds=$run.ElapsedMilliseconds})
 $unchanged=(($before|ConvertTo-Json -Compress) -ceq (@(Inventory)|ConvertTo-Json -Compress))
 [ordered]@{Schema='HVX.Disconnect.Focused/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');HardwareAccessed=$false;UserSettingsWritten=$false;SourceUnchanged=$unchanged;FullRegression=$false;Inputs=$before;Tests=$results}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
 $run.Output|Write-Output
 if($run.ExitCode -ne 0 -or -not $unchanged){throw ('Disconnect offline regression failed: '+$qa)}
}
Write-Output ('DISCONNECT_PROOF='+$qa)
