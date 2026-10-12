param([Parameter(Mandatory=$true)][string]$BuildRoot,[string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root=[IO.Path]::GetFullPath($BuildRoot)
if(!$root.StartsWith($repo+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Build must be inside this project.'}
$out=if([string]::IsNullOrWhiteSpace($OutputDirectory)){Join-Path $root 'qa/native-model-static'}else{[IO.Path]::GetFullPath($OutputDirectory)}
if(-not $out.StartsWith($root.TrimEnd('\')+'\qa\',[StringComparison]::OrdinalIgnoreCase)){throw 'Static review output must belong to this build/qa.'}
if(Test-Path -LiteralPath $out){throw 'Audit output already exists; preserve it.'}
$exe=Join-Path $root 'Xiongxin.OWTS.Desktop.exe'
$initialHash=(Get-FileHash -LiteralPath $exe).Hash
$bm=Get-Content -LiteralPath (Join-Path $root 'build-manifest.json') -Raw|ConvertFrom-Json
if($bm.ExecutableSha256 -cne $initialHash){throw 'Candidate executable changed.'}
$dn='D:\PD_AI_LabVIEW2026_Project\Pulse400Hz_Extraction_DLL\tools\ConfuserEx-CLI-1.6.0\dnlib.dll'
[Reflection.Assembly]::LoadFrom($dn)|Out-Null
$module=[dnlib.DotNet.ModuleDefMD]::Load($exe)
$rows=New-Object 'System.Collections.Generic.List[object]'
$models=New-Object 'System.Collections.Generic.List[object]'
$native=New-Object 'System.Collections.Generic.List[object]'
function Hash-Bytes([byte[]]$bytes){$s=[Security.Cryptography.SHA256]::Create();try{([BitConverter]::ToString($s.ComputeHash($bytes))).Replace('-','')}finally{$s.Dispose()}}
function Assert-Native([string]$path){
 $b=[IO.File]::ReadAllBytes($path);$pe=[BitConverter]::ToInt32($b,60)
 if([BitConverter]::ToUInt32($b,$pe) -ne 17744 -or [BitConverter]::ToUInt16($b,$pe+4) -ne 34404){throw 'Not AMD64 PE'}
 $optional=$pe+24
 if([BitConverter]::ToUInt16($b,$optional) -ne 523 -or [BitConverter]::ToUInt32($b,$optional+112+14*8) -ne 0){throw 'Not native PE32+ without CLI header'}
 [pscustomobject]@{Path=$path.Substring($root.Length+1).Replace('\','/');SHA256=(Hash-Bytes $b);Bytes=$b.Length;Machine='AMD64';CliHeaderPresent=$false}
}
try {
 $resources=@{};foreach($r in $module.Resources){if($resources.ContainsKey($r.Name.String)){throw 'Duplicate managed resource'};$resources[$r.Name.String]=$r}
 $index=$resources['HVX.Modules.Index.v1'].CreateReader().ToArray()
 if((Hash-Bytes $index) -cne $bm.EmbeddedModules.BundleHash){throw 'Embedded index differs from manifest'}
 $lines=[Text.Encoding]::UTF8.GetString($index).Split("`n")
 if($lines[0] -cne 'XIONGXIN.Modules.v1'){throw 'Invalid embedded index header'}
 $seen=@{};$decoded=@{};$expanded=0L
 foreach($line in $lines[1..($lines.Length-1)]){
  if(!$line){continue};$p=$line.Split("`t");if($p.Count -ne 4){throw 'Invalid embedded index row'}
  $rel=$p[0];if($rel -match '(^/|\\|:|(^|/)\.\.(/|$))' -or $seen.ContainsKey($rel)){throw 'Unsafe/duplicate embedded path'};$seen[$rel]=$true
  $disk=Join-Path (Join-Path $root 'modules') $rel
  if((Get-FileHash -LiteralPath $disk).Hash -cne $p[2] -or (Get-Item -LiteralPath $disk).Length -ne [long]$p[1]){throw "Embedded/disk mismatch: $rel"}
  if(!$decoded.ContainsKey($p[3])){
   $compressed=$resources[$p[3]].CreateReader().ToArray();$ms=New-Object IO.MemoryStream(,$compressed)
   $gz=New-Object IO.Compression.GZipStream($ms,[IO.Compression.CompressionMode]::Decompress)
   $dest=New-Object IO.MemoryStream
   try {$gz.CopyTo($dest);if($dest.Length -gt 268435456){throw 'Unexpected payload size'};$decoded[$p[3]]=@{Hash=(Hash-Bytes $dest.ToArray());Length=$dest.Length}}finally{$dest.Dispose();$gz.Dispose();$ms.Dispose()}
  }
  if($decoded[$p[3]].Hash -cne $p[2] -or $decoded[$p[3]].Length -ne [long]$p[1]){throw "Embedded bytes invalid: $rel"}
  $expanded += [long]$p[1];$rows.Add([pscustomobject]@{Path=$rel;SHA256=$p[2];Bytes=[long]$p[1]})
 }
 $diskFiles=@(Get-ChildItem -LiteralPath (Join-Path $root 'modules') -File -Recurse)
 if($diskFiles.Count -ne $rows.Count -or $rows.Count -ne $bm.EmbeddedModules.FileCount -or $decoded.Count -ne $bm.EmbeddedModules.UniqueResources -or $expanded -ne $bm.EmbeddedModules.ExpandedBytes){throw 'Cache inventory counts differ'}
 foreach($f in $diskFiles){if($f.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse payload file'};$rel=$f.FullName.Substring((Join-Path $root 'modules').Length+1).Replace('\','/');if(!$seen.ContainsKey($rel)){throw 'Unlisted payload file'}}
 $expectedModels=@{'5581BF7A60798CB8F349A3EE3A59C2F8C121D44AB98D23172453EC856D360D16'=1109556;'A32A19B489CD7FB0CF51F9D027C214E7636A5621D7663384D88925F2A4CEB32B'=14708}
 $modelFiles=@(Get-ChildItem -LiteralPath (Join-Path $root 'model-payload') -File -Recurse)
 if($modelFiles.Count -ne 2){throw 'Unexpected model count'}
 foreach($f in $modelFiles){$hash=(Get-FileHash -LiteralPath $f.FullName).Hash;$bytes=[IO.File]::ReadAllBytes($f.FullName);if(!$expectedModels.ContainsKey($hash) -or $f.Length -ne $expectedModels[$hash] -or [Text.Encoding]::ASCII.GetString($bytes,0,4) -cne 'PDM2' -or $f.Name -cne ($hash.ToLowerInvariant()+'.hxm')){throw 'Unexpected model content/container'};$models.Add([pscustomobject]@{Path=$f.FullName.Substring($root.Length+1);SHA256=$hash;Bytes=$f.Length;Header='PDM2'});$expectedModels.Remove($hash)}
 if($expectedModels.Count){throw 'Missing expected model'}
 $pins=Get-Content -LiteralPath (Join-Path $root 'research-pins.json') -Raw|ConvertFrom-Json
 $oldPins=Get-Content -LiteralPath 'D:\codex\HVXR27Release\build\r27v1007d\research-pins.json' -Raw|ConvertFrom-Json
 $hasE=$null -ne $pins.PSObject.Properties['q79.cp312-win_amd64.pyd']
 $hasPacket=$null -ne $pins.PSObject.Properties['q81.cp312-win_amd64.pyd']
 $hasFinalScore=$null -ne $pins.PSObject.Properties['q84.cp312-win_amd64.pyd']
 if($hasFinalScore -and ($null -eq $pins.PSObject.Properties['q85.cp312-win_amd64.pyd'] -or -not $hasPacket)){throw 'Incomplete final-score native module set'}
 $nativeNames=if($hasFinalScore){@(71..85|ForEach-Object{'q'+$_+'.cp312-win_amd64.pyd'})}elseif($hasPacket){@(71..83|ForEach-Object{'q'+$_+'.cp312-win_amd64.pyd'})}elseif($hasE){@(71..80|ForEach-Object{'q'+$_+'.cp312-win_amd64.pyd'})}else{@('q71.cp312-win_amd64.pyd','q72.cp312-win_amd64.pyd','q73.cp312-win_amd64.pyd')}
 $thirdParty=0;$sourceLike=0
 foreach($pin in $pins.PSObject.Properties){
  $path=Join-Path (Join-Path $root 'modules/r71') $pin.Name
  if((Get-FileHash -LiteralPath $path).Hash -cne $pin.Value){throw 'Research pin mismatch'}
  if($pin.Name -in $nativeNames){$native.Add((Assert-Native $path))}else{if($oldPins.($pin.Name) -cne $pin.Value){throw 'Third-party frozen input changed'};$thirdParty++}
 }
 $expectedResearchCount=436+$nativeNames.Count
 if(@($pins.PSObject.Properties).Count -ne $expectedResearchCount -or $thirdParty -ne 436 -or $native.Count -ne $nativeNames.Count){throw 'Research inventory count mismatch'}
 $native.Add((Assert-Native (Join-Path $root 'modules/Hybrid/HvxHybridCore.dll')))
 foreach($f in $diskFiles){
  $rel=$f.FullName.Substring((Join-Path $root 'modules').Length+1).Replace('\','/')
  if($f.Extension -in '.pdm','.hxm','.onnx','.pb'){throw 'Unexpected model/plain-model payload inside modules'}
  if($f.Extension -in '.py','.pyc','.pyo','.pyx','.c','.cpp','.cs','.pdb'){
   if(!$rel.StartsWith('r71/numpy/',[StringComparison]::Ordinal) -or !$pins.PSObject.Properties[$rel.Substring(4)]){throw "Readable proprietary source/bytecode artifact: $rel"};$sourceLike++
  }
 }
 $task12=@($diskFiles|Where-Object {$_.Name -eq 'ec59cbc4e4ac0bf1804fc88b.V1.0.12.dll'})
 if($task12.Count -ne 2){throw 'Task12 consumer count mismatch'}
 foreach($f in $task12){if((Get-FileHash -LiteralPath $f.FullName).Hash -cne '324F1A405B745045FE5A0E76F924DBDA23A20A3678F0FF0250A4A300ADE477E2'){throw 'Task12 changed from static reviewed input'}}
 $modelSource=@($bm.Sources|Where-Object {$_.Path -eq 'src/DefectModelCatalog.cs'})
 if($modelSource.Count -ne 1 -or (Get-FileHash -LiteralPath (Join-Path $repo $modelSource[0].Path)).Hash -cne $modelSource[0].Sha256){throw 'Model host source not current frozen input'}
 if((Get-FileHash -LiteralPath $exe).Hash -cne $initialHash){throw 'Executable changed during audit'}
 New-Item -ItemType Directory -Path $out|Out-Null
 $report=[ordered]@{Schema='HVX.NativeModelStaticReview/1';Utc=[DateTime]::UtcNow.ToString('o');BuildRoot=$root;Status='PASS_SCOPED_STATIC_CHECKS';ExecutableSHA256=$initialHash;HardwareAccessed=$false;ProductExecuted=$false;ModelDecrypted=$false;InferenceExecuted=$false;ActualUserCacheWritten=$false;AbsoluteProtectionClaimed=$false;CompatibilityClaimed=$false;CachePayloadComparedFromFinalPe=$true;EmbeddedIndexSHA256=(Hash-Bytes $index);CacheEntries=$rows.Count;UniqueCompressedResources=$decoded.Count;ExpandedBytes=$expanded;FrozenThirdPartyFiles=$thirdParty;ThirdPartyReadableSourceArtifacts=$sourceLike;DirectReadableProprietarySourceArtifactFound=$false;NativeLibraries=$native.ToArray();Models=$models.ToArray();Task12ConsumerCopies=$task12.Count;Task12SHA256='324F1A405B745045FE5A0E76F924DBDA23A20A3678F0FF0250A4A300ADE477E2';ModelCatalogSource=$modelSource[0];MetadataToolSHA256=(Get-FileHash -LiteralPath $dn).Hash;Limitations=@('Scoped to final payload/cache, models, C/D native and Hybrid native. Managed protection and algorithm compatibility require separate evidence.','Native code can be disassembled; symbols and API strings are not encrypted. Models remain client-decryptable at inference time. No absolute secrecy claim.','Task12 is byte-identical to the separately audited DLL; ILSpy failure alone is not accepted as full confidentiality proof.','Frozen Python 3.12.14/OpenSSL 3.5.8 retain disclosed maintenance debt; current algorithms do not expose identified network/archive paths.','The containing build directory includes private source/build/QA materials and must not be distributed. Only explicit installer/runtime payload scope is reviewed.');CacheInventory=$rows.ToArray()}
 $report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $out 'verification.json') -Encoding UTF8
 [pscustomobject]$report|Select-Object Status,ExecutableSHA256,CacheEntries,UniqueCompressedResources,ExpandedBytes,FrozenThirdPartyFiles,ThirdPartyReadableSourceArtifacts,DirectReadableProprietarySourceArtifactFound,Task12ConsumerCopies|ConvertTo-Json
}finally{$module.Dispose()}
