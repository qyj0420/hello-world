param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root=$project
$output=Join-Path $project ('build\emergency-display-focused-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded test runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $output -AllowMissing
New-Item -ItemType Directory -Path $output|Out-Null
$sources=@(Get-ChildItem -LiteralPath (Join-Path $project 'modules-src\FactoryWorker') -Recurse -File -Filter '*.cs'|Sort-Object FullName|ForEach-Object FullName)
$sources+=Join-Path $project 'tests\FactoryEmergencyDisplaySmoke.cs'
$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=$sources+@($PSCommandPath,$compiler,$helper)
$inventory=@($inputs|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$exe=Join-Path $output 'FactoryEmergencyDisplaySmoke.exe'
$compile=@(& $compiler /nologo /target:exe /platform:x64 /langversion:latest /main:FactoryEmergencyDisplaySmoke /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:'+$exe) @sources 2>&1|ForEach-Object{$_.ToString()})
$compileExit=$LASTEXITCODE
$compile|Set-Content -LiteralPath (Join-Path $output 'compile.txt') -Encoding UTF8
if($compileExit -ne 0){throw ($compile -join "`n")}
$fixture=Join-Path $output 'qa'
New-Item -ItemType Directory -Path $fixture|Out-Null
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($fixture) -LogPrefix (Join-Path $output 'emergency-display') -TimeoutMilliseconds 45000
$same=$true
foreach($entry in $inventory){if((Get-FileHash -LiteralPath $entry.Path).Hash -cne $entry.SHA256){$same=$false}}
[ordered]@{Schema='HVX.EmergencyDisplay.Focused/1';CapturedUtc=[DateTime]::UtcNow.ToString('o');
 Status=$(if($run.ExitCode -eq 0 -and $same){'PASS'}else{'FAIL'});SourceUnchanged=$same;SourceInputs=$inventory;
 CompiledTestSHA256=(Get-FileHash -LiteralPath $exe).Hash;Execution=$run;
 HardwareAccessed=$false;SDKCalled=$false;ProductEntryExecuted=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;
 Limitations='Fake DI and managed blocked Open/journal only; no real driver timing, Windows/USB response or physical high voltage/discharge acceptance.'
}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
$run.Output|Write-Output
Write-Output ('EmergencyDisplayProof='+$output)
if($run.ExitCode -ne 0 -or -not $same){throw 'Focused DI display tests failed or source inputs changed.'}
