[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$BuildRoot,
    [Parameter(Mandatory=$true)][string]$PriorAudit
)
# Supplement only the compatibility checks NOT reached by the retained audit.
# Cache privilege-retry evidence is reviewed separately; no prior failure is
# overwritten, downgraded to a skip, or turned into confidentiality approval.
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
    if($nodes.Count -ne 1){throw ('Ambiguous/missing test helper: '+$name)}
    . ([ScriptBlock]::Create($nodes[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $BuildRoot
function CandidateFile([string]$relative){
    if($relative -notmatch '^[^/:\\]+(/[^/:\\]+)*$' -or @($relative.Split('/')|Where-Object {$_ -in @('.','..')}).Count){throw 'Unsafe candidate-relative path.'}
    $path=[IO.Path]::GetFullPath((Join-Path $BuildRoot $relative))
    if(-not $path.StartsWith($BuildRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Candidate path escapes build.'}
    Assert-RegressionUnlinkedPath $path
    if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw ('Missing candidate file: '+$relative)}
    return $path
}
function ConfinedFile([string]$path){
    $full=[IO.Path]::GetFullPath($path)
    if(-not $full.StartsWith($BuildRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence must belong to this exact candidate.'}
    return CandidateFile $full.Substring($BuildRoot.Length+1).Replace('\','/')
}
function CoreInventory {
    $files=@(Get-Item -LiteralPath (CandidateFile 'Xiongxin.OWTS.Desktop.exe'))
    foreach($folder in @('modules','model-payload')){$files+=@(Get-RegressionTreeFiles (Join-Path $BuildRoot $folder))}
    @($files|Sort-Object FullName|ForEach-Object{[ordered]@{Path=$_.FullName.Substring($BuildRoot.Length+1).Replace('\','/');SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash;Bytes=$_.Length}})
}
function FullInventory {
    $rows=@(CoreInventory)
    $file=Get-Item -LiteralPath (CandidateFile 'Xiongxin.OWTS.Desktop.exe.config')
    $rows+=@([ordered]@{Path='Xiongxin.OWTS.Desktop.exe.config';SHA256=(Get-FileHash -LiteralPath $file.FullName).Hash;Bytes=$file.Length})
    @($rows|Sort-Object Path)
}
# Validate the earlier failed evidence before creating any new fixture.
$PriorAudit=ConfinedFile $PriorAudit
$prior=Get-Content -LiteralPath $PriorAudit -Raw|ConvertFrom-Json
if($prior.Schema -cne 'HVX.FinalProtectionAudit/1' -or $prior.BuildRoot -cne $BuildRoot -or $prior.Status -cne 'FAILED' -or
   $prior.Failure -cne 'EmbeddedModulePayloadSmoke failed; original diagnostics retained.' -or $prior.RuntimeUnchanged -ne $true -or
   $prior.HardwareAccessed -ne $false -or $prior.ProductEntryExecuted -ne $false -or $prior.WorkerStarted -ne $false){throw 'Continuation requires the original same-candidate cache-fixture failure.'}
$coreBefore=@(CoreInventory)
if(($prior.Runtime|ConvertTo-Json -Depth 5 -Compress) -cne ($coreBefore|ConvertTo-Json -Depth 5 -Compress)){throw 'Candidate bytes differ from retained audit.'}
$cacheRows=@($prior.Steps|Where-Object Name -ceq 'EmbeddedModulePayloadSmoke')
if($cacheRows.Count -ne 1 -or $cacheRows[0].ExitCode -ne 1 -or $cacheRows[0].TimedOut -ne $false -or $cacheRows[0].ProcessExitConfirmed -ne $true){throw 'Unexpected original cache failure contract.'}
$cacheRow=$cacheRows[0]
$cacheExe=ConfinedFile (Join-Path ([IO.Path]::GetDirectoryName($PriorAudit)) 'EmbeddedModulePayloadSmoke.exe')
if((Get-FileHash -LiteralPath $cacheExe).Hash -cne $cacheRow.TestSHA256){throw 'Original cache test byte identity changed.'}
foreach($stream in @('Stdout','Stderr')){
    $path=ConfinedFile ([string]$cacheRow.($stream+'Path'))
    if((Get-FileHash -LiteralPath $path).Hash -cne $cacheRow.($stream+'SHA256')){throw 'Original cache diagnostics changed.'}
}
if((Get-Content -LiteralPath $cacheRow.StderrPath -Raw) -notmatch 'Create isolated hard-link fixture; WindowsError=5'){throw 'Do not use this continuation for a different failure.'}
foreach($name in @('DefectModelTrustSmoke','ActualConsumersCompatibility','ActualModelRoutingAndSyntheticInference')){
    if(@($prior.Steps|Where-Object Name -ceq $name).Count){throw 'A requested remaining check was already executed; retain its evidence instead.'}
}
$inputRows=New-Object 'System.Collections.Generic.List[object]'
$inputSeen=@{}
function PinInput([string]$path,[string]$expected=''){
    $full=[IO.Path]::GetFullPath($path)
    Assert-RegressionUnlinkedPath $full
    $hash=(Get-FileHash -LiteralPath $full).Hash
    if($expected -and $hash -cne $expected){throw ('Frozen input changed: '+$full)}
    if(-not $inputSeen.ContainsKey($full)){$inputRows.Add([ordered]@{Path=$full;SHA256=$hash});$inputSeen[$full]=$true}
}
PinInput $PriorAudit
PinInput $cacheExe $cacheRow.TestSHA256
foreach($input in $prior.Inputs){PinInput ([string]$input.Path) ([string]$input.SHA256)}
foreach($step in $prior.Steps){
    if($null -ne $step.PSObject.Properties['Path']){PinInput (CandidateFile ([string]$step.Path)) ([string]$step.SHA256)}
}
foreach($path in @($PSCommandPath,(Join-Path $PSScriptRoot 'Test-FinalProtectionAudit.ps1'),(Join-Path $PSScriptRoot 'Test-UiRepairFocused.ps1'),
  (Join-Path $PSScriptRoot 'Test-DeviceConnectNoPassword.ps1'),(Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),(Join-Path $PSScriptRoot 'compiler/csc.exe'),
  (Join-Path $root 'tests/DefectModelTrustSmoke.cs'),(Join-Path $root 'tests/HybridRuntimeSmoke.cs'),(Join-Path $root 'tests/AnalysisCalibrationArchiveSmoke.cs'),
  (Join-Path $root 'tests/AnalysisCalibrationInputScopeSmoke.cs'),(Join-Path $root 'tests/Task12ModelRoutingSmoke.cs'),(Join-Path $root 'app.manifest'),
  (Join-Path $root 'assets/xiongxin.ico'),(Join-Path $root 'assets/xiongxin-original.png'))){PinInput $path}
$bmPath=CandidateFile 'build-manifest.json';$mmPath=CandidateFile 'module-manifest.json'
PinInput $bmPath;PinInput $mmPath
$bm=Get-Content -LiteralPath $bmPath -Raw|ConvertFrom-Json
if($bm.ExecutableSha256 -cne (Get-FileHash -LiteralPath (CandidateFile 'Xiongxin.OWTS.Desktop.exe')).Hash){throw 'Executable/build manifest mismatch.'}
$sourcePaths=@()
foreach($source in $bm.Sources){
    $path=[IO.Path]::GetFullPath((Join-Path $root ([string]$source.Path)))
    if(-not $path.StartsWith($root+'\src\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected host source path.'}
    PinInput $path ([string]$source.Sha256)
    $sourcePaths+=$path
}
$actualSources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -File -Filter '*.cs'|Sort-Object FullName|ForEach-Object FullName)
if((@($sourcePaths|Sort-Object)|ConvertTo-Json -Compress) -cne ($actualSources|ConvertTo-Json -Compress)){throw 'Source-built routing fixture input set differs from final build.'}
$before=@(FullInventory)
$qa=Join-Path $BuildRoot ('qa/final-protection-continuation-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
Assert-RegressionUnlinkedPath $qa -AllowMissing
New-Item -ItemType Directory -Path $qa|Out-Null
$started=[DateTime]::UtcNow.ToString('o');$steps=New-Object 'System.Collections.Generic.List[object]'
$inferenceStarted=$false;$inferenceVerified=$false
function SaveReceipt([string]$status,[string]$failure=''){
    $unchanged=$false
    try{$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(FullInventory)|ConvertTo-Json -Depth 5 -Compress))}catch{}
    [ordered]@{Schema='HVX.FinalProtectionContinuation/1';BuildRoot=$BuildRoot;StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Status=$status;Failure=$failure;
        PriorAudit=$PriorAudit;PriorAuditSHA256=(Get-FileHash -LiteralPath $PriorAudit).Hash;PriorAuditStatus='FAILED';OriginalFailurePreserved=$true;
        CacheFixtureRepeated=$false;CachePrivilegeRetryMustBeReviewedSeparately=$true;PriorOwnedNumericsAndStaticSamplesRepeated=$false;
        HardwareAccessed=$false;ProductEntryExecuted=$false;WorkerStarted=$false;UserProjectsWritten=$false;ActualUserCacheWritten=$false;PhysicalAcceptance=$false;
        InferenceExecutionAttempted=$inferenceStarted;InferenceExecuted=$(if($inferenceStarted -and -not $inferenceVerified){$null}else{$inferenceVerified});
        RuntimeUnchanged=$unchanged;Runtime=$before;Inputs=$inputRows.ToArray();Steps=$steps.ToArray();
        ManualReviewRequired=$true;GateApprovalGenerated=$false;FullConfidentialityAccepted=$false;FullCompatibilityAccepted=$false;AbsoluteProtectionClaimed=$false;
        Limitations=@('Only compatibility checks not reached by the original cache-fixture failure are supplemented; no previous failure is overwritten.',
          'Root reviewer must independently merge the original audit, exact-byte cache privilege retry, this supplement and proprietary-scope review.',
          'Source-built host routing uses exact final-build sources and actual final model/consumer bytes; it does not execute the product entry point.',
          'Synthetic offline inference is not physical/electrical acceptance or a confidentiality guarantee.',
          'QA fixtures, unprotected originals and decompiled samples MUST NOT be distributed.')}|
        ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
    if($status -ne 'FAILED' -and -not $unchanged){throw 'Candidate runtime changed during inspection.'}
}
function AddProof([string]$name,[string]$path){
    $full=ConfinedFile $path
    $steps.Add([ordered]@{Name=$name;Path=$full.Substring($BuildRoot.Length+1).Replace('\','/');SHA256=(Get-FileHash -LiteralPath $full).Hash})
}
function RunManaged([string]$name,[string[]]$sources,[string[]]$arguments){
    $exe=Join-Path $qa ($name+'.exe')
    $compile=@(& (Join-Path $PSScriptRoot 'compiler/csc.exe') /nologo /target:exe /platform:x64 /langversion:7.3 /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/main:'+$name) ('/out:'+$exe) @sources 2>&1|ForEach-Object{$_.ToString()})
    $code=$LASTEXITCODE
    $compile|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
    if($code -ne 0){throw ($name+' compile failed; original diagnostics retained.')}
    $run=Invoke-RegressionExecutable -Executable $exe -Arguments $arguments -LogPrefix (Join-Path $qa $name)
    $steps.Add([ordered]@{Name=$name;ExitCode=$run.ExitCode;TimedOut=$run.TimedOut;ProcessExitConfirmed=$run.ProcessExitConfirmed;Milliseconds=$run.ElapsedMilliseconds;
        TestSHA256=(Get-FileHash -LiteralPath $exe).Hash;StdoutPath=$run.StdoutPath;StdoutSHA256=$run.StdoutSHA256;StderrPath=$run.StderrPath;StderrSHA256=$run.StderrSHA256})
    SaveReceipt 'IN_PROGRESS'
    if($run.ExitCode -ne 0 -or $run.TimedOut -or -not $run.ProcessExitConfirmed){throw ($name+' failed; original diagnostics retained.')}
}
try {
    SaveReceipt 'IN_PROGRESS'
    $standard=CandidateFile 'model-payload/5581bf7a60798cb8f349a3ee3a59c2f8c121d44ab98d23172453ec856d360d16.hxm'
    $competition=CandidateFile 'model-payload/a32a19b489cd7fb0cf51f9d027c214e7636a5621d7663384d88925f2a4ceb32b.hxm'
    RunManaged 'DefectModelTrustSmoke' @((Join-Path $root 'src/DefectModelCatalog.cs'),(Join-Path $root 'tests/DefectModelTrustSmoke.cs')) @((Join-Path $qa 'model-trust'),$standard,$competition)
    $ui=@(& (Join-Path $PSScriptRoot 'Test-UiRepairFocused.ps1') -BuildRoot $BuildRoot -Names @('HybridRuntimeSmoke','AnalysisCalibrationArchiveSmoke','AnalysisCalibrationInputScopeSmoke'))
    $ui|Set-Content -LiteralPath (Join-Path $qa 'consumer-compatibility.txt') -Encoding UTF8
    $line=@($ui|Where-Object {$_ -is [string] -and $_.StartsWith('UI_REPAIR_PROOF=',[StringComparison]::Ordinal)})
    if($line.Count -ne 1){throw 'Actual consumer compatibility proof missing.'}
    $uiProof=ConfinedFile (Join-Path $line[0].Substring('UI_REPAIR_PROOF='.Length) 'verification-ui-repair.json')
    $result=Get-Content -LiteralPath $uiProof -Raw|ConvertFrom-Json
    if($result.RuntimeUnchanged -ne $true -or $result.HardwareAccessed -ne $false -or $result.Tests.Count -ne 3 -or
       @($result.Tests|Where-Object {$_.ExitCode -ne 0 -or $_.TimedOut -ne $false -or $_.ProcessExitConfirmed -ne $true}).Count){throw 'Consumer compatibility result contract failed.'}
    AddProof 'ActualConsumersCompatibility' $uiProof
    $inferenceStarted=$true;SaveReceipt 'IN_PROGRESS'
    $model=@(& (Join-Path $PSScriptRoot 'Test-DeviceConnectNoPassword.ps1') -BuildRoot $BuildRoot -Names @('Task12ModelRoutingSmoke'))
    $model|Set-Content -LiteralPath (Join-Path $qa 'model-routing-inference.txt') -Encoding UTF8
    $line=@($model|Where-Object {$_ -is [string] -and $_.StartsWith('PASSWORD_FREE_SOURCE_PROOF=',[StringComparison]::Ordinal)})
    if($line.Count -ne 1){throw 'Actual model routing/inference proof missing; do not bypass missing runtime.'}
    $modelProof=ConfinedFile (Join-Path $line[0].Substring('PASSWORD_FREE_SOURCE_PROOF='.Length) 'verification.json')
    $result=Get-Content -LiteralPath $modelProof -Raw|ConvertFrom-Json
    if($result.RuntimeUnchanged -ne $true -or $result.SourceUnchanged -ne $true -or $result.HardwareAccessed -ne $false -or $result.Tests.Count -ne 1 -or
       $result.Tests[0].Name -cne 'Task12ModelRoutingSmoke' -or $result.Tests[0].ExitCode -ne 0 -or $result.Tests[0].TimedOut -ne $false -or $result.Tests[0].ProcessExitConfirmed -ne $true){throw 'Model routing/inference result contract failed.'}
    AddProof 'ActualModelRoutingAndSyntheticInference' $modelProof
    $inferenceVerified=$true
    foreach($input in $inputRows){if((Get-FileHash -LiteralPath $input.Path).Hash -cne $input.SHA256){throw 'Audited/test source or tool changed during collection.'}}
    SaveReceipt 'SCOPED_REMAINING_CHECKS_COMPLETE_MANUAL_REVIEW_REQUIRED'
    Write-Output ('FINAL_PROTECTION_CONTINUATION='+$qa)
}catch{SaveReceipt 'FAILED' $_.Exception.Message;throw}
