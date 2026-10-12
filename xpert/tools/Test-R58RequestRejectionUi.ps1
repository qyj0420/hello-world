param([Parameter(Mandatory=$true)][string]$AnalysisPath)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=Join-Path $root ('build\qa-r58-request-rejection-ui-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$module=Join-Path $output 'modules\PressureTest';New-Item -ItemType Directory -Path $module|Out-Null
$analysis=[IO.Path]::GetFullPath($AnalysisPath)
$algorithm=Join-Path $root 'dependencies\Algorithms\Task17\fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'
$files=@(Get-ChildItem -LiteralPath (Join-Path $root 'modules-src\PressureTest\src') -Filter '*.cs' -File|Sort-Object FullName|ForEach-Object FullName)
$files+=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','ZeroVoltageNoiseReference.cs','LocationCycleRecord.cs','PressureRecordPublication.cs','R2RoundedButton.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$files+=Join-Path $root 'modules-src\FactoryWorker\AdjacentHardwareRangePolicy.cs'
$manifest=Get-Content -LiteralPath (Join-Path $root 'modules-src\Task39InputWorker\provenance\source-manifest.json') -Raw|ConvertFrom-Json
foreach($name in @('BenchModels.cs','PicoCaptureConfiguration.cs','LocationRangeAdvisor.cs','LocationRangeWindow.cs','RangeDecision.cs')){
    $entry=@($manifest.Files|Where-Object {$_.Path -ceq ('src/'+$name)});if($entry.Count -ne 1){throw 'Pinned managed input contract missing.'}
    $files+=& (Join-Path $PSScriptRoot 'Test-PressureTask39Contract.ps1') -Name $name -OriginalHash $entry[0].SHA256
}
$test=Join-Path $root 'tests\PressurePreparationOutcomeSmoke.cs'
$inventory=@($files)+@($test,$analysis,$algorithm,$PSCommandPath)|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}}
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$common=@('/nologo','/langversion:latest','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
$dll=Join-Path $module 'Xiongxin.OWTS.PressureTest.dll'
$compile=@(& $csc @common /target:library ('/out:'+$dll) ('/r:'+$analysis) @files 2>&1|ForEach-Object{$_.ToString()})
$exit=$LASTEXITCODE;$compile|Set-Content -LiteralPath (Join-Path $output 'module-compile.txt') -Encoding UTF8;if($exit -ne 0){throw ($compile -join "`n")}
Copy-Item -LiteralPath $analysis -Destination (Join-Path $module 'HVX.PdReview.V1.0.dll')
Copy-Item -LiteralPath $algorithm -Destination (Join-Path $module 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll')
$exe=Join-Path $output 'PressurePreparationOutcomeSmoke.exe'
$compile=@(& $csc @common /target:exe /main:PressurePreparationOutcomeSmoke ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()})
$exit=$LASTEXITCODE;$compile|Set-Content -LiteralPath (Join-Path $output 'test-compile.txt') -Encoding UTF8;if($exit -ne 0){throw ($compile -join "`n")}
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){$node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name);if($node.Count -ne 1){throw 'Missing bounded regression runner.'};. ([ScriptBlock]::Create($node[0].Extent.Text))}
$scenarios='request-rejected,request-rejected-unverified,request-rejected-di-error,committed,round-stopped,round-stopped-unverified,round-stopped-di-error'
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($output,(Join-Path $output 'cases'),$scenarios) -LogPrefix (Join-Path $output 'ui') -TimeoutMilliseconds 60000
$unchanged=$true;foreach($entry in $inventory){if((Get-FileHash -LiteralPath $entry.Path).Hash -cne $entry.SHA256){$unchanged=$false}}
[ordered]@{Schema='HVX.R58.RequestRejectionUI.Offline/1';Utc=[DateTime]::UtcNow.ToString('o');Inputs=$inventory;SourceUnchanged=$unchanged;Execution=$run;ModuleSHA256=(Get-FileHash -LiteralPath $dll).Hash;ProductStarted=$false;SDKCalled=$false;HardwareAccessed=$false;WorkersStarted=0;PhysicalAcceptance=$false;Uncovered=@('Actual device safety and GUI rendering on customer computer','Final executable integration')}|ConvertTo-Json -Depth 9|Set-Content -LiteralPath (Join-Path $output 'proof.json') -Encoding UTF8
$run.Output|Write-Output;Write-Output ('R58_REQUEST_REJECTION_UI_PROOF='+$output)
if($run.ExitCode -ne 0 -or -not $unchanged){throw 'Request rejection UI regression failed.'}
