param([string]$OutputRoot='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputRoot){$OutputRoot=Join-Path $root ('build\qa-pressure-gui-preview-'+[Guid]::NewGuid().ToString('N').Substring(0,10))}
$qa=[IO.Path]::GetFullPath($OutputRoot)
if(-not $qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $qa)){throw 'Use a new isolated project build directory.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Regression helper parse failed.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded regression helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$files=@('tests\PressureActionGuiPreview.cs','modules-src\PressureTest\src\PressureActionButton.cs','modules-src\PressureTest\src\PressureTestTheme.cs')|ForEach-Object{Join-Path $root $_}
$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=@($files)+@($compiler,$helper,$PSCommandPath)
$inventory=@($inputs|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
New-Item -ItemType Directory -Path $qa|Out-Null
$exe=Join-Path $qa 'PressureActionGuiPreview.Managed.exe'
$png=Join-Path $qa '加压按钮GUI.png'
$compile=@(& $compiler /nologo /langversion:latest /target:exe /platform:x64 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:'+$exe) @files 2>&1|ForEach-Object{$_.ToString()})
$compileExit=$LASTEXITCODE
$compile|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($compileExit -ne 0){throw ($compile -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($png) -LogPrefix (Join-Path $qa 'render') -TimeoutMilliseconds 30000
$unchanged=@($inventory|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count -eq 0
[ordered]@{Schema='HVX.PressureActionGuiPreview/1';Status=$(if($run.ExitCode -eq 0 -and $unchanged){'PASS'}else{'FAIL'});CompletedUtc=[DateTime]::UtcNow.ToString('o');SourceUnchanged=$unchanged;Inputs=$inventory;Execution=$run;
 ExecutableSHA256=(Get-FileHash -LiteralPath $exe).Hash;PreviewPath=$png;PreviewSHA256=$(if(Test-Path -LiteralPath $png){(Get-FileHash -LiteralPath $png).Hash}else{$null});
 ProductMainExecuted=$false;SDKCalled=$false;HardwareAccessed=$false;PhysicalAcceptance=$false}|
 ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $qa 'proof.json') -Encoding UTF8
$run.Output|Write-Output
if($run.ExitCode -ne 0 -or -not $unchanged){throw 'GUI preview failed; original evidence retained.'}
Write-Output ('GUI_PREVIEW='+$png)
