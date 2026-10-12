param([string]$OutputRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if([String]::IsNullOrWhiteSpace($OutputRoot)){$OutputRoot=Join-Path $root ('build\r51-pressure-page-exit-'+[Guid]::NewGuid().ToString('N').Substring(0,10))}
$output=[IO.Path]::GetFullPath($OutputRoot)
if(-not $output.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output must remain under project build.'}
if(Test-Path -LiteralPath $output){throw 'Existing evidence must not be overwritten.'}
$workerFiles=@(Get-ChildItem -LiteralPath (Join-Path $root 'modules-src\FactoryWorker') -Recurse -File -Filter '*.cs'|Sort-Object FullName)
$hostFiles=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -File -Filter '*.cs'|Sort-Object FullName)
$hostFiles+=Get-Item -LiteralPath (Join-Path $root 'tests\UnifiedPressureEmergencyR50Integration.cs')
$hostFiles+=Get-Item -LiteralPath (Join-Path $root 'tests\PressurePageExitManagedTests.cs')
$files=$workerFiles+$hostFiles
$inputs=@($files|ForEach-Object{[ordered]@{Path=$_.FullName;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
New-Item -ItemType Directory -Path $output|Out-Null
$exe=Join-Path $output 'R51.PressurePageExit.ManagedTests.exe'
$worker=Join-Path $output 'R51.FactoryWorker.ManagedOnly.dll'
$compiler=Join-Path $root 'tools\compiler\csc.exe'
$started=[DateTime]::UtcNow.ToString('o')
$common=@('/nologo','/langversion:latest','/platform:x64','/optimize+','/deterministic+',
 '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll',
 '/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
$workerCompiled=@(& $compiler @common /target:library ('/out:'+$worker) @($workerFiles.FullName) 2>&1|ForEach-Object{$_.ToString()})
$workerCompile=$LASTEXITCODE;$workerCompiled|Set-Content -LiteralPath (Join-Path $output 'worker.compile.txt') -Encoding UTF8
if($workerCompile -ne 0){throw ($workerCompiled -join "`n")}
$compiled=@(& $compiler @common /target:exe /main:UnifiedPressureEmergencyR50Integration ('/out:'+$exe) @($hostFiles.FullName) 2>&1|ForEach-Object{$_.ToString()})
$compile=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $output 'compile.txt') -Encoding UTF8
$exit=$null;$run=@()
# This is a separate explicit Main with fake I/O/control/bridge. Never invoke
# Program.Main, UnifiedWorkerEntry.Main, Program.Worker or any native SDK.
if($compile -eq 0){$run=@(& $exe (Join-Path $output 'fixtures') $worker 2>&1|ForEach-Object{$_.ToString()});$exit=$LASTEXITCODE}
$run|Set-Content -LiteralPath (Join-Path $output 'stdout.txt') -Encoding UTF8
$unchanged=$true;foreach($entry in $inputs){if((Get-FileHash -LiteralPath $entry.Path).Hash -cne $entry.SHA256){$unchanged=$false}}
$proof=Join-Path $output 'verification-r51-pressure-page-exit.json'
[ordered]@{Schema='HVX.R51.PressurePageExit.ManagedIntegration/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');
 SourceInventory=$inputs;SourceUnchanged=$unchanged;CompileExit=$compile;ExitCode=$exit;Output=$run;
 Executable=$exe;ExecutableSHA256=$(if(Test-Path -LiteralPath $exe){(Get-FileHash -LiteralPath $exe).Hash}else{$null});WorkerLibrarySHA256=(Get-FileHash -LiteralPath $worker).Hash;
 ProductMainExecuted=$false;WorkerMainExecuted=$false;ProgramWorkerExecuted=$false;SDKCalled=$false;HardwareAccessed=$false;
 UserProjectsWritten=$false;UserSettingsWritten=$false;PhysicalAcceptance=$false;FullProductGUIExecuted=$false;
 Exercised=@('FactoryRunner fake I/O HVUP->physical LOW->HV OFF/AO0','actual UnifiedWorkerEntry.RunPressureRound callback adapter',
 'actual UnifiedWorkerEntry.PressureSession capability proxy','actual UnifiedWorkerEntry.RunResidentCore request/receipt protocol',
 'actual client bound stop-receipt classification','actual FactoryPressureSession scoped DI callback; in-page LOW preserves unique DI until explicit exit',
 'actual MainForm.ShowHomeAsync joins initial and phase-change preparations; awaits save/mailbox and original Close',
 'injected 50ms post-close interval blocks Home polling/navigation until complete',
 'cleanup failure and retired failure retain owner; one initialization failure modal',
 'actual MainForm window-close joins same release workflow; fake exact-node Home probe after successful release');
 Uncovered=@('Real hardware HV OFF/electrical discharge','SDK timing/USB/native resource cleanup','Full product pressure-page start/capture GUI','Other-PC startup failure cause')
}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $proof -Encoding UTF8
$run|Write-Output
Write-Output ('R51_PRESSURE_PAGE_EXIT_PROOF='+$proof)
if($compile -ne 0){throw ($compiled -join "`n")}
if($exit -ne 0 -or -not $unchanged){throw ('R51 isolated pressure-page regression failed or inputs changed: '+$output)}
