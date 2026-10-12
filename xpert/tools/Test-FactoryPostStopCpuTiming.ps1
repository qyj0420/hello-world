param([string]$OutputRoot='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
if(-not $OutputRoot){$OutputRoot=Join-Path $root ('build\qa-factory-poststop-cpu-'+[Guid]::NewGuid().ToString('N').Substring(0,10))}
if(-not [IO.Path]::IsPathRooted($OutputRoot)){$OutputRoot=Join-Path $root $OutputRoot}
$qa=[IO.Path]::GetFullPath($OutputRoot)
if(-not $qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $qa)){throw 'Output must be a new isolated project build directory.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Bounded regression helper parse failed.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded regression helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$source=Join-Path $root 'modules-src\FactoryWorker'
$factory=@(Get-ChildItem -LiteralPath $source -Recurse -File -Filter '*.cs'|Sort-Object FullName|ForEach-Object FullName)
$tests=@('FactoryPostStopCpuTimingSmoke.cs','FactoryHighPassLegacyOracle.cs','FactoryHighPassFastCandidate.cs')|ForEach-Object{Join-Path $root ('tests\'+$_)}
$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$gate=Join-Path $PSScriptRoot 'Test-FactoryBatchSource.ps1'
$inputs=@($factory)+@($tests)+@($PSCommandPath,$helper,$gate,(Join-Path $root 'App.config'))+
 @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'compiler') -File|ForEach-Object FullName)+
 @(Get-ChildItem -LiteralPath $source -File -Filter '*.json'|ForEach-Object FullName)
$inputs=@($inputs|Sort-Object -Unique)
function Inventory { @($inputs|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}}) }
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
$hpPath=Join-Path $source 'Imported\Unified100\PdLinearHighPass.cs'
$hpText=[IO.File]::ReadAllText($hpPath)
$exactDelta='            // Interior identity avoids modulo; keep old overflow semantics for'+"`n"+
 '            // extreme counts too (actual capture arrays are bounded to 50M).'+"`n"+
 '            if(index>=0 && index<count && count<=1073741824)return index;'+"`n"
if(([regex]::Matches($hpText,[regex]::Escape($exactDelta))).Count -ne 1){throw 'Expected one exact local HP fast-path delta.'}
$sha=[Security.Cryptography.SHA256]::Create()
try{$reversedHash=([BitConverter]::ToString($sha.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes($hpText.Replace($exactDelta,''))))).Replace('-','')}
finally{$sha.Dispose()}
$frozenHP='28780887CE26F71C84115A0E32E06DB9AFB9F85DD861E21E6C5F5B6FC6823A22'
if($reversedHash -cne $frozenHP){throw 'Reversing only the interior fast return must reproduce the exact frozen Factory HP bytes.'}
$identity=& $gate
New-Item -ItemType Directory -Path $qa|Out-Null
$exe=Join-Path $qa 'FactorySyntheticCpuOnly.exe'
$args=@('/nologo','/optimize+','/deterministic+','/langversion:latest','/platform:x64','/target:exe','/main:FactoryPostStopCpuTimingSmoke',
 '/r:System.dll','/r:System.Core.dll','/r:System.Xml.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',('/out:'+$exe))
$compile=@(& $compiler @args @factory @tests 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compile|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($code -ne 0){throw ($compile -join "`n")}
Copy-Item -LiteralPath (Join-Path $root 'App.config') -Destination ($exe+'.config')
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @() -LogPrefix (Join-Path $qa 'cpu') -TimeoutMilliseconds 120000
$after=@(Inventory);$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq ($after|ConvertTo-Json -Depth 5 -Compress))
$stages=@($run.Output -split '\r?\n'|Where-Object{$_ -like 'CPU_STAGE|*'}|ForEach-Object{
 $parts=$_ -split '\|';[ordered]@{Stage=$parts[1];Iteration=[int]$parts[2];ElapsedMs=[double]::Parse($parts[3],[Globalization.CultureInfo]::InvariantCulture);Clock='Stopwatch elapsed on synthetic CPU-only fixture';InputPdSamples=6250000;InputArtSamples=4000}
})
$summary=@($stages|Group-Object -Property {$_['Stage']}|ForEach-Object{
 $values=@($_.Group|ForEach-Object{$_['ElapsedMs']}|Sort-Object);[ordered]@{Stage=$_.Name;Count=$values.Count;MinimumMs=$values[0];MedianMs=$values[[int][Math]::Floor($values.Count/2)];MaximumMs=$values[-1]}
})
$proof=Join-Path $qa 'proof.json'
[ordered]@{Schema='HVX.Factory.PostStopSyntheticCpu/1';Status=$(if($run.ExitCode -eq 0 -and $unchanged){'PASS'}else{'FAIL'});
 StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');SourceUnchanged=$unchanged;Inputs=$before;FactorySourceIdentity=$identity;
 TestExecutableSHA256=(Get-FileHash -LiteralPath $exe).Hash;Execution=$run;Stages=$stages;Summary=$summary;
 ExactLocalDeltaReverseMatchedFrozen=$true;FrozenHighPassSHA256=$frozenHP;HighPassReverseSHA256=$reversedHash;
 HardwareAccessed=$false;SDKCalled=$false;WorkerEntryExecuted=$false;ProductEntryExecuted=$false;UserProjectsRead=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;FullRegression=$false;
 Limitations='Synthetic 50 ms / 125 MS/s arrays, not original field EXE or capture. Old frozen FIR retained test-only. Direct index random/extreme/overflow/short-domain cases plus algebraic identity interval, not exhaustive 2^64 cases. Full four-pass output compared bit-for-bit including 6250000 samples. Stage timers are independent overlapping scopes, not additive full-round timing; no promised field speedup.'}|
 ConvertTo-Json -Depth 20|Set-Content -LiteralPath $proof -Encoding UTF8
$run.Output|Write-Output;$summary|ConvertTo-Json -Depth 5|Write-Output;Write-Output ('FACTORY_CPU_PROOF='+$proof)
if($run.ExitCode -ne 0 -or -not $unchanged){throw ('Factory synthetic CPU regression failed; preserved evidence: '+$qa)}
