param([string]$OutputRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if([String]::IsNullOrWhiteSpace($OutputRoot)){$OutputRoot=Join-Path $project ('build\feedback-availability-'+[Guid]::NewGuid().ToString('N').Substring(0,10))}
$output=[IO.Path]::GetFullPath($OutputRoot)
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be under project build.'}
if(Test-Path -LiteralPath $output){throw 'Use a new isolated output directory.'}
$files=@(Get-ChildItem -LiteralPath (Join-Path $project 'modules-src\FactoryWorker') -Recurse -File -Filter '*.cs'|Sort-Object FullName)
$compiler=Join-Path $project 'tools\compiler\csc.exe'
$inputs=@($files.FullName)+@($PSCommandPath,$compiler)
$inventory=@($inputs|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})
New-Item -ItemType Directory -Path $output|Out-Null
$assemblyPath=Join-Path $output 'FactoryFeedbackAvailability.Focused.dll'
$arguments=@('/nologo','/langversion:latest','/target:library','/platform:x64','/optimize+','/deterministic+',
    '/r:System.dll','/r:System.Core.dll','/r:System.Xml.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll',('/out:'+$assemblyPath))
& $compiler @arguments @($files.FullName)
if($LASTEXITCODE -ne 0){throw 'Focused library compilation failed.'}
$assembly=[Reflection.Assembly]::LoadFrom($assemblyPath)
# Deliberately fixed allowlist: only pure fake fixtures, never product/worker entry points.
$names=@('FactoryFeedbackAvailabilityTests','FactoryArtTransientReadTests','FactoryEmergencyTimingContractTests','FactoryBufferEmergencyCurrentTests',
    'FactorySelectedFeedbackTests','FactoryFeedbackConfigurationTests',
    'FactoryNoResidualPressureTests','FactoryPressurePrecisionTests','FactoryNativeSessionTests')
$results=@()
foreach($name in $names){
    $type=$assembly.GetType('Xiongxin.FactoryTest.'+$name,$true)
    $method=$type.GetMethod('Run',[Reflection.BindingFlags]'NonPublic,Static')
    try{
        $rawResult=$method.Invoke($null,$null)
        if($name -eq 'FactoryEmergencyTimingContractTests'){
            $count=[int]$rawResult
            if($count -le 0){throw 'DI timing fixture did not execute its checks.'}
            $result='DI timing/deadline policy: '+$count+' managed checks PASS; fake delegates only; HardwareAccessed=False; SDKCalled=False'
        }else{$result=[string]$rawResult}
    }catch{if($null -ne $_.Exception.InnerException){throw $_.Exception.InnerException};throw}
    if(-not $result.Contains('PASS')){throw ('Focused test did not pass: '+$name)}
    $result|Set-Content -LiteralPath (Join-Path $output ($name+'.stdout.txt')) -Encoding UTF8
    $results+=[ordered]@{Name=$name;Result=$result;ExitCode=0}
    Write-Output $result
}
foreach($entry in $inventory){if((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -cne $entry.SHA256){throw ('Source changed during compile/test: '+$entry.Path)}}
[ordered]@{Schema='HVX.Factory.FeedbackAvailability.Offline/1';CapturedUtc=[DateTime]::UtcNow.ToString('o');SourceInputs=$inventory;
    Compiler=$compiler;CompiledAssembly=$assemblyPath;CompiledAssemblySHA256=(Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash;
    Tests=$results;HardwareAccessed=$false;SDKCalled=$false;PhysicalAcceptance=$false;FinalExeIntegration=$false;FullRegression=$false
}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'verification-feedback-availability.json') -Encoding UTF8
Write-Output ('FocusedProof='+$output)
