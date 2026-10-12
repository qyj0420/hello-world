[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$BuildRoot)
# New final-byte functional evidence, not a continuation/reclassification of an
# earlier protection audit. Never executes Main, a worker, SDK or installation.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$BuildRoot=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
if(-not $BuildRoot.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an isolated candidate under this project/build.'}
Set-Location -LiteralPath $root
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded test helpers.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Get-RegressionTreeFiles','Invoke-RegressionExecutable')){
 $nodes=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($nodes.Count -ne 1){throw ('Ambiguous/missing helper: '+$name)}
 . ([ScriptBlock]::Create($nodes[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $BuildRoot
function CandidateFile([string]$relative){
 if($relative -notmatch '^[^/:\\]+(/[^/:\\]+)*$' -or @($relative.Split('/')|Where-Object {$_ -in @('.','..')}).Count){throw 'Unsafe candidate-relative path.'}
 $path=[IO.Path]::GetFullPath((Join-Path $BuildRoot $relative))
 if(-not $path.StartsWith($BuildRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Candidate path escapes build.'}
 Assert-RegressionUnlinkedPath $path
 if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw ('Missing candidate: '+$relative)}
 return $path
}
function Inventory {
 $files=@(Get-Item -LiteralPath (CandidateFile 'Xiongxin.OWTS.Desktop.exe'),(CandidateFile 'Xiongxin.OWTS.Desktop.exe.config'),(CandidateFile 'build-manifest.json'),(CandidateFile 'module-manifest.json'),(CandidateFile 'research-pins.json'))
 foreach($folder in @('modules','model-payload')){$files+=@(Get-RegressionTreeFiles (Join-Path $BuildRoot $folder))}
 @($files|Sort-Object FullName|ForEach-Object{[ordered]@{Path=$_.FullName;RelativePath=$_.FullName.Substring($BuildRoot.Length+1).Replace('\','/');SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash;Bytes=$_.Length}})
}
$inputs=New-Object 'System.Collections.Generic.List[object]'
function Pin([string]$path,[string]$expected=''){
 $path=[IO.Path]::GetFullPath($path);Assert-RegressionUnlinkedPath $path
 $hash=(Get-FileHash -LiteralPath $path).Hash
 if($expected -and $hash -cne $expected){throw ('Final-build source mismatch: '+$path)}
 $inputs.Add([ordered]@{Path=$path;SHA256=$hash})
}
$bm=Get-Content -LiteralPath (CandidateFile 'build-manifest.json') -Raw|ConvertFrom-Json
if($bm.FileVersion -cne '1.1.2.0' -or $bm.ConfigurationProfile -cne 'PublicBeta-NoDriver-1.1.2-20261011' -or $bm.ProtectedRelease -ne $true -or $bm.MaintenancePasswordEnabled -ne $true){throw 'Wrong final version/profile/guard flags.'}
if($bm.ExecutableSha256 -cne (Get-FileHash -LiteralPath (CandidateFile 'Xiongxin.OWTS.Desktop.exe')).Hash){throw 'Executable/build manifest mismatch.'}
if(@($bm.HostCompilation.CompilerDefines) -cnotcontains 'PUBLIC_BETA_NO_DRIVER_V112_20261011' -or @($bm.HostCompilation.CompilerDefines) -cnotcontains 'MAINTENANCE_PASSWORD_GUARD'){throw 'Final host profile/guard defines missing.'}
$sourcePaths=@()
foreach($source in $bm.Sources){
 $path=[IO.Path]::GetFullPath((Join-Path $root ([string]$source.Path)))
 if(-not $path.StartsWith($root+'\src\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected host source path.'}
 Pin $path ([string]$source.Sha256);$sourcePaths+=$path
}
$actual=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -File -Filter '*.cs'|Sort-Object FullName|ForEach-Object FullName)
if((@($sourcePaths|Sort-Object)|ConvertTo-Json -Compress) -cne ($actual|ConvertTo-Json -Compress)){throw 'Routing fixture source set differs from final build.'}
foreach($relative in @('tools/Test-FinalReleaseCompatibility.ps1','tools/Test-UiRepairFocused.ps1','tools/Test-DeviceConnectNoPassword.ps1','tools/Test-Internal12-Calibration.ps1','tools/compiler/csc.exe','tests/PublicBetaStartupVersionSmoke.cs','tests/DefectModelTrustSmoke.cs','tests/AnalysisCalibrationArchiveSmoke.cs','tests/AnalysisCalibrationInputScopeSmoke.cs','tests/HybridRuntimeSmoke.cs','tests/Task12ModelRoutingSmoke.cs','app.manifest','assets/xiongxin.ico','assets/xiongxin-original.png')){Pin (Join-Path $root $relative)}
$before=@(Inventory)
$qa=Join-Path $BuildRoot ('qa/final-compatibility-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
Assert-RegressionUnlinkedPath $qa -AllowMissing
New-Item -ItemType Directory -Path $qa|Out-Null
$started=[DateTime]::UtcNow.ToString('o');$steps=New-Object 'System.Collections.Generic.List[object]'
$inferenceStarted=$false;$inferenceVerified=$false
function Receipt([string]$status,[string]$failure=''){
 $unchanged=$false;try{$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))}catch{}
 [ordered]@{Schema='HVX.FinalReleaseCompatibility/1';BuildRoot=$BuildRoot;Version='1.1.2.0';Profile=$bm.ConfigurationProfile;StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Status=$status;Failure=$failure;Passed=($status -ceq 'PASS');TestExitCode=$(if($status -ceq 'PASS'){0}elseif($status -ceq 'FAILED'){1}else{$null});Scope='Final-byte startup, model trust, source-bound routing/synthetic inference and three actual consumer fixtures only';
  HardwareAccessed=$false;ProductEntryExecuted=$false;WorkerStarted=$false;SDKCalled=$false;InstallerExecuted=$false;UserProjectsWritten=$false;ActualUserCacheWritten=$false;PhysicalAcceptance=$false;
  InferenceExecutionAttempted=$inferenceStarted;InferenceExecuted=$(if($inferenceStarted -and -not $inferenceVerified){$null}else{$inferenceVerified});
  RuntimeUnchanged=$unchanged;Runtime=$before;Inputs=$inputs.ToArray();Steps=$steps.ToArray();FullCompatibilityAccepted=$false;FullConfidentialityAccepted=$false;GateApprovalGenerated=$false;
  Limitations=@('Scoped functional regression only; retained prior failures and incomplete confidentiality review are not converted to PASS.',
   'Source-built routing uses final host sources and compiler defines with actual model/consumer bytes; product Main is never executed.',
   'Synthetic/offline testing is not physical acceptance, full-language GUI coverage or proof against reverse engineering.')}|
  ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
 if($status -ne 'FAILED' -and -not $unchanged){throw 'Final runtime bytes changed during checks.'}
}
function AddProof([string]$name,[string]$path){
 $path=[IO.Path]::GetFullPath($path)
 if(-not $path.StartsWith($BuildRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Proof escaped final candidate.'}
 Assert-RegressionUnlinkedPath $path
 $steps.Add([ordered]@{Name=$name;Path=$path;SHA256=(Get-FileHash -LiteralPath $path).Hash})
}
function Managed([string]$name,[string[]]$sources,[string[]]$arguments){
 $exe=Join-Path $qa ($name+'.exe')
 $compile=@(& (Join-Path $PSScriptRoot 'compiler/csc.exe') /nologo /target:exe /platform:x64 /langversion:7.3 /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/main:'+$name) ('/out:'+$exe) @sources 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$compile|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ($name+' compile failed; diagnostics retained.')}
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments $arguments -LogPrefix (Join-Path $qa $name)
 $steps.Add([ordered]@{Name=$name;ExitCode=$run.ExitCode;TimedOut=$run.TimedOut;ProcessExitConfirmed=$run.ProcessExitConfirmed;Milliseconds=$run.ElapsedMilliseconds;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash;StdoutPath=$run.StdoutPath;StdoutSHA256=$run.StdoutSHA256;StderrPath=$run.StderrPath;StderrSHA256=$run.StderrSHA256})
 Receipt 'IN_PROGRESS'
 if($run.ExitCode -ne 0 -or $run.TimedOut -or -not $run.ProcessExitConfirmed){throw ($name+' failed; diagnostics retained.')}
}
try {
 Receipt 'IN_PROGRESS'
 Managed 'PublicBetaStartupVersionSmoke' @((Join-Path $root 'tests/PublicBetaStartupVersionSmoke.cs')) @($BuildRoot)
 Managed 'DefectModelTrustSmoke' @((Join-Path $root 'src/DefectModelCatalog.cs'),(Join-Path $root 'tests/DefectModelTrustSmoke.cs')) @((Join-Path $qa 'model-trust'),(CandidateFile 'model-payload/5581bf7a60798cb8f349a3ee3a59c2f8c121d44ab98d23172453ec856d360d16.hxm'),(CandidateFile 'model-payload/a32a19b489cd7fb0cf51f9d027c214e7636a5621d7663384d88925f2a4ceb32b.hxm'))
 $ui=@(& (Join-Path $PSScriptRoot 'Test-UiRepairFocused.ps1') -BuildRoot $BuildRoot -Names @('HybridRuntimeSmoke','AnalysisCalibrationArchiveSmoke','AnalysisCalibrationInputScopeSmoke'))
 $ui|Set-Content -LiteralPath (Join-Path $qa 'consumer-compatibility.txt') -Encoding UTF8
 $line=@($ui|Where-Object {$_ -is [string] -and $_.StartsWith('UI_REPAIR_PROOF=',[StringComparison]::Ordinal)})
 if($line.Count -ne 1){throw 'Actual consumer proof missing.'}
 $proof=Join-Path $line[0].Substring('UI_REPAIR_PROOF='.Length) 'verification-ui-repair.json'
 $result=Get-Content -LiteralPath $proof -Raw|ConvertFrom-Json
 if($result.RuntimeUnchanged -ne $true -or $result.HardwareAccessed -ne $false -or $result.Tests.Count -ne 3 -or @($result.Tests|Where-Object {$_.ExitCode -ne 0 -or $_.TimedOut -ne $false -or $_.ProcessExitConfirmed -ne $true}).Count){throw 'Consumer proof contract failed.'}
 AddProof 'ActualConsumersCompatibility' $proof
 $inferenceStarted=$true;Receipt 'IN_PROGRESS'
 $model=@(& (Join-Path $PSScriptRoot 'Test-DeviceConnectNoPassword.ps1') -BuildRoot $BuildRoot -Names @('Task12ModelRoutingSmoke'))
 $model|Set-Content -LiteralPath (Join-Path $qa 'model-routing-inference.txt') -Encoding UTF8
 $line=@($model|Where-Object {$_ -is [string] -and $_.StartsWith('PASSWORD_FREE_SOURCE_PROOF=',[StringComparison]::Ordinal)})
 if($line.Count -ne 1){throw 'Actual model routing/inference proof missing.'}
 $proof=Join-Path $line[0].Substring('PASSWORD_FREE_SOURCE_PROOF='.Length) 'verification.json'
 $result=Get-Content -LiteralPath $proof -Raw|ConvertFrom-Json
 if($result.RuntimeUnchanged -ne $true -or $result.SourceUnchanged -ne $true -or $result.HardwareAccessed -ne $false -or $result.Tests.Count -ne 1 -or $result.Tests[0].Name -cne 'Task12ModelRoutingSmoke' -or $result.Tests[0].ExitCode -ne 0 -or $result.Tests[0].TimedOut -ne $false -or $result.Tests[0].ProcessExitConfirmed -ne $true -or (@($result.CompilerDefines)|ConvertTo-Json -Compress) -cne (@($bm.HostCompilation.CompilerDefines)|ConvertTo-Json -Compress)){throw 'Model routing/inference proof contract failed.'}
 AddProof 'ActualModelRoutingAndSyntheticInference' $proof;$inferenceVerified=$true
 foreach($input in $inputs){if((Get-FileHash -LiteralPath $input.Path).Hash -cne $input.SHA256){throw 'Test/source input changed during collection.'}}
 Receipt 'PASS'
 Write-Output ('FINAL_RELEASE_COMPATIBILITY='+$qa)
}catch{Receipt 'FAILED' $_.Exception.Message;throw}
