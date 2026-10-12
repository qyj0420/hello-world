param([string]$OutputRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if([String]::IsNullOrWhiteSpace($OutputRoot)){$OutputRoot=Join-Path $project ('build\art-negative-feedback-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$output=[IO.Path]::GetFullPath($OutputRoot)
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be under the project build directory.'}
if(Test-Path -LiteralPath $output){throw 'Use a new isolated output directory; existing proof is not overwritten.'}
$sourceRoot=Join-Path $project 'modules-src\FactoryWorker'
$files=@(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter '*.cs'|Sort-Object FullName)
$inventory=@($files|ForEach-Object{[ordered]@{Path=$_.FullName;SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
New-Item -ItemType Directory -Path $output|Out-Null
$assemblyPath=Join-Path $output 'FactoryArtFeedback.Focused.dll'
$compiler=Join-Path $project 'tools\compiler\csc.exe'
$arguments=@('/nologo','/langversion:latest','/target:library','/platform:x64','/optimize+','/deterministic+',
    '/r:System.dll','/r:System.Core.dll','/r:System.Xml.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll',('/out:'+$assemblyPath))
& $compiler @arguments @($files.FullName)
if($LASTEXITCODE -ne 0){throw 'Focused library compilation failed.'}
$assembly=[Reflection.Assembly]::LoadFrom($assemblyPath)
$results=@()
# Reflection selects only pure replay/fake tests, not Program.Main, self-test,
# resident worker entry, UI message loop, or any native device implementation.
foreach($name in @('FactoryArtNegativeFeedbackTests','FactoryStartupFeedbackTests','FactoryBackgroundNoiseTests')){
    $type=$assembly.GetType('Xiongxin.FactoryTest.'+$name,$true)
    $method=$type.GetMethod('Run',[Reflection.BindingFlags]'NonPublic,Static')
    if($null -eq $method){throw ('Missing focused test '+$name)}
    try{$result=[string]$method.Invoke($null,$null)}catch{if($null -ne $_.Exception.InnerException){throw $_.Exception.InnerException};throw}
    if(-not $result.Contains('PASS')){throw ('Focused test did not pass: '+$name)}
    $result|Set-Content -LiteralPath (Join-Path $output ($name+'.stdout.txt')) -Encoding UTF8
    $results+=[ordered]@{Name=$name;Result=$result;ExitCode=0}
    Write-Output $result
}
foreach($entry in $inventory){if((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -cne $entry.SHA256){throw ('Source changed during focused compile/test: '+$entry.Path)}}
[ordered]@{
    Schema='HVX.Factory.ArtNegativeFeedback.Focused/1';CapturedUtc=[DateTime]::UtcNow.ToString('o');SourceInputs=$inventory;
    Compiler=$compiler;CompiledAssembly=$assemblyPath;CompiledAssemblySHA256=(Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash;
    CompileArguments='/target:library /platform:x64; exact listed FactoryWorker C# closure';Tests=$results;
    TestInvocation='Only named non-public static Run methods reflected from an isolated library; no product/native/session entry launched.';
    HardwareAccessed=$false;SDKCalled=$false;PhysicalAcceptance=$false;FinalExeIntegration=$false;FullRegression=$false
}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'verification-art-negative-focused.json') -Encoding UTF8
Write-Output ('FocusedProof='+$output)
