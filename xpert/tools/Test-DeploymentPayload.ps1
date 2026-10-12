param([Parameter(Mandatory=$true)][string]$BuildRoot,[string]$EvidencePath='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$build=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
$checks=0
$evidence=New-Object System.Collections.Generic.List[object]
function Get-ContainedFile([string]$Base,[string]$Relative) {
 if([IO.Path]::IsPathRooted($Relative)){throw 'Payload manifest contains an absolute path.'}
 $path=[IO.Path]::GetFullPath((Join-Path $Base $Relative))
 if(-not $path.StartsWith($Base.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Payload path escapes its root.'}
 if(-not (Test-Path -LiteralPath $path -PathType Leaf)){throw ('Distribution file missing: '+$Relative)}
 $walk=$path
 while($walk.Length -ge $Base.Length){
  if((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw ('Linked payload path: '+$Relative)}
  $walk=[IO.Path]::GetDirectoryName($walk)
 }
 return $path
}
function Assert-FileHash([string]$Relative,[string]$Expected) {
 $path=Get-ContainedFile $build $Relative
 if($Expected -notmatch '^[A-Fa-f0-9]{64}$'){throw ('Missing payload hash: '+$Relative)}
 $actual=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
 if($actual -ne $Expected){throw ('Distribution hash mismatch: '+$Relative)}
 $script:checks++
 $evidence.Add([ordered]@{Path=$Relative;SHA256=$actual})
 return $path
}
$manifest=Get-Content -LiteralPath (Get-ContainedFile $build 'module-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($manifest.Schema -ne 'XIONGXIN.UnifiedDesktop.Modules.v2'){throw 'Complete module staging manifest is required.'}
$buildManifest=Get-Content -LiteralPath (Get-ContainedFile $build 'build-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$null=Assert-FileHash 'Xiongxin.OWTS.Desktop.exe' $buildManifest.ExecutableSha256
$seen=@{}
foreach($entry in $manifest.Modules){
 if($seen.ContainsKey($entry.Path)){throw ('Duplicate manifest entry: '+$entry.Path)}
 $seen[$entry.Path]=$true
 $null=Assert-FileHash $entry.Path $entry.Sha256
}
# A transformed binary must be tied to its original compiled input and retained
# conversion evidence. Neither a new self-reported hash nor an old PASS suffices.
if($buildManifest.PSObject.Properties['ProtectedRelease'] -and $buildManifest.ProtectedRelease){
 $expectedProtection=@('modules/DataAnalysis/HVX.PdReview.V1.0.dll','modules/PressureTest/HVX.PdReview.V1.0.dll',
  'modules/DataBrowser/Xiongxin.OWTS.DataBrowser.dll','modules/ReportGenerator/Xiongxin.OWTS.ReportGenerator.dll',
  'modules/CalibrationTest/Xiongxin.OWTS.CalibrationTest.dll','modules/CalibrationAcquisition/XIONGXIN.Calibration.Verification.exe',
  'modules/PressureTest/Xiongxin.OWTS.PressureTest.dll','modules/PressureRuntime/Xiongxin.Pressure.Runtime.exe',
  'modules/Task39/Xiongxin.Task39.InputWorker.exe','modules/Task38/HVX.OWTS.Task38.V1.0.38.dll','Xiongxin.OWTS.Desktop.exe')
 $conversions=@($manifest.OwnedAlgorithmProtection)+@($buildManifest.OwnedHostAlgorithmProtection)
 if($conversions.Count -ne $expectedProtection.Count){throw 'Owned algorithm protection coverage is incomplete.'}
 foreach($relative in $expectedProtection){
  $matches=@($conversions|Where-Object {$_.Path -ceq $relative})
  if($matches.Count -ne 1){throw ('Missing/duplicate protection mapping: '+$relative)}
  $conversion=$matches[0]
  $receiptPath=Get-ContainedFile $build $conversion.ReceiptPath
  if((Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash -cne $conversion.ReceiptSHA256){throw 'Protection conversion receipt changed.'}
  $receipt=Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
  if($receipt.Schema -cne 'HVX.OwnedAlgorithmProtection/1' -or $receipt.Profile -cne $conversion.Profile -or
     $receipt.InputSHA256 -cne $conversion.InputSHA256 -or $receipt.OutputSHA256 -cne $conversion.OutputSHA256 -or
     -not $receipt.ExistingMetadataPreserved -or -not $receipt.InputUnchanged -or $receipt.HardwareAccessed){throw 'Invalid protection conversion evidence.'}
  $original=Join-Path ([IO.Path]::GetDirectoryName($receiptPath)) ('input/'+[IO.Path]::GetFileName($relative))
  $original=Get-ContainedFile $build $original.Substring($build.Length+1)
  if((Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -cne $conversion.InputSHA256){throw 'Retained pre-protection binary changed.'}
  $null=Assert-FileHash $relative $conversion.OutputSHA256
  if($receipt.ScopeSHA256 -cne (Get-FileHash -LiteralPath (Join-Path $project 'tools/owned-algorithm-protection.json') -Algorithm SHA256).Hash){throw 'Protection scope changed after conversion.'}
  $checks++
 }
}
# Hashing only files that happen to exist cannot detect omitted dependencies.
# Require every declared runtime target independently of the output manifest.
$dependencies=Get-Content -LiteralPath (Join-Path $project 'dependencies\Algorithms\runtime-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($dependencies.Schema -ne 'XIONGXIN.LocalAlgorithmDependencies.v1' -or @($dependencies.Files).Count -ne 12){throw 'Invalid dependency contract.'}
# Models are independently staged for installation outside {app}; they must not
# disappear when removed from the embedded algorithm-dependency list.
$models=Get-Content -LiteralPath (Join-Path $project 'dependencies/Algorithms/models-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($models.Schema -ne 'XIONGXIN.EncryptedModels.v1' -or @($models.Files).Count -ne 2){throw 'Invalid separate model contract.'}
$modelKinds=@{}
foreach($entry in $models.Files){
 if($entry.Kind -notin @('standard','competition') -or $modelKinds.ContainsKey($entry.Kind) -or $entry.Source -notmatch '^Algorithms/Task12/[a-zA-Z0-9_]+\.pdm$' -or $entry.Sha256 -notmatch '^[A-F0-9]{64}$'){throw 'Invalid separate model entry.'}
 $modelKinds[$entry.Kind]=$true
 $original=Get-ContainedFile (Join-Path $project 'dependencies') $entry.Source
 if((Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -cne $entry.Sha256){throw 'Model source differs from the approved model contract.'}
 $null=Assert-FileHash ('model-payload/'+$entry.Sha256.ToLowerInvariant()+'.hxm') $entry.Sha256
}
if(@(Get-ChildItem -LiteralPath (Join-Path $build 'model-payload') -Recurse -File).Count -ne 2){throw 'Unexpected model payload file.'}
if(@(Get-ChildItem -LiteralPath (Join-Path $build 'modules') -Recurse -File | Where-Object { $_.Extension -in @('.pdm','.hxm') }).Count -ne 0){throw 'Model files must not enter the embedded modules.'}
$checks+=2
if(@(Get-ChildItem -LiteralPath (Join-Path $build 'modules') -Recurse -File -Filter '*.V1.0.15.dll').Count -ne 0){throw 'Legacy Task15 runtime must not be included after embedded-source migration.'}
$checks++
$locationName='fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll'
# The report engine, like location, is now rebuilt from current source. Verify
# the explicit source contract and deterministic rebuild; never approve it just
# because its output manifest contains its own hash, nor compare it to the old DLL.
$reportSources=@('modules-src/ReportEngine/src/AssemblyInfo.cs','modules-src/ReportEngine/src/ReportGenerator.cs','modules-src/ReportEngine/shared/BoundedDocx.cs','modules-src/ReportEngine/shared/ReportOutputSafety.cs')
if(@($manifest.SourceReportEngine.Sources).Count -ne $reportSources.Count){throw 'Missing report source contract.'}
foreach($name in $reportSources){
 $entries=@($manifest.SourceReportEngine.Sources | Where-Object {$_.Path -ceq $name})
 if($entries.Count -ne 1 -or $entries[0].Sha256 -cne (Get-FileHash -LiteralPath (Get-ContainedFile $project $name) -Algorithm SHA256).Hash){throw ('Report source contract mismatch: '+$name)}
 $checks++
}
$reportProof=Join-Path $build ('qa/report-rebuild-'+[Guid]::NewGuid().ToString('N'))
$null=& (Join-Path $project 'tools/Build-ReportEngine.ps1') -OutputDirectory $reportProof
if((Get-FileHash -LiteralPath (Join-Path $reportProof 'HVX.Report.Engine.dll') -Algorithm SHA256).Hash -cne $manifest.SourceReportEngine.Sha256){throw 'Report engine differs from a deterministic source rebuild.'}
$checks++
foreach($entry in $dependencies.Files){foreach($target in $entry.Targets){
 $expected=if([IO.Path]::GetFileName($target) -eq $locationName){$manifest.SourceLocationAlgorithm.Sha256}elseif([IO.Path]::GetFileName($target) -eq 'HVX.Report.Engine.dll'){$manifest.SourceReportEngine.Sha256}else{$entry.Sha256}
 $null=Assert-FileHash ('modules/'+$target) $expected
}}
foreach($required in @('ProjectCreation/Xiongxin.OWTS.ProjectCreation.dll','ProjectManagement/Xiongxin.OWTS.ProjectManagement.dll',
 'DataBrowser/Xiongxin.OWTS.DataBrowser.dll','DataAnalysis/HVX.PdReview.V1.0.dll','ReportGenerator/Xiongxin.OWTS.ReportGenerator.dll',
 'PressureTest/Xiongxin.OWTS.PressureTest.dll','CalibrationTest/Xiongxin.OWTS.CalibrationTest.dll',
 'CalibrationAcquisition/XIONGXIN.Calibration.Verification.exe','Task38/Task38.ControlPanel.exe','HardwareIntegration/HardwareIntegration.exe')) {
 $null=Get-ContainedFile $build ('modules/'+$required)
 if(-not $seen.ContainsKey('modules/'+$required)){throw ('Required module omitted from manifest: '+$required)}
 $checks++
}
foreach($consumer in @('DataAnalysis','PressureTest')){
 $path=Get-ContainedFile $build ('modules/'+$consumer+'/'+$locationName)
 $identity=[Reflection.AssemblyName]::GetAssemblyName($path)
 $version=[version][Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion
 if($identity.Name -ne [IO.Path]::GetFileNameWithoutExtension($locationName) -or $version -ne [version]'3.0.22.0'){
  throw ('Incompatible location algorithm: '+$consumer)
 }
 $checks++
}
$result=[ordered]@{Status='PASS';Checks=$checks;CheckedUtc=[DateTime]::UtcNow.ToString('o');BuildRoot=$build;HardwareAccessed=$false;Files=$evidence.ToArray()}
if(-not [string]::IsNullOrWhiteSpace($EvidencePath)){
 $fullEvidence=[IO.Path]::GetFullPath($EvidencePath)
 if(-not $fullEvidence.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence must remain beneath project build.'}
 $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $fullEvidence -Encoding UTF8
}
Write-Output ('DEPLOYMENT_PAYLOAD_PASS='+$checks)
