param([ValidateSet('Capture','Verify')][string]$Mode='Capture',
      [Parameter(Mandatory=$true)][string]$EvidencePath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$evidence=[IO.Path]::GetFullPath($EvidencePath)
if(-not $evidence.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Freeze evidence must be inside project build.'}
function Assert-Unlinked([string]$Path){
 $walk=$Path
 while($walk.Length -ge $root.Length){
  if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw ('Linked freeze path: '+$walk)}
  $walk=[IO.Path]::GetDirectoryName($walk)
 }
}
Assert-Unlinked $evidence
function Inventory {
 $files=New-Object 'System.Collections.Generic.List[object]'
 foreach($name in @('src','modules-src','dependencies','assets','tools','hybrid-src','installer','docs\international')){
  $start=Join-Path $root $name
  if(-not (Test-Path -LiteralPath $start -PathType Container)){throw ('Missing freeze input '+$name)}
  $pending=New-Object 'System.Collections.Generic.Stack[string]';$pending.Push($start)
  while($pending.Count){
   $directory=$pending.Pop();Assert-Unlinked $directory
   foreach($item in Get-ChildItem -LiteralPath $directory -Force){
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw ('Linked freeze entry '+$item.FullName)}
    if($item.PSIsContainer){$pending.Push($item.FullName)}else{$files.Add($item)}
   }
  }
 }
 foreach($item in Get-ChildItem -LiteralPath $root -File){
  if($item.Extension -in @('.ps1','.config','.manifest')){$files.Add($item)}
 }
 $files.Add((Get-Item -LiteralPath (Join-Path $root 'docs\20261006冻结内测状态.txt')))
 @($files|Sort-Object FullName|ForEach-Object{[ordered]@{Path=$_.FullName.Substring($root.Length+1).Replace('\','/');Length=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
}
$now=@(Inventory)
$canonical=$now|ConvertTo-Json -Depth 5 -Compress
if($Mode -eq 'Capture'){
 if(Test-Path -LiteralPath $evidence){throw 'An existing freeze must never be overwritten.'}
 $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($evidence)) -Force
 $sha=[Security.Cryptography.SHA256]::Create()
 try{$digest=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))).Replace('-','')}finally{$sha.Dispose()}
 [ordered]@{Schema='HVX.FrozenInternal.Inputs/1';FrozenUtc=[DateTime]::UtcNow.ToString('o');ProjectRoot=$root;InputDigest=$digest;FileCount=$now.Count;AlgorithmAccuracyVerified=$false;HardwareAccessed=$false;Files=$now}|
 ConvertTo-Json -Depth 8|Set-Content -LiteralPath $evidence -Encoding UTF8
 Write-Output ('FREEZE_CAPTURED='+$now.Count+'; SHA256='+$digest)
}else{
 $saved=Get-Content -LiteralPath $evidence -Raw -Encoding UTF8|ConvertFrom-Json
 if($saved.Schema -ne 'HVX.FrozenInternal.Inputs/1' -or $saved.ProjectRoot -cne $root){throw 'Incorrect freeze manifest.'}
 if(($saved.Files|ConvertTo-Json -Depth 5 -Compress) -cne $canonical){throw 'Frozen build inputs changed; do not release this build.'}
 Write-Output ('FREEZE_UNCHANGED='+$now.Count)
}
