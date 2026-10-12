param([string]$BuildRoot='build\diag-browser-entry-20261007',[string[]]$Names=@('BrowserEntryPerformanceSmoke'))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
if(-not [IO.Path]::IsPathRooted($BuildRoot)){$BuildRoot=Join-Path $root $BuildRoot}
$BuildRoot=[IO.Path]::GetFullPath($BuildRoot)
if(-not $BuildRoot.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an isolated QA directory under build.'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded test runner.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
$qa=Join-Path $BuildRoot ('qa\browser-entry-'+[guid]::NewGuid().ToString('N').Substring(0,8))
Assert-RegressionUnlinkedPath $qa -AllowMissing
$moduleFolder=Join-Path $qa 'modules\DataBrowser'
New-Item -ItemType Directory -Path $moduleFolder | Out-Null
$sources=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','CableJointStore.cs','CableJointDialog.cs','CableVisualStyle.cs','AnalysisDateText.cs')|ForEach-Object{Join-Path $root ('src\'+$_)}
$sources+=Join-Path $root 'src/PressureRecordPublication.cs'
foreach($part in @('src','vendor\Item','vendor\Calibration','vendor\Calibration\Common','vendor\Oscillation')){
 $sources+=@(Get-ChildItem -LiteralPath (Join-Path $root ('modules-src\DataBrowser\'+$part)) -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
}
$tests=@($Names|ForEach-Object{Join-Path $root ('tests\'+$_+'.cs')})
$resource=Join-Path $root 'assets\xiongxin.ico'
$inputs=@(($sources+$tests+@($resource,$PSCommandPath))|ForEach-Object{[ordered]@{Path=[IO.Path]::GetFullPath($_);SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Accessibility.dll')
$dll=Join-Path $moduleFolder 'Xiongxin.OWTS.DataBrowser.dll'
$lines=@(& $csc /nologo /noconfig /langversion:7.3 /optimize+ /deterministic+ /target:library /platform:x64 @refs ('/out:'+$dll) ('/resource:'+$resource+',XIONGXIN.Brand.ico') @sources 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa 'module.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($lines -join "`n")}
$moduleHash=(Get-FileHash -LiteralPath $dll).Hash
$results=New-Object 'System.Collections.Generic.List[object]'
foreach($name in $Names){
 $exe=Join-Path $qa ($name+'.exe')
 $testSources=@(Join-Path $root ('tests\'+$name+'.cs'))
 $lines=@(& $csc /nologo /langversion:7.3 /target:exe /platform:x64 @refs ('/out:'+$exe) @testSources 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ($lines -join "`n")}
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa,(Join-Path $qa ('case-'+$results.Count))) -LogPrefix (Join-Path $qa $name)
 $results.Add([ordered]@{Name=$name;Result=$run});$run.Output|Write-Output
 $unchanged=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count -eq 0
 [ordered]@{Schema='HVX.Browser.EntryPerformance/1';CompletedUtc=[DateTime]::UtcNow.ToString('o');HardwareAccessed=$false;UserSettingsWritten=$false;UserProjectsWritten=$false;Inputs=$inputs;SourceUnchanged=$unchanged;Module=$dll;ModuleSHA256=$moduleHash;ModuleUnchanged=((Get-FileHash -LiteralPath $dll).Hash -ceq $moduleHash);Tests=$results.ToArray()}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
 if($run.ExitCode -ne 0 -or -not $unchanged){throw ('Browser regression failed: '+$qa)}
}
Write-Output ('BROWSER_ENTRY_PROOF='+$qa)
