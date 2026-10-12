param()
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $projectRoot
$root=$projectRoot
$sourceRoot=Join-Path $projectRoot 'modules-src';$compiler=Join-Path $PSScriptRoot 'compiler/csc.exe'
$qa=Join-Path $projectRoot ('build/qa-maintenance-access-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$moduleBuilder=Join-Path $PSScriptRoot 'Build-UiModules.ps1';$bounded=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
foreach($pair in @(@($moduleBuilder,'Get-ModuleSources'),@($moduleBuilder,'Build-Module'),@($bounded,'Assert-RegressionUnlinkedPath'),@($bounded,'Invoke-RegressionExecutable'))){
 $tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($pair[0],[ref]$tokens,[ref]$errors)
 if($errors.Count){throw 'Offline helper parse failed.'}
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $pair[1])
 if($node.Count -ne 1){throw 'Offline helper identity failed.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$hostSources=@(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' -File|Sort-Object FullName|ForEach-Object FullName)
$analysisSources=Get-ModuleSources 'DataAnalysis' @('src','vendor/PdMap','vendor/PdMap/Common','vendor/Spectrum','vendor/Task7','vendor/Task28','vendor/LocationTree','shared')
$analysisSources+=@('ZeroVoltageNoiseReference.cs','DefectModelCatalog.cs','AnalysisDateText.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','NoisePulseTemplates.cs','LiveCalibrationRecord.cs','CalibrationPacketIdentityContract.cs','LegacyCalibrationBinding.cs','CableJointStore.cs','CableJointDialog.cs'|ForEach-Object{Join-Path $projectRoot ('src/'+$_)})
$analysisSources+=Join-Path $sourceRoot 'LocationABCD/ABRelease/AB/source/PeakAnchoredLeadingEdgeMath.cs'
$analysisSources+=Join-Path $sourceRoot 'ConclusionAlgorithm/EvidenceV3/HVX.Task15.Embedded.cs'
$test=Join-Path $projectRoot 'tests/MaintenanceAdvancedAccessPublishedSmoke.cs';$brand=Join-Path $projectRoot 'assets/xiongxin.ico'
$legacy=Join-Path $sourceRoot 'LocationABCD/ABRelease/confirmed-legacy-inputs.json'
$inputs=$hostSources+$analysisSources+@($test,$brand,$legacy,$PSCommandPath,$moduleBuilder,$bounded,$compiler)
function Inventory { @($inputs|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}}) }
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
New-Item -ItemType Directory -Path $qa|Out-Null
$OutputRoot=Join-Path $qa 'modules';$records=New-Object 'System.Collections.Generic.List[object]'
$common=@('/nologo','/noconfig','/langversion:7.3','/optimize+','/deterministic+','/target:library','/platform:x64','/define:MAINTENANCE_PASSWORD_GUARD',
 '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:Accessibility.dll',
 '/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Microsoft.CSharp.dll')
$pins=Join-Path $qa 'legacy-unit-pins.json';@((Get-Content -Raw -Encoding UTF8 -LiteralPath $legacy|ConvertFrom-Json)|ForEach-Object{$_.sha256})|ConvertTo-Json|Set-Content -LiteralPath $pins -Encoding UTF8
$analysis=Build-Module 'DataAnalysis' 'HVX.PdReview.V1.0' $analysisSources @() @(($brand+',XIONGXIN.Brand.ico'),($pins+',HVX.LegacyUnitPins'))
$hostDll=Join-Path $qa 'CurrentUiHost.dll';$rsp=Join-Path $qa 'host.rsp'
@($common+@(('/out:'+$hostDll),('/resource:'+$brand+',Brand.ico'))+$hostSources|ForEach-Object{'"'+$_+'"'})|Set-Content -LiteralPath $rsp -Encoding UTF8
$lines=@(& $compiler ('@'+$rsp) 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa 'host.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($lines -join "`n")}
$exe=Join-Path $qa 'MaintenanceAdvancedAccessPublishedSmoke.exe'
$testCommon=@($common|Where-Object{$_ -notmatch '^/target:|^/define:'})
$lines=@(& $compiler @testCommon /target:exe ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa 'test.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($lines -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa,(Join-Path $qa 'case'),$hostDll) -LogPrefix (Join-Path $qa 'MaintenanceAdvancedAccessPublishedSmoke') -TimeoutMilliseconds 60000
$after=@(Inventory);$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq ($after|ConvertTo-Json -Depth 5 -Compress))
$proof=Join-Path $qa 'verification-maintenance-focused.json'
[ordered]@{Schema='HVX.MaintenanceAccess.Focused/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');
 Status=$(if($run.ExitCode -eq 0 -and $unchanged){'PASS'}else{'FAIL'});SourceUnchanged=$unchanged;Inputs=$before;
 HostSHA256=(Get-FileHash -LiteralPath $hostDll).Hash;AnalysisSHA256=(Get-FileHash -LiteralPath $analysis).Hash;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash;
 CompileExit=0;Result=$run;HardwareAccessed=$false;ProductExecuted=$false;SDKCalled=$false;UserSettingsWritten=$false;CredentialMaterialIncluded=$false;
 PrivateQaLibrariesOnly=$true;DeliveryBuilt=$false;KnownCorrectCredentialEntered=$false;FullRegression=$false}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $proof -Encoding UTF8
$run.Output|Write-Output;Write-Output ('MAINTENANCE_FOCUSED_PROOF='+$proof)
if($run.ExitCode -ne 0 -or -not $unchanged){throw ('Maintenance focused checks failed: '+$proof)}
