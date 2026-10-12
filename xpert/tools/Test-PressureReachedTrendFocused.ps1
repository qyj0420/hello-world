param([string]$OutputRoot,[switch]$ExpectMissingFlush)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if([String]::IsNullOrWhiteSpace($OutputRoot)){$OutputRoot=Join-Path $project ('build\qa-reached-trend-'+[Guid]::NewGuid().ToString('N').Substring(0,10))}
$output=[IO.Path]::GetFullPath($OutputRoot)
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be under this project build directory.'}
if(Test-Path -LiteralPath $output){throw 'Use a new isolated output directory.'}
$files=@(Get-ChildItem -LiteralPath (Join-Path $project 'modules-src\FactoryWorker') -Recurse -File -Filter '*.cs'|Sort-Object FullName)
$probe=Join-Path $PSScriptRoot 'PressureReachedTrendSmoke.cs'
$inputs=@($files.FullName)+@($probe,$PSCommandPath)
$inventory=@($inputs|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})
New-Item -ItemType Directory -Path $output|Out-Null
$assemblyPath=Join-Path $output 'PressureReachedTrend.Focused.dll'
$compiler=Join-Path $project 'tools\compiler\csc.exe'
$arguments=@('/nologo','/langversion:latest','/target:library','/platform:x64','/optimize+','/deterministic+',
    '/r:System.dll','/r:System.Core.dll','/r:System.Xml.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll',('/out:'+$assemblyPath))
& $compiler @arguments @($files.FullName) $probe
if($LASTEXITCODE -ne 0){throw 'Focused managed library compilation failed.'}
$assembly=[Reflection.Assembly]::LoadFrom($assemblyPath)
$method=$assembly.GetType('Xiongxin.FactoryTest.PressureReachedTrendSmoke',$true).GetMethod('Run',[Reflection.BindingFlags]'NonPublic,Static')
$passed=$false;$result='';$failure=''
try{$result=[string]$method.Invoke($null,$null);$passed=$result.Contains('PRESSURE_REACHED_TREND_PASS=')}
catch{$failure=$_.Exception.ToString();if($null -ne $_.Exception.InnerException){$failure=$_.Exception.InnerException.ToString()}}
if($passed){$result|Set-Content -LiteralPath (Join-Path $output 'test.stdout.txt') -Encoding UTF8}
else{$failure|Set-Content -LiteralPath (Join-Path $output 'test.failure.txt') -Encoding UTF8}
$unchanged=$true
foreach($entry in $inventory){if((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -cne $entry.SHA256){$unchanged=$false}}
$negativeObserved=(-not $passed -and $failure.Contains('last reached feedback must publish before ARM'))
[ordered]@{Schema='HVX.Factory.ReachedTrend.Focused/1';CapturedUtc=[DateTime]::UtcNow.ToString('o');SourceInputs=$inventory;
    Compiler=$compiler;CompiledAssembly=$assemblyPath;CompiledAssemblySHA256=(Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash;
    TestPassed=$passed;TestResult=$result;Failure=$failure;ExpectedMissingFlush=[bool]$ExpectMissingFlush;MissingFlushReproduced=$negativeObserved;
    InputsUnchanged=$unchanged;HardwareAccessed=$false;SDKCalled=$false;ProductEntryExecuted=$false;
    WorkerEntryExecuted=$false;ResidentStarted=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;FinalExeIntegration=$false;FullRegression=$false
}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'verification-reached-trend.json') -Encoding UTF8
Write-Output ('FocusedProof='+$output)
if(-not $unchanged){throw 'Source inputs changed during focused compilation/test.'}
if($ExpectMissingFlush){if(-not $negativeObserved){throw 'Expected original missing-flush failure was not reproduced.'};Write-Output 'NEGATIVE_REPRODUCED: original runner did not publish reached feedback before ARM; TestPassed=False.'}
elseif(-not $passed){throw $failure}
else{Write-Output $result}
