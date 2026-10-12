param([Parameter(Mandatory=$true)][string]$BuildRoot,[string[]]$Names=@('PressureRangeSingleStepSmoke','PressurePresetVoltageDisplaySmoke','PressurePlanClickSmoke','PressureProjectPlanCorrespondenceSmoke','PressureCompletedPlanSelectionSmoke','PressureNextRangeAutomaticSmoke','PressurePlanRangeFloorSmoke','PressureRangeServerParitySmoke'))
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$build=[IO.Path]::GetFullPath((Join-Path $root $BuildRoot))
if(-not $build.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use a project build directory.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Invalid bounded runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $build
$qa=Join-Path $build ('qa\pressure-focused-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
Assert-RegressionUnlinkedPath $qa -AllowMissing
New-Item -ItemType Directory -Path $qa|Out-Null
$files=@(Get-ChildItem -LiteralPath (Join-Path $build 'modules') -Recurse -File)+@($names|ForEach-Object{if($_ -notmatch '^[A-Za-z0-9]+$'){throw 'Invalid test name.'};Get-Item -LiteralPath (Join-Path $root ('tests\'+$_+'.cs'))})+@(Get-Item -LiteralPath $PSCommandPath,$helper,(Join-Path $build 'Xiongxin.OWTS.Desktop.exe'))
$hostSources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File);$files+=$hostSources
function Inventory{@($files|Sort-Object FullName|ForEach-Object{Assert-RegressionUnlinkedPath $_.FullName;[ordered]@{Path=$_.FullName;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o');$runs=@()
foreach($name in $names){
 $exe=Join-Path $qa ($name+'.exe')
 $extra=@();if($name -eq 'PressureProjectPlanCorrespondenceSmoke'){$extra=@($hostSources.FullName)}
 $lines=@(& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /langversion:latest /target:exe /platform:x64 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll /r:System.Security.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Microsoft.CSharp.dll ('/main:'+$name) ('/out:'+$exe) (Join-Path $root ('tests\'+$name+'.cs')) @extra 2>&1|ForEach-Object{$_.ToString()})
 $compile=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($compile -ne 0){$runs+=@{Name=$name;CompileExit=$compile;Output=$lines};continue}
 $runtimeRoot=$build;if($name -eq 'PressureProjectPlanCorrespondenceSmoke'){$runtimeRoot=Join-Path $build 'modules'}
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments @($runtimeRoot,(Join-Path $qa ($name+'-fixture'))) -LogPrefix (Join-Path $qa $name) -TimeoutMilliseconds 60000
 $runs+=@{Name=$name;CompileExit=0;Execution=$run;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash};$run.Output|Write-Output
}
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$passed=@($runs|Where-Object {$_.CompileExit -ne 0 -or $_.Execution.ExitCode -ne 0}).Count -eq 0
$proof=Join-Path $qa 'verification-pressure-focused.json'
@{Schema='HVX.Pressure.ReleaseFocused/1';Status=$(if($passed -and $unchanged){'PASS'}else{'FAIL'});BuildRoot=$build;StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Inputs=$before;RuntimeUnchanged=$unchanged;Runs=$runs;HardwareAccessed=$false;SDKCalled=$false;ProductEntryExecuted=$false;PhysicalAcceptance=$false;FullRegression=$false}|ConvertTo-Json -Depth 15|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output ('PRESSURE_FOCUSED_PROOF='+$proof)
if(-not $passed -or -not $unchanged){throw 'Pressure release focused checks failed; evidence retained.'}
