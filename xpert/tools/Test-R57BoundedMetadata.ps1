[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')); $qa=[IO.Path]::GetFullPath($OutputRoot)
if(-not $qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an owned build output folder.'}
if(Test-Path -LiteralPath $qa){throw 'Use a fresh output folder.'}
$relative=@('src\ProjectMeasurementMode.cs','src\CableJointStore.cs','modules-src\ProjectCreation\src\ProjectCreationModels.cs',
 'modules-src\ProjectCreation\src\ProjectCreationStorage.cs','modules-src\ProjectCreation\src\ProjectCreationStorageTransaction.cs',
 'modules-src\ProjectCreation\src\ProjectCreationSettings.cs','modules-src\DataBrowser\vendor\Item\ItemDocument.cs','tests\R57BoundedMetadataSmoke.cs')
$sources=@($relative|ForEach-Object{Join-Path $root $_}); $compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=@((@($sources)+@($PSCommandPath,$compiler))|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
New-Item -ItemType Directory -Path $qa | Out-Null
$exe=Join-Path $qa 'R57BoundedMetadataSmoke.exe'
$compile=@(& $compiler /nologo /noconfig /langversion:7.3 /platform:x64 /optimize+ /deterministic+ /debug- /target:exe ('/out:'+$exe) /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll @sources 2>&1|ForEach-Object{$_.ToString()})
$compileExit=$LASTEXITCODE; $compile|Set-Content -LiteralPath (Join-Path $qa 'compile.log') -Encoding UTF8
if($compileExit-ne0){throw ('Compilation failed: '+($compile-join"`n"))}
$hash=(Get-FileHash -LiteralPath $exe).Hash
$output=@(& $exe (Join-Path $qa 'fixtures') 2>&1 | ForEach-Object{$_.ToString()}); $exit=$LASTEXITCODE
$output|Set-Content -LiteralPath (Join-Path $qa 'execution.log') -Encoding UTF8; $output|Write-Output
$drift=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash-cne$_.SHA256})
$passed=$exit-eq0-and$drift.Count-eq0-and(Get-FileHash -LiteralPath $exe).Hash-ceq$hash
[ordered]@{Schema='HVX.R57.BoundedMetadata/1';Status=$(if($passed){'PASS'}else{'FAIL'});CompletedUtc=[DateTime]::UtcNow.ToString('o');Inputs=$inputs;SourceUnchanged=($drift.Count-eq0);InputMismatchCount=$drift.Count;FixtureExecutable=$exe;FixtureExecutableSHA256=$hash;Output=$output;ExitCode=$exit;MaximumBytes=2097152;MaximumCharacters=2097152;MaximumLines=32768;MaximumLineCharacters=65536;ProductEntryExecuted=$false;ControlsConstructed=$false;WordExecuted=$false;SDKCalled=$false;HardwareAccessed=$false;UserProjectWritten=$false;SameFinalProductBytes=$false}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'proof.json') -Encoding UTF8
Write-Output ('R57_METADATA_PROOF='+$qa+'\proof.json')
if(-not$passed){throw 'Metadata regression failed or inputs drifted; evidence retained.'}
