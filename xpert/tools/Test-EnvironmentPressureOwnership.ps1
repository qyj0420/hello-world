param([string]$OutputRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if([String]::IsNullOrWhiteSpace($OutputRoot)){$OutputRoot=Join-Path $root ('build\r52-environment-owner-'+[Guid]::NewGuid().ToString('N').Substring(0,10))}
$output=[IO.Path]::GetFullPath($OutputRoot)
if(-not $output.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output must remain under project build.'}
if(Test-Path -LiteralPath $output){throw 'Existing evidence must not be overwritten.'}
New-Item -ItemType Directory -Path $output|Out-Null
$files=@('tests\EnvironmentAutoRefreshSmoke.cs','src\ModuleHost.Environment.cs','src\EnvironmentalReadings.cs')|ForEach-Object{Join-Path $root $_}
$sources=@($files|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$exe=Join-Path $output 'EnvironmentAutoRefresh.ManagedTests.exe'
$compile=@(& (Join-Path $root 'tools\compiler\csc.exe') /nologo /langversion:latest /target:exe /platform:x64 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:'+$exe) @files 2>&1|ForEach-Object{$_.ToString()})
$compileExit=$LASTEXITCODE;$compile|Set-Content -LiteralPath (Join-Path $output 'compile.txt') -Encoding UTF8
$run=@();$exit=$null
if($compileExit -eq 0){$run=@(& $exe 2>&1|ForEach-Object{$_.ToString()});$exit=$LASTEXITCODE}
$run|Set-Content -LiteralPath (Join-Path $output 'stdout.txt') -Encoding UTF8
$unchanged=$true;foreach($entry in $sources){if((Get-FileHash -LiteralPath $entry.Path).Hash -cne $entry.SHA256){$unchanged=$false}}
$proof=Join-Path $output 'verification-environment-pressure-owner.json'
[ordered]@{Schema='HVX.R52.EnvironmentPressureOwner.Managed/1';CompletedUtc=[DateTime]::UtcNow.ToString('o');SourceInventory=$sources;
 SourceUnchanged=$unchanged;CompileExit=$compileExit;ExitCode=$exit;Output=$run;ProductMainExecuted=$false;SDKCalled=$false;NetworkAccessed=$false;HardwareAccessed=$false;
 PhysicalAcceptance=$false;Executable=$exe;ExecutableSHA256=$(if(Test-Path -LiteralPath $exe){(Get-FileHash -LiteralPath $exe).Hash}else{$null});
 Uncovered=@('Real ART task exclusion/cleanup and other-PC device availability','Full project environment GUI rendering')
}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $proof -Encoding UTF8
$run|Write-Output;Write-Output ('ENVIRONMENT_PRESSURE_OWNER_PROOF='+$proof)
if($compileExit -ne 0 -or $exit -ne 0 -or -not $unchanged){throw ('Managed environment ownership test failed: '+$output)}
