param([Parameter(Mandatory=$true)][string]$BuildRoot,[switch]$Pipeline,[switch]$NoisePolicy)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$BuildRoot=[IO.Path]::GetFullPath($BuildRoot)
if(-not $BuildRoot.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use a project-local build root.'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')) {
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
if($Pipeline -and $NoisePolicy){throw 'Choose one offline fixture.'}
$caseName=if($Pipeline){'pipeline'}elseif($NoisePolicy){'noise-40ms'}else{'capture-replay'}
$qa=Join-Path $BuildRoot ('qa\'+$caseName+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
Assert-RegressionUnlinkedPath $qa -AllowMissing
New-Item -ItemType Directory -Path $qa | Out-Null
$testSource=if($NoisePolicy){'tests\CalibrationNoise40MsSmoke.cs'}else{'tests\CalibrationAcquisitionFlowSmoke.cs'}
$sources=@($testSource,'src\NoisePulseTemplates.cs','src\PdLinearHighPass.cs','src\PdLinearLowPass.cs','src\ProjectMeasurementMode.cs')
$sources+=@(Get-ChildItem -LiteralPath 'modules-src\CalibrationStandalone' -Filter '*.cs' -File|Where-Object Name -notin @('Program.cs','ControlBoxTests.cs')|ForEach-Object FullName)
$sources+=@(Get-ChildItem -LiteralPath 'modules-src\CalibrationStandalone\PicoCore' -Filter '*.cs' -File|ForEach-Object FullName)
if($Pipeline) {
 $sources=@('tests\CalibrationPipelineRegression.cs','src\CableJointStore.cs','src\NoisePulseTemplates.cs','src\LiveCalibrationRecord.cs',
 'modules-src\CalibrationTest\src\CalibrationProjectStore.cs','modules-src\CalibrationStandalone\AcquisitionPlanning.cs',
 'modules-src\CalibrationStandalone\CalibrationAcquisitionFlow.cs','modules-src\CalibrationStandalone\CalibrationWindowing.cs',
 'modules-src\CalibrationStandalone\PicoCore\Models.cs','modules-src\CalibrationStandalone\PicoCore\CaptureVoltage.cs','modules-src\CalibrationStandalone\ProjectDefaults.cs',
 'modules-src\CalibrationStandalone\TdrCorrelation.cs','src\PdLinearHighPass.cs','src\PdLinearLowPass.cs','src\ProjectMeasurementMode.cs')
}
$sources+=@('src\PicoCaptureCadence.cs','src\OemUiBranding.cs','src\R2RoundedButton.cs')
$inputPaths=@($sources)+@($PSCommandPath,(Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),
    (Join-Path $root 'modules-src\CalibrationStandalone\assets\xiongxin.ico'),(Join-Path $root 'modules-src\CalibrationStandalone\assets\xiongxin-original.png'))+
    @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'compiler') -File|Where-Object Extension -in @('.exe','.dll','.rsp','.config')|ForEach-Object FullName)
$inputs=@($inputPaths|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=[IO.Path]::GetFullPath($_);SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$exe=Join-Path $qa 'CaptureReplay.exe'
$lines=@(& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /target:exe /platform:x64 /langversion:7.3 /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:Accessibility.dll ('/out:'+$exe) '/resource:modules-src\CalibrationStandalone\assets\xiongxin.ico,Brand.ico' '/resource:modules-src\CalibrationStandalone\assets\xiongxin-original.png,Brand.png' $sources 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($code -ne 0){throw ($lines -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @((Join-Path $qa 'case')) -LogPrefix (Join-Path $qa 'capture')
$mismatches=@($inputs|Where-Object {(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count
$unchanged=$mismatches -eq 0
[ordered]@{Schema='HVX.Calibration.CaptureReplay/2';Test=$caseName;HardwareAccessed=$false;ProductMainExecuted=$false;SdkOrActualWorkerExecuted=$false;OutputTasksCreated=$false;UserProjectsWritten=$false;SourceUnchanged=$unchanged;InputMismatchCount=$mismatches;Inputs=$inputs;TestExecutableSHA256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash;Result=$run}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
$run.Output|Write-Output
if($run.ExitCode -ne 0 -or -not $unchanged){throw ('Capture replay failed; '+$qa)}
Write-Output ('CAPTURE_REPLAY_PROOF='+$qa)
