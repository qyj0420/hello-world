param([string]$ModuleBuild='build\qa-manual-overrange-c6b86f932f')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dependency=[IO.Path]::GetFullPath((Join-Path $root $ModuleBuild))
$qa=Join-Path $root ('build\qa-voltage-precision-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$test=Join-Path $root 'tests\PressureCustomVoltageMinimumSmoke.cs'
$layout=Join-Path $root 'modules-src\PressureTest\src\PressureTestLayout.cs'
$proof=Get-Content -LiteralPath (Join-Path $dependency 'proof.json') -Raw|ConvertFrom-Json
if($proof.Status -cne 'PASS' -or -not $proof.SourceUnchanged){throw 'QA module source receipt must pass.'}
$compiledLayout=@($proof.Inputs|Where-Object {$_.Path -ceq $layout})
if($compiledLayout.Count -ne 1 -or $compiledLayout[0].SHA256 -cne (Get-FileHash -LiteralPath $layout).Hash){throw 'QA module does not contain current display source.'}
$module=Join-Path $dependency 'modules\PressureTest\Xiongxin.OWTS.PressureTest.dll'
if($proof.ModuleSHA256 -cne (Get-FileHash -LiteralPath $module).Hash){throw 'QA module hash mismatch.'}
$inputs=@($test,$layout,$module,$PSCommandPath,$helper)
$before=@($inputs|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
New-Item -ItemType Directory -Path $qa|Out-Null
$exe=Join-Path $qa 'PressureVoltagePrecisionFakeOnly.exe'
$compiled=@(& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /target:exe /platform:x64 /langversion:7.3 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()})
if($LASTEXITCODE -ne 0){throw ($compiled -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($dependency) -LogPrefix (Join-Path $qa 'run') -TimeoutMilliseconds 60000
$same=$true;foreach($entry in $before){if((Get-FileHash -LiteralPath $entry.Path).Hash -cne $entry.SHA256){$same=$false}}
[ordered]@{Schema='HVX.VoltagePrecision.Offline/1';Status=$(if($run.ExitCode -eq 0 -and $same){'PASS'}else{'FAIL'});Inputs=$before;SourceUnchanged=$same;Run=$run;
 ProductStarted=$false;SDKCalled=$false;HardwareAccessed=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $qa 'proof.json') -Encoding UTF8
$run.Output|Write-Output;Write-Output ('VOLTAGE_PRECISION_PROOF='+$qa)
if($run.ExitCode -ne 0 -or -not $same){throw 'Voltage precision regression failed.'}
