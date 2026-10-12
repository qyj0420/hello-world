param([string]$OutputRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if([String]::IsNullOrWhiteSpace($OutputRoot)){$OutputRoot=Join-Path $project ('build\end-residual-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$output=[IO.Path]::GetFullPath($OutputRoot)
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be below the project build directory.'}
if(Test-Path -LiteralPath $output){throw 'Use a new isolated output directory; existing proof is not overwritten.'}
$sourceRoot=Join-Path $project 'modules-src\FactoryWorker'
$files=@(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter '*.cs'|Sort-Object FullName)
$inventory=@($files|ForEach-Object{[ordered]@{Path=$_.FullName;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
New-Item -ItemType Directory -Path $output|Out-Null
$assemblyPath=Join-Path $output 'FactoryEndResidual.Focused.dll'
$compiler=Join-Path $project 'tools\compiler\csc.exe'
$arguments=@('/nologo','/langversion:latest','/target:library','/platform:x64','/optimize+','/deterministic+',
 '/r:System.dll','/r:System.Core.dll','/r:System.Xml.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll',('/out:'+$assemblyPath))
& $compiler @arguments @($files.FullName)
if($LASTEXITCODE -ne 0){throw 'Focused library compilation failed.'}
$assembly=[Reflection.Assembly]::LoadFrom($assemblyPath)
$results=@()
# Reflection invokes only these managed fake/replay tests. No product entry,
# self-test switch, discovery, resident session, driver load or UI is launched.
foreach($name in @('FactoryEndResidualTests','FactoryResidualStageTests','FactoryFaultResidualTests','FactoryCaptureEndTests','FactoryBatchAuthorizationTests','FactoryZeroInputTests')){
 $type=$assembly.GetType('Xiongxin.FactoryTest.'+$name,$true)
 $method=$type.GetMethod('Run',[Reflection.BindingFlags]'NonPublic,Static')
 if($null -eq $method){throw ('Missing focused test '+$name)}
 try{$result=[string]$method.Invoke($null,$null)}catch{if($null -ne $_.Exception.InnerException){throw $_.Exception.InnerException};throw}
 if(-not $result.Contains('PASS')){throw ('Focused test did not pass: '+$name)}
 $result|Set-Content -LiteralPath (Join-Path $output ($name+'.stdout.txt')) -Encoding UTF8
 $results+=[ordered]@{Name=$name;Result=$result;ExitCode=0};Write-Output $result
}
foreach($entry in $inventory){if((Get-FileHash -LiteralPath $entry.Path).Hash -cne $entry.SHA256){throw ('Source changed during focused test: '+$entry.Path)}}
[ordered]@{Schema='HVX.Factory.EndResidual.Focused/1';CapturedUtc=[DateTime]::UtcNow.ToString('o');SourceInputs=$inventory;
 Compiler=$compiler;CompiledAssembly=$assemblyPath;CompiledAssemblySHA256=(Get-FileHash -LiteralPath $assemblyPath).Hash;
 Tests=$results;HardwareAccessed=$false;SDKCalled=$false;UserProjectsWritten=$false;PhysicalAcceptance=$false;FinalExeIntegration=$false;FullRegression=$false}|
 ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'verification-end-residual-focused.json') -Encoding UTF8
Write-Output ('FocusedProof='+$output)
