param(
    [Parameter(Mandatory=$true)][string]$BuildRoot,
    [Parameter(Mandatory=$true)][string]$VerificationPath,
    [string]$OutputName='HV-DAC_Xpert_1.1.7_验证版_20260930'
)
$ErrorActionPreference='Stop'
throw 'Historical 1.1.7 publishing is retired; no new unguarded delivery can be generated from this entry.'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buildBase=Join-Path $root 'build'
$source=[IO.Path]::GetFullPath((Join-Path $root $BuildRoot))
$proof=[IO.Path]::GetFullPath((Join-Path $root $VerificationPath))
if(-not $source.StartsWith($buildBase+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Source must be under this project build directory.'}
if(-not $proof.StartsWith($source+'\qa\',[StringComparison]::OrdinalIgnoreCase)){throw 'Verification must belong to the source build.'}
if([IO.Path]::GetFileName($OutputName) -ne $OutputName -or $OutputName -match '[\\/:]'){throw 'OutputName must be a single directory name.'}
$destination=Join-Path $buildBase $OutputName
if(Test-Path -LiteralPath $destination){throw 'Refusing to overwrite an existing delivery.'}
$verification=Get-Content -LiteralPath $proof -Raw | ConvertFrom-Json
if($verification.Status -ne 'PASS' -or $verification.StartAtTest -or @($verification.Tests).Count -lt 90){throw 'A complete passing regression record is required.'}
foreach($entry in $verification.ProductBinaries){
    $binary=[IO.Path]::GetFullPath((Join-Path $source $entry.Path))
    if(-not $binary.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid product manifest path.'}
    if((Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ne $entry.SHA256){throw ('Verified binary changed: '+$entry.Path)}
}
foreach($required in @('1.1.7_使用说明.md','1.1.7_修正说明.md','1.1.7_审核汇总.md','1.1.7_验证记录.md')){
    if(-not (Test-Path -LiteralPath (Join-Path $root ('docs\'+$required)))){throw ('Missing delivery document: '+$required)}
}
New-Item -ItemType Directory -Path $destination | Out-Null
foreach($name in @('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config','build-manifest.json')){
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $destination $name)
}
Copy-Item -LiteralPath (Join-Path $source 'assets') -Destination (Join-Path $destination 'assets') -Recurse
$documents=@{'1.1.7_使用说明.md'='使用说明.md';'1.1.7_修正说明.md'='修正汇总.md';'1.1.7_审核汇总.md'='审核汇总.md';'1.1.7_验证记录.md'='验证记录.md'}
foreach($item in $documents.GetEnumerator()){
    Copy-Item -LiteralPath (Join-Path $root ('docs\'+$item.Key)) -Destination (Join-Path $destination $item.Value)
}
$auditDirectory=Join-Path $destination '审核证据'
New-Item -ItemType Directory -Path $auditDirectory | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'docs') -File | Where-Object { $_.Name -like 'audit-1.1.7-*.md' -or $_.Name -eq 'task15-v3-integration-20260930.md' } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $auditDirectory $_.Name)
}
$evidenceDirectory=Join-Path $destination '验证证据'
New-Item -ItemType Directory -Path $evidenceDirectory | Out-Null
Copy-Item -LiteralPath $proof -Destination (Join-Path $evidenceDirectory 'verification.json')
foreach($extraName in @('task15-analysis-with-host-final4.json','task15-report-with-host-final4.json')){
    $extraPath=Join-Path $source ('qa\'+$extraName)
    if(Test-Path -LiteralPath $extraPath){Copy-Item -LiteralPath $extraPath -Destination (Join-Path $evidenceDirectory $extraName)}
}
Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($proof)) -File | Where-Object {$_.Name -like '*.result.txt' -or $_.Name -eq 'deployment-payload.json'} | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $evidenceDirectory $_.Name)
}
$originalHash=(Get-FileHash -LiteralPath (Join-Path $source 'Xiongxin.OWTS.Desktop.exe') -Algorithm SHA256).Hash
$deliveredHash=(Get-FileHash -LiteralPath (Join-Path $destination 'Xiongxin.OWTS.Desktop.exe') -Algorithm SHA256).Hash
if($originalHash -ne $deliveredHash){throw 'Delivery hash mismatch.'}
Write-Output ('DELIVERY='+$destination)
Write-Output ('EXE_SHA256='+$deliveredHash)
Write-Output ('FULL_REGRESSION_PROGRAMS='+@($verification.Tests).Count)
