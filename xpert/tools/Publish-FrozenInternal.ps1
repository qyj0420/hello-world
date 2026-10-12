param([Parameter(Mandatory=$true)][string]$BuildRoot,
      [Parameter(Mandatory=$true)][string]$FreezeManifest,
      [Parameter(Mandatory=$true)][string]$DeploymentProof,
      [Parameter(Mandatory=$true)][string]$UiProof,
      [string]$OutputName='XIONGXIN_HV-DAC_Xpert_20261006_Frozen_Internal_01')
# Separate, explicitly limited INTERNAL lane. Never replaces or weakens the
# formal Publish-Verification119 full-regression gate.
$ErrorActionPreference='Stop'
throw 'Historical frozen publishing is read-only material; new deliveries require the protected maintenance V30 publisher.'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$build=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
function Require-BuildFile([string]$Path){
 $full=[IO.Path]::GetFullPath($Path)
 if(-not $full.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $full -PathType Leaf)){throw ('Invalid build evidence '+$full)}
 $walk=$full
 while($walk.Length -ge $root.Length){
  if((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked build evidence prohibited.'}
  $walk=[IO.Path]::GetDirectoryName($walk)
 }
 return $full
}
if(-not $build.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Select a local build.'}
if($OutputName -notmatch '^XIONGXIN_HV-DAC_Xpert_20261006_Frozen_Internal_[0-9]{2}$'){throw 'Use an explicit dated internal release name.'}
$null=& (Join-Path $PSScriptRoot 'Freeze-InternalPreview.ps1') -Mode Verify -EvidencePath (Require-BuildFile $FreezeManifest)
$frozen=Get-Content -LiteralPath $FreezeManifest -Raw -Encoding UTF8|ConvertFrom-Json
$deploy=Get-Content -LiteralPath (Require-BuildFile $DeploymentProof) -Raw -Encoding UTF8|ConvertFrom-Json
$ui=Get-Content -LiteralPath (Require-BuildFile $UiProof) -Raw -Encoding UTF8|ConvertFrom-Json
$bm=Get-Content -LiteralPath (Require-BuildFile (Join-Path $build 'build-manifest.json')) -Raw -Encoding UTF8|ConvertFrom-Json
if($deploy.Status -ne 'PASS' -or $deploy.BuildRoot.TrimEnd('\') -ine $build -or $deploy.HardwareAccessed){throw 'Deployment completeness proof is missing.'}
if($ui.BuildRoot.TrimEnd('\') -ine $build -or -not $ui.RuntimeUnchanged -or $ui.HardwareAccessed -or $ui.UserProjectsWritten -or $ui.FullRegression){throw 'Incorrect limited UI proof.'}
foreach($name in @('FrozenPreviewIsolationSmoke','HostDialogSafetySmoke','ReportSettingsHeaderLayoutSmoke','PressurePlanLayoutSmoke')){
 $test=@($ui.Tests|Where-Object Name -eq $name)
 if($test.Count -ne 1 -or $test[0].CompileExit -ne 0 -or $test[0].ExitCode -ne 0 -or $test[0].TimedOut -or -not $test[0].ProcessExitConfirmed){throw ('Required internal packaging check failed: '+$name)}
}
foreach($test in $ui.Tests){if($test.CompileExit -ne 0 -or $test.ExitCode -ne 0){throw 'Do not hide a failed focused test.'}}
foreach($entry in $ui.Runtime){
 $file=Require-BuildFile (Join-Path $build $entry.Path)
 if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne $entry.SHA256){throw ('Tested runtime changed: '+$entry.Path)}
}
$exe=Require-BuildFile (Join-Path $build 'Xiongxin.OWTS.Desktop.exe')
if(-not $bm.FrozenInternalProfile -or -not $bm.HybridArchitecture.Included -or $bm.ExecutableSha256 -cne (Get-FileHash -LiteralPath $exe).Hash){throw 'Not the frozen hybrid build.'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -ne '1.1.9.0'){throw 'Unexpected main version.'}
$release=Join-Path $root ('deliverables\'+$OutputName)
if(Test-Path -LiteralPath $release){throw 'Never overwrite an existing release.'}
$candidate=Join-Path $root ('build\frozen-package-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $candidate
foreach($name in @('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config','module-manifest.json','build-manifest.json')){
 Copy-Item -LiteralPath (Join-Path $build $name) -Destination (Join-Path $candidate $name)
}
foreach($name in @('assets','docs\international')){Copy-Item -LiteralPath (Join-Path $build $name) -Destination (Join-Path $candidate $name) -Recurse}
# Separate installer source only: these files are deployed to CommonAppData,
# not copied to {app}. Preserve the exact approved bytes and reject omissions.
$modelManifest=Get-Content -LiteralPath (Join-Path $root 'dependencies/Algorithms/models-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($modelManifest.Schema -ne 'XIONGXIN.EncryptedModels.v1' -or @($modelManifest.Files).Count -ne 2){throw 'Separate model contract is missing.'}
$null=New-Item -ItemType Directory -Path (Join-Path $candidate 'model-payload')
foreach($model in $modelManifest.Files){
 if($model.Sha256 -notmatch '^[A-F0-9]{64}$'){throw 'Invalid model digest.'}
 $name=$model.Sha256.ToLowerInvariant()+'.hxm'
 $modelInput=Require-BuildFile (Join-Path $build ('model-payload/'+$name))
 if((Get-FileHash -LiteralPath $modelInput).Hash -cne $model.Sha256){throw 'Approved model payload changed.'}
 $modelOutput=Join-Path $candidate ('model-payload/'+$name)
 Copy-Item -LiteralPath $modelInput -Destination $modelOutput
 if((Get-FileHash -LiteralPath $modelOutput).Hash -cne $model.Sha256){throw 'Copied model payload changed.'}
}
# Only authored, non-secret delivery notes. No source trees, QA dumps,
# administration credentials, private keys or customer project directories.
$null=New-Item -ItemType Directory -Path (Join-Path $candidate 'docs') -Force
foreach($name in @('20261006冻结内测状态.txt','冻结安装前只读审核_20261006.md','冻结内测交接_20261006.md')){
 Copy-Item -LiteralPath (Join-Path $root ('docs\'+$name)) -Destination (Join-Path $candidate ('docs\'+$name))
}
$null=New-Item -ItemType Directory -Path (Join-Path $candidate 'prerequisites')
$runtime=Join-Path $root 'build\prerequisites\NDP48-x86-x64-AllOS-ENU.exe'
$signature=Get-AuthenticodeSignature -LiteralPath $runtime
if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation' -or (Get-FileHash -LiteralPath $runtime).Hash -ne '0A3A390C47E639D0F7FC65B21195FEE6B7F65B066F80F70C60FAB191D14B7E40'){throw 'Invalid offline framework prerequisite.'}
Copy-Item -LiteralPath $runtime -Destination (Join-Path $candidate 'prerequisites\NDP48-x86-x64-AllOS-ENU.exe')
$license=Join-Path $build 'modules\DataAnalysis\Task12\ONNX_RUNTIME_LICENSE.txt'
Copy-Item -LiteralPath $license -Destination (Join-Path $candidate 'docs\ONNX_RUNTIME_LICENSE.txt')
Copy-Item -LiteralPath $FreezeManifest -Destination (Join-Path $candidate 'freeze-inputs.json')
Copy-Item -LiteralPath $DeploymentProof -Destination (Join-Path $candidate 'deployment-proof.json')
# Do not distribute reflection-test output (may contain private settings paths).
$receipt=[ordered]@{
 Schema='HVX.FrozenInternal.Release/1';ReleaseStatus='FrozenInternalPreview';PublishedUtc=[DateTime]::UtcNow.ToString('o');FrozenUtc=$frozen.FrozenUtc
 FrozenInputDigest=$frozen.InputDigest;FrozenInputFiles=$frozen.FileCount;ExecutableSHA256=$bm.ExecutableSha256
 Profile='Frozen-20261006-Internal01';Candidate='ABC_INTERNAL_20261006_R01';CandidateManifestSHA256='2298100252C4BB65CF651B0CDDAC13D1AF6B440291F58D3A91D7152F59C88474'
 AlgorithmSpecialistTestingPerformed=$false;AlgorithmAccuracyVerified=$false;FullRegression=$false;TenRoundRegression=$false
 HardwareAccessed=$false;PhysicalAcceptance=$false;TrainedLocationNeuralModelIncluded=$false;BundledPython=$false
 FormalPublishGateUnchanged=$true;DeploymentChecks=$deploy.Checks
 FocusedTests=@($ui.Tests|ForEach-Object{[ordered]@{Name=$_.Name;ExitCode=$_.ExitCode;Milliseconds=$_.Milliseconds;SourceSHA256=$_.SourceSHA256;TestSHA256=$_.TestSHA256}})
 ApplicationSignature=[string](Get-AuthenticodeSignature -LiteralPath $exe).Status
 RuntimePrerequisiteSignature=[string]$signature.Status
}
$receipt|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $candidate 'frozen-release.json') -Encoding UTF8
# Validate the exact copied executable; remaining components are embedded and
# covered by the tested executable hash and staged module receipt.
if((Get-FileHash -LiteralPath (Join-Path $candidate 'Xiongxin.OWTS.Desktop.exe')).Hash -cne $bm.ExecutableSha256){throw 'Copied executable changed.'}
$null=& (Join-Path $PSScriptRoot 'Freeze-InternalPreview.ps1') -Mode Verify -EvidencePath $FreezeManifest
$sums=@(Get-ChildItem -LiteralPath $candidate -Recurse -File|Sort-Object FullName|ForEach-Object{(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash+'  '+$_.FullName.Substring($candidate.Length+1).Replace('\','/')})
[IO.File]::WriteAllLines((Join-Path $candidate 'SHA256SUMS.txt'),$sums,(New-Object Text.UTF8Encoding($false)))
$null=New-Item -ItemType Directory -Path (Join-Path $root 'deliverables') -Force
[IO.Directory]::Move($candidate,$release)
Write-Output ('FROZEN_INTERNAL_PAYLOAD='+$release)
