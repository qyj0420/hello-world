[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($PSVersionTable.PSEdition-ne'Desktop'){throw 'Use Windows PowerShell 5.1.'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$qa=[IO.Path]::GetFullPath($OutputRoot)
if(-not$qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $qa)){throw 'Use a fresh owned build subdirectory.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n-is[Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count-ne1){throw 'Bounded helper missing.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$sources=@('src\ProjectMeasurementMode.cs','modules-src\DataBrowser\src\CalibrationPersistence.cs','tests\BrowserEnglishPersistenceSmoke.cs'|ForEach-Object{Join-Path $root $_})
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=@((@($sources)+@($PSCommandPath,$helper,$csc))|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
New-Item -ItemType Directory -Path $qa|Out-Null
$exe=Join-Path $qa 'BrowserEnglishPersistenceSmoke.exe'
$compiled=@(& $csc /nologo /noconfig /langversion:7.3 /platform:x64 /optimize+ /deterministic+ /target:exe /r:System.dll /r:System.Core.dll ('/out:'+$exe) @sources 2>&1|ForEach-Object{$_.ToString()})
$compileCode=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'compile.log') -Encoding UTF8
if($compileCode-ne0){throw ($compiled-join"`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @((Join-Path $qa 'synthetic')) -LogPrefix (Join-Path $qa 'persistence') -TimeoutMilliseconds 60000
$run.Output|Write-Output
$drift=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash-cne$_.SHA256})
[ordered]@{Schema='HVX.BrowserEnglishPersistence.Offline/1';CompletedUtc=[DateTime]::UtcNow.ToString('o');Inputs=$inputs;SourceUnchanged=($drift.Count-eq0);Execution=$run;ControlsConstructed=$false;WindowsShown=$false;ProductEntryExecuted=$false;ProfilesRead=$false;SDKCalled=$false;HardwareAccessed=$false;UserProjectWritten=$false}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $qa 'proof.json') -Encoding UTF8
Write-Output ('BROWSER_ENGLISH_PERSISTENCE_PROOF='+$qa+'\proof.json')
if($run.ExitCode-ne0-or$drift.Count-ne0){throw 'Browser English persistence regression failed or inputs drifted.'}
