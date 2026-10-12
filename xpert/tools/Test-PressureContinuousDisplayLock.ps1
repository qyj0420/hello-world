[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ModuleBuildRoot)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$moduleRoot=[IO.Path]::GetFullPath($ModuleBuildRoot).TrimEnd('\')
if(-not $moduleRoot.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use isolated compile-only UI build.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Bounded helper parse failure.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Bounded helper missing.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
$manifestPath=Join-Path $moduleRoot 'ui-module-build.json';$manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8|ConvertFrom-Json
$entry=@($manifest.Modules|Where-Object Module -ceq 'PressureTest')
if($entry.Count -ne 1 -or -not $entry[0].InputsUnchanged){throw 'Source-bound PressureTest compile required.'}
$inputs=New-Object 'System.Collections.Generic.List[object]';$index=@{}
function Add-Input([string]$Path,[string]$Expected=''){
 $p=[IO.Path]::GetFullPath($Path);Assert-RegressionUnlinkedPath $p;$hash=(Get-FileHash -LiteralPath $p).Hash
 if($Expected -and $Expected -cne $hash){throw ('Changed compile input: '+$p)}
 if(-not $index.ContainsKey($p)){$index[$p]=$hash;$inputs.Add([ordered]@{Path=$p;SHA256=$hash})}
}
Add-Input $PSCommandPath;Add-Input $helper;Add-Input $manifestPath
foreach($r in @($entry[0].CompileInputs)){Add-Input (Join-Path $root $r.Path) $r.SHA256}
$dll=Join-Path $moduleRoot $entry[0].File;Add-Input $dll $entry[0].SHA256
foreach($dep in @(Get-ChildItem -LiteralPath (Split-Path -Parent $dll) -File -Filter '*.dll')){Add-Input $dep.FullName}
$test=Join-Path $root 'tests/PressureContinuousDisplayLockSmoke.cs';$csc=Join-Path $PSScriptRoot 'compiler/csc.exe';Add-Input $test;Add-Input $csc
$qa=Join-Path $root ('build/r50-continuous-display-'+[Guid]::NewGuid().ToString('N').Substring(0,10));Assert-RegressionUnlinkedPath $qa -AllowMissing
New-Item -ItemType Directory -Path $qa|Out-Null;$exe=Join-Path $qa 'PressureContinuousDisplayLockSmoke.exe'
$lines=@(& $csc /nologo /noconfig /target:exe /platform:x64 /langversion:7.3 /deterministic+ /main:PressureContinuousDisplayLockSmoke /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8;if($code -ne 0){throw ($lines -join "`n")};Add-Input $exe
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($dll) -LogPrefix (Join-Path $qa 'test') -TimeoutMilliseconds 60000
$mismatches=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count
$passed=($run.Output -join "`n") -match 'PRESSURE_CONTINUOUS_DISPLAY_PASS=(\d+);ProductStarted=False;SDKCalled=False;ActualWorkerStarted=False;HardwareAccessed=False;BackendConfigured=False;PhysicalAcceptance=False'
$checks=if($passed){[int]$Matches[1]}else{0};$proof=Join-Path $qa 'verification.json'
[ordered]@{Schema='HVX.Pressure.ContinuousDisplay.Offline/1';UTC=[DateTime]::UtcNow.ToString('o');Checks=$checks;Inputs=$inputs.ToArray();InputMismatchCount=$mismatches;SourceBoundCompile=$true;UI=$dll;UI_SHA256=$index[$dll];Result=$run;OutputVerified=$passed;ProductStarted=$false;SDKCalled=$false;ActualWorkerStarted=$false;HardwareAccessed=$false;BackendConfigured=$false;PhysicalAcceptance=$false;Scope='Actual WinForms EnabledChanged and lifecycle/UI methods with synthetic private lifecycle state, not a hardware or full automatic-plan acceptance.'}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $proof -Encoding UTF8
$run.Output|Write-Output;Write-Output ('PRESSURE_CONTINUOUS_DISPLAY_PROOF='+$proof)
if($run.ExitCode -ne 0 -or -not $passed -or $mismatches -ne 0){throw 'Continuous display regression failed.'}
