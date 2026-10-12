param([string]$Upstream='D:\PD_AI_LabVIEW2026_Project\FactoryAcceptanceTester_R21',[string]$OutputRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(!$OutputRoot){$OutputRoot=Join-Path $root ('build\v30-source-verification-'+[Guid]::NewGuid().ToString('N').Substring(0,10))}
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
if(!$OutputRoot.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $OutputRoot)){throw 'Output must be a new isolated project build directory.'}
$Upstream=[IO.Path]::GetFullPath($Upstream)
$release=Join-Path $Upstream 'release\出厂测试程序_V1.0.30'
$manifestPath=Join-Path $release 'manifest.json'
$manifestHash=(Get-FileHash -LiteralPath $manifestPath).Hash
$manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if($manifest.FileVersion-ne'1.0.30.0'-or$manifest.ProductVersion-ne'V1.0.30'){throw 'Wrong upstream release identity.'}
function RequireUnlinked([string]$path){$node=Get-Item -LiteralPath $path;while($node){if($node.Attributes-band[IO.FileAttributes]::ReparsePoint){throw ('Linked path: '+$path)};$node=if($node-is[IO.FileInfo]){$node.Directory}else{$node.Parent}}}
RequireUnlinked $Upstream
$artifacts=@(foreach($item in $manifest.Files){
 $path=[IO.Path]::GetFullPath((Join-Path $release $item.Name))
 if(!$path.StartsWith($release+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Manifest escaped release root.'}
 RequireUnlinked $path
 $actual=Get-Item -LiteralPath $path;$hash=(Get-FileHash -LiteralPath $path).Hash
 if($actual.Length-ne$item.Bytes-or$hash-cne$item.SHA256){throw ('Release artifact mismatch: '+$item.Name)}
 [ordered]@{Path=$item.Name;Bytes=$actual.Length;SHA256=$hash}
})
$sources=@(Get-ChildItem -LiteralPath $Upstream -Filter '*.cs' -Recurse -File | Where-Object{$_.FullName-notlike '*\release\*'-and$_.FullName-notlike '*\verification\*'})
$inputs=@($sources.FullName)+@((Join-Path $Upstream 'Build.ps1'),(Join-Path $Upstream 'AGENTS.md'),(Join-Path $Upstream 'assets\xiongxin.ico'),(Join-Path $Upstream 'assets\xiongxin-original.png'))
$inventory=@(foreach($path in $inputs){RequireUnlinked $path;[ordered]@{Path=$path.Substring($Upstream.Length+1);SHA256=(Get-FileHash -LiteralPath $path).Hash}})
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$compiler='D:\PD_AI_LabVIEW2026_Project\build\tools\roslyn48\tasks\net472\csc.exe'
$exe=Join-Path $OutputRoot 'Xiongxin.FactoryAcceptance.exe'
$icon=Join-Path $Upstream 'assets\xiongxin.ico';$logo=Join-Path $Upstream 'assets\xiongxin-original.png'
# Exactly the upstream Build.ps1 compile invocation, without starting its tests/UI.
$compile=@(& $compiler /nologo /langversion:latest /target:winexe /platform:x64 /optimize+ /debug- /main:Xiongxin.FactoryTest.Program /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ('/win32icon:'+$icon) ('/resource:'+$icon+',Factory.Brand.ico') ('/resource:'+$logo+',Factory.Brand.Logo') ('/out:'+$exe) @($sources.FullName) 2>&1 | ForEach-Object{$_.ToString()})
$exit=$LASTEXITCODE
foreach($entry in $inventory){if((Get-FileHash -LiteralPath (Join-Path $Upstream $entry.Path)).Hash-cne$entry.SHA256){throw ('Upstream source changed during verification: '+$entry.Path)}}
if($exit-ne0){throw ('Offline reference compilation failed: '+($compile-join "`n"))}
if((Get-FileHash -LiteralPath $manifestPath).Hash-cne$manifestHash){throw 'Release manifest changed during verification.'}
$released=Join-Path $release 'Xiongxin.FactoryAcceptance.exe'
[byte[]]$expected=[IO.File]::ReadAllBytes($released);[byte[]]$actual=[IO.File]::ReadAllBytes($exe)
if($expected.Length-ne$actual.Length){throw 'Rebuilt source does not have the release binary length.'}
function MaskIdentity([byte[]]$bytes){
 $pe=[BitConverter]::ToInt32($bytes,0x3c)
 if([BitConverter]::ToInt32($bytes,$pe)-ne0x4550){throw 'Invalid PE signature.'}
 $optional=$pe+24;$size=[BitConverter]::ToUInt16($bytes,$pe+20)
 $sections=[BitConverter]::ToUInt16($bytes,$pe+6)
 $directory=if([BitConverter]::ToUInt16($bytes,$optional)-eq0x20b){$optional+112}else{$optional+96}
 function RvaOffset([uint32]$rva){
  for($s=0;$s-lt$sections;$s++){$offset=$optional+$size+40*$s;$va=[BitConverter]::ToUInt32($bytes,$offset+12);$length=[Math]::Max([BitConverter]::ToUInt32($bytes,$offset+8),[BitConverter]::ToUInt32($bytes,$offset+16));if($rva-ge$va-and$rva-lt$va+$length){return [int]($rva-$va+[BitConverter]::ToUInt32($bytes,$offset+20))}}
  throw 'Unmapped metadata RVA.'
 }
 $clr=RvaOffset ([BitConverter]::ToUInt32($bytes,$directory+14*8))
 $metadata=RvaOffset ([BitConverter]::ToUInt32($bytes,$clr+8))
 if([BitConverter]::ToUInt32($bytes,$metadata)-ne0x424a5342){throw 'Invalid CLR metadata signature.'}
 $streamHeader=$metadata+16+[BitConverter]::ToInt32($bytes,$metadata+12)
 $streams=[BitConverter]::ToUInt16($bytes,$streamHeader+2);$p=$streamHeader+4;$tables=-1;$guidHeap=-1
 for($s=0;$s-lt$streams;$s++){
  $start=$p+8;$end=$start;while($bytes[$end]-ne0){$end++}
  $name=[Text.Encoding]::ASCII.GetString($bytes,$start,$end-$start)
  $absolute=$metadata+[BitConverter]::ToInt32($bytes,$p)
  if($name-eq'#~'-or$name-eq'#-'){$tables=$absolute};if($name-eq'#GUID'){$guidHeap=$absolute}
  $p=$start+([int][Math]::Ceiling(($end-$start+1)/4.0))*4
 }
 if($tables-lt0-or$guidHeap-lt0){throw 'Missing metadata tables or GUID heap.'}
 $heapSizes=$bytes[$tables+6];$valid=[BitConverter]::ToUInt64($bytes,$tables+8);$rows=$tables+24
 if(($valid-band1)-eq0-or[BitConverter]::ToUInt32($bytes,$rows)-ne1){throw 'Expected one Module row.'}
 for($t=0;$t-lt64;$t++){if((($valid-shr$t)-band1)-ne0){$rows+=4}}
 $stringIndexSize=if($heapSizes-band1){4}else{2};$guidIndexSize=if($heapSizes-band2){4}else{2}
 $indexOffset=$rows+2+$stringIndexSize
 $index=if($guidIndexSize-eq4){[BitConverter]::ToUInt32($bytes,$indexOffset)}else{[BitConverter]::ToUInt16($bytes,$indexOffset)}
 if($index-lt1){throw 'Module MVID index is absent.'}
 $timestamp=$pe+8;$mvid=$guidHeap+16*($index-1)
 [byte[]]$guid=$bytes[$mvid..($mvid+15)];$guidText=(New-Object Guid (,$guid)).ToString()
 for($j=0;$j-lt4;$j++){$bytes[$timestamp+$j]=0};for($j=0;$j-lt16;$j++){$bytes[$mvid+$j]=0}
 [ordered]@{TimestampOffset=$timestamp;MvidOffset=$mvid;Mvid=$guidText}
}
$expectedMask=MaskIdentity $expected;$actualMask=MaskIdentity $actual
$firstDifference=-1
for($i=0;$i-lt$expected.Length;$i++){if($expected[$i]-ne$actual[$i]){$firstDifference=$i;break}}
if($firstDifference-ge0){throw ('Source does not reproduce release code/resources. Unexplained byte difference at '+$firstDifference)}
if($expectedMask.TimestampOffset-ne$actualMask.TimestampOffset-or$expectedMask.MvidOffset-ne$actualMask.MvidOffset){throw 'PE identity offsets differ.'}
$proof=[ordered]@{Schema='HVX.Factory.SourceCorrespondence/1';Version='1.0.30.0';VerifiedUtc=[DateTime]::UtcNow.ToString('O');Upstream=$Upstream;Release=$release;ManifestSHA256=$manifestHash;ReleasedBinarySHA256=(Get-FileHash -LiteralPath $released).Hash;RecompiledBinarySHA256=(Get-FileHash -LiteralPath $exe).Hash;CompilerSHA256=(Get-FileHash -LiteralPath $compiler).Hash;SourceUnchanged=$true;CodeAndResourcesByteIdentical=$true;OnlyAllowedDifferences=@('PE TimeDateStamp (4 bytes)','CLR ModuleVersionId (16 bytes)');ReleasedIdentity=$expectedMask;RecompiledIdentity=$actualMask;HardwareAccessed=$false;PhysicalAcceptance=$false;PublishedFiles=$artifacts;SourceFiles=$inventory}
$proof | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputRoot 'source-correspondence.json') -Encoding UTF8
Write-Output ('V30_SOURCE_CORRESPONDENCE='+$OutputRoot)
