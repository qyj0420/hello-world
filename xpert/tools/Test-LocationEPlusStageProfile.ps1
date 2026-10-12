param([Parameter(Mandatory=$true)][string]$OutputRoot,[Parameter(Mandatory=$true)][string]$ModulePath,[Parameter(Mandatory=$true)][string]$ModulesRoot,[Parameter(Mandatory=$true)][string]$ReadOnlyLocationFile)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$qa=[IO.Path]::GetFullPath($OutputRoot)
if($PSVersionTable.PSEdition-ne'Desktop'){throw 'Use Windows PowerShell 5.1.'}
if(-not $qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $qa)){throw 'Fresh owned QA directory required.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded helper.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n-is[Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count-ne1){throw 'Bounded helper unavailable.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$pins=Join-Path (Split-Path -Parent $ModulesRoot) 'research-pins.json';if(-not(Test-Path -LiteralPath $pins)){$pins=Join-Path $ModulesRoot 'research-pins.json'}
$source=Join-Path $root 'tests\LocationEPlusStageProfile.cs';$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$paths=@($source,$csc,$helper,$PSCommandPath,$ModulePath,$pins,$ReadOnlyLocationFile)+@(Get-ChildItem -LiteralPath (Join-Path $ModulesRoot 'r71') -File -Recurse|ForEach-Object FullName)
$inputs=@($paths|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=[IO.Path]::GetFullPath($_);SHA256=(Get-FileHash -LiteralPath $_).Hash}})
New-Item -ItemType Directory -Path $qa|Out-Null
$exe=Join-Path $qa 'LocationEPlusStageProfile.exe'
$compiled=@(& $csc /nologo /target:exe /platform:x64 /langversion:7.3 /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll ('/resource:'+$pins+',HVX.Research.Pins') ('/out:'+$exe) $source 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'compile.log') -Encoding UTF8
if($code-ne0){throw ($compiled-join"`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($ModulePath,$ModulesRoot,$ReadOnlyLocationFile) -LogPrefix (Join-Path $qa 'profile') -TimeoutMilliseconds 90000
$changed=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash-cne$_.SHA256})
[ordered]@{Schema='HVX.LocationEPlus.StageProfile.Offline/1';Inputs=$inputs;InputsUnchanged=($changed.Count-eq0);Result=$run;TestSha256=(Get-FileHash -LiteralPath $exe).Hash;ProductStarted=$false;HardwareWorkerStarted=$false;SDKCalled=$false;HardwareUsed=$false;IndependentPinnedAlgorithmProcessStarted=$true}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'proof.json') -Encoding UTF8
$run.Output|Write-Output;Write-Output ('EPLUS_PROFILE_PROOF='+$qa+'\proof.json')
if($run.ExitCode-ne0-or$changed.Count-ne0){throw 'Profile failed or inputs changed; retained evidence.'}
