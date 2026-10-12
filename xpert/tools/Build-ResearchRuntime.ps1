param([Parameter(Mandatory=$true)][string]$ModulesRoot,[string]$RuntimeInputManifestPath='', [string]$PythonBuildInterpreter='')
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$modules=[IO.Path]::GetFullPath($ModulesRoot)
if(-not $modules.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid runtime staging root'}
$python=$PythonBuildInterpreter
if([String]::IsNullOrWhiteSpace($python)){$python=Join-Path $project 'build\protection-tools-env\Scripts\python.exe'}
if(-not(Test-Path -LiteralPath $python) -and [String]::IsNullOrWhiteSpace($PythonBuildInterpreter)){$python=Join-Path (Split-Path -Parent (Split-Path -Parent $project)) 'build\protection-tools-env\Scripts\python.exe'}
if(-not(Test-Path -LiteralPath $python)){throw 'Missing local Cython 3.1.6 build environment'}
if([String]::IsNullOrWhiteSpace($RuntimeInputManifestPath)){$RuntimeInputManifestPath=Join-Path $project 'dependencies\ResearchRuntime\frozen-runtime-inputs.json'}
$RuntimeInputManifestPath=[IO.Path]::GetFullPath($RuntimeInputManifestPath)
$inputSchema=(Get-Content -LiteralPath $RuntimeInputManifestPath -Raw|ConvertFrom-Json).schema
$stageScript=if($inputSchema -eq 'HVX.ResearchRuntime.FrozenInputs/1'){Join-Path $PSScriptRoot 'Stage-ResearchFrozenRuntime.py'}else{Join-Path $PSScriptRoot 'Stage-ResearchRuntime.py'}
# Reject unreviewed mutable machine inputs before compiling anything.
& $python -B $stageScript --manifest $RuntimeInputManifestPath --check-only
if($LASTEXITCODE -ne 0){throw 'Research runtime inputs are missing, unapproved, or changed. Review the fixed input manifest before building; dependencies were not replaced.'}
$runtimeInputManifestHash=(Get-FileHash -LiteralPath $RuntimeInputManifestPath -Algorithm SHA256).Hash
$native=Join-Path (Split-Path -Parent $modules) 'research-native'
$toolchain=& (Join-Path $PSScriptRoot 'Get-CppToolchain.ps1')
$prior=@{};foreach($name in @('INCLUDE','LIB','PATH','DISTUTILS_USE_SDK')){$prior[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
try{
 $env:INCLUDE=$toolchain.Include;$env:LIB=$toolchain.Lib;$env:PATH=$toolchain.SearchPath+';'+$prior['PATH'];$env:DISTUTILS_USE_SDK='1'
 & $python (Join-Path $PSScriptRoot 'Build-ResearchNative.py') $native
 if($LASTEXITCODE -ne 0){throw 'Research native compilation failed'}
}finally{foreach($name in $prior.Keys){[Environment]::SetEnvironmentVariable($name,$prior[$name],'Process')}}
if((Get-FileHash -LiteralPath $RuntimeInputManifestPath -Algorithm SHA256).Hash -cne $runtimeInputManifestHash){throw 'Research runtime approval manifest changed during native compilation.'}
& $python -B $stageScript $native (Join-Path $modules 'r71') --manifest $RuntimeInputManifestPath
if($LASTEXITCODE -ne 0){throw 'Research runtime staging failed'}
# Avoid nested native-command quotes: Windows PowerShell 5.1 otherwise strips
# the marker's quotes and evaluates E-Eplus as Python identifiers.
& (Join-Path $modules 'r71\python.exe') -I -B -c 'import q71,q72,q73,q74,q75,q76,q77,q78,q79,q80,q81,q82,q83,q84,q85,q86,numpy; print(q71.VERSION,q72.VERSION,q79.__name__,q80.__name__,q82.VERSION,q83.VERSION,q84.VERSION,q85.VERSION,q86.VERSION,numpy.__version__)'
if($LASTEXITCODE -ne 0){throw 'Standalone protected runtime did not load'}
