param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-settings-language-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File|Sort-Object FullName|ForEach-Object FullName)
$test=Join-Path $root 'tests\SettingsUiLanguageTreeSmoke.cs'
$brand=Join-Path $root 'assets\xiongxin.ico'
$inputs=$sources+@($test,$brand,$PSCommandPath)
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
New-Item -ItemType Directory -Path $qa|Out-Null
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$common=@('/nologo','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll')
$dll=Join-Path $qa 'CurrentUiHost.dll';$rsp=Join-Path $qa 'ui-only-host.rsp'
@($common+@('/target:library',('/resource:'+$brand+',Brand.ico'),('/out:'+$dll))+$sources|ForEach-Object{'"'+$_+'"'})|Set-Content -LiteralPath $rsp -Encoding UTF8
$compiled=@(& $csc ('@'+$rsp) 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE
$compiled|Set-Content -LiteralPath (Join-Path $qa 'host.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
$exe=Join-Path $qa 'SettingsUiLanguageTreeSmoke.exe'
$compiled=@(& $csc @common /target:exe /main:SettingsUiLanguageTreeSmoke ('/out:'+$exe) $test 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE
$compiled|Set-Content -LiteralPath (Join-Path $qa 'test.compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa,(Join-Path $qa 'case')) -LogPrefix (Join-Path $qa 'SettingsUiLanguageTreeSmoke') -TimeoutMilliseconds 30000
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
[ordered]@{Schema='HVX.Settings.UiLanguage.Focused/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');
 HardwareAccessed=$false;SDKCalled=$false;ProductEntryStarted=$false;UserProjectsWritten=$false;SourceUnchanged=$unchanged;FullRegression=$false;
 Inputs=$before;HostSHA256=(Get-FileHash -LiteralPath $dll).Hash;Result=$run}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'verification-settings-language.json') -Encoding UTF8
$run.Output|Write-Output
Write-Output ('SETTINGS_UI_PROOF='+(Join-Path $qa 'verification-settings-language.json'))
if($run.ExitCode -ne 0 -or -not $unchanged){throw ('Settings focused regression failed: '+$qa)}
