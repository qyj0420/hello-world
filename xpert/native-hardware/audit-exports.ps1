param()
$ErrorActionPreference='Stop'
$nativeRoot=$PSScriptRoot
$projectRoot=Split-Path -Parent $nativeRoot
$nativeOut=Join-Path $nativeRoot 'out'
$exportsText=Get-Content -LiteralPath (Join-Path $nativeOut 'production-exports.txt') -Raw
$exports=@([regex]::Matches($exportsText,'(?m)^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(\w+)\s*$') | ForEach-Object {$_.Groups[1].Value})
if(!$exports.Count){throw 'No production exports parsed'}
if(@($exports | Where-Object {$_ -like 'HvxTest*'}).Count){throw 'Test injection hook leaked into production DLL'}
$records=@()
$sourceRoots=@((Join-Path $projectRoot 'src'),(Join-Path $projectRoot 'modules-src'))
foreach($file in Get-ChildItem -LiteralPath $sourceRoots -Filter '*.cs' -Recurse -File){
 $source=Get-Content -LiteralPath $file.FullName -Raw
 # Capture the DllImport attribute and the next extern declaration, including aliases.
 $matches=[regex]::Matches($source,'(?s)\[DllImport\((?<attribute>[^\]]+)\)\]\s*(?:\[[^\]]+\]\s*)*(?:(?:private|public|internal|protected|static|unsafe)\s+)*extern\s+\S+\s+(?<method>\w+)\s*\(')
 foreach($match in $matches){
  $entryMatch=[regex]::Match($match.Groups['attribute'].Value,'EntryPoint\s*=\s*"(?<entry>[^"]+)"')
  $entry=if($entryMatch.Success){$entryMatch.Groups['entry'].Value}else{$match.Groups['method'].Value}
  if($entry -match '^(ArtDAQ_|ps5000a)'){
   $records+=@{entry=$entry;file=$file.FullName.Substring($projectRoot.Length+1);line=1+([regex]::Matches($source.Substring(0,$match.Index),"`n").Count);sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
  }
 }
}
$required=@($records.entry | Sort-Object -Unique)
$missing=@($required | Where-Object {$_ -notin $exports})
$sdkExports=@($exports | Where-Object {$_ -match '^(ArtDAQ_|ps5000a)'})
$headers=@('C:\Program Files (x86)\ART Technology\ART-DAQ\Include\Art_DAQ.h','C:\Program Files\Pico Technology\SDK\inc\ps5000aApi.h','C:\Program Files\Pico Technology\SDK\inc\PicoStatus.h')
$headerEvidence=@($headers | Where-Object {Test-Path -LiteralPath $_ -PathType Leaf} | ForEach-Object {@{path=$_;sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})
$result=@{utc=[DateTime]::UtcNow.ToString('o');requiredCount=$required.Count;exportCount=$sdkExports.Count;missing=$missing;required=$required;exports=$exports;declarations=$records;officialHeadersReadOnly=$headerEvidence;testHooksAbsent=$true}
$result | ConvertTo-Json -Depth 6 | Out-File -LiteralPath (Join-Path $nativeOut 'api-coverage.json') -Encoding utf8
Write-Output ('SDK exports: '+$sdkExports.Count+'; unique current C# SDK entries: '+$required.Count+'; missing: '+$missing.Count+'; production test hooks: 0')
if($missing.Count){throw ('Missing production SDK entries: '+($missing -join ', '))}
if($required.Count -lt 47){throw 'Fewer than the audited 47 SDK entries were found; inspect inventory extraction'}
