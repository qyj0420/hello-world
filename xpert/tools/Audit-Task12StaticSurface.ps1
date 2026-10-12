param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$out=[IO.Path]::GetFullPath($OutputRoot)
if(!$out.StartsWith($repo+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $out)){throw 'Use a new audit output directory.'}
$assemblyPath=Join-Path $repo 'dependencies/Algorithms/Task12/ec59cbc4e4ac0bf1804fc88b.V1.0.12.dll'
$tool='D:\PD_AI_LabVIEW2026_Project\Pulse400Hz_Extraction_DLL\tools\ConfuserEx-CLI-1.6.0\dnlib.dll'
$null=[Reflection.Assembly]::LoadFrom($tool)
$module=[dnlib.DotNet.ModuleDefMD]::Load($assemblyPath)
try {
 $rows=@();$types=@($module.GetTypes())
 foreach($type in $types){foreach($method in $type.Methods){
  if(!$method.RVA){continue}
  $readable=$false;$instructions=0;$stringCount=0;$cryptographicCalls=0;$errorType=''
  try {
   $body=$method.Body
   if($body -and $body.Instructions.Count -gt 0){
    $readable=$true;$instructions=$body.Instructions.Count
    foreach($ins in $body.Instructions){
     if($ins.OpCode.Code -eq [dnlib.DotNet.Emit.Code]::Ldstr){$stringCount++}
     if($ins.Operand -is [dnlib.DotNet.IMethod] -and $ins.Operand.FullName -match 'System.Security.Cryptography'){ $cryptographicCalls++ }
    }
   }
  }catch {$errorType=$_.Exception.GetType().Name}
  # Only counts and metadata tokens are emitted: never plaintext operands/keys.
  $rows += [pscustomobject][ordered]@{Token=$method.MDToken.ToString();PublicApi=($type.FullName -like 'PDDefect.Product.*');DecodedInstructions=$instructions;StringOperandCount=$stringCount;CryptographicCallCount=$cryptographicCalls;BodyReadable=$readable;ErrorType=$errorType}
 }}
 New-Item -ItemType Directory -Path $out|Out-Null
 $report=[pscustomobject][ordered]@{Schema='HVX.Task12.StaticSurface/1';Utc=[DateTime]::UtcNow.ToString('o');Input=$assemblyPath;InputSHA256=(Get-FileHash -LiteralPath $assemblyPath).Hash;MetadataReaderSHA256=(Get-FileHash -LiteralPath $tool).Hash;Types=$types.Count;MethodsWithRva=$rows.Count;ReadableBodies=@($rows|Where-Object BodyReadable).Count;CryptographicCalls=($rows|Measure-Object CryptographicCallCount -Sum).Sum;ModelDecrypted=$false;ProductExecuted=$false;HardwareAccessed=$false;StringOperandsEmitted=$false;Methods=$rows}
 $report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $out 'verification.json') -Encoding UTF8
 $report|Select-Object Schema,InputSHA256,Types,MethodsWithRva,ReadableBodies,CryptographicCalls,ModelDecrypted,ProductExecuted,HardwareAccessed,StringOperandsEmitted|ConvertTo-Json
}finally{$module.Dispose()}
