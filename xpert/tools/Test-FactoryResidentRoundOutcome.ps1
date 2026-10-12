param([string]$OutputRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if([String]::IsNullOrWhiteSpace($OutputRoot)){$OutputRoot=Join-Path $project ('build\resident-round-outcome-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$output=[IO.Path]::GetFullPath($OutputRoot)
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be under project build.'}
if(Test-Path -LiteralPath $output){throw 'Use a new isolated output directory; preserve previous proof.'}
$files=@(Get-ChildItem -LiteralPath (Join-Path $project 'modules-src\FactoryWorker') -Recurse -File -Filter '*.cs'|Sort-Object FullName)
$testFile=Join-Path $PSScriptRoot 'tests\FactoryResidentRoundOutcomeFocusedTests.cs'
$inventory=@($files.FullName+$testFile|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})
New-Item -ItemType Directory -Path $output|Out-Null
$assemblyPath=Join-Path $output 'FactoryResidentRoundOutcome.Focused.dll'
$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$arguments=@('/nologo','/langversion:latest','/target:library','/platform:x64','/optimize+','/deterministic+',
    '/r:System.dll','/r:System.Core.dll','/r:System.Xml.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll',('/out:'+$assemblyPath))
& $compiler @arguments @($files.FullName) $testFile
if($LASTEXITCODE -ne 0){throw 'Focused library compilation failed.'}
$assembly=[Reflection.Assembly]::LoadFrom($assemblyPath)
$type=$assembly.GetType('Xiongxin.FactoryTest.FactoryResidentRoundOutcomeFocusedTests',$true)
$type.GetField('FixtureRoot',[Reflection.BindingFlags]'NonPublic,Static').SetValue($null,$output)
$method=$type.GetMethod('Run',[Reflection.BindingFlags]'NonPublic,Static')
try{$result=[string]$method.Invoke($null,$null)}catch{if($null -ne $_.Exception.InnerException){throw $_.Exception.InnerException};throw}
if(-not $result.Contains('PASS')){throw 'Focused method did not pass.'}
$result|Set-Content -LiteralPath (Join-Path $output 'FactoryResidentRoundOutcomeFocusedTests.stdout.txt') -Encoding UTF8
foreach($entry in $inventory){if((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -cne $entry.SHA256){throw ('Source changed during focused compile/test: '+$entry.Path)}}
[ordered]@{Schema='HVX.Factory.ResidentRoundOutcome.Focused/1';CapturedUtc=[DateTime]::UtcNow.ToString('o');SourceInputs=$inventory;
    Compiler=$compiler;CompiledAssembly=$assemblyPath;CompiledAssemblySHA256=(Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash;
    Tests=@([ordered]@{Name='FactoryResidentRoundOutcomeFocusedTests.Run';Result=$result;ExitCode=0});
    TestInvocation='Only named pure managed classifier/record method; no process Start, Program, product or SDK entry.';
    HardwareAccessed=$false;SDKCalled=$false;ProductEntryStarted=$false;WorkerProcessStarted=$false;
    UserProjectsWritten=$false;PhysicalAcceptance=$false;FinalExeIntegration=$false;FullRegression=$false
}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'verification-resident-round-outcome.json') -Encoding UTF8
Write-Output $result
Write-Output ('FocusedProof='+$output)
