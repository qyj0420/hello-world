param([Parameter(Mandatory=$true)][string]$InputDll,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Protection output must stay inside build'}
if(Test-Path -LiteralPath $output){throw 'Protection output already exists'}
New-Item -ItemType Directory -Path $output | Out-Null
$obfuscar='D:\PD_AI_LabVIEW2026_Project\dll\packages\obfuscar.2.2.50\tools\Obfuscar.Console.exe'
$confuser='D:\PD_AI_LabVIEW2026_Project\Pulse400Hz_Extraction_DLL\tools\ConfuserEx-CLI-1.6.0\Confuser.CLI.exe'
$key='D:\PD_AI_LabVIEW2026_Project\HVX_Encrypted_DLL\keys\HVX.Location.Engine.Release.snk'
foreach($path in @($InputDll,$obfuscar,$confuser,$key)){if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw 'Missing local protection prerequisite'}}
$name=[IO.Path]::GetFileName($InputDll)
$private=Join-Path $output 'obfuscar'
$protected=Join-Path $output 'protected'
$escapedInput=[Security.SecurityElement]::Escape([IO.Path]::GetFullPath($InputDll))
$config=@"
<Obfuscator>
  <Var name="OutPath" value="$private"/>
  <Var name="KeepPublicApi" value="true"/>
  <Var name="HidePrivateApi" value="true"/>
  <Var name="HideStrings" value="true"/>
  <Var name="RenameFields" value="false"/>
  <Var name="RenameProperties" value="false"/>
  <Var name="RenameEvents" value="true"/>
  <Var name="SkipGenerated" value="true"/>
  <Var name="KeyFile" value="$key"/>
  <Var name="XmlMapping" value="true"/>
  <Var name="LogFile" value="$output\mapping.xml"/>
  <AssemblySearchPath path="$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"/>
  <Module file="$escapedInput"/>
</Obfuscator>
"@
$config | Set-Content -LiteralPath (Join-Path $output 'obfuscar.xml') -Encoding UTF8
& $obfuscar (Join-Path $output 'obfuscar.xml') *> (Join-Path $output 'obfuscar.log')
if($LASTEXITCODE -ne 0){throw 'Obfuscation failed; no fallback distribution created'}
$config=@"
<project outputDir="$protected" baseDir="$private" debug="false" xmlns="http://confuser.codeplex.com">
  <rule pattern="true" preset="maximum" inherit="false">
    <protection id="rename" action="remove"/>
    <protection id="anti dump" action="remove"/>
  </rule>
  <module path="$name" snKey="$key"/>
</project>
"@
$config | Set-Content -LiteralPath (Join-Path $output 'protect.crproj') -Encoding UTF8
& $confuser -n (Join-Path $output 'protect.crproj') *> (Join-Path $output 'protect.log')
if($LASTEXITCODE -ne 0){throw 'Protection failed; no fallback distribution created'}
$result=Join-Path $protected $name
$dump=Join-Path $output 'ApiDump.exe'
& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /target:exe /platform:x64 /r:System.Core.dll ('/out:'+$dump) (Join-Path $project 'tests\ProtectedApiDump.cs')
if($LASTEXITCODE -ne 0){throw 'API checker compilation failed'}
$before=@(& $dump $InputDll);if($LASTEXITCODE -ne 0){throw 'Unprotected API could not be inspected'}
$after=@(& $dump $result);if($LASTEXITCODE -ne 0){throw 'Protected API could not be inspected'}
$before | Set-Content -LiteralPath (Join-Path $output 'api-before.txt') -Encoding UTF8
$after | Set-Content -LiteralPath (Join-Path $output 'api-after.txt') -Encoding UTF8
if($before.Count -lt 50 -or @(Compare-Object $before $after).Count -ne 0){throw 'Public API or reflection parameter names changed'}
$check=Join-Path $output 'NumericCheck.exe'
& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /target:exe /platform:x64 /r:System.Core.dll ('/out:'+$check) (Join-Path $project 'tests\AlgorithmProtectionRegression.cs')
if($LASTEXITCODE -ne 0){throw 'Protection numeric checker compilation failed'}
& $check $InputDll $result (Join-Path $output 'numeric') *> (Join-Path $output 'numeric.log')
if($LASTEXITCODE -ne 0){throw 'Protected numerical results or diagnostic contract changed'}
$receipt=[ordered]@{Schema='HVX.AlgorithmProtection.v1';InputSha256=(Get-FileHash -LiteralPath $InputDll).Hash;OutputSha256=(Get-FileHash -LiteralPath $result).Hash;PublicApiRows=$before.Count;Pipeline='Obfuscar 2.2.50 + ConfuserEx 1.6.0 (serialization field/property names retained)';NumericRegressionPassed=$true;NumericCases=12;PhysicalAcceptance=$false}
$receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'receipt.json') -Encoding UTF8
$result
