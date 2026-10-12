param([string]$AnalysisLibrary='build\pressure-firsttest-20261010-r43\modules\DataAnalysis\HVX.PdReview.V1.0.dll')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-emergency-pending-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$tokens=$null;$errors=$null
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
New-Item -ItemType Directory -Path $qa|Out-Null
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$common=@('/nologo','/langversion:latest','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
$test=Join-Path $PSScriptRoot 'PressureEmergencyPendingHostSmoke.cs'
$fixture=Join-Path $PSScriptRoot 'PressureManualZeroHostProgressSmoke.cs'
$analysis=[IO.Path]::GetFullPath((Join-Path $root $AnalysisLibrary));Assert-RegressionUnlinkedPath $analysis
$algorithm=Join-Path ([IO.Path]::GetDirectoryName($analysis)) 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll';Assert-RegressionUnlinkedPath $algorithm
$module=Join-Path $qa 'modules\PressureTest';$review=Join-Path $qa 'modules\DataAnalysis'
New-Item -ItemType Directory -Path $module,$review|Out-Null
$hostSources=@(Get-ChildItem -LiteralPath 'src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$moduleSources=@(Get-ChildItem -LiteralPath 'modules-src\PressureTest\src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$moduleSources+=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','ZeroVoltageNoiseReference.cs','LocationCycleRecord.cs','PressureRecordPublication.cs','R2RoundedButton.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$workerSources=@(Get-ChildItem -LiteralPath 'modules-src\FactoryWorker' -File -Recurse -Filter '*.cs'|Sort-Object FullName|ForEach-Object FullName)
$manifestPath=Join-Path $root 'modules-src\Task39InputWorker\provenance\source-manifest.json'
$contractTool=Join-Path $PSScriptRoot 'Test-PressureTask39Contract.ps1'
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
foreach($name in @('BenchModels.cs','PicoCaptureConfiguration.cs','LocationRangeAdvisor.cs','LocationRangeWindow.cs','RangeDecision.cs')){
 $entry=@($manifest.Files|Where-Object {$_.Path -ceq ('src/'+$name)})
 if($entry.Count -ne 1){throw 'Pinned pure input contract missing.'}
 $moduleSources+=& $contractTool -Name $name -OriginalHash $entry[0].SHA256
}
$inputs=@($test,$fixture,$PSCommandPath,$csc,$helper,$analysis,$algorithm,$manifestPath,$contractTool)+$hostSources+$moduleSources+$workerSources
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
$hostAssembly=Join-Path $qa 'Xiongxin.OWTS.Desktop.exe'
$dll=Join-Path $module 'Xiongxin.OWTS.PressureTest.dll'
$worker=Join-Path $qa 'FactoryWorker.FakeOnly.dll'
foreach($build in @(@{Name='worker';Output=$worker;Sources=$workerSources;Reference=@()},@{Name='host';Output=$hostAssembly;Sources=$hostSources;Reference=@()},@{Name='module';Output=$dll;Sources=$moduleSources;Reference=@('/r:'+$analysis)})){
 $compiled=@(& $csc @common /target:library ('/out:'+$build.Output) @($build.Reference) @($build.Sources) 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa ($build.Name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ($compiled -join "`n")}
}
Copy-Item -LiteralPath $analysis -Destination (Join-Path $module 'HVX.PdReview.V1.0.dll')
Copy-Item -LiteralPath $analysis -Destination (Join-Path $review 'HVX.PdReview.V1.0.dll')
Copy-Item -LiteralPath $algorithm -Destination (Join-Path $review 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll')
$exe=Join-Path $qa 'PressureEmergencyPendingHostSmoke.exe'
$compiled=@(& $csc @common /target:exe /main:PressureEmergencyPendingHostSmoke ('/out:'+$exe) $test $fixture 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'test.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa,(Join-Path $qa 'case'),$worker) -LogPrefix (Join-Path $qa 'pending') -TimeoutMilliseconds 90000
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$proof=Join-Path $qa 'verification-emergency-pending.json'
[ordered]@{Schema='HVX.Pressure.EmergencyPending.Focused/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');
 HardwareAccessed=$false;ProductEntryStarted=$false;WorkerStarted=$false;ResidentStarted=$false;SDKCalled=$false;UserProjectsWritten=$false;
 SourceBuild=$true;InputsUnchanged=$unchanged;FullRegression=$false;Inputs=$before;
 HostSHA256=(Get-FileHash -LiteralPath $hostAssembly).Hash;ModuleSHA256=(Get-FileHash -LiteralPath $dll).Hash;WorkerSHA256=(Get-FileHash -LiteralPath $worker).Hash;Result=$run}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $proof -Encoding UTF8
$run.Output|Write-Output
Write-Output ('PENDING_HOST_PROOF='+$proof)
if($run.ExitCode -ne 0 -or -not $unchanged){throw ('Pending Host regression failed or inputs changed: '+$qa)}
