param([Parameter(Mandatory=$true)][string]$SeedBuildRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$SeedBuildRoot=[IO.Path]::GetFullPath($SeedBuildRoot)
$qa=Join-Path $root ('build\HostDialogSourceQA-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
# Compile only a non-runnable diagnostic library. No Build/Stage/publisher,
# embedded runtime payload, hardware startup or user project is involved.
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded regression runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $nodes=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($nodes.Count -ne 1){throw ('Missing helper '+$name)}
 . ([ScriptBlock]::Create($nodes[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
Assert-RegressionUnlinkedPath $SeedBuildRoot
New-Item -ItemType Directory -Path $qa | Out-Null
$source=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File|Sort-Object FullName|ForEach-Object FullName)
$test=Join-Path $root 'tests\HostDialogSafetySmoke.cs'
$assets=@((Join-Path $root 'assets\xiongxin.ico'),(Join-Path $root 'assets\xiongxin-original.png'))
$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=@($source)+@($test)+$assets+@($compiler)
function Snapshot { @($inputs|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}}) }
$before=@(Snapshot)
# Freeze inputs for this diagnostic library. A different task may keep editing
# the live catalog while tests run; this is snapshot evidence, not release proof.
$snapshot=Join-Path $qa 'inputs'
New-Item -ItemType Directory -Path $snapshot | Out-Null
foreach($path in @($source)+@($test)+$assets){
 $relative=$path.Substring($root.Length+1);$target=Join-Path $snapshot $relative
 New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
 Copy-Item -LiteralPath $path -Destination $target
}
if(($before|ConvertTo-Json -Compress -Depth 4) -cne ((Snapshot)|ConvertTo-Json -Compress -Depth 4)){throw 'Source changed during snapshot capture; retry a fresh diagnostic run.'}
$source=@($source|ForEach-Object{Join-Path $snapshot $_.Substring($root.Length+1)})
$test=Join-Path $snapshot 'tests\HostDialogSafetySmoke.cs'
$assets=@($assets|ForEach-Object{Join-Path $snapshot $_.Substring($root.Length+1)})
$hostLibrary=Join-Path $qa 'HostUiUnderTest.dll'
$arguments=@('/nologo','/target:library','/platform:x64','/langversion:7.3','/deterministic+',
 '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',
 '/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll',
 ('/resource:'+$assets[0]+',Brand.ico'),('/resource:'+$assets[1]+',Brand.png'),('/out:'+$hostLibrary))
$compile=@(& $compiler @arguments @source 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE
$compile|Set-Content -LiteralPath (Join-Path $qa 'host.compile.txt') -Encoding UTF8
if($code -ne 0){throw ('Host diagnostic library compile failed: '+($compile -join "`n"))}
$testExe=Join-Path $qa 'HostDialogSafetySmoke.exe'
$compile=@(& $compiler /nologo /target:exe /platform:x64 /langversion:7.3 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:'+$testExe) $test 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE
$compile|Set-Content -LiteralPath (Join-Path $qa 'test.compile.txt') -Encoding UTF8
if($code -ne 0){throw ('Test compilation failed: '+($compile -join "`n"))}
$pressure=Join-Path $SeedBuildRoot 'modules\PressureTest\Xiongxin.OWTS.PressureTest.dll'
$pressureHash=(Get-FileHash -LiteralPath $pressure).Hash
$result=Invoke-RegressionExecutable -Executable $testExe -Arguments @($SeedBuildRoot,(Join-Path $qa 'screens'),$hostLibrary) -LogPrefix (Join-Path $qa 'test')
$unchanged=(($before|ConvertTo-Json -Compress -Depth 4) -ceq ((Snapshot)|ConvertTo-Json -Compress -Depth 4))
[ordered]@{Schema='HVX.HostDialog.SourceQA/1';ReleaseEvidence=$false;SoftwareVersionGenerated=$false;HardwareAccessed=$false;UserProjectsWritten=$false;
 Scope='Current host source compiled as diagnostic DLL; existing pressure module checked separately. No full regression or hardware acceptance.';
 Inputs=$before;SnapshotRoot=$snapshot;LiveInputsUnchanged=$unchanged;HostLibrary=$hostLibrary;HostLibrarySHA256=(Get-FileHash -LiteralPath $hostLibrary).Hash;
 PressureModule=$pressure;PressureSHA256=$pressureHash;PressureUnchanged=($pressureHash -ceq (Get-FileHash -LiteralPath $pressure).Hash);Test=$result}|
 ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
$result.Output|Write-Output
Write-Output ('HOST_SOURCE_QA='+$qa)
if($result.ExitCode -ne 0){throw 'Host dialog source QA failed; retain evidence.'}
