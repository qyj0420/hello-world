param([Parameter(Mandatory=$true)][string]$DataAnalysisDll,[Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$NativeDirectory='',
    [string]$AlgorithmLibrary='', [string]$NativePins='', [string]$NativeReceipt='')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(-not$output.StartsWith($root+'\qa\',[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $output)){throw 'Use a fresh owned qa directory.'}
$dll=[IO.Path]::GetFullPath($DataAnalysisDll)
$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$source=Join-Path $root 'tests\EPlusPacketManagedSmoke.cs'
$python='C:\Users\qyj04\AppData\Local\Programs\Python\Python312\python.exe'
$runner=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded regression helpers.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){$n=@($ast.FindAll({param($x)$x -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name);if($n.Count-ne 1){throw 'Missing bounded helper'};. ([ScriptBlock]::Create($n[0].Extent.Text))}
foreach($p in @($dll,$compiler,$source,$python,$runner)){Assert-RegressionUnlinkedPath $p;if(!(Test-Path -LiteralPath $p -PathType Leaf)){throw ('Missing input '+$p)}}
Assert-RegressionUnlinkedPath $output -AllowMissing
$registration=Get-Content -LiteralPath 'C:\Users\qyj04\Documents\TDR算法\outputs\eplus_packet_guard_20261011_r01\run-two-r02\registration.json' -Raw|ConvertFrom-Json
$selected=@($registration.projects[0].files|Where-Object id -in 4,23)+@($registration.projects[1].files|Where-Object id -eq 14)
if($selected.Count-ne 3){throw 'Missing registered same-input records'}
foreach($f in $selected){if((Get-FileHash -LiteralPath $f.path).Hash.ToLowerInvariant()-cne$f.sha256){throw 'Registered LOC changed'}}
New-Item -ItemType Directory -Path $output|Out-Null
# A private byte-identical managed module copy provides the cache's required
# sibling Task17 fingerprint; the Task17 assembly is never invoked here.
$managed=Join-Path $output 'tested-managed';New-Item -ItemType Directory -Path $managed|Out-Null
$testedDll=Join-Path $managed ([IO.Path]::GetFileName($dll));Copy-Item -LiteralPath $dll -Destination $testedDll
$algorithm=if($AlgorithmLibrary){[IO.Path]::GetFullPath($AlgorithmLibrary)}else{Join-Path $root 'dependencies\Algorithms\Task17\fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'}
Assert-RegressionUnlinkedPath $algorithm
Copy-Item -LiteralPath $algorithm -Destination (Join-Path $managed ([IO.Path]::GetFileName($algorithm)))
if((Get-FileHash -LiteralPath $testedDll).Hash-cne(Get-FileHash -LiteralPath $dll).Hash){throw 'Managed fixture copy changed'}
$paths=Join-Path $output 'locations.json'
[IO.File]::WriteAllText($paths,(@($selected|ForEach-Object path)|ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
$exe=Join-Path $output 'EPlusPacketManagedSmoke.exe'
& $compiler /nologo /target:exe /platform:x64 /langversion:7.3 "/out:$exe" "/reference:$dll" /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll $source
if($LASTEXITCODE-ne 0){throw 'Managed same-input helper compilation failed'}
$inputs=Join-Path $output 'same-inputs'
$a=Invoke-RegressionExecutable -Executable $exe -Arguments @($testedDll,'export',$paths,$inputs) -LogPrefix (Join-Path $output 'managed-export')
if($a.ExitCode-ne 0){$a.Output|Write-Output;throw 'Formal reader export failed'}
$arguments=@('-B',(Join-Path $PSScriptRoot 'Test-EPlusPacketSameInput.py'),'--inputs',$inputs)
if($NativeDirectory){
    $native=[IO.Path]::GetFullPath($NativeDirectory)
    $nativePinsPath=if($NativePins){[IO.Path]::GetFullPath($NativePins)}else{Join-Path (Split-Path $native) 'research-pins.json'}
    $nativeReceiptPath=if($NativeReceipt){[IO.Path]::GetFullPath($NativeReceipt)}else{Join-Path (Split-Path $native) 'research-runtime-input-receipt.json'}
    foreach($p in @($native,$nativePinsPath,$nativeReceiptPath)){Assert-RegressionUnlinkedPath $p}
    $arguments+=@('--native',$native,'--reference-python',$python,'--native-pins',$nativePinsPath,'--native-receipt',$nativeReceiptPath)
    $executionPython=Join-Path $native 'python.exe'
}else{$executionPython=$python}
& $executionPython @arguments
if($LASTEXITCODE-ne 0){throw 'Same-input Python replay failed; partial logs retained as failure evidence'}
$a=Invoke-RegressionExecutable -Executable $exe -Arguments @($testedDll,'adapt',$inputs,(Join-Path $output 'managed-adapt')) -LogPrefix (Join-Path $output 'managed-adapt')
if($a.ExitCode-ne 0){$a.Output|Write-Output;throw 'Managed matrix/quality/UI/cache adaptation failed'}
$proofInputs=@($source,$PSCommandPath,(Join-Path $PSScriptRoot 'Test-EPlusPacketSameInput.py'),$dll,$algorithm)
if($NativeDirectory){$proofInputs+=@($nativePinsPath,$nativeReceiptPath)}
$proof=[ordered]@{schema='HVX.EPlusPacket.Integration/1';passed=$true;hardwareAccessed=$false;productStarted=$false;physicalValidation=$false;nativeExecuted=[bool]$NativeDirectory;algorithmFingerprintInvoked=$false;inputHashes=@($proofInputs|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash}});pythonProof=Join-Path $inputs $(if($NativeDirectory){'verification-native.json'}else{'verification-python.json'});managedProof=Join-Path $output 'managed-adapt\verification-managed.json';limitations=@('Three selected historical records only, not all project replay or physical validation.','Auxiliary 10/20 MHz lowpass not executed; Task28 is not replaced.','Observed-spectral near-EOF modulo behavior retained as named-candidate known limitation.')}
[IO.File]::WriteAllText((Join-Path $output 'verification.json'),($proof|ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false)))
$proof|ConvertTo-Json -Depth 5
