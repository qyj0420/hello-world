param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath($OutputRoot)
if(!$output.StartsWith($repo+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Fresh project-local output required.'}
$sources=@((Join-Path $repo 'src/DefectModelCatalog.cs'),(Join-Path $repo 'tests/DefectModelTrustSmoke.cs'))
$models=@((Join-Path $repo 'dependencies/Algorithms/Task12/pd_model_machine.pdm'),(Join-Path $repo 'dependencies/Algorithms/Task12/pd_model_owts36_3class_temp_20260828.pdm'))
$runtime=Join-Path $repo 'modules-src/DataAnalysis/src/ReviewTask12Runtime.cs'
$inputs=@(@($sources)+@($models)+@($runtime,$PSCommandPath)|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$runtimeText=Get-Content -LiteralPath $runtime -Raw
if(!$runtimeText.Contains('using(var verifiedModel=DefectModelCatalog.OpenVerified(path))') -or $runtimeText.Contains('DefectModelCatalog.ValidateHeader(path);')){throw 'Runtime no longer holds a verified model lease.'}
New-Item -ItemType Directory -Path $output|Out-Null
$exe=Join-Path $output 'ModelTrustFilesOnly.exe'
$compile=@(& (Join-Path $PSScriptRoot 'compiler/csc.exe') /nologo /target:exe /platform:x64 /langversion:7.3 /r:System.dll /r:System.Core.dll /main:DefectModelTrustSmoke ('/out:'+$exe) @sources 2>&1|ForEach-Object{$_.ToString()})
$compileExit=$LASTEXITCODE
$compile|Set-Content -LiteralPath (Join-Path $output 'compile.log') -Encoding UTF8
if($compileExit -ne 0){throw ($compile-join "`n")}
$test=@(& $exe (Join-Path $output 'fixtures') @models 2>&1|ForEach-Object{$_.ToString()});$testExit=$LASTEXITCODE
$test|Set-Content -LiteralPath (Join-Path $output 'test.log') -Encoding UTF8
$same=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count -eq 0
[ordered]@{Schema='HVX.ModelTrust.FilesOnly/1';Inputs=$inputs;InputsUnchanged=$same;CompileExit=$compileExit;TestExit=$testExit;ModelDecrypted=$false;InferenceRun=$false;HardwareAccessed=$false;ProductStarted=$false;Output=$test}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
$test|Write-Output
if($testExit -ne 0 -or !$same){throw 'Model content trust regression failed.'}
