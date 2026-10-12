[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ModuleBuildRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$moduleRoot=[IO.Path]::GetFullPath($ModuleBuildRoot).TrimEnd('\')
if(-not $moduleRoot.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an isolated compile-only UI build directory.'}
$manifestPath=Join-Path $moduleRoot 'ui-module-build.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8|ConvertFrom-Json
$entry=@($manifest.Modules|Where-Object Module -ceq 'PressureTest')
if($entry.Count -ne 1 -or $entry[0].InputsUnchanged -isnot [bool] -or -not $entry[0].InputsUnchanged){throw 'Pressure compilation requires source-bound compile evidence.'}
$inputs=New-Object 'System.Collections.Generic.List[object]'
$index=@{}
function Add-Input([string]$Path,[string]$Expected=''){
 $full=[IO.Path]::GetFullPath($Path)
 if(-not $full.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Input outside project.'}
 Assert-RegressionUnlinkedPath $full
 $hash=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
 if($Expected -and $Expected -cne $hash){throw ('Compile input changed: '+$full)}
 if(-not $index.ContainsKey($full)){$index[$full]=$hash;$inputs.Add([ordered]@{Path=$full;SHA256=$hash})}
}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Bounded runner parse failure.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Bounded helper missing.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Add-Input $PSCommandPath
Add-Input $helper
Add-Input $manifestPath
Add-Input (Join-Path $PSScriptRoot 'Build-UiModules.ps1')
Add-Input (Join-Path $root 'src/ModuleHost.FactoryPressure.cs')
foreach($inputRecord in @($entry[0].CompileInputs)){Add-Input (Join-Path $root $inputRecord.Path) $inputRecord.SHA256}
$dll=Join-Path $moduleRoot $entry[0].File
Add-Input $dll $entry[0].SHA256
foreach($dependency in @(Get-ChildItem -LiteralPath (Split-Path -Parent $dll) -Filter '*.dll' -File)){Add-Input $dependency.FullName}
$test=Join-Path $root 'tests/PressureNoiseInitializationSmoke.cs'
$compiler=Join-Path $PSScriptRoot 'compiler/csc.exe'
Add-Input $test
Add-Input $compiler
foreach($dependency in @(Get-ChildItem -LiteralPath (Split-Path -Parent $compiler) -File|Where-Object Extension -in @('.dll','.config','.rsp'))){Add-Input $dependency.FullName}
$qa=Join-Path $root ('build/r50-pressure-noise-initialization-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
Assert-RegressionUnlinkedPath $qa -AllowMissing
New-Item -ItemType Directory -Path $qa|Out-Null
$exe=Join-Path $qa 'PressureNoiseInitializationSmoke.exe'
$lines=@(& $compiler /nologo /noconfig /target:exe /platform:x64 /langversion:7.3 /deterministic+ /main:PressureNoiseInitializationSmoke /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE
$lines|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($code -ne 0){throw ($lines -join "`n")}
Add-Input $exe
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($dll,(Join-Path $qa 'synthetic')) -LogPrefix (Join-Path $qa 'test') -TimeoutMilliseconds 60000
$mismatches=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count
$passed=($run.Output -join "`n") -match 'PRESSURE_NOISE_INITIALIZATION_PASS=(\d+);InputRequests=(\d+);ActualSyntheticSources=(\d+);ProductStarted=False;SDKCalled=False;ActualWorkerStarted=False;HardwareAccessed=False;OutputTasksCreated=0;PhysicalAcceptance=False'
$checks=if($passed){[int]$Matches[1]}else{0}
[ordered]@{Schema='HVX.PressureNoiseInitialization.Offline/1';UTC=[DateTime]::UtcNow.ToString('o');Checks=$checks;Inputs=$inputs.ToArray();InputMismatchCount=$mismatches;SourceBoundCompile=$true;UI=$dll;UI_SHA256=$index[$dll];Result=$run;OutputVerified=$passed;ProductStarted=$false;SDKCalled=$false;ActualWorkerStarted=$false;HardwareAccessed=$false;OutputTasksCreated=0;PhysicalAcceptance=$false}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
$run.Output|Write-Output
Write-Output ('PRESSURE_NOISE_INITIALIZATION_PROOF='+(Join-Path $qa 'verification.json'))
if($run.ExitCode -ne 0 -or -not $passed -or $mismatches -ne 0){throw 'Pressure initialization fake input regression failed.'}
