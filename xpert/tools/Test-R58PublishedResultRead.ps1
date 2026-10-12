param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=Join-Path $root ('build\qa-r58-published-result-read-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
New-Item -ItemType Directory -Path $output|Out-Null
$files=@(Get-ChildItem -LiteralPath (Join-Path $root 'modules-src\FactoryWorker') -Recurse -File -Filter '*.cs'|Sort-Object FullName|ForEach-Object FullName)
$files+=Join-Path $root 'tests\R58PublishedResultReadSmoke.cs'
$inputs=@(@($files)+@($PSCommandPath)|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$exe=Join-Path $output 'PublishedResultReadFakeOnly.exe'
$compiled=@(& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /target:exe /platform:x64 /langversion:latest /main:R58PublishedResultReadSmoke /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:'+$exe) @files 2>&1|ForEach-Object{$_.ToString()})
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
[ordered]@{Schema='HVX.R58.PublishedResultRead.Offline/1';Utc=[DateTime]::UtcNow.ToString('o');Inputs=$inputs;SourceUnchanged=$unchanged;CompileExit=$code;ExitCode=$exit;Output=$text;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash;ProductStarted=$false;SDKCalled=$false;HardwareAccessed=$false;NetworkAccessed=$false;PhysicalAcceptance=$false}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $output 'proof.json') -Encoding UTF8
$text|Write-Output
Write-Output ('R58_PUBLISHED_RESULT_READ_PROOF='+(Join-Path $output 'proof.json'))
if($exit -ne 0 -or !$unchanged){throw 'Offline published result checks failed or source changed.'}
