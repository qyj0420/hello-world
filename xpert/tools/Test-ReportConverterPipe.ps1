param([Parameter(Mandatory=$true)][string]$QaRoot)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$QaRoot=[IO.Path]::GetFullPath($QaRoot)
if(-not $QaRoot.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use the isolated report QA folder.'}
$engine=Join-Path $QaRoot 'modules\ReportGenerator\runtime\HVX.Report.Engine.dll'
$out=Join-Path $QaRoot ('pipe-'+[guid]::NewGuid().ToString('N').Substring(0,8))
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded test runner.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $out -AllowMissing
New-Item -ItemType Directory -Path $out|Out-Null
$inputs=@($engine,(Join-Path $root 'tests\ReportConverterFixture.cs'),(Join-Path $root 'tests\ReportConverterPipeSmoke.cs'),$PSCommandPath)|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}}
Copy-Item -LiteralPath $engine -Destination $out
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$fake=Join-Path $out 'soffice.exe';$exe=Join-Path $out 'ReportConverterPipeSmoke.exe'
& $csc /nologo /target:exe /platform:x64 ('/out:'+$fake) (Join-Path $root 'tests\ReportConverterFixture.cs')
if($LASTEXITCODE){throw 'Converter fixture compilation failed.'}
& $csc /nologo /target:exe /platform:x64 /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ('/r:'+$engine) ('/out:'+$exe) (Join-Path $root 'tests\ReportConverterPipeSmoke.cs')
if($LASTEXITCODE){throw 'Pipe regression compilation failed.'}
$result=Invoke-RegressionExecutable -Executable $exe -Arguments @($out,$fake) -LogPrefix (Join-Path $out 'ReportConverterPipeSmoke')
$result.Output|Write-Output
$unchanged=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count -eq 0
[ordered]@{Schema='HVX.Report.ConverterPipe/1';Utc=[DateTime]::UtcNow.ToString('o');SyntheticConverterOnly=$true;HardwareAccessed=$false;Inputs=$inputs;InputUnchanged=$unchanged;Result=$result}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $out 'verification.json') -Encoding UTF8
if($result.ExitCode -ne 0 -or -not $unchanged){throw 'Converter pipe regression failed.'}
Write-Output ('REPORT_PIPE_PROOF='+$out)
