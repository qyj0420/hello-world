param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$qa=[IO.Path]::GetFullPath($OutputRoot)
if($PSVersionTable.PSEdition-ne'Desktop'){throw 'Use Windows PowerShell 5.1.'}
if(-not$qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $qa)){throw 'Fresh project-owned QA directory required.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded helper.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n-is[Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count-ne1){throw 'Required bounded helper missing.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$sources=@('src\PdLinearLowPass.cs','modules-src\CalibrationStandalone\TdrCorrelation.cs','tests\CalibrationDisplayPolicyAudit.cs'|ForEach-Object{Join-Path $root $_})
$inputs=@(($sources+@($csc,$helper,$PSCommandPath))|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
New-Item -ItemType Directory -Path $qa|Out-Null;$exe=Join-Path $qa 'CalibrationDisplayPolicyPureFixture.exe'
$compiled=@(& $csc /nologo /target:exe /platform:x64 /langversion:7.3 /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll ('/out:'+$exe) @sources 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa 'compile.log') -Encoding UTF8
if($code-ne0){throw ($compiled-join"`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @() -LogPrefix (Join-Path $qa 'display-policy') -TimeoutMilliseconds 30000
$changed=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash-cne$_.SHA256})
[ordered]@{Schema='HVX.Calibration.DisplayPolicy.Offline/1';Inputs=$inputs;SourcesUnchanged=($changed.Count-eq0);Execution=$run;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash;ProductStarted=$false;UIConstructed=$false;WorkerStarted=$false;SDKCalled=$false;HardwareUsed=$false;PhysicalAcceptance=$false}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'proof.json') -Encoding UTF8
$run.Output|Write-Output;Write-Output ('CAL_DISPLAY_POLICY_PROOF='+$qa+'\proof.json')
if($run.ExitCode-ne0-or$changed.Count-ne0){throw 'Display policy pure fixture failed or inputs changed; retained evidence.'}
