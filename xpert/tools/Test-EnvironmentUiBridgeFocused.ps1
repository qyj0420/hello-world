param([string]$DependencyBuild='build\public-beta-1.1.1-20261011-r51')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dependency=[IO.Path]::GetFullPath((Join-Path $root $DependencyBuild))
$output=Join-Path $root ('build\r52-environment-ui-bridge-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded regression runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded regression helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $output -AllowMissing
New-Item -ItemType Directory -Path $output|Out-Null
$files=@('tests\EnvironmentUiBridgeSmoke.cs','src\ModuleHost.Environment.cs','src\EnvironmentalReadings.cs')|ForEach-Object{Join-Path $root $_}
$ui=Join-Path $dependency 'modules\ProjectCreation\Xiongxin.OWTS.ProjectCreation.dll'
$inputs=@($files)+@($ui,$helper,$PSCommandPath,(Join-Path $PSScriptRoot 'compiler\csc.exe'))
$sources=@($inputs|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$exe=Join-Path $output 'EnvironmentUiBridge.ManagedTests.exe'
$compile=@(& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /langversion:latest /target:exe /platform:x64 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:'+$exe) @files 2>&1|ForEach-Object{$_.ToString()})
$compileExit=$LASTEXITCODE;$compile|Set-Content -LiteralPath (Join-Path $output 'compile.txt') -Encoding UTF8
$run=$null
if($compileExit -eq 0){$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($dependency) -LogPrefix (Join-Path $output 'ui-bridge') -TimeoutMilliseconds 60000}
$unchanged=$true;foreach($entry in $sources){if((Get-FileHash -LiteralPath $entry.Path).Hash -cne $entry.SHA256){$unchanged=$false}}
$proof=Join-Path $output 'verification-environment-ui-bridge.json'
[ordered]@{Schema='HVX.R52.EnvironmentUiBridge.Managed/1';CompletedUtc=[DateTime]::UtcNow.ToString('o');SourceInventory=$sources;
 SourceUnchanged=$unchanged;CompileExit=$compileExit;Execution=$run;ProductMainExecuted=$false;SDKCalled=$false;NetworkAccessed=$false;HardwareAccessed=$false;
 PhysicalAcceptance=$false;Executable=$exe;ExecutableSHA256=$(if(Test-Path -LiteralPath $exe){(Get-FileHash -LiteralPath $exe).Hash}else{$null});
 Uncovered=@('Actual native owner cleanup and hardware availability','Current full ProjectCreation module build; this UI dependency remains r51')
}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $proof -Encoding UTF8
if($run){$run.Output|Write-Output};Write-Output ('ENVIRONMENT_UI_BRIDGE_PROOF='+$proof)
if($compileExit -ne 0 -or $null -eq $run -or $run.ExitCode -ne 0 -or -not $unchanged){throw ('Environment UI bridge test failed: '+$output)}
