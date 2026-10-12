param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=Join-Path $root ('build\qa-r58-connector-busy-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
New-Item -ItemType Directory -Path $output|Out-Null
$connector=Join-Path $root 'modules-src\HardwareConnector'
$files=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -File -Filter '*.cs'|Where-Object Name -ne 'AssemblyInfo.cs'|Sort-Object Name|ForEach-Object FullName)
$files+=@('Program.cs','Display.cs','DeviceNames.cs','ConnectionHealth.cs','Workflow.cs','Localization.cs','LocaleCatalog.cs','ArtAliases.cs','ModuleIdentity.cs','Integration.cs','Integration.Discovery.cs','Integration.LocalInspection.cs')|ForEach-Object{Join-Path $connector $_}
$files+=Join-Path $root 'tests\R58ConnectorBusySmoke.cs'
$inputs=@(@($files)+@($PSCommandPath)|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$exe=Join-Path $output 'ConnectorBusyFakeOnly.exe'
$args=@('/nologo','/target:exe','/platform:x64','/langversion:7.3','/main:R58ConnectorBusySmoke',
 '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll',
 '/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.ServiceProcess.dll',('/out:'+$exe))
$compiled=@(& (Join-Path $PSScriptRoot 'compiler\csc.exe') @args @files 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $output 'compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compiled-join "`n")}
$start=New-Object Diagnostics.ProcessStartInfo
$start.FileName=$exe;$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
$process=New-Object Diagnostics.Process;$process.StartInfo=$start
if(!$process.Start()){throw 'Offline fixture did not start.'}
$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
if(!$process.WaitForExit(30000)){$process.Kill();throw 'Offline fixture exceeded 30 seconds.'}
$text=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult();$exit=$process.ExitCode;$process.Dispose()
$text|Set-Content -LiteralPath (Join-Path $output 'stdout.txt') -Encoding UTF8
$unchanged=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count -eq 0
[ordered]@{Schema='HVX.R58.ConnectorBusy.Offline/1';Utc=[DateTime]::UtcNow.ToString('o');Inputs=$inputs;SourceUnchanged=$unchanged;CompileExit=$code;ExitCode=$exit;Output=$text;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash;ProductStarted=$false;SDKCalled=$false;HardwareAccessed=$false;NetworkAccessed=$false;PhysicalAcceptance=$false;Uncovered=@('Actual multi-process SDK-family contention and hardware connection lifecycle')}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $output 'proof.json') -Encoding UTF8
$text|Write-Output
Write-Output ('R58_CONNECTOR_BUSY_PROOF='+(Join-Path $output 'proof.json'))
if($exit -ne 0 -or !$unchanged){throw 'Offline connector Busy checks failed or source changed.'}
