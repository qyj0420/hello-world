param()
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-cycle-settings-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$runner=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$sources=@(Get-ChildItem -LiteralPath 'src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$test=Join-Path $root 'tests\CycleSettingsLimitSmoke.cs';$csc=Join-Path $PSScriptRoot 'compiler\csc.exe';$brand=Join-Path $root 'assets\xiongxin.ico'
$inputs=$sources+@($test,$PSCommandPath,$runner,$csc,$brand)
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);New-Item -ItemType Directory -Path $qa|Out-Null
$exe=Join-Path $qa 'CycleSettingsLimitSmoke.exe';$rsp=$exe+'.rsp'
$arguments=@('/nologo','/langversion:7.3','/platform:x64','/target:exe','/main:CycleSettingsLimitSmoke',('/out:'+$exe),('/resource:'+$brand+',Brand.ico'),
 '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')+$sources+@($test)
@($arguments|ForEach-Object{'"'+$_+'"'})|Set-Content -LiteralPath $rsp -Encoding UTF8
$compile=@(& $csc ('@'+$rsp) 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE;$compile|Set-Content -LiteralPath ($exe+'.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compile -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa,(Join-Path $qa 'fixture')) -LogPrefix (Join-Path $qa 'CycleSettingsLimitSmoke') -TimeoutMilliseconds 60000
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress));$passed=$run.ExitCode -eq 0
[ordered]@{Schema='HVX.CycleSettings.Focused/1';CompletedUtc=[DateTime]::UtcNow.ToString('o');Status=$(if($passed -and $unchanged){'PASS'}else{'FAIL'});
 SourceUnchanged=$unchanged;Inputs=$before;Run=$run;HardwareAccessed=$false;SDKCalled=$false;ProductEntryStarted=$false;DeliveryBuilt=$false;
 Scope='One operator cycle setting1..5/default1 shared by saving and range evaluation; legacy XML retained; no independent range editor; legacy valid6..100 recorded migration5; no SDK/product entry.'}|
 ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'verification-cycle-settings.json') -Encoding UTF8
$run.Output|Write-Output;Write-Output ('CYCLE_SETTINGS_PROOF='+$qa)
if(-not $passed -or -not $unchanged){throw ('Cycle settings regression failed: '+$qa)}
