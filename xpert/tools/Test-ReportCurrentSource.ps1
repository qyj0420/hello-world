param([string]$BuildRoot='build\diag-report-output-20261007',[string[]]$Names=@('ReportMultilingualExportSmoke','ReportBundledTemplateSmoke','ReportTemplateQuotaSmoke','ReportWaveformGeometrySmoke','ReportAppendixLayoutSmoke','StateGridReportSmoke','ReportSavedConclusionSmoke','ReportFileNamingSmoke'))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
if(-not [IO.Path]::IsPathRooted($BuildRoot)){$BuildRoot=Join-Path $root $BuildRoot}
$BuildRoot=[IO.Path]::GetFullPath($BuildRoot)
if(-not $BuildRoot.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an isolated report QA directory.'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded test runner.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
$qa=Join-Path $BuildRoot ('qa\report-current-'+[guid]::NewGuid().ToString('N').Substring(0,8))
Assert-RegressionUnlinkedPath $qa -AllowMissing
$moduleFolder=Join-Path $qa 'modules\ReportGenerator'
New-Item -ItemType Directory -Path "$moduleFolder/runtime","$moduleFolder/templates" | Out-Null
$sources=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','CableJointStore.cs','CableVisualStyle.cs','AnalysisDateText.cs','PressureFixtureJournal.cs')|ForEach-Object{Join-Path $root ('src\'+$_)}
$sources+=Join-Path $root 'src/PressureRecordPublication.cs'
$sources+=@(Get-ChildItem -LiteralPath "$root/modules-src/ReportGenerator/src" -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$sources+=@('modules-src/DataBrowser/src/BrowserData.cs','modules-src/ConclusionAlgorithm/EvidenceV3/HVX.Task15.Embedded.cs','modules-src/ReportEngine/shared/BoundedDocx.cs','modules-src/ReportEngine/shared/ReportOutputSafety.cs')|ForEach-Object{Join-Path $root $_}
$engineSources=@(Get-ChildItem -LiteralPath "$root/modules-src/ReportEngine/src","$root/modules-src/ReportEngine/shared" -File -Filter '*.cs'|Sort-Object FullName|ForEach-Object FullName)
$tests=@($Names|ForEach-Object{Join-Path $root ('tests\'+$_+'.cs')})
$resource=Join-Path $root 'assets\xiongxin.ico';$template=Join-Path $root 'dependencies\Report\Templates\EPFT_OWTS_Report_Template_v1.0.docx'
$inputs=@(($sources+$engineSources+$tests+@($resource,$template,$PSCommandPath))|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=[IO.Path]::GetFullPath($_);SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Accessibility.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$dll=Join-Path $moduleFolder 'Xiongxin.OWTS.ReportGenerator.dll';$engine=Join-Path $moduleFolder 'runtime\HVX.Report.Engine.dll'
foreach($build in @(@{Path=$dll;Sources=$sources},@{Path=$engine;Sources=$engineSources})){
 $lines=@(& $csc /nologo /noconfig /langversion:7.3 /optimize+ /deterministic+ /target:library /platform:x64 @refs ('/out:'+$build.Path) ('/resource:'+$resource+',XIONGXIN.Brand.ico') @($build.Sources) 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$lines|Set-Content -LiteralPath ($build.Path+'.compile.txt') -Encoding UTF8
 if($code -ne 0){throw ($lines -join "`n")}
}
Copy-Item -LiteralPath $template -Destination "$moduleFolder/templates"
New-Item -ItemType Directory -Path "$qa/engine" | Out-Null
Copy-Item -LiteralPath $engine -Destination "$qa/engine/HVX.Report.Engine.dll"
$moduleHash=(Get-FileHash -LiteralPath $dll).Hash;$engineHash=(Get-FileHash -LiteralPath $engine).Hash
$results=New-Object 'System.Collections.Generic.List[object]'
foreach($name in $Names){
 if($name -notmatch '^[A-Za-z0-9]+$'){throw 'Invalid test name.'}
 $exe=Join-Path $qa ($name+'.exe')
 $lines=@(& $csc /nologo /langversion:7.3 /target:exe /platform:x64 @refs ('/out:'+$exe) (Join-Path $root ('tests\'+$name+'.cs')) 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ($lines -join "`n")}
 $case=if($name -eq 'ReportFileNamingSmoke'){Join-Path (Join-Path $root 'build') ('rn-'+[guid]::NewGuid().ToString('N').Substring(0,8))}else{Join-Path $qa ('case-'+$results.Count)}
 New-Item -ItemType Directory -Path $case|Out-Null
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa,$case) -LogPrefix (Join-Path $qa $name)
 $results.Add([ordered]@{Name=$name;FixtureRoot=$case;Result=$run});$run.Output|Write-Output
 $unchanged=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count -eq 0
 [ordered]@{Schema='HVX.Report.CurrentSource/1';CompletedUtc=[DateTime]::UtcNow.ToString('o');HardwareAccessed=$false;UserSettingsWritten=$false;UserProjectsWritten=$false;Inputs=$inputs;SourceUnchanged=$unchanged;Module=$dll;ModuleSHA256=$moduleHash;Engine=$engine;EngineSHA256=$engineHash;RuntimeUnchanged=(((Get-FileHash -LiteralPath $dll).Hash -ceq $moduleHash) -and ((Get-FileHash -LiteralPath $engine).Hash -ceq $engineHash));Tests=$results.ToArray()}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
 if($run.ExitCode -ne 0 -or -not $unchanged){throw ('Report regression failed: '+$qa)}
}
Write-Output ('REPORT_CURRENT_PROOF='+$qa)
