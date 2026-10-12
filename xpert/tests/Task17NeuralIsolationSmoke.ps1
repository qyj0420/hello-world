param([Parameter(Mandatory=$true)][string]$BuildRoot,[Parameter(Mandatory=$true)][string]$QaRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$build=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
$qa=[IO.Path]::GetFullPath($QaRoot).TrimEnd('\')
if(-not $build.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase) -or -not $qa.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Neural isolation paths must remain under project build.'}
foreach($target in @($build,$qa)){
 $walk=$target
 while($walk.Length -ge $project.Length){
  if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked QA/build paths prohibited.'}
  $walk=[IO.Path]::GetDirectoryName($walk)
 }
}
$excluded=@('ReflectionLearningEngine.cs','ReflectionLearningFeatures.cs','ReflectionLearningPrediction.cs','ReflectionLearningNeuralModel.cs','ReflectionLearningNeuralPrediction.cs','ReflectionLearningNeuralTraining.cs')
$source=Join-Path $project 'modules-src\LocationAlgorithm'
$all=@(Get-ChildItem -LiteralPath (Join-Path $source 'src') -Filter '*.cs' -File | Sort-Object Name)
if(@($all | Where-Object Name -in $excluded).Count -ne 6){throw 'Expected all six review-only learning source additions.'}
$files=@($all | Where-Object Name -notin $excluded | ForEach-Object FullName)
$files+=Join-Path $project 'src\LocationCycleRecord.cs'
$runtime=@(Get-ChildItem -LiteralPath (Join-Path $source 'runtime') -Filter '*.dll' -File | Sort-Object Name)
if($runtime.Count -ne 3 -or $files.Count -lt 90){throw 'Preserved physical baseline source/runtime incomplete.'}
$run=Join-Path $qa ('t17iso-'+[guid]::NewGuid().ToString('N'))
$baseline=Join-Path $run 'baseline'
New-Item -ItemType Directory -Path $baseline -Force | Out-Null
$dll=Join-Path $baseline 'fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'
$compiler=Join-Path $project 'tools\compiler\csc.exe'
$arguments=@('/nologo','/noconfig','/langversion:latest','/optimize+','/deterministic+','/debug-','/target:library','/platform:x64',
 '/r:System.dll','/r:System.Core.dll','/r:System.Numerics.dll','/r:System.Web.Extensions.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Runtime.Serialization.dll','/r:Microsoft.CSharp.dll',('/out:'+$dll))
foreach($item in $runtime){$arguments+=('/resource:'+$item.FullName+',Task17.Runtime.'+$item.Name)}
& $compiler @arguments @files
if($LASTEXITCODE -ne 0){throw 'Independent physical-only baseline compilation failed.'}
$exe=Join-Path $run 'Task17NeuralIsolationSmoke.exe'
& $compiler /nologo /noconfig /langversion:7.3 /target:exe /platform:x64 /optimize+ /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll ('/out:'+$exe) ('/win32icon:'+(Join-Path $project 'assets\xiongxin.ico')) (Join-Path $PSScriptRoot 'Task17NeuralIsolationSmoke.cs')
if($LASTEXITCODE -ne 0){throw 'Physical-neural isolation fixture compile failed.'}
& $exe $build $run $dll
if($LASTEXITCODE -ne 0){throw 'Physical-neural isolation failed.'}
Write-Output ('TASK17_NEURAL_ISOLATION_EVIDENCE='+$run)
