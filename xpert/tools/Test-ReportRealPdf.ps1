param([Parameter(Mandatory=$true)][string]$QaRoot,[string]$SofficePath='C:\Program Files\LibreOffice\program\soffice.exe')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$qa=[IO.Path]::GetFullPath($QaRoot)
if(-not $qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'PDF verification must stay inside report QA.'}
if(-not (Test-Path -LiteralPath $SofficePath -PathType Leaf)){throw 'An already installed LibreOffice is required; this test will not install software.'}
$source=Join-Path $root 'tests\ReportRealPdfSmoke.cs';$engine=Join-Path $qa 'modules\ReportGenerator\runtime\HVX.Report.Engine.dll'
$sourceHash=(Get-FileHash -LiteralPath $source).Hash;$engineHash=(Get-FileHash -LiteralPath $engine).Hash
$exe=Join-Path $qa 'ReportRealPdfSmoke.exe'
& "$root/tools/compiler/csc.exe" /nologo /langversion:7.3 /target:exe /platform:x64 /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll ('/out:'+$exe) $source
if($LASTEXITCODE){throw 'PDF test compilation failed.'}
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded test runner.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($qa,$SofficePath) -LogPrefix (Join-Path $qa 'ReportRealPdfSmoke')
$run.Output|Write-Output
[ordered]@{Schema='HVX.Report.RealPdf.1';CompletedUtc=[DateTime]::UtcNow.ToString('o');SourceSHA256=$sourceHash;EngineSHA256=$engineHash;SourceUnchanged=((Get-FileHash -LiteralPath $source).Hash -ceq $sourceHash);RuntimeUnchanged=((Get-FileHash -LiteralPath $engine).Hash -ceq $engineHash);Converter=$SofficePath;ConverterVersion=(Get-Item -LiteralPath $SofficePath).VersionInfo.FileVersion;ConverterSHA256=(Get-FileHash -LiteralPath $SofficePath).Hash;SyntheticOnly=$true;HardwareAccessed=$false;UserOfficeProfile=$false;Result=$run}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $qa 'verification-real-pdf.json') -Encoding UTF8
if($run.ExitCode -ne 0){throw 'PDF verification failed; evidence retained.'}
