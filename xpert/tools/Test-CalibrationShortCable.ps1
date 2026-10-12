param([ValidateSet('CalibrationShortCableSmoke','CalibrationPacketTimingSmoke','CalibrationCustomerFeedbackSmoke','CalibrationReflectionBandwidthSmoke','CalibrationPipelineRegression','CalibrationInteractionRegressionSmoke','CalibrationCandidateQualitySmoke','CalibrationMeasurementFeedbackSmoke','CalibrationDisplayFeedbackLanguagesSmoke')][string]$TestClass='CalibrationShortCableSmoke')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-calibration-short-cable-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')) {
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Bounded runner missing.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing;New-Item -ItemType Directory -Path $qa|Out-Null
$sources=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','R2RoundedButton.cs',
 'PressureRecordPublication.cs','PdLinearHighPass.cs','PdLinearLowPass.cs','LiveCalibrationRecord.cs','LegacyCalibrationBinding.cs','NoisePulseTemplates.cs',
 'CalibrationPulseDisplayWindow.cs','PicoCaptureCadence.cs','CableJointStore.cs','CableJointDialog.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$sources+=@(Get-ChildItem -LiteralPath 'modules-src\CalibrationTest\src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$sources+=@(Get-ChildItem -LiteralPath 'modules-src\CalibrationStandalone' -File -Filter '*.cs'|Where-Object Name -ne 'Program.cs'|Sort-Object Name|ForEach-Object FullName)
$sources+=@(Get-ChildItem -LiteralPath 'modules-src\CalibrationStandalone\PicoCore' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$sources+=@('modules-src\LocationABCD\ABRelease\AB\source\PeakAnchoredLeadingEdgeMath.cs','tests\CalibrationArtNativeOwnershipSmoke.cs',('tests\'+$TestClass+'.cs'))
$resources=@('assets\xiongxin.ico,XIONGXIN.Brand.ico','modules-src\CalibrationStandalone\assets\xiongxin.ico,Brand.ico',
 'modules-src\CalibrationStandalone\assets\xiongxin-original.png,Brand.png',
 'modules-src\CalibrationTest\assets\approvedUI.png,HVX.CalibrationProgram.ApprovedUi.png',
 'modules-src\CalibrationTest\assets\brand.ico,HVX.CalibrationProgram.Xiongxin.ico')
$paths=$sources+@($resources|ForEach-Object{$_.Split(',')[0]})+@($PSCommandPath,$helper,'tools\compiler\csc.exe','tools\Generate-InternationalCatalog.ps1')+@(Get-ChildItem -LiteralPath 'assets\i18n' -Filter '*.packs.txt' -File|ForEach-Object FullName)
$inputs=@($paths|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=[IO.Path]::GetFullPath($_);SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$generatedCatalog=Join-Path $qa 'InternationalText.Regenerated.cs'
& .\tools\Generate-InternationalCatalog.ps1 -OutputPath $generatedCatalog|Out-Null
$currentCatalog=Get-Content -LiteralPath 'src\InternationalText.Generated.cs' -Raw -Encoding UTF8
$regeneratedCatalog=Get-Content -LiteralPath $generatedCatalog -Raw -Encoding UTF8
foreach($name in @('CoreRows','ExtraRows','CalibrationRows','CalibrationSafetyRows','CalibrationFlowRows','CalibrationShortCableRows')) {
 $pattern='(?s)static readonly string\[\] '+[Regex]::Escape($name)+' = \{(.*?)\r?\n\};'
 $currentBlock=[Regex]::Match($currentCatalog,$pattern);$generatedBlock=[Regex]::Match($regeneratedCatalog,$pattern)
 if(-not $currentBlock.Success -or -not $generatedBlock.Success){throw ('Missing generated caption table: '+$name)}
 $currentRows=@([Regex]::Matches($currentBlock.Groups[1].Value,'@"((?:[^"]|"")*)"')|ForEach-Object{$_.Groups[1].Value})
 $generatedRows=@([Regex]::Matches($generatedBlock.Groups[1].Value,'@"((?:[^"]|"")*)"')|ForEach-Object{$_.Groups[1].Value})
 if((ConvertTo-Json -InputObject $currentRows -Compress) -cne (ConvertTo-Json -InputObject $generatedRows -Compress)){throw ('Caption source regeneration differs: '+$name)}
}
$exe=Join-Path $qa 'CalibrationShortCableFakeOnly.exe'
$common=@('/nologo','/target:exe','/platform:x64','/langversion:7.3',('/main:'+$TestClass),'/r:System.dll','/r:System.Core.dll','/r:System.Xml.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',
 '/r:System.Web.Extensions.dll','/r:System.Xml.Linq.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll','/r:Microsoft.CSharp.dll')
$compiled=@(& (Join-Path $PSScriptRoot 'compiler\csc.exe') @common ('/out:'+$exe) @($resources|ForEach-Object{'/resource:'+$_}) @sources 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
$arguments=@((Join-Path $qa 'synthetic'))
if($TestClass -in @('CalibrationInteractionRegressionSmoke','CalibrationCandidateQualitySmoke','CalibrationMeasurementFeedbackSmoke')) {
 $module=Join-Path $qa 'modules\CalibrationTest';New-Item -ItemType Directory -Path $module|Out-Null
 Copy-Item -LiteralPath $exe -Destination (Join-Path $module 'Xiongxin.OWTS.CalibrationTest.dll')
 $arguments=@($qa,(Join-Path $qa 'synthetic'))
}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments $arguments -LogPrefix (Join-Path $qa 'short-cable') -TimeoutMilliseconds 90000
$changed=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count
$proof=Join-Path $qa 'proof.json'
[ordered]@{Schema='HVX.Calibration.ShortCable.Offline/1';UTC=[DateTime]::UtcNow.ToString('o');TestClass=$TestClass;SourceUnchanged=($changed -eq 0);Inputs=$inputs;Result=$run;
 ProductStarted=$false;WorkerStarted=$false;SDKCalled=$false;HardwareAccessed=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;FullRegression=$false;
 CatalogRegenerationMatches=$true;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $proof -Encoding UTF8
$run.Output|Write-Output;Write-Output ('CALIBRATION_SHORT_CABLE_PROOF='+$proof)
if($run.ExitCode -ne 0 -or $changed -ne 0){throw 'Calibration short-cable regression failed.'}
