param(
 [string]$AnalysisReference='D:\codex\Task28_FilterB_20261010\qa\host\modules\DataAnalysis\HVX.PdReview.V1.0.dll',
 [string]$PressureReferenceRoot='D:\codex\HVXUnifiedDesktop\build\qa-pressure-value-refresh-cef2545de5\modules\PressureTest'
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-location-filter-settings-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$runner=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
Assert-RegressionUnlinkedPath $AnalysisReference
Assert-RegressionUnlinkedPath $PressureReferenceRoot
$pressureReferences=@(Get-ChildItem -LiteralPath $PressureReferenceRoot -Filter '*.dll' -File|Sort-Object FullName|ForEach-Object FullName)
if(-not @($pressureReferences|Where-Object{[IO.Path]::GetFileName($_) -eq 'Xiongxin.OWTS.PressureTest.dll'}).Count){throw 'Private isolated PressureTest reference is absent.'}
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File|Sort-Object FullName|ForEach-Object FullName)
$tests=@('LocationFilterSettingsSmoke','SettingsUiLanguageTreeSmoke')
$brand=Join-Path $root 'assets\xiongxin.ico';$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=$sources+@($tests|ForEach-Object{Join-Path $root ('tests\'+$_+'.cs')})+@($brand,$csc,$runner,$PSCommandPath,$AnalysisReference)+$pressureReferences
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
New-Item -ItemType Directory -Path $qa|Out-Null
$analysisDir=Join-Path $qa 'modules\DataAnalysis';$pressureDir=Join-Path $qa 'modules\PressureTest'
New-Item -ItemType Directory -Path $analysisDir,$pressureDir|Out-Null
Copy-Item -LiteralPath $AnalysisReference -Destination (Join-Path $analysisDir 'HVX.PdReview.V1.0.dll')
foreach($dll in $pressureReferences){Assert-RegressionUnlinkedPath $dll;Copy-Item -LiteralPath $dll -Destination (Join-Path $pressureDir ([IO.Path]::GetFileName($dll)))}
$common=@('/nologo','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll')
$dll=Join-Path $qa 'CurrentUiHost.dll';$rsp=Join-Path $qa 'private-host.rsp'
@($common+@('/target:library',('/resource:'+$brand+',Brand.ico'),('/out:'+$dll))+$sources|ForEach-Object{'"'+$_+'"'})|Set-Content -LiteralPath $rsp -Encoding UTF8
$compiled=@(& $csc ('@'+$rsp) 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE
$compiled|Set-Content -LiteralPath (Join-Path $qa 'host.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
$results=@()
foreach($name in $tests){
 $exe=Join-Path $qa ($name+'.exe');$test=Join-Path $root ('tests\'+$name+'.cs')
 $compiled=@(& $csc @common /target:exe ('/main:'+$name) ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE
 $compiled|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ($compiled -join "`n")}
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa,(Join-Path $qa ($name+'-case'))) -LogPrefix (Join-Path $qa $name) -TimeoutMilliseconds 45000
 $results+=,[ordered]@{Name=$name;Result=$run};$run.Output|Write-Output
}
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$failed=@($results|Where-Object{$_.Result.ExitCode -ne 0})
$proof=Join-Path $qa 'verification-location-filter-settings.json'
[ordered]@{Schema='HVX.LocationFilter.Settings.Focused/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');
 HardwareAccessed=$false;SDKCalled=$false;ProductEntryStarted=$false;UserProjectsWritten=$false;SourceOnlyChange=$true;PrivateQaLibrariesOnly=$true;
 DeliveryBuilt=$false;FullRegression=$false;SourceUnchanged=$unchanged;Status=$(if($failed.Count -eq 0 -and $unchanged){'PASS'}else{'FAIL'});
 Inputs=$before;HostSHA256=(Get-FileHash -LiteralPath $dll).Hash;Results=$results}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output ('LOCATION_FILTER_SETTINGS_PROOF='+$proof)
if($failed.Count -ne 0 -or -not $unchanged){throw ('Location filtering settings focused regression failed: '+$qa)}
