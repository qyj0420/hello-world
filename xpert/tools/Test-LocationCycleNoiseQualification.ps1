param([string]$RecordedDirectory='D:\OWTSData\软件测试-软件\2026-10-07_13-23-28\Acquisition\Pressure\20261009-223411-827-c4150c8a86b247919c1302ae3b4252a2')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$qa=Join-Path $root ('build\qa-cycle-noise-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
New-Item -ItemType Directory -Path $qa | Out-Null
$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$source=Join-Path $root 'src\LocationCycleRecord.cs'
$inputs=@($source,(Join-Path $root 'tests\LocationCycleWindowSmoke.cs'),(Join-Path $root 'tests\LocationCycleNoiseQualificationSmoke.cs'),$PSCommandPath,$compiler)
$before=@($inputs | ForEach-Object {[ordered]@{File=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$rawInputs=@('capture.bin','settings.xml'|ForEach-Object {Join-Path $RecordedDirectory $_})
$rawBefore=@($rawInputs|ForEach-Object {[ordered]@{File=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$results=[Collections.Generic.List[object]]::new()
foreach($name in @('LocationCycleWindowSmoke','LocationCycleNoiseQualificationSmoke')) {
 $exe=Join-Path $qa ($name+'.exe')
 & $compiler /nologo /langversion:7.3 /target:exe /platform:x64 ('/main:'+$name) ('/out:'+$exe) /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Xml.dll $source (Join-Path $root ('tests\'+$name+'.cs')) 2>&1 | Out-File -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($LASTEXITCODE -ne 0){throw ('Compilation failed: '+$name)}
 $arguments=@(if($name -eq 'LocationCycleNoiseQualificationSmoke'){$RecordedDirectory})
 $stdout=Join-Path $qa ($name+'.stdout.txt');$stderr=Join-Path $qa ($name+'.stderr.txt')
 $start=@{FilePath=$exe;WindowStyle='Hidden';PassThru=$true;RedirectStandardOutput=$stdout;RedirectStandardError=$stderr}
 if($arguments.Count){$start.ArgumentList=($arguments|ForEach-Object {'"'+$_+'"'}) -join ' '}
 $p=Start-Process @start
 if(-not $p.WaitForExit(55000)){$p.Kill();$p.WaitForExit();$code=-999}else{$p.WaitForExit();$code=$p.ExitCode}
 $results.Add([ordered]@{Name=$name;ExitCode=$code;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash;Output=(Get-Content -LiteralPath $stdout -Raw);Error=(Get-Content -LiteralPath $stderr -Raw)})
 Get-Content -LiteralPath $stdout;Get-Content -LiteralPath $stderr
}
$unchanged=(@($before|Where-Object {(Get-FileHash -LiteralPath $_.File).Hash -ne $_.SHA256}).Count -eq 0)
$rawUnchanged=(@($rawBefore|Where-Object {(Get-FileHash -LiteralPath $_.File).Hash -ne $_.SHA256}).Count -eq 0)
$pass=$unchanged -and $rawUnchanged -and @($results|Where-Object ExitCode -ne 0).Count -eq 0
[ordered]@{Schema='HVX.RawCrossingQualification/1';Status=$(if($pass){'PASS'}else{'FAIL'});Inputs=$before;OriginalRecordedInputs=$rawBefore;SourceUnchanged=$unchanged;OriginalInputsUnchanged=$rawUnchanged;ProductMainExecuted=$false;WorkerEntryExecuted=$false;NativeSdkExecuted=$false;DeviceOperations=0;Results=$results.ToArray();NotCovered=@('Actual devices','Final protected bundle integration','Other historical recorded captures')} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $qa 'verification-cycle-noise.json') -Encoding UTF8
Write-Host ('PROOF='+$qa)
if(-not $pass){throw 'Raw cycle qualification failed; original FAIL proof retained.'}
