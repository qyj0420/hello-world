[CmdletBinding()]
param([string]$OutputRoot = 'build/qa-r57-report-merge')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not [IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot = Join-Path $root $OutputRoot }
if (Test-Path -LiteralPath $OutputRoot) { throw 'Use a new evidence directory; prior results must be preserved.' }
$null = New-Item -ItemType Directory -Path $OutputRoot
$sourceFiles = @(
 'modules-src/ProjectManagement/src/ReportMergeService.cs',
 'modules-src/ProjectManagement/src/ProjectModels.cs',
 'modules-src/ProjectManagement/src/ProjectInsulationStatus.cs',
 'modules-src/ReportEngine/shared/BoundedDocx.cs',
 'tools/Test-R57ReportMergeSecurity.cs') | ForEach-Object { Join-Path $root $_ }
$before = @($sourceFiles | ForEach-Object { [ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash} })
$exe = Join-Path $OutputRoot 'ReportMergeSecurityOffline.exe'
& (Join-Path $PSScriptRoot 'compiler/csc.exe') /nologo /target:exe /platform:x64 /langversion:7.3 /deterministic+ /optimize+ /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Xml.dll /r:System.Xml.Linq.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ('/out:'+$exe) @sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Pure report merge security fixture did not compile.' }
& $exe (Join-Path $OutputRoot 'fixtures') (Join-Path $OutputRoot 'proof.json')
if ($LASTEXITCODE -ne 0) { throw 'Pure report merge security fixture failed.' }
foreach ($entry in $before) { if ((Get-FileHash -LiteralPath $entry.Path).Hash -cne $entry.SHA256) { throw 'Source changed during verification.' } }
[ordered]@{Schema='HVX.R57.ReportMergeSecurityInputs/1';InputsUnchanged=$true;Inputs=$before;FixtureSHA256=(Get-FileHash -LiteralPath $exe).Hash;ProofSHA256=(Get-FileHash -LiteralPath (Join-Path $OutputRoot 'proof.json')).Hash;WordExecuted=$false;ProductExecuted=$false;HardwareAccessed=$false} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputRoot 'inputs.json') -Encoding UTF8
