param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
if(-not $OutputRoot.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use isolated project build output.'}
if(Test-Path -LiteralPath $OutputRoot){throw 'Use a new output folder; retain prior evidence.'}
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$sources=@('src\PicoCaptureCadence.cs','modules-src\CalibrationStandalone\PicoCore\Models.cs','modules-src\HardwareLinkStress\Core\StressContracts.cs','modules-src\HardwareLinkStress\Core\TransferTierPlanner.cs','tests\PicoCaptureCadenceSmoke.cs')|ForEach-Object{Join-Path $root $_}
$reviewSources=@('modules-src\CalibrationStandalone\PicoCore\Engine.cs','src\ModuleHost.FactoryPressure.cs','src\ModuleHost.Task39Input.cs','modules-src\HardwareLinkStress\Adapters\PicoInputOnlyStressEndpoint.cs')|ForEach-Object{Join-Path $root $_}
$inputs=@(($sources+$reviewSources)|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$exe=Join-Path $OutputRoot 'PicoCaptureCadenceSmoke.exe'
$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$lines=@(& $compiler /nologo /target:exe /platform:x64 /langversion:7.3 /r:System.dll /r:System.Core.dll /r:System.Xml.dll ('/out:'+$exe) @sources 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE
$lines|Set-Content -LiteralPath (Join-Path $OutputRoot 'compile.txt') -Encoding UTF8
if($code-ne 0){throw ($lines-join "`n")}
# This EXE contains only the managed cadence policy and fake-clock tests. It
# neither references a native device API nor launches any product executable.
$process=Start-Process -FilePath $exe -ArgumentList @(('"'+(Join-Path $OutputRoot 'fixture')+'"'),('"'+$root+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $OutputRoot 'stdout.txt') -RedirectStandardError (Join-Path $OutputRoot 'stderr.txt')
if(-not $process.WaitForExit(30000)){ $process.Kill();$process.WaitForExit();throw 'Cadence fake test timeout.' }
$code=$process.ExitCode
$unchanged=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash-ne $_.SHA256}).Count-eq 0
[ordered]@{Schema='HVX.PicoCaptureCadence.QA/1';HardwareAccessed=$false;NativeCalls=$false;UserProjectsWritten=$false;Inputs=$inputs;InputsUnchanged=$unchanged;ExitCode=$code;ExecutableSHA256=(Get-FileHash -LiteralPath $exe).Hash}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $OutputRoot 'verification.json') -Encoding UTF8
Get-Content -LiteralPath (Join-Path $OutputRoot 'stdout.txt')
if($code-ne 0-or-not $unchanged){Get-Content -LiteralPath (Join-Path $OutputRoot 'stderr.txt');throw ('Cadence test failed: '+$OutputRoot)}
Write-Output ('CADENCE_PROOF='+$OutputRoot)
