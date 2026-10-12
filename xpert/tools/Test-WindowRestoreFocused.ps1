param([switch]$Baseline)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-window-restore-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name);if($node.Count -ne 1){throw 'Missing bounded runner helper.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$hostSources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File|Sort-Object FullName|ForEach-Object FullName)
$projectSources=@('UiLanguage.cs','InternationalText.cs','InternationalText.Generated.cs','ProjectMeasurementMode.cs','OemUiBranding.cs','PressureRecordPublication.cs','CableJointStore.cs','CableJointDialog.cs','CableVisualStyle.cs'|ForEach-Object{Join-Path $root ('src\'+$_)})
$projectSources+=@(Get-ChildItem -LiteralPath (Join-Path $root 'modules-src\ProjectCreation\src') -Filter '*.cs' -File|Sort-Object FullName|ForEach-Object FullName)
$brand=Join-Path $root 'assets\xiongxin.ico';$image=Join-Path $root 'assets\xiongxin-capacitor-hd.png';$test=Join-Path $root 'tests\WindowMinimizeRestoreSmoke.cs'
$inputs=$hostSources+$projectSources+@($brand,$image,$test,$PSCommandPath)
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o');New-Item -ItemType Directory -Path $qa|Out-Null
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe';$common=@('/nologo','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
foreach($module in @(@{Name='CurrentUiHost';Sources=$hostSources;Resources=@(('/resource:'+$brand+',Brand.ico'))},@{Name='ProjectCreation';Sources=$projectSources;Resources=@(('/resource:'+$brand+',XIONGXIN.Brand.ico'),('/resource:'+$image+',XIONGXIN.Capacitor.png'))})){
 $rsp=Join-Path $qa ($module.Name+'.rsp');$dll=Join-Path $qa ($module.Name+'.dll')
 @($common+@('/target:library',('/out:'+$dll))+$module.Resources+$module.Sources|ForEach-Object{'"'+$_+'"'})|Set-Content -LiteralPath $rsp -Encoding UTF8
 $compiled=@(& $csc ('@'+$rsp) 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa ($module.Name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ($compiled -join "`n")}
}
$exe=Join-Path $qa 'WindowMinimizeRestoreSmoke.exe';$compiled=@(& $csc @common /target:exe /main:WindowMinimizeRestoreSmoke ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()});if($LASTEXITCODE -ne 0){throw ($compiled -join "`n")}
$runArgs=@($qa,(Join-Path $qa 'case'));if($Baseline){$runArgs+='baseline'}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments $runArgs -LogPrefix (Join-Path $qa 'WindowMinimizeRestoreSmoke') -TimeoutMilliseconds 30000
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
[ordered]@{Schema='HVX.Window.Restore.Focused/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Baseline=[bool]$Baseline;HardwareAccessed=$false;SDKCalled=$false;ProductEntryStarted=$false;UserProjectsWritten=$false;SourceUnchanged=$unchanged;PhysicalDpiTested=$false;FullRegression=$false;Inputs=$before;Result=$run}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'verification-window-restore.json') -Encoding UTF8
$run.Output|Write-Output;Write-Output ('WINDOW_RESTORE_PROOF='+(Join-Path $qa 'verification-window-restore.json'))
if($run.ExitCode -ne 0 -or -not $unchanged){throw ('Window focused regression failed: '+$qa)}
