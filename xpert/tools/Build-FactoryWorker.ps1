param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath($OutputRoot)
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid output directory'}
$source=Join-Path $project 'modules-src\FactoryWorker'
# One verifier binds upstream hashes, reviewed local differences and the exact
# compiled closure. Keep the two identities instead of relabelling patched code.
$verifiedSource=& (Join-Path $PSScriptRoot 'Test-FactoryBatchSource.ps1')
$proofPath=Join-Path $source 'v30-batch-integration-provenance.json'
$proof=Get-Content -LiteralPath $proofPath -Raw|ConvertFrom-Json
if($proof.Schema -cne 'HVX.Pressure.BatchSourceImport/1' -or $proof.SourceVersion -cne '1.0.30.9' -or
 $proof.SourceExecutableSHA256 -cne '181A121A5AAE4E8F10D3BDFB99B3BBF16AED325267DB940E496F80831505FD41' -or
 $proof.SourceManifestSHA256 -cne '27BF37B8322A7D255F3B87FC3E110839B463F75BE4A84C25D8EF762CFD138B7B' -or
 $proof.HardwareAccessed -ne $false -or $proof.PhysicalAcceptance -ne $false){throw 'Frozen V1.0.30.9 candidate provenance is invalid.'}
if((Get-FileHash -LiteralPath (Join-Path $source 'v30-batch-upstream-manifest.json')).Hash -cne $proof.SourceManifestSHA256){throw 'Upstream manifest changed.'}
foreach($entry in $proof.Files){
 if([IO.Path]::IsPathRooted($entry.Path) -or $entry.Path.Contains('..') -or $entry.Path.Contains(':')){throw 'Invalid source path.'}
 if((Get-FileHash -LiteralPath (Join-Path $source $entry.Path)).Hash -cne $entry.IntegratedSHA256){throw ('Source changed after review: '+$entry.Path)}
}
$files=@(Get-ChildItem -LiteralPath $source -Recurse -File -Filter '*.cs' | Sort-Object FullName)
$expected=@($proof.Files|ForEach-Object {$_.Path.Replace('/','\')})+@($verifiedSource.LocalAdapters|ForEach-Object{$_.Path.Replace('/','\')})
if($files.Count -ne $expected.Count){throw 'Unexpected compiled source set.'}
foreach($file in $files){if($file.FullName.Substring($source.Length+1) -cnotin $expected){throw 'Unexpected source file.'}}
$inventory=@($files | ForEach-Object { [ordered]@{Path=$_.FullName.Substring($source.Length+1);SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash} })
$target=Join-Path $output 'PressureRuntime'
New-Item -ItemType Directory -Path $target -Force | Out-Null
$exe=Join-Path $target 'Xiongxin.Pressure.Runtime.exe'
$compiler=Join-Path $project 'tools\compiler\csc.exe'
$arguments=@('/nologo','/langversion:latest','/target:winexe','/platform:x64','/optimize+','/deterministic+',
 '/main:Xiongxin.FactoryTest.UnifiedWorkerEntry','/r:System.dll','/r:System.Core.dll','/r:System.Xml.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll',
 ('/win32icon:'+(Join-Path $project 'assets\xiongxin.ico')),('/resource:'+(Join-Path $project 'assets\xiongxin.ico')+',Factory.Brand.ico'),
 ('/resource:'+(Join-Path $project 'assets\xiongxin-original.png')+',Factory.Brand.Logo'),('/out:'+$exe))
& $compiler @arguments @($files.FullName)
if($LASTEXITCODE -ne 0){throw 'Pressure runtime compilation failed'}
Copy-Item -LiteralPath (Join-Path $project 'App.config') -Destination ($exe+'.config')
foreach($entry in $inventory){if((Get-FileHash -LiteralPath (Join-Path $source $entry.Path)).Hash -ne $entry.SHA256){throw 'Worker sources changed during build'}}
$verifiedAfter=& (Join-Path $PSScriptRoot 'Test-FactoryBatchSource.ps1')
if((ConvertTo-Json -InputObject $verifiedSource -Depth 8 -Compress) -cne (ConvertTo-Json -InputObject $verifiedAfter -Depth 8 -Compress)){throw 'Reviewed pressure source identity changed during build.'}
[ordered]@{Origin='Factory V1.0.30.9 base with explicitly reviewed local adaptations; not an unchanged upstream executable';
 SourceVersion=$proof.SourceVersion;UpstreamBinarySHA256=$proof.SourceExecutableSHA256;UpstreamManifestSHA256=$proof.SourceManifestSHA256;
 LocalAdaptationSHA256=$verifiedSource.LocalAdaptationSHA256;ExactUpstreamSourceCount=$verifiedSource.ExactUpstreamSourceCount;
 HardwareAccessed=$false;PhysicalAcceptance=$false;Files=$inventory;BinarySHA256=(Get-FileHash -LiteralPath $exe).Hash} |
 ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $target 'source-manifest.json') -Encoding UTF8
