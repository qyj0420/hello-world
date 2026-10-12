param([Parameter(Mandatory=$true)][string]$OutputRoot,[ValidateSet('CalibrationResidentNavigationSmoke','CalibrationNavigationSmoke','CalibrationHostIdentitySmoke')][string]$TestClass='CalibrationResidentNavigationSmoke')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root=$repo
Set-Location -LiteralPath $repo
$output=[IO.Path]::GetFullPath($OutputRoot)
if(!$output.StartsWith($repo+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Use a new project-local proof directory.'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded regression helper.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $nodes=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($nodes.Count -ne 1){throw 'Bounded regression helper missing.'}
 . ([ScriptBlock]::Create($nodes[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $output -AllowMissing
New-Item -ItemType Directory -Path $output|Out-Null
$sources=@(Get-ChildItem -LiteralPath (Join-Path $repo 'src') -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$sources+=Join-Path $repo ('tests\'+$TestClass+'.cs')
if($TestClass -eq 'CalibrationHostIdentitySmoke'){$sources+=Join-Path $repo 'modules-src/HardwareConnector/ModuleIdentity.cs'}
$inputs=@(@($sources)+@($PSCommandPath)|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$exe=Join-Path $output 'CalibrationNavigationFakeOnly.exe'
$arguments=@('/nologo','/target:exe','/platform:x64','/langversion:7.3',('/main:'+$TestClass),
 '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll',
 '/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll',('/out:'+$exe),
 '/resource:assets/xiongxin.ico,Brand.ico','/resource:assets/xiongxin-original.png,Brand.png')
$compiled=@(& (Join-Path $PSScriptRoot 'compiler/csc.exe') @arguments @sources 2>&1|ForEach-Object{$_.ToString()})
$compileCode=$LASTEXITCODE
$compiled|Set-Content -LiteralPath (Join-Path $output 'compile.txt') -Encoding UTF8
if($compileCode -ne 0){throw ($compiled-join "`n")}
$run=Invoke-RegressionExecutable -Executable $exe -Arguments @() -LogPrefix (Join-Path $output 'navigation')
$unchanged=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count -eq 0
[ordered]@{Schema='HVX.Calibration.ResidentNavigation.Offline/1';UTC=[DateTime]::UtcNow.ToString('o');HardwareAccessed=$false;ProductEntryStarted=$false;
 SourceUnchanged=$unchanged;Inputs=$inputs;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash;Result=$run}|ConvertTo-Json -Depth 9|Set-Content -LiteralPath (Join-Path $output 'proof.json') -Encoding UTF8
$run.Output|Write-Output
Write-Output ('NAVIGATION_PROOF='+(Join-Path $output 'proof.json'))
if($run.ExitCode -ne 0 -or !$unchanged){throw 'Calibration navigation fake regression failed.'}
