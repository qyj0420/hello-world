[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$BuildRoot,
    [switch]$RunPairedStaticSamples,
    [switch]$RunCompatibility
)
# This is an evidence collector, NOT an approval generator. It never writes
# HVX.AlgorithmConfidentialityReview/1, runs a product entry point or accesses a
# device SDK. Compatibility explicitly includes synthetic offline ONNX inference.
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
    if(-not $full.StartsWith($BuildRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Protection evidence/output must belong to this exact candidate.'}
    return CandidateFile $full.Substring($BuildRoot.Length+1).Replace('\','/')
}
function Inventory {
    $files=@(Get-Item -LiteralPath (CandidateFile 'Xiongxin.OWTS.Desktop.exe'))
    foreach($folder in @('modules','model-payload')){$files+=@(Get-RegressionTreeFiles (Join-Path $BuildRoot $folder))}
    @($files|Sort-Object FullName|ForEach-Object{[ordered]@{Path=$_.FullName.Substring($BuildRoot.Length+1).Replace('\','/');SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash;Bytes=$_.Length}})
}
$qa=Join-Path $BuildRoot ('qa/final-protection-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
Assert-RegressionUnlinkedPath $qa -AllowMissing
New-Item -ItemType Directory -Path $qa|Out-Null
$before=@(Inventory);$started=[DateTime]::UtcNow.ToString('o')
$steps=New-Object 'System.Collections.Generic.List[object]'
$coverage=New-Object 'System.Collections.Generic.List[object]'
$inputRows=New-Object 'System.Collections.Generic.List[object]'
$scopeNotes=New-Object 'System.Collections.Generic.List[string]'
$inferenceStarted=$false;$inferenceVerified=$false
function SaveReceipt([string]$status,[string]$failure=''){
    $unchanged=$false
    try{$unchanged=(($before|ConvertTo-Json -Depth 5 -Compress) -ceq (@(Inventory)|ConvertTo-Json -Depth 5 -Compress))}catch{}
    [ordered]@{Schema='HVX.FinalProtectionAudit/1';BuildRoot=$BuildRoot;StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Status=$status;Failure=$failure;
        HardwareAccessed=$false;ProductEntryExecuted=$false;WorkerStarted=$false;UserProjectsWritten=$false;ActualUserCacheWritten=$false;
        ModelInferenceRequested=[bool]$RunCompatibility;InferenceExecutionAttempted=$inferenceStarted;InferenceExecuted=$(if($inferenceStarted -and -not $inferenceVerified){$null}else{$inferenceVerified});
        RuntimeUnchanged=$unchanged;Runtime=$before;Inputs=$inputRows.ToArray();OwnedConsumerCoverage=$coverage.ToArray();Steps=$steps.ToArray();ScopeNotes=$scopeNotes.ToArray();
        ManualReviewRequired=$true;GateApprovalGenerated=$false;FullConfidentialityAccepted=$false;FullCompatibilityAccepted=$false;AbsoluteProtectionClaimed=$false;
        Limitations=@('Paired managed decompiler samples and encrypted-section coverage are not a full proprietary-scope review or proof against reverse engineering.',
          'Native code, runtime memory/model plaintext, source-to-release provenance and intended public/excluded code require explicit reviewer classification.',
          'Compatibility runs synthetic algorithms and isolated cache/file fixtures only; no physical/electrical acceptance is implied.',
          'Build folders, immutable unprotected inputs and QA/decompiled samples are private and MUST NOT be distributed.')}|
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
    $bm=Get-Content -LiteralPath (CandidateFile 'build-manifest.json') -Raw|ConvertFrom-Json
    $mm=Get-Content -LiteralPath (CandidateFile 'module-manifest.json') -Raw|ConvertFrom-Json
    if($bm.ExecutableSha256 -cne (Get-FileHash -LiteralPath (CandidateFile 'Xiongxin.OWTS.Desktop.exe')).Hash){throw 'Executable/build manifest mismatch.'}
    $scopePath=Join-Path $PSScriptRoot 'owned-algorithm-protection.json'
    $scopeHash=(Get-FileHash -LiteralPath $scopePath).Hash
    $scope=Get-Content -LiteralPath $scopePath -Raw|ConvertFrom-Json
    if($scope.Schema -cne 'HVX.OwnedAlgorithmProtectionScope/1'){throw 'Unknown owned scope schema.'}
    # Reuse ONLY the pure metadata-contract reader, not the transforming script.
    $protectAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Protect-OwnedAlgorithms.ps1'),[ref]$tokens,[ref]$errors)
    if($errors.Count){throw 'Cannot parse metadata-contract reader.'}
    $contractNodes=@($protectAst.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq 'Get-Contract')
    if($contractNodes.Count -ne 1){throw 'Missing/ambiguous pure metadata reader.'}
    . ([ScriptBlock]::Create($contractNodes[0].Extent.Text))
    foreach($path in @($PSCommandPath,$scopePath,(Join-Path $PSScriptRoot 'AlgorithmConfidentialityGate.ps1'),(Join-Path $PSScriptRoot 'Protect-OwnedAlgorithms.ps1'),
      (Join-Path $PSScriptRoot 'Audit-FinalNativeModelPayload.ps1'),(Join-Path $PSScriptRoot 'Test-OwnedAlgorithmStaticRecovery.ps1'),(Join-Path $PSScriptRoot 'Test-OwnedAlgorithmProtection.ps1'),
      (Join-Path $PSScriptRoot 'Test-UiRepairFocused.ps1'),(Join-Path $PSScriptRoot 'Test-DeviceConnectNoPassword.ps1'),(Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),
      (Join-Path $PSScriptRoot 'compiler/csc.exe'),(Join-Path $root 'tests/EmbeddedModulePayloadSmoke.cs'),(Join-Path $root 'tests/DefectModelTrustSmoke.cs'),
      (Join-Path $root 'tests/OwnedAlgorithmBodyProbe.cs'),(Join-Path $root 'tests/Task12ModelRoutingSmoke.cs'),(Join-Path $root 'tests/HybridRuntimeSmoke.cs'),
      (Join-Path $root 'tests/AnalysisCalibrationArchiveSmoke.cs'),(Join-Path $root 'tests/AnalysisCalibrationInputScopeSmoke.cs'))){
        Assert-RegressionUnlinkedPath $path
        $inputRows.Add([ordered]@{Path=$path;SHA256=(Get-FileHash -LiteralPath $path).Hash})
    }
    # The host source-built routing fixture is permitted only with the exact
    # final build's source bytes, never a later source tree relabelled as final.
    foreach($source in $bm.Sources){
        $path=[IO.Path]::GetFullPath((Join-Path $root ([string]$source.Path)))
        if(-not $path.StartsWith($root+'\src\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected host source path.'}
        Assert-RegressionUnlinkedPath $path
        if((Get-FileHash -LiteralPath $path).Hash -cne $source.Sha256){throw ('Current source differs from final build: '+$source.Path)}
        $inputRows.Add([ordered]@{Path=$path;SHA256=$source.Sha256})
    }
    $expected=@{
        'Xiongxin.OWTS.Desktop.exe'='HostPure';'modules/DataAnalysis/HVX.PdReview.V1.0.dll'='PdReview';'modules/PressureTest/HVX.PdReview.V1.0.dll'='PdReview';
        'modules/DataBrowser/Xiongxin.OWTS.DataBrowser.dll'='DataBrowser';'modules/ReportGenerator/Xiongxin.OWTS.ReportGenerator.dll'='ReportGenerator';
        'modules/CalibrationTest/Xiongxin.OWTS.CalibrationTest.dll'='CalibrationTest';'modules/CalibrationAcquisition/XIONGXIN.Calibration.Verification.exe'='CalibrationAcquisition';
        'modules/PressureTest/Xiongxin.OWTS.PressureTest.dll'='PressureTest';'modules/PressureRuntime/Xiongxin.Pressure.Runtime.exe'='PressureRuntimePure';
        'modules/Task39/Xiongxin.Task39.InputWorker.exe'='Task39InputPure';'modules/Task38/HVX.OWTS.Task38.V1.0.38.dll'='Task38LibraryPure'
    }
    $mappings=@($mm.OwnedAlgorithmProtection)+@($bm.OwnedHostAlgorithmProtection)
    if($mappings.Count -ne $expected.Count){throw 'Incomplete/extra owned consumer protection mapping.'}
    $dn='D:\PD_AI_LabVIEW2026_Project\Pulse400Hz_Extraction_DLL\tools\ConfuserEx-CLI-1.6.0\dnlib.dll'
    Assert-RegressionUnlinkedPath $dn
    $inputRows.Add([ordered]@{Path=$dn;SHA256=(Get-FileHash -LiteralPath $dn).Hash})
    [Reflection.Assembly]::LoadFrom($dn)|Out-Null
    $seen=@{};$unique=@{}
    foreach($mapping in $mappings){
        $relative=[string]$mapping.Path
        if(-not $expected.ContainsKey($relative) -or $mapping.Profile -cne $expected[$relative] -or $seen.ContainsKey($relative)){throw 'Wrong/duplicate protection consumer.'}
        $seen[$relative]=$true
        $consumer=CandidateFile $relative;$receiptPath=CandidateFile ([string]$mapping.ReceiptPath)
        if((Get-FileHash -LiteralPath $receiptPath).Hash -cne $mapping.ReceiptSHA256){throw 'Receipt changed from build mapping.'}
        $receipt=Get-Content -LiteralPath $receiptPath -Raw|ConvertFrom-Json
        if($receipt.Schema -cne 'HVX.OwnedAlgorithmProtection/1' -or $receipt.Profile -cne $mapping.Profile -or $receipt.ScopeSHA256 -cne $scopeHash -or
           $receipt.InputSHA256 -cne $mapping.InputSHA256 -or $receipt.OutputSHA256 -cne $mapping.OutputSHA256 -or
           $receipt.HardwareAccessed -ne $false -or $receipt.ProductEntryExecuted -ne $false -or $receipt.ModelInference -ne $false -or
           $receipt.ExistingMetadataPreserved -ne $true -or $receipt.InputUnchanged -ne $true -or
           $receipt.ToolSHA256 -cne '0790671F930BD1375FA06ECE29A776404CEE44695E083CACE46C905433644130'){throw 'Protection receipt contract/scope mismatch.'}
        $directory=[IO.Path]::GetDirectoryName($receiptPath)
        $input=ConfinedFile (Join-Path $directory ('input/'+[IO.Path]::GetFileName($receipt.Input)))
        $output=ConfinedFile ([string]$receipt.Output)
        if((Get-FileHash -LiteralPath $input).Hash -cne $receipt.InputSHA256 -or (Get-FileHash -LiteralPath $output).Hash -cne $receipt.OutputSHA256 -or
           (Get-FileHash -LiteralPath $consumer).Hash -cne $receipt.OutputSHA256){throw 'Immutable snapshot/protected output/final consumer mismatch.'}
        # Derive every expected method from the immutable original and declared
        # exact scope. Do not let a truncated self-consistent receipt claim full
        # method coverage merely by lowering EncryptedMethodCount.
        $plan=$scope.Profiles.([string]$mapping.Profile)
        $wanted=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        $original=[dnlib.DotNet.ModuleDefMD]::Load($input)
        try {
            if($original.Assembly.Name.String -cne $plan.AssemblyName -or $original.Assembly.PublicKey.Data.Length -ne 0){throw 'Wrong original owned assembly identity.'}
            $oldContract=@(Get-Contract $original)
            $originalTypes=@($original.GetTypes());$roots=@($plan.Types);$exempt=@()
            if($null -ne $plan.PSObject.Properties['AdditionalTypes']){$roots+=@($plan.AdditionalTypes)}
            if($null -ne $plan.PSObject.Properties['UnprotectedContractTypes']){$exempt=@($plan.UnprotectedContractTypes)}
            if($roots.Count -ne $receipt.ExactRootCount -or @($receipt.Targets).Count -ne $roots.Count){throw 'Exact root count mismatch.'}
            foreach($rootType in $roots){
                if(@($originalTypes|Where-Object FullName -ceq $rootType).Count -ne 1 -or
                   @($receipt.Targets|Where-Object Type -ceq $rootType).Count -ne 1){throw 'Exact owned root absent/duplicated.'}
                foreach($type in $originalTypes){
                    if(($type.FullName -ceq $rootType -or $type.FullName.StartsWith($rootType+'/',[StringComparison]::Ordinal)) -and $type.FullName -cnotin $exempt){
                        foreach($method in $type.Methods){if($method.HasBody){$null=$wanted.Add($method.FullName)}}
                    }
                }
            }
            if($null -ne $plan.PSObject.Properties['Methods']){
                foreach($group in $plan.Methods){
                    $owners=@($originalTypes|Where-Object FullName -ceq $group.Type)
                    if($owners.Count -ne 1){throw 'Mixed method owner absent/duplicated.'}
                    foreach($name in $group.Names){
                        $named=@($owners[0].Methods|Where-Object {$_.Name.String -ceq $name -and $_.HasBody})
                        if($named.Count -eq 0){throw 'Declared mixed calculation method missing.'}
                        foreach($method in $named){$null=$wanted.Add($method.FullName)}
                    }
                }
            }
        }finally{$original.Dispose()}
        $module=[dnlib.DotNet.ModuleDefMD]::Load($consumer)
        try {
            $newContract=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
            foreach($line in @(Get-Contract $module)){$null=$newContract.Add($line)}
            if(@($oldContract|Where-Object {-not $newContract.Contains($_)}).Count){throw 'Actual final metadata/reflection/serialization contract changed.'}
            $methods=@{};foreach($type in $module.GetTypes()){foreach($method in $type.Methods){$methods[$method.FullName]=$method}}
            $count=0;$methodSeen=@{}
            foreach($record in $receipt.Methods){
                $id=[string]$record.Method
                if(-not $methods.ContainsKey($id) -or $methodSeen.ContainsKey($id) -or -not $wanted.Contains($id)){throw 'Missing/duplicate/out-of-scope protected method.'}
                $methodSeen[$id]=$true;$rva=[uint32]$methods[$id].RVA
                $section=@($module.MetaData.PEImage.ImageSectionHeaders|Where-Object {$rva -ge [uint32]$_.VirtualAddress -and $rva -lt ([uint32]$_.VirtualAddress+[uint32]$_.VirtualSize)})
                if($rva -ne $record.RVA -or $record.EncryptedSection -ne $true -or $section.Count -ne 1 -or $section[0].DisplayName -in @('.text','.rsrc','.reloc')){throw 'Actual final method is not at the recorded protected section.'}
                $count++
            }
            if($count -le 0 -or $count -ne $receipt.EncryptedMethodCount -or $count -ne $wanted.Count){throw 'Empty/incomplete method coverage count.'}
            $fir=@($module.GetTypes()|Where-Object FullName -ceq 'HVX.SignalProcessing.PdLinearHighPass')
            if($fir.Count -eq 1 -and 'HVX.SignalProcessing.PdLinearHighPass' -cin @($receipt.Targets|ForEach-Object Type)){
                $reflect=@($fir[0].Methods|Where-Object {$_.Name.String -ceq 'Reflect'})
                if($reflect.Count -ne 1 -or -not $methodSeen.ContainsKey($reflect[0].FullName)){throw 'FIR Reflect is not actually covered.'}
            }
            if($mapping.Profile -ceq 'PressureTest'){
                foreach($helper in @('HVX.Controls.PressureDisplayMedian','HVX.Controls.PressurePdDisplayEnvelope')){
                    if(@($module.GetTypes()|Where-Object FullName -ceq $helper).Count -ne 1){throw ('Current display helper missing: '+$helper)}
                    $listed=$helper -cin @($receipt.Targets|ForEach-Object Type)
                    $scopeNotes.Add($helper+': '+$(if($listed){'explicit owned protected root'}else{'not an owned protected root; reviewer must classify this standard display-only helper, not claim protected'}))
                }
            }
            $coverage.Add([ordered]@{Path=$relative;Profile=$mapping.Profile;InputSHA256=$receipt.InputSHA256;SHA256=$receipt.OutputSHA256;ReceiptPath=$mapping.ReceiptPath;ReceiptSHA256=$mapping.ReceiptSHA256;ActualProtectedMethodCount=$count})
        }finally{$module.Dispose()}
        if(-not $unique.ContainsKey([string]$mapping.Profile)){$unique[[string]$mapping.Profile]=[pscustomobject]@{Directory=$directory;Consumer=$consumer;Receipt=$receipt}}
    }
    $scopeNotes.Add('Task17 A/B separate signed/protected pipeline must retain its actual source, receipt and final-consumer chain; not covered by owned-profile samples.')
    $scopeNotes.Add('Task12 pinned binary and model containers require their independent provenance/static review; offline inference success is not confidentiality proof.')
    $scopeNotes.Add('Native C/D/Hybrid compiled files are not managed encrypted bodies; audit their actual final PE and public third-party Python artifacts separately.')
    SaveReceipt 'IN_PROGRESS'
    $nativeReviewRoot=Join-Path $qa 'native-model-static'
    & (Join-Path $PSScriptRoot 'Audit-FinalNativeModelPayload.ps1') -BuildRoot $BuildRoot -OutputDirectory $nativeReviewRoot
    AddProof 'FinalPeCacheNativeAndModelStatic' (Join-Path $nativeReviewRoot 'verification.json')
    if($RunPairedStaticSamples){
        foreach($profile in @($unique.Keys|Sort-Object)){
            $item=$unique[$profile]
            $sampleTag='static-recovery-'+[IO.Path]::GetFileName($qa)
            & (Join-Path $PSScriptRoot 'Test-OwnedAlgorithmStaticRecovery.ps1') -ProtectionDirectory $item.Directory -PackageAssembly $item.Consumer -OutputTag $sampleTag
            AddProof ('PairedStaticSample-'+$profile) (Join-Path $item.Directory ($sampleTag+'/verification.json'))
            SaveReceipt 'IN_PROGRESS'
        }
    }
    if($RunCompatibility){
        foreach($profile in @($unique.Keys|Sort-Object)){
            $item=$unique[$profile]
            if(Test-Path -LiteralPath (Join-Path $item.Directory 'numeric-verification.json')){throw 'Prior numerical evidence exists; retain it, do not overwrite.'}
            & (Join-Path $PSScriptRoot 'Test-OwnedAlgorithmProtection.ps1') -ProtectionDirectory $item.Directory
            AddProof ('PairedPureNumerics-'+$profile) (Join-Path $item.Directory 'numeric-verification.json')
            SaveReceipt 'IN_PROGRESS'
        }
        RunManaged 'EmbeddedModulePayloadSmoke' @((Join-Path $root 'tests/EmbeddedModulePayloadSmoke.cs')) @($BuildRoot,(Join-Path $qa 'cache'))
        $standard=CandidateFile 'model-payload/5581bf7a60798cb8f349a3ee3a59c2f8c121d44ab98d23172453ec856d360d16.hxm'
        $competition=CandidateFile 'model-payload/a32a19b489cd7fb0cf51f9d027c214e7636a5621d7663384d88925f2a4ceb32b.hxm'
        RunManaged 'DefectModelTrustSmoke' @((Join-Path $root 'src/DefectModelCatalog.cs'),(Join-Path $root 'tests/DefectModelTrustSmoke.cs')) @((Join-Path $qa 'model-trust'),$standard,$competition)
        $ui=@(& (Join-Path $PSScriptRoot 'Test-UiRepairFocused.ps1') -BuildRoot $BuildRoot -Names @('HybridRuntimeSmoke','AnalysisCalibrationArchiveSmoke','AnalysisCalibrationInputScopeSmoke'))
        $ui|Set-Content -LiteralPath (Join-Path $qa 'consumer-compatibility.txt') -Encoding UTF8
        $line=@($ui|Where-Object {$_ -is [string] -and $_.StartsWith('UI_REPAIR_PROOF=',[StringComparison]::Ordinal)})
        if($line.Count -ne 1){throw 'Actual consumer compatibility proof missing.'}
        AddProof 'ActualConsumersCompatibility' (Join-Path $line[0].Substring('UI_REPAIR_PROOF='.Length) 'verification-ui-repair.json')
        $inferenceStarted=$true;SaveReceipt 'IN_PROGRESS'
        $model=@(& (Join-Path $PSScriptRoot 'Test-DeviceConnectNoPassword.ps1') -BuildRoot $BuildRoot -Names @('Task12ModelRoutingSmoke'))
        $model|Set-Content -LiteralPath (Join-Path $qa 'model-routing-inference.txt') -Encoding UTF8
        $line=@($model|Where-Object {$_ -is [string] -and $_.StartsWith('PASSWORD_FREE_SOURCE_PROOF=',[StringComparison]::Ordinal)})
        if($line.Count -ne 1){throw 'Actual model routing/inference proof missing; do not bypass missing runtime.'}
        AddProof 'ActualModelRoutingAndSyntheticInference' (Join-Path $line[0].Substring('PASSWORD_FREE_SOURCE_PROOF='.Length) 'verification.json')
        $inferenceVerified=$true
    }
    foreach($input in $inputRows){if((Get-FileHash -LiteralPath $input.Path).Hash -cne $input.SHA256){throw 'Audited/test source or tool changed during collection.'}}
    SaveReceipt 'SCOPED_CHECKS_COMPLETE_MANUAL_REVIEW_REQUIRED'
    Write-Output ('FINAL_PROTECTION_AUDIT='+$qa)
}catch{SaveReceipt 'FAILED' $_.Exception.Message;throw}
