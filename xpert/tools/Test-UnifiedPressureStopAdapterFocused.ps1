param([string]$OutputRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if([String]::IsNullOrWhiteSpace($OutputRoot)){$OutputRoot=Join-Path $root ('build\qa-unified-stop-adapter-'+[Guid]::NewGuid().ToString('N').Substring(0,10))}
$output=[IO.Path]::GetFullPath($OutputRoot)
if(-not $output.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be a new project build subdirectory.'}
if(Test-Path -LiteralPath $output){throw 'Existing evidence must not be overwritten.'}
$files=@(Get-ChildItem -LiteralPath (Join-Path $root 'modules-src\FactoryWorker') -Recurse -File -Filter '*.cs'|Sort-Object FullName)
$files+=Get-Item -LiteralPath (Join-Path $root 'tests\UnifiedPressureStopAdapterFocused.cs')
$inputs=@($files|ForEach-Object{[ordered]@{Path=$_.FullName;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
New-Item -ItemType Directory -Path $output|Out-Null
$exe=Join-Path $output 'UnifiedPressureStopAdapterFocused.exe'
$compiler=Join-Path $root 'tools\compiler\csc.exe'
$start=[DateTime]::UtcNow.ToString('o')
$lines=@(& $compiler /nologo /langversion:latest /target:exe /main:UnifiedPressureStopAdapterFocused /platform:x64 /optimize+ /deterministic+ /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ('/out:'+$exe) @($files.FullName) 2>&1|ForEach-Object{$_.ToString()})
$compile=$LASTEXITCODE
$lines|Set-Content -LiteralPath (Join-Path $output 'compile.txt') -Encoding UTF8
$exit=$null;$run=@()
if($compile -eq 0){$run=@(& $exe 2>&1|ForEach-Object{$_.ToString()});$exit=$LASTEXITCODE}
$run|Set-Content -LiteralPath (Join-Path $output 'stdout.txt') -Encoding UTF8
$unchanged=$true;foreach($entry in $inputs){if((Get-FileHash -LiteralPath $entry.Path).Hash -cne $entry.SHA256){$unchanged=$false}}
[ordered]@{Schema='HVX.UnifiedPressureStopAdapter.Focused/1';StartedUtc=$start;CompletedUtc=[DateTime]::UtcNow.ToString('o');
 SourceInventory=$inputs;SourceUnchanged=$unchanged;CompileExit=$compile;ExitCode=$exit;Output=$run;
 Executable=$exe;ExecutableSHA256=$(if(Test-Path -LiteralPath $exe){(Get-FileHash -LiteralPath $exe).Hash}else{$null});
 HardwareAccessed=$false;SDKCalled=$false;ProductExecuted=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;FinalPackageVerified=$false}|
 ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $output 'verification-unified-stop-adapter.json') -Encoding UTF8
$run|Write-Output
if($compile -ne 0 -or $exit -ne 0 -or -not $unchanged){throw ('Focused production adapter regression failed: '+$output)}
Write-Output ('UNIFIED_STOP_ADAPTER_PROOF='+$output)
