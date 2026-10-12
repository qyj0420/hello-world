param([Parameter(Mandatory=$true)][string]$BuildRoot,[Parameter(Mandatory=$true)][string]$CurrentEvidence,[ValidatePattern('^r50[a-z]?$')][string]$DeliveryRevision='r50a')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$build=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
if(-not $build.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use the isolated verified build.'}
$sourceExe=Join-Path $build 'Xiongxin.OWTS.Desktop.exe'
$exeHash=(Get-FileHash -LiteralPath $sourceExe).Hash
$buildManifest=Get-Content -LiteralPath (Join-Path $build 'build-manifest.json') -Raw|ConvertFrom-Json
if($buildManifest.ExecutableSha256 -cne $exeHash -or (Get-Item -LiteralPath $sourceExe).VersionInfo.FileVersion -ne '1.1.1.0'){throw 'Final EXE identity mismatch.'}
$evidenceFile=[IO.Path]::GetFullPath($CurrentEvidence)
$evidence=Get-Content -LiteralPath $evidenceFile -Raw|ConvertFrom-Json
if(-not $evidence.AllCurrentHashBindingsPassed -or @($evidence.Results).Count -lt 18){throw 'Current-source evidence is incomplete.'}
foreach($row in $evidence.Results){if((Get-FileHash -LiteralPath (Join-Path $project $row.Proof)).Hash -cne $row.ProofSHA256){throw 'A verified receipt changed.'}}
$delivery=Join-Path $project ('deliverables\PublicBeta_V1.1.1_CalPressure_20261011_'+$DeliveryRevision)
$zip=Join-Path $project ('deliverables\公测版V1.1.1_校准加压定向测试_20261011_'+$DeliveryRevision+'.zip')
if((Test-Path -LiteralPath $delivery) -or (Test-Path -LiteralPath $zip)){throw 'Preserve existing delivery; choose a new reviewed revision instead of overwriting.'}
New-Item -ItemType Directory -Path $delivery|Out-Null
function Copy-Delivery([string]$Source,[string]$Relative){
 $target=Join-Path $delivery $Relative
 New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force|Out-Null
 Copy-Item -LiteralPath $Source -Destination $target
 if((Get-FileHash -LiteralPath $Source).Hash -cne (Get-FileHash -LiteralPath $target).Hash){throw 'Copied file changed.'}
}
Copy-Delivery $sourceExe '公测版V1.1.1.exe'
Copy-Delivery (Join-Path $build 'Xiongxin.OWTS.Desktop.exe.config') '公测版V1.1.1.exe.config'
foreach($asset in @(Get-ChildItem -LiteralPath (Join-Path $build 'assets') -Recurse -File)){
 Copy-Delivery $asset.FullName ('assets\'+$asset.FullName.Substring((Join-Path $build 'assets').Length+1))
}
Copy-Delivery (Join-Path $project 'tools\Invoke-R50DeploymentPreflight.ps1') '核对部署.ps1'
Copy-Delivery (Join-Path $project 'docs\20261011_r50_先读.txt') 'README_先读.txt'
foreach($document in @('20261011_r50_测试部署与实机边界.md','20261011_r50_修正与自检交接.md','20261011_r50_逐项修正工作表.md','20261011_r50_依赖核查边界.md','20261011_R50_急停主页DI与连续轮显示_离线修正.txt','20261011_E_Eplus_集成与离线证据.txt')){
 Copy-Delivery (Join-Path $project ('docs\'+$document)) ('说明\'+$document)
}
Copy-Delivery $evidenceFile 'verification\current-source-evidence.json'
foreach($row in $evidence.Results){
 $name=([IO.Path]::GetDirectoryName($row.Proof).Replace('\','_').Replace('/','_'))+'_'+[IO.Path]::GetFileName($row.Proof)
 Copy-Delivery (Join-Path $project $row.Proof) ('verification\'+$name)
}
foreach($extra in @(
 @{Source=(Join-Path $build 'build-manifest.json');Name='build-manifest.json'},
 @{Source=(Join-Path $build 'module-manifest.json');Name='module-manifest.json'},
 @{Source=(Join-Path $build 'research-pins.json');Name='research-pins.json'},
 @{Source=(Join-Path $build 'qa\deployment-payload.json');Name='deployment-payload.json'},
 @{Source=(Join-Path $build 'qa\native-model-static\verification.json');Name='native-payload-static.json'},
 @{Source=(Join-Path $project 'qa\location-e-r50\route-final2-01\verification.json');Name='E-Eplus-final-route.json'},
 @{Source=(Join-Path $project 'qa\location-e-r50\native-final2.json');Name='E-Eplus-final-native-reference.json'},
 @{Source=(Join-Path $project 'qa\location-e-r50\upstream-current.json');Name='E-Eplus-upstream-reference.json'}
)) {Copy-Delivery $extra.Source ('verification\'+$extra.Name)}
$runtime=@(Get-ChildItem -LiteralPath $delivery -Recurse -File|Where-Object{
 $_.FullName -eq (Join-Path $delivery '公测版V1.1.1.exe') -or $_.FullName -eq (Join-Path $delivery '公测版V1.1.1.exe.config') -or $_.FullName.StartsWith((Join-Path $delivery 'assets')+'\',[StringComparison]::OrdinalIgnoreCase)
}|Sort-Object FullName|ForEach-Object{[ordered]@{Path=$_.FullName.Substring($delivery.Length+1).Replace('\','/');Bytes=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$files=@(Get-ChildItem -LiteralPath $delivery -Recurse -File|Sort-Object FullName|ForEach-Object{[ordered]@{Path=$_.FullName.Substring($delivery.Length+1).Replace('\','/');Bytes=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
[ordered]@{Schema='HVX.R50.TestDelivery/1';CreatedUtc=[DateTime]::UtcNow.ToString('o');DeliveryRevision=$DeliveryRevision;ProgramFileName='公测版V1.1.1.exe';ProgramSHA256=$exeHash;ProgramBytes=(Get-Item -LiteralPath $sourceExe).Length;
 FileVersion='1.1.1.0';BuildRoot=$build;TestOnly=$true;CustomerInstaller=$false;ProductStarted=$false;HardwareAccessed=$false;PhysicalAcceptance=$false;SignedRelease=$false;
 RuntimeFiles=$runtime;Files=$files;SharedProfile='XIONGXIN/HVXUnifiedDesktop/Validation-V30-Batch-20261008';
 Remaining='Physical HV OFF/DI continuity and performance; target-PC entry error logs; original reflection/location evidence; dependency updates and full security review.'}|
 ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $delivery 'delivery-manifest.json') -Encoding UTF8
Get-ChildItem -LiteralPath $delivery -Recurse -File|Sort-Object FullName|ForEach-Object{(Get-FileHash -LiteralPath $_.FullName).Hash+'  '+$_.FullName.Substring($delivery.Length+1)}|
 Set-Content -LiteralPath (Join-Path $delivery 'SHA256SUMS.txt') -Encoding UTF8
Compress-Archive -LiteralPath $delivery -DestinationPath $zip -CompressionLevel Optimal
# Inspect the ZIP stream itself; do not execute or install any contained file.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead($zip)
$verified=0
try{
 foreach($entry in $archive.Entries){
  if($entry.FullName.EndsWith('/')){continue}
  $prefix=[IO.Path]::GetFileName($delivery)+'/'
  if(-not $entry.FullName.StartsWith($prefix,[StringComparison]::Ordinal)){throw 'Unexpected ZIP root.'}
  $relative=$entry.FullName.Substring($prefix.Length)
  if($relative.Split('/') -contains '..'){throw 'ZIP path escaped delivery.'}
  $expected=(Get-FileHash -LiteralPath (Join-Path $delivery $relative)).Hash
  $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
  try{$actual=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$sha.Dispose();$stream.Dispose()}
  if($expected -cne $actual){throw 'ZIP byte verification failed.'};$verified++
 }
 if($verified -ne @(Get-ChildItem -LiteralPath $delivery -Recurse -File).Count){throw 'ZIP file inventory mismatch.'}
}finally{$archive.Dispose()}
$receipt=Join-Path $project ('deliverables\公测版V1.1.1_'+$DeliveryRevision+'_交付校验.json')
[ordered]@{Schema='HVX.R50.ExportedDelivery/1';CreatedUtc=[DateTime]::UtcNow.ToString('o');DeliveryDirectory=$delivery;Zip=$zip;ZipSHA256=(Get-FileHash -LiteralPath $zip).Hash;VerifiedZipFiles=$verified;ProgramSHA256=$exeHash;
 DeliveryManifestSHA256=(Get-FileHash -LiteralPath (Join-Path $delivery 'delivery-manifest.json')).Hash;ExporterSHA256=(Get-FileHash -LiteralPath $PSCommandPath).Hash;
 NoExistingDeliveryOverwritten=$true;NoInstalledProductChanged=$true;ProductStarted=$false;HardwareAccessed=$false;PhysicalAcceptance=$false}|
 ConvertTo-Json -Depth 5|Set-Content -LiteralPath $receipt -Encoding UTF8
Write-Output ('DELIVERY_DIRECTORY='+$delivery)
Write-Output ('DELIVERY_ZIP='+$zip)
Write-Output ('DELIVERY_BYTE_VERIFICATION='+$verified+';EXE_SHA256='+$exeHash)
