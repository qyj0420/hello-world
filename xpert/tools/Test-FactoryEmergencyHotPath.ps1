param([string]$OutputRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if([String]::IsNullOrWhiteSpace($OutputRoot)){$OutputRoot=Join-Path $project ('build\qa-emergency-hotpath-'+[Guid]::NewGuid().ToString('N').Substring(0,10))}
$output=[IO.Path]::GetFullPath($OutputRoot)
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be under project build.'}
if(Test-Path -LiteralPath $output){throw 'Use a new isolated output directory.'}
$files=@(Get-ChildItem -LiteralPath (Join-Path $project 'modules-src\FactoryWorker') -Recurse -File -Filter '*.cs'|Sort-Object FullName)
$test=Join-Path $project 'tests\FactoryEmergencyHotPathFocusedTests.cs'
$compiler=Join-Path $project 'tools\compiler\csc.exe'
$inputs=@($files.FullName)+@($test,$PSCommandPath,$compiler)
$inventory=@($inputs|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})
New-Item -ItemType Directory -Path $output|Out-Null
$assemblyPath=Join-Path $output 'FactoryEmergencyHotPath.Focused.dll'
$arguments=@('/nologo','/langversion:latest','/target:library','/platform:x64','/optimize+','/deterministic+',
    '/r:System.dll','/r:System.Core.dll','/r:System.Xml.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll',('/out:'+$assemblyPath))
& $compiler @arguments @($files.FullName) $test
if($LASTEXITCODE -ne 0){throw 'Native closure library compilation failed.'}
$assembly=[Reflection.Assembly]::LoadFrom($assemblyPath)
$names=@('FactoryEmergencyHotPathFocusedTests','FactoryPicoLifecycleTests','FactoryPicoTimingTests','FactoryIoTimingTests')
$results=@()
foreach($name in $names){
    $type=$assembly.GetType('Xiongxin.FactoryTest.'+$name,$true)
    $method=$type.GetMethod('Run',[Reflection.BindingFlags]'NonPublic,Static')
    try{$result=[string]$method.Invoke($null,$null)}catch{if($null -ne $_.Exception.InnerException){throw $_.Exception.InnerException};throw}
    if(-not $result.Contains('PASS')){throw 'Focused test did not pass.'}
    $result|Set-Content -LiteralPath (Join-Path $output ($name+'.stdout.txt')) -Encoding UTF8
    $results+=[ordered]@{Name=$name;Result=$result;ExitCode=0};Write-Output $result
}
foreach($entry in $inventory){if((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -cne $entry.SHA256){throw ('Source changed during compile/test: '+$entry.Path)}}
[ordered]@{Schema='HVX.Factory.EmergencyHotPath.Focused/1';CapturedUtc=[DateTime]::UtcNow.ToString('o');Result='PASS';SourceInputs=$inventory;SourceUnchanged=$true;
    Compiler=$compiler;CompiledAssembly=$assemblyPath;CompiledAssemblySHA256=(Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash;
    Tests=$results;HardwareAccessed=$false;SDKCalled=$false;PhysicalAcceptance=$false;FinalExeIntegration=$false;FullRegression=$false
}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'verification-emergency-hotpath.json') -Encoding UTF8
Write-Output ('FocusedProof='+$output)
