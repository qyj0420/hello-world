param([string]$OutputRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if([String]::IsNullOrWhiteSpace($OutputRoot)){$OutputRoot=Join-Path $project ('build\qa-progress-journal-'+[Guid]::NewGuid().ToString('N').Substring(0,10))}
$output=[IO.Path]::GetFullPath($OutputRoot)
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be under this project build directory.'}
if(Test-Path -LiteralPath $output){throw 'Use a new isolated output directory.'}
$files=@(Get-ChildItem -LiteralPath (Join-Path $project 'modules-src\FactoryWorker') -Recurse -File -Filter '*.cs'|Sort-Object FullName)
$probes=@((Join-Path $PSScriptRoot 'PressureProgressJournalSmoke.cs'),(Join-Path $PSScriptRoot 'PressureReachedTrendSmoke.cs'))
$inputs=@($files.FullName)+$probes+@($PSCommandPath)
$inventory=@($inputs|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})
New-Item -ItemType Directory -Path $output|Out-Null
$assemblyPath=Join-Path $output 'PressureProgressJournal.Focused.dll'
$compiler=Join-Path $project 'tools\compiler\csc.exe'
$arguments=@('/nologo','/langversion:latest','/target:library','/platform:x64','/optimize+','/deterministic+',
    '/r:System.dll','/r:System.Core.dll','/r:System.Xml.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll',('/out:'+$assemblyPath))
& $compiler @arguments @($files.FullName) @probes
if($LASTEXITCODE -ne 0){throw 'Focused managed library compilation failed.'}
$assembly=[Reflection.Assembly]::LoadFrom($assemblyPath)
$passed=$false;$results=@();$failure=''
try{
    foreach($name in @('PressureProgressJournalSmoke','PressureReachedTrendSmoke')){
        $method=$assembly.GetType(('Xiongxin.FactoryTest.'+$name),$true).GetMethod('Run',[Reflection.BindingFlags]'NonPublic,Static')
        $results+=([string]$method.Invoke($null,$null))
    }
    $passed=$results.Count -eq 2 -and $results[0].Contains('PRESSURE_PROGRESS_JOURNAL_PASS=') -and $results[1].Contains('PRESSURE_REACHED_TREND_PASS=')
}catch{$failure=$_.Exception.ToString();if($null -ne $_.Exception.InnerException){$failure=$_.Exception.InnerException.ToString()}}
if($passed){$results|Set-Content -LiteralPath (Join-Path $output 'test.stdout.txt') -Encoding UTF8}
else{$failure|Set-Content -LiteralPath (Join-Path $output 'test.failure.txt') -Encoding UTF8}
$unchanged=$true
foreach($entry in $inventory){if((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -cne $entry.SHA256){$unchanged=$false}}
[ordered]@{Schema='HVX.Factory.ProgressJournal.Focused/1';CapturedUtc=[DateTime]::UtcNow.ToString('o');SourceInputs=$inventory;
    Compiler=$compiler;CompiledAssembly=$assemblyPath;CompiledAssemblySHA256=(Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash;
    TestPassed=$passed;TestResults=$results;Failure=$failure;InputsUnchanged=$unchanged;HardwareAccessed=$false;SDKCalled=$false;
    ProductEntryExecuted=$false;WorkerEntryExecuted=$false;ResidentStarted=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;
    FinalExeIntegration=$false;FullRegression=$false
}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'verification-progress-journal.json') -Encoding UTF8
Write-Output ('FocusedProof='+$output)
if(-not $unchanged){throw 'Source inputs changed during focused compilation/test.'}
if(-not $passed){throw $failure}
$results
