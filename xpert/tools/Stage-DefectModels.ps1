param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)-or[IO.Path]::GetFileName($output)-ne'model-payload'){throw 'Use an isolated model-payload beneath the project build directory.'}
function Assert-Unlinked([string]$path){
 for($walk=$path;$walk.Length -ge $project.Length;$walk=[IO.Path]::GetDirectoryName($walk)){
  if((Test-Path -LiteralPath $walk)-and((Get-Item -LiteralPath $walk -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Linked model path is prohibited.'}
 }
}
Assert-Unlinked $output
$manifest=Get-Content -LiteralPath (Join-Path $project 'dependencies/Algorithms/models-manifest.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($manifest.Schema-ne'XIONGXIN.EncryptedModels.v1'-or@($manifest.Files).Count-ne2){throw 'Unexpected model manifest.'}
$copies=@();$kinds=@{}
foreach($entry in $manifest.Files){
 if($entry.Kind-notin@('standard','competition')-or$kinds.ContainsKey($entry.Kind)-or$entry.Source-notmatch'^Algorithms/Task12/[a-zA-Z0-9_]+\.pdm$'-or$entry.Sha256-notmatch'^[A-F0-9]{64}$'){throw 'Invalid model entry.'}
 $kinds[$entry.Kind]=$true
 $source=Join-Path (Join-Path $project 'dependencies') $entry.Source;Assert-Unlinked $source
 if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash-cne$entry.Sha256){throw ('Model changed: '+$entry.Kind)}
 $copies+=@{Source=$source;Name=$entry.Sha256.ToLowerInvariant()+'.hxm';Hash=$entry.Sha256}
}
New-Item -ItemType Directory -Path $output -Force|Out-Null
foreach($copy in $copies){
 $destination=Join-Path $output $copy.Name;Assert-Unlinked $destination
 Copy-Item -LiteralPath $copy.Source -Destination $destination -Force
 if((Get-FileHash -LiteralPath $destination).Hash-cne$copy.Hash){throw 'Model staging verification failed.'}
}
Write-Output ('ENCRYPTED_MODELS_STAGED='+$copies.Count+'; INSTALL_OUTSIDE_APP=True')
