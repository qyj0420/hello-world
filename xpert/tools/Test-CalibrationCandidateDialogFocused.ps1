param([Parameter(Mandatory=$true)][string]$FixtureBuildRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$fixture=[IO.Path]::GetFullPath($FixtureBuildRoot)
if(!$fixture.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an isolated format-routing fixture build.'}
$qa=Join-Path $root ('build\qa-calibration-candidate-dialog-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
New-Item -ItemType Directory -Path $qa|Out-Null
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')) {
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
$exe=Join-Path $qa 'CalibrationCandidateDialogFakeOnly.exe'
$ref=Join-Path $fixture 'CalibrationSessionCleanupFakeOnly.exe'
Copy-Item -LiteralPath $ref -Destination $qa
$sources=@('src/LegacyCalibrationImportDialog.cs','src/R2RoundedButton.cs','tests/CalibrationCandidateDialogFocusedSmoke.cs')
$inputs=@($sources+$ref|ForEach-Object{[ordered]@{Path=[IO.Path]::GetFullPath($_);SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$compile=@(& (Join-Path $PSScriptRoot 'compiler/csc.exe') /nologo /target:exe /platform:x64 /langversion:7.3 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/r:'+$ref) ('/out:'+$exe) @sources 2>&1|ForEach-Object{$_.ToString()})
if($LASTEXITCODE -ne 0){throw ($compile -join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @((Join-Path $fixture 'synthetic')) -LogPrefix (Join-Path $qa 'dialog')
[ordered]@{Schema='HVX.Calibration.CandidateDialog.Offline/1';UTC=[DateTime]::UtcNow.ToString('o');ProductStarted=$false;HardwareAccessed=$false;UserProjectsWritten=$false;Inputs=$inputs;Result=$run}|
ConvertTo-Json -Depth 9|Set-Content -LiteralPath (Join-Path $qa 'proof.json') -Encoding UTF8
$run.Output|Write-Output
Write-Output ('CALIBRATION_DIALOG_PROOF='+(Join-Path $qa 'proof.json'))
if($run.ExitCode -ne 0){throw 'Dialog fixture failed.'}
