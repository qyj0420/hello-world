param([string]$OutputRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if([String]::IsNullOrWhiteSpace($OutputRoot)){$OutputRoot=Join-Path $root ('build\r52-pressure-action-dpi-'+[Guid]::NewGuid().ToString('N').Substring(0,10))}
$output=[IO.Path]::GetFullPath($OutputRoot)
if(-not $output.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output must remain under project build.'}
if(Test-Path -LiteralPath $output){throw 'Existing evidence must not be overwritten.'}
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
$files=@('tests\PressureActionTextDpiSmoke.cs','modules-src\PressureTest\src\PressureActionButton.cs','modules-src\PressureTest\src\PressureTestTheme.cs')|ForEach-Object{Join-Path $root $_}
$gestureSource=Join-Path $root 'tests\PressureActionGestureSmoke.cs'
$inputs=@($files)+@($gestureSource,$helper,$PSCommandPath,(Join-Path $PSScriptRoot 'compiler\csc.exe'))
$sources=@($inputs|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$exe=Join-Path $output 'PressureActionTextDpi.ManagedTests.exe'
$compile=@(& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /langversion:latest /target:exe /platform:x64 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:'+$exe) @files 2>&1|ForEach-Object{$_.ToString()})
$compileExit=$LASTEXITCODE;$compile|Set-Content -LiteralPath (Join-Path $output 'compile.txt') -Encoding UTF8
$run=$null
if($compileExit -eq 0){$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($output) -LogPrefix (Join-Path $output 'render') -TimeoutMilliseconds 60000}
$gestureExe=Join-Path $output 'PressureActionGesture.ManagedTests.exe'
$gestureCompile=@(& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /langversion:latest /target:exe /platform:x64 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:'+$gestureExe) $gestureSource $files[1] $files[2] 2>&1|ForEach-Object{$_.ToString()})
$gestureCompileExit=$LASTEXITCODE;$gestureCompile|Set-Content -LiteralPath (Join-Path $output 'gesture.compile.txt') -Encoding UTF8
$gestureRun=$null
if($gestureCompileExit -eq 0){$gestureRun=Invoke-RegressionExecutable -Executable $gestureExe -Arguments @('--source',$output) -LogPrefix (Join-Path $output 'gesture') -TimeoutMilliseconds 30000}
$unchanged=$true;foreach($entry in $sources){if((Get-FileHash -LiteralPath $entry.Path).Hash -cne $entry.SHA256){$unchanged=$false}}
$proof=Join-Path $output 'verification-pressure-action-text-dpi.json'
[ordered]@{Schema='HVX.R52.PressureActionTextDpi.Managed/1';CompletedUtc=[DateTime]::UtcNow.ToString('o');SourceInventory=$sources;
 SourceUnchanged=$unchanged;CompileExit=$compileExit;Execution=$run;GestureCompileExit=$gestureCompileExit;GestureExecution=$gestureRun;ProductMainExecuted=$false;SDKCalled=$false;NetworkAccessed=$false;HardwareAccessed=$false;
 PhysicalAcceptance=$false;Executable=$exe;ExecutableSHA256=$(if(Test-Path -LiteralPath $exe){(Get-FileHash -LiteralPath $exe).Hash}else{$null});
 Uncovered=@('Actual per-monitor DPI transitions, full sidebar layout and installed executable identity','Customer OS fonts, accessibility text scaling and physical interaction')
}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $proof -Encoding UTF8
if($run){$run.Output|Write-Output};if($gestureRun){$gestureRun.Output|Write-Output};Write-Output ('PRESSURE_ACTION_TEXT_DPI_PROOF='+$proof)
if($compileExit -ne 0 -or $null -eq $run -or $run.ExitCode -ne 0 -or $gestureCompileExit -ne 0 -or $null -eq $gestureRun -or $gestureRun.ExitCode -ne 0 -or -not $unchanged){throw ('Pressure text DPI test failed: '+$output)}
