param([ValidateSet('OemLogoPresentationSmoke','OemBrandingSmoke','SettingsUiLanguageTreeSmoke','CustomerAssetsRoutingSmoke')][string[]]$TestClass=@('OemLogoPresentationSmoke','OemBrandingSmoke','SettingsUiLanguageTreeSmoke','CustomerAssetsRoutingSmoke'))
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-oem-logo-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$runner=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$sources=@(Get-ChildItem -LiteralPath 'src' -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$names=@($TestClass)
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe';$brand=Join-Path $root 'assets\xiongxin.ico'
$neutral=Join-Path $root 'assets\shortcuts\256.ico'
$inputs=$sources+@($names|ForEach-Object{Join-Path $root ('tests\'+$_+'.cs')})+@($PSCommandPath,$runner,$csc,$brand,$neutral)
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);New-Item -ItemType Directory -Path $qa|Out-Null;$runs=@()
$hostLibrary=Join-Path $qa 'CurrentUiHost.dll'
$libraryArgs=@('/nologo','/langversion:7.3','/platform:x64','/target:library',('/out:'+$hostLibrary),('/resource:'+$brand+',Brand.ico'),('/resource:'+$neutral+',HVX.NeutralWindow.ico'),
 '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')+$sources
$libraryRsp=$hostLibrary+'.rsp';@($libraryArgs|ForEach-Object{'"'+$_+'"'})|Set-Content -LiteralPath $libraryRsp -Encoding UTF8
$libraryCompile=@(& $csc ('@'+$libraryRsp) 2>&1|ForEach-Object{$_.ToString()});$libraryCode=$LASTEXITCODE
$libraryCompile|Set-Content -LiteralPath ($hostLibrary+'.compile.txt') -Encoding UTF8
if($libraryCode -ne 0){throw ($libraryCompile -join "`n")}
foreach($name in $names){
 $exe=Join-Path $qa ($name+'.exe');$rsp=$exe+'.rsp';$test=Join-Path $root ('tests\'+$name+'.cs')
 $arguments=@('/nologo','/langversion:7.3','/platform:x64','/target:exe',('/main:'+$name),('/out:'+$exe),('/resource:'+$brand+',Brand.ico'),('/resource:'+$neutral+',HVX.NeutralWindow.ico'),
  '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')+$sources+@($test)
 @($arguments|ForEach-Object{'"'+$_+'"'})|Set-Content -LiteralPath $rsp -Encoding UTF8
 $compile=@(& $csc ('@'+$rsp) 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE;$compile|Set-Content -LiteralPath ($exe+'.compile.txt') -Encoding UTF8
 if($code -ne 0){throw ($compile -join "`n")}
 $fixture=Join-Path $qa ($name+'-fixture')
 $testArguments=@($fixture,$fixture);if($name -eq 'SettingsUiLanguageTreeSmoke'){$testArguments=@($qa,$fixture)}
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments $testArguments -LogPrefix (Join-Path $qa $name) -TimeoutMilliseconds 60000
 $runs+=,[ordered]@{Name=$name;Result=$run};$run.Output|Write-Output
}
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress));$passed=@($runs|Where-Object{$_.Result.ExitCode -ne 0}).Count -eq 0
[ordered]@{Schema='HVX.OemLogo.Focused/1';CompletedUtc=[DateTime]::UtcNow.ToString('o');Status=$(if($passed -and $unchanged){'PASS'}else{'FAIL'});
 SourceUnchanged=$unchanged;Inputs=$before;Runs=$runs;HardwareAccessed=$false;SDKCalled=$false;ProductEntryStarted=$false;DeliveryBuilt=$false;
 Scope='PNG alpha, exact transparent/white blank perimeter trimming, retain all nonblank pixels and exact original bytes, unchanged Zoom viewport/contain, original/cropped sizes/aspects, async locale preview; quotas/bad input/config safety.'}|
 ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'verification-oem-logo.json') -Encoding UTF8
Write-Output ('OEM_LOGO_PROOF='+$qa)
if(-not $passed -or -not $unchanged){throw ('OEM logo regression failed: '+$qa)}
