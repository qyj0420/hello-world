param([string]$BuildRoot='', [switch]$BuildSourceUi)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-r50-presentation-'+[guid]::NewGuid().ToString('N').Substring(0,10))
$tokens=$null;$errors=$null
$runner=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded test runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Get-RegressionTreeFiles','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
New-Item -ItemType Directory -Path $qa|Out-Null
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$common=@('/nologo','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
$inputs=@($PSCommandPath,$csc,$runner,(Join-Path $PSScriptRoot 'Build-UiModules.ps1'),(Join-Path $root 'assets\xiongxin.ico'),(Join-Path $root 'assets\xiongxin-original.png'))
$inputs+=@(Get-RegressionTreeFiles (Join-Path $root 'src')|Where-Object Extension -eq '.cs'|ForEach-Object FullName)
foreach($module in @('DataAnalysis','PressureTest','ProjectCreation','ProjectManagement','CalibrationTest','CalibrationStandalone')){
 $inputs+=@(Get-RegressionTreeFiles (Join-Path $root ('modules-src\'+$module))|Where-Object Extension -eq '.cs'|ForEach-Object FullName)
}
$suite=@(
 @{Name='CalibrationEntryRenderingSmoke';Sources=@('tests\CalibrationEntryRenderingSmoke.cs');Args=@('BUILD','CASE')},
 @{Name='PressurePresetVoltageDisplaySmoke';Sources=@('tests\PressurePresetVoltageDisplaySmoke.cs');Args=@('BUILD')},
 @{Name='ZeroVoltageLocationWindowTests';Sources=@('tests\ZeroVoltageLocationWindowTests.cs','modules-src\PressureTest\src\ZeroVoltageLocationWindow.cs');Args=@()},
 @{Name='PressureZeroVoltageAnalysisSmoke';Sources=@('tests\PressureZeroVoltageAnalysisSmoke.cs');Args=@('BUILD','CASE')},
 @{Name='R50LocationPopulationSmoke';Sources=@('tests\R50LocationPopulationSmoke.cs');Args=@('BUILD')},
 @{Name='R50UnicodeLanguageSmoke';Sources=@('tests\R50UnicodeLanguageSmoke.cs');Args=@('BUILD','CASE')},
 @{Name='SettingsUiLanguageTreeSmoke';Sources=@('tests\SettingsUiLanguageTreeSmoke.cs');Args=@('BUILD','CASE')},
 @{Name='PressureSidebarLayoutSmoke';Sources=@('tests\PressureSidebarLayoutSmoke.cs');Args=@('BUILD','CASE')},
 @{Name='PressureSpectrumContentLayoutSmoke';Sources=@('tests\PressureSpectrumContentLayoutSmoke.cs');Args=@('BUILD')},
 @{Name='ProjectSaveTransactionSmoke';Sources=@('tests\ProjectSaveTransactionSmoke.cs');Args=@('BUILD','CASE')}
)
$inputs+=@($suite|ForEach-Object{$_.Sources}|ForEach-Object{Join-Path $root $_})
function Inventory { @($inputs|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}}) }
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
if($BuildSourceUi){
 $BuildRoot=Join-Path $qa 'runtime';New-Item -ItemType Directory -Path $BuildRoot|Out-Null
 $assetRoot=Join-Path $BuildRoot 'assets';New-Item -ItemType Directory -Path $assetRoot|Out-Null
 Copy-Item -LiteralPath (Join-Path $root 'assets\xiongxin.ico') -Destination (Join-Path $assetRoot 'xiongxin.ico')
 & (Join-Path $PSScriptRoot 'Build-UiModules.ps1') -OutputRoot (Join-Path $BuildRoot 'modules')
 if($LASTEXITCODE -ne 0){throw 'UI compilation failed.'}
 $hostSources=@(Get-ChildItem -LiteralPath 'src' -File -Filter '*.cs'|Sort-Object FullName|ForEach-Object FullName)
 $out=@(& $csc @common /target:library ('/out:'+(Join-Path $BuildRoot 'Xiongxin.OWTS.Desktop.exe')) ('/resource:'+(Join-Path $root 'assets\xiongxin.ico')+',Brand.ico') ('/resource:'+(Join-Path $root 'assets\xiongxin-original.png')+',Brand.png') @hostSources 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$out|Set-Content -LiteralPath (Join-Path $qa 'host.compile.txt') -Encoding UTF8
 if($code -ne 0){throw ($out -join "`n")}
}else{
 if([string]::IsNullOrWhiteSpace($BuildRoot)){throw 'Specify BuildRoot or BuildSourceUi.'}
 $BuildRoot=[IO.Path]::GetFullPath($BuildRoot);Assert-RegressionUnlinkedPath $BuildRoot
}
$runtime=@(Get-RegressionTreeFiles (Join-Path $BuildRoot 'modules')|Where-Object Extension -in @('.dll','.exe','.json','.config')|ForEach-Object{[ordered]@{Path=$_.FullName;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$runtime+=@([ordered]@{Path=(Join-Path $BuildRoot 'assets\xiongxin.ico');SHA256=(Get-FileHash -LiteralPath (Join-Path $BuildRoot 'assets\xiongxin.ico')).Hash})
$hostAssembly=Join-Path $BuildRoot 'Xiongxin.OWTS.Desktop.exe'
$runtime+=@([ordered]@{Path=$hostAssembly;SHA256=(Get-FileHash -LiteralPath $hostAssembly).Hash})
$results=New-Object System.Collections.Generic.List[object]
foreach($test in $suite){
 $exe=Join-Path $qa ($test.Name+'.exe')
 $sources=@($test.Sources|ForEach-Object{Join-Path $root $_})
 $out=@(& $csc @common /target:exe ('/main:'+$test.Name) ('/out:'+$exe) ('/resource:'+(Join-Path $root 'assets\xiongxin.ico')+',Brand.ico') ('/resource:'+(Join-Path $root 'assets\xiongxin.ico')+',XIONGXIN.Brand.ico') @sources 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$out|Set-Content -LiteralPath (Join-Path $qa ($test.Name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ($out -join "`n")}
 $arguments=@($test.Args|ForEach-Object{if($_ -eq 'BUILD'){$BuildRoot}elseif($_ -eq 'CASE'){Join-Path $qa ($test.Name+'-case')}else{$_}})
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments $arguments -LogPrefix (Join-Path $qa $test.Name) -TimeoutMilliseconds 180000
 $results.Add([ordered]@{Name=$test.Name;ExecutableSHA256=(Get-FileHash -LiteralPath $exe).Hash;Result=$run})
 $run.Output|Write-Output
}
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$runtimeUnchanged=@($runtime|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count -eq 0
$pass=@($results|Where-Object{$_.Result.ExitCode -ne 0}).Count -eq 0
$proof=Join-Path $qa 'verification-r50-presentation-records.json'
[ordered]@{Schema='HVX.R50.PresentationRecords/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');BuildRoot=$BuildRoot;SourceUiFixture=[bool]$BuildSourceUi;
 Passed=$pass;ExitCode=$(if($pass -and $unchanged -and $runtimeUnchanged){0}else{1});InputsUnchanged=$unchanged;RuntimeUnchanged=$runtimeUnchanged;Inputs=$before;Runtime=$runtime;Tests=$results.ToArray();
 ProductEntryStarted=$false;HardwareAccessed=$false;WorkerStarted=$false;SDKCalled=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;FullRegression=$false}|
 ConvertTo-Json -Depth 12|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output ('R50_PRESENTATION_PROOF='+$proof)
if(-not $pass -or -not $unchanged -or -not $runtimeUnchanged){throw ('Focused presentation/record regression failed or inputs changed: '+$qa)}
