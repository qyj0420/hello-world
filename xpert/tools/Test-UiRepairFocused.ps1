param([Parameter(Mandatory=$true)][string]$BuildRoot,[string[]]$Names=@('HostDialogSafetySmoke','OwnedModuleDialogLayoutSmoke','ReportSettingsHeaderLayoutSmoke'))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$BuildRoot=[IO.Path]::GetFullPath($BuildRoot)
$qa=Join-Path $BuildRoot ('qa\ui-repair-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded regression runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Get-RegressionTreeFiles','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw ('Missing runner helper '+$name)}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $BuildRoot
Assert-RegressionUnlinkedPath $qa -AllowMissing
New-Item -ItemType Directory -Path $qa | Out-Null
function Inventory {
 $files=@(Get-Item -LiteralPath (Join-Path $BuildRoot 'Xiongxin.OWTS.Desktop.exe'),(Join-Path $BuildRoot 'Xiongxin.OWTS.Desktop.exe.config'))
 $files+=@(Get-RegressionTreeFiles (Join-Path $BuildRoot 'modules'))
 @($files|Sort-Object FullName|ForEach-Object{[ordered]@{Path=$_.FullName.Substring($BuildRoot.Length+1);SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
}
$before=@(Inventory);$results=New-Object 'System.Collections.Generic.List[object]'
$startedUtc=[DateTime]::UtcNow.ToString('o')
function Receipt {
 $after=@(Inventory)
 [ordered]@{Schema='HVX.UiRepair.Focused/1';BuildRoot=$BuildRoot;StartedUtc=$startedUtc;CompletedUtc=[DateTime]::UtcNow.ToString('o');HardwareAccessed=$false;UserProjectsWritten=$false;PhysicalDpiTested=$false;FullRegression=$false;
 RuntimeUnchanged=(($before|ConvertTo-Json -Depth 4 -Compress) -ceq ($after|ConvertTo-Json -Depth 4 -Compress));Runtime=$before;Tests=$results.ToArray()}|
 ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'verification-ui-repair.json') -Encoding UTF8
}
foreach($name in $Names){
 if($name -notmatch '^[A-Za-z0-9]+$'){throw 'Invalid smoke test name.'}
 $source=Join-Path $root ('tests\'+$name+'.cs');$exe=Join-Path $qa ($name+'.exe')
 if($name -ceq 'PressureManualZeroHostProgressSmoke'){$source=Join-Path $root 'tools/PressureManualZeroHostProgressSmoke.cs'}
 $sourceHash=(Get-FileHash -LiteralPath $source).Hash
 $dpiArgs=@()
 $extraSources=@()
 if($name -ceq 'PressureActionFeedbackSmoke'){$extraSources+=@(Join-Path $root 'src/PressureFeedbackDisplay.cs')}
 if($name -ceq 'PressureAnalysisOptionsSmoke'){$extraSources+=@((Join-Path $root 'src/PressureAnalysisTrial.cs'),(Join-Path $root 'src/PressureBackgroundAnalysisQueue.cs'))}
 if($name -ceq 'PressureAnalysisHostOptionsSmoke'){
  $extraSources+=@(Join-Path $root 'tests/PressureCumulativeAnalysisSmoke.cs');$dpiArgs+='/main:PressureAnalysisHostOptionsSmoke'
 }
 if($name -ceq 'LegacyCalibrationUnitsSmoke'){
  $extraSources+=@(Join-Path $root 'tests/LegacyCalibrationBindingSmoke.cs');$dpiArgs+='/main:LegacyCalibrationUnitsSmoke'
 }
 if($name -ceq 'PressureWaveformMetricsSmoke'){$extraSources+=@((Join-Path $root 'src/LocationCycleRecord.cs'),(Join-Path $root 'modules-src/PressureTest/src/PressureMetricWindow.cs'))}
 if($name -ceq 'PressureMainDpiLayoutSmoke') {
  $dpiArgs+=('/win32manifest:'+(Join-Path $root 'app.manifest'))
  Copy-Item -LiteralPath (Join-Path $BuildRoot 'Xiongxin.OWTS.Desktop.exe.config') -Destination ($exe+'.config')
 }
 $lines=@(& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /target:exe /platform:x64 /langversion:7.3 /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Xml.Linq.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:Accessibility.dll @dpiArgs ('/out:'+$exe) $source @extraSources 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){$results.Add([ordered]@{Name=$name;CompileExit=$code;ExitCode=$null;Output=$lines});Receipt;throw ($name+' failed to compile: '+($lines -join "`n"))}
 # Keep synthetic project paths below legacy Win32 limits; long descriptive test
 # names belong in the receipt, not repeated within nested project directories.
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments @($BuildRoot,(Join-Path $qa ('case-'+$results.Count))) -LogPrefix (Join-Path $qa $name)
 $results.Add([ordered]@{Name=$name;CompileExit=0;ExitCode=$run.ExitCode;Output=$run.Output;Milliseconds=$run.ElapsedMilliseconds;TimedOut=$run.TimedOut;ProcessExitConfirmed=$run.ProcessExitConfirmed;SourceSHA256=$sourceHash;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash})
 Receipt;$run.Output|Write-Output
 if($run.ExitCode -ne 0){throw ($name+' failed; evidence '+$qa)}
 if((Get-FileHash -LiteralPath $source).Hash -cne $sourceHash){throw 'Test source changed during run.'}
}
Write-Output ('UI_REPAIR_PROOF='+$qa)
