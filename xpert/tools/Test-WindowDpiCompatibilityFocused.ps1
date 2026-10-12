$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-window-dpi-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name);if($node.Count -ne 1){throw 'Missing bounded offline helper.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File|Sort-Object FullName|ForEach-Object FullName)
$names=@('WindowLayoutMemorySmoke','DpiLayoutSmoke');$tests=@($names|ForEach-Object{Join-Path $root ('tests\'+$_+'.cs')})
$icon=Join-Path $root 'assets\xiongxin.ico';$logo=Join-Path $root 'assets\xiongxin-original.png';$manifest=Join-Path $root 'app.manifest';$config=Join-Path $root 'App.config'
$inputs=$sources+$tests+@($icon,$logo,$manifest,$config,$PSCommandPath)
function Inventory{@($inputs|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o');New-Item -ItemType Directory -Path $qa|Out-Null
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe';$common=@('/nologo','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$results=New-Object 'System.Collections.Generic.List[object]';$failed=$false
foreach($name in $names){
 $exe=Join-Path $qa ($name+'.exe');$rsp=Join-Path $qa ($name+'.rsp');$test=Join-Path $root ('tests\'+$name+'.cs')
 @($common+@('/target:exe',('/main:'+$name),('/win32manifest:'+$manifest),('/resource:'+$icon+',Brand.ico'),('/resource:'+$logo+',Brand.png'),('/out:'+$exe))+$sources+@($test)|ForEach-Object{'"'+$_+'"'})|Set-Content -LiteralPath $rsp -Encoding UTF8
 $lines=@(& $csc ('@'+$rsp) 2>&1|ForEach-Object{$_.ToString()});$compile=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($compile -ne 0){throw ($lines -join "`n")}
 Copy-Item -LiteralPath $config -Destination ($exe+'.config')
 # Test entry only: constructors/display and synthetic QA files. No product
 # Program.Main, SDK, worker entry, device connection or user-project writes.
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments @((Join-Path $qa ('case-'+$name))) -LogPrefix (Join-Path $qa $name) -TimeoutMilliseconds 60000
 $results.Add([ordered]@{Name=$name;SourceSHA256=(Get-FileHash -LiteralPath $test).Hash;CompileExit=$compile;ExitCode=$run.ExitCode;Output=$run.Output});$run.Output|Write-Output;if($run.ExitCode -ne 0){$failed=$true}
}
$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))
$proof=Join-Path $qa 'verification-window-dpi.json'
[ordered]@{Schema='HVX.Window.DpiCompatibility.Focused/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');HardwareAccessed=$false;SDKCalled=$false;ProductEntryStarted=$false;UserProjectsWritten=$false;SourceUnchanged=$unchanged;PhysicalDpiTested=$false;SimulatedDpiMetricsTested=$true;FullRegression=$false;Inputs=$before;Tests=$results.ToArray()}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output ('WINDOW_DPI_PROOF='+$proof)
if($failed -or -not $unchanged){throw ('Window/DPI focused regression failed: '+$qa)}
