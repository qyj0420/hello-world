param([Parameter(Mandatory=$true)][string]$OutputRoot,
      [Parameter(Mandatory=$true)][string]$FrozenModulesRoot,
      [Parameter(Mandatory=$true)][string]$FrozenPinsPath,
      [Parameter(Mandatory=$true)][string]$ExpectedFrozenPinsSha256)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath($OutputRoot)
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $output)){throw 'Fresh owned output required.'}
function NoLinks([string]$path){
 for($current=[IO.Path]::GetFullPath($path);$current;$current=[IO.Path]::GetDirectoryName($current)){
  if((Test-Path -LiteralPath $current)-and((Get-Item -LiteralPath $current -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Linked runtime input/output refused.'}
 }
}
NoLinks $output;NoLinks $FrozenPinsPath
if((Get-FileHash -LiteralPath $FrozenPinsPath).Hash-cne$ExpectedFrozenPinsSha256){throw 'Frozen pins identity mismatch.'}
$pins=Get-Content -LiteralPath $FrozenPinsPath -Raw|ConvertFrom-Json
$runtime=[IO.Path]::GetFullPath((Join-Path $FrozenModulesRoot 'r71'))
NoLinks $runtime
if(@($pins.PSObject.Properties).Count-ne452){throw 'Expected complete reviewed runtime of 452 files.'}
$verified=@{}
foreach($entry in $pins.PSObject.Properties){
 if($entry.Name-match '(^/|\\|:|(^|/)\.\.(/|$))'){throw 'Unsafe runtime entry.'}
 $path=[IO.Path]::GetFullPath((Join-Path $runtime $entry.Name))
 if(-not$path.StartsWith($runtime+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Escaping runtime entry.'}
 NoLinks $path
 if((Get-FileHash -LiteralPath $path).Hash-cne$entry.Value){throw 'Frozen runtime bytes differ.'}
 $verified[$entry.Name]=$path
}
$actual=@(Get-ChildItem -LiteralPath $runtime -Recurse -File)
if($actual.Count-ne$verified.Count){throw 'Unexpected additional runtime files.'}
New-Item -ItemType Directory -Path $output|Out-Null
$python=Join-Path (Split-Path -Parent (Split-Path -Parent $project)) 'build\protection-tools-env\Scripts\python.exe'
if(-not(Test-Path -LiteralPath $python)){throw 'Reviewed Cython build interpreter missing.'}
$toolchain=& (Join-Path $PSScriptRoot 'Get-CppToolchain.ps1')
$prior=@{};foreach($name in @('INCLUDE','LIB','PATH','DISTUTILS_USE_SDK')){$prior[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
try{
 $env:INCLUDE=$toolchain.Include;$env:LIB=$toolchain.Lib;$env:PATH=$toolchain.SearchPath+';'+$prior['PATH'];$env:DISTUTILS_USE_SDK='1'
 & $python -B (Join-Path $PSScriptRoot 'Build-ResearchAdapterNative.py') (Join-Path $output 'adapter-native')
 if($LASTEXITCODE-ne0){throw 'Adapter-only native build failed.'}
}finally{foreach($name in $prior.Keys){[Environment]::SetEnvironmentVariable($name,$prior[$name],'Process')}}
$target=Join-Path $output 'modules\r71'
New-Item -ItemType Directory -Path $target|Out-Null
$newPins=[ordered]@{}
foreach($entry in @($pins.PSObject.Properties|Sort-Object Name)){
 $source=$verified[$entry.Name]
 if($entry.Name-ceq'q73.cp312-win_amd64.pyd'){$source=Join-Path $output 'adapter-native\runtime\q73.cp312-win_amd64.pyd'}
 $to=Join-Path $target $entry.Name
 New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to)|Out-Null
 Copy-Item -LiteralPath $source -Destination $to
 $hash=(Get-FileHash -LiteralPath $to).Hash
 if($hash-cne(Get-FileHash -LiteralPath $source).Hash){throw 'Copied runtime mismatch.'}
 if($entry.Name-cne'q73.cp312-win_amd64.pyd'-and$hash-cne$entry.Value){throw 'Unexpected algorithm/runtime change.'}
 $newPins[$entry.Name]=$hash
}
foreach($entry in $pins.PSObject.Properties){if((Get-FileHash -LiteralPath $verified[$entry.Name]).Hash-cne$entry.Value){throw 'Frozen input changed during staging.'}}
$newPinsPath=Join-Path $output 'research-pins.json'
$newPins|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $newPinsPath -Encoding UTF8
[ordered]@{Schema='HVX.R58.AnalysisRuntimeSession/1';FrozenPinsPath=[IO.Path]::GetFullPath($FrozenPinsPath);FrozenPinsSHA256=$ExpectedFrozenPinsSha256;
 SourceRoot=$project;ModulesRoot=(Split-Path -Parent $target);PinsSHA256=(Get-FileHash -LiteralPath $newPinsPath).Hash;
 Files=452;ByteIdenticalReusedFiles=451;ChangedModule='q73.cp312-win_amd64.pyd';AdapterReceipt=(Get-Content -LiteralPath (Join-Path $output 'adapter-native\source-receipt.json') -Raw|ConvertFrom-Json);
 ProductStarted=$false;SDKCalled=$false;HardwareUsed=$false;PhysicalAcceptance=$false;AlgorithmRulesChanged=$false}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'runtime-session-receipt.json') -Encoding UTF8
Write-Output ('ANALYSIS_RUNTIME='+$output)
