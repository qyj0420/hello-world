param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$qa=Join-Path $root ('build\BetaHostPresentationSourceQA-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
# Compile only non-runnable diagnostic libraries. No product entry point,
# module loading, SDK calls, hardware startup, installer or publisher is run.
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded regression runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $nodes=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($nodes.Count -ne 1){throw ('Missing helper '+$name)}
 . ([ScriptBlock]::Create($nodes[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
New-Item -ItemType Directory -Path $qa | Out-Null
$source=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File|Sort-Object FullName|ForEach-Object FullName)
$test=Join-Path $root 'tests\BetaHostPresentationSmoke.cs'
$assets=@((Join-Path $root 'assets\xiongxin.ico'),(Join-Path $root 'assets\xiongxin-original.png'))
$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=@($source)+@($test)+$assets+@($compiler)
function Snapshot { @($inputs|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}}) }
$before=@(Snapshot)
$snapshot=Join-Path $qa 'inputs'
New-Item -ItemType Directory -Path $snapshot | Out-Null
foreach($path in @($source)+@($test)+$assets){
 $relative=$path.Substring($root.Length+1);$target=Join-Path $snapshot $relative
 New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
 Copy-Item -LiteralPath $path -Destination $target
}
if(($before|ConvertTo-Json -Compress -Depth 4) -cne ((Snapshot)|ConvertTo-Json -Compress -Depth 4)){throw 'Source changed during snapshot capture; retry a fresh diagnostic run.'}
$source=@($source|ForEach-Object{Join-Path $snapshot $_.Substring($root.Length+1)})
$test=Join-Path $snapshot 'tests\BetaHostPresentationSmoke.cs'
$assets=@($assets|ForEach-Object{Join-Path $snapshot $_.Substring($root.Length+1)})
$testExe=Join-Path $qa 'BetaHostPresentationSmoke.exe'
$compile=@(& $compiler /nologo /target:exe /platform:x64 /langversion:7.3 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll ('/out:'+$testExe) $test 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE
$compile|Set-Content -LiteralPath (Join-Path $qa 'test.compile.txt') -Encoding UTF8
if($code -ne 0){throw ('Test compilation failed: '+($compile -join "`n"))}
$results=@()
foreach($mode in @('Normal','Beta')){
 $hostLibrary=Join-Path $qa ('HostUi'+$mode+'.dll')
 $arguments=@('/nologo','/target:library','/platform:x64','/langversion:7.3','/deterministic+',
  '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',
  '/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll',
  ('/resource:'+$assets[0]+',Brand.ico'),('/resource:'+$assets[1]+',Brand.png'),('/out:'+$hostLibrary))
 if($mode -eq 'Beta'){$arguments+=@('/define:DIRECT_DEBUG_20261010')}
 $compile=@(& $compiler @arguments @source 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE
 $compile|Set-Content -LiteralPath (Join-Path $qa ($mode+'.host.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ('Host diagnostic '+$mode+' library compile failed: '+($compile -join "`n"))}
 $result=Invoke-RegressionExecutable -Executable $testExe -Arguments @($hostLibrary,($mode -eq 'Beta').ToString(),(Join-Path $qa ($mode+'-fixtures'))) -LogPrefix (Join-Path $qa $mode)
 $results+=@([ordered]@{Mode=$mode;HostLibrary=$hostLibrary;SHA256=(Get-FileHash -LiteralPath $hostLibrary).Hash;Test=$result})
 $result.Output|Write-Output
 if($result.ExitCode -ne 0){throw ('Host presentation '+$mode+' QA failed; retain evidence: '+$qa)}
}
$unchanged=(($before|ConvertTo-Json -Compress -Depth 4) -ceq ((Snapshot)|ConvertTo-Json -Compress -Depth 4))
[ordered]@{Schema='HVX.BetaHostPresentation.SourceQA/1';ReleaseEvidence=$false;SoftwareVersionGenerated=$false;HardwareAccessed=$false;UserProjectsWritten=$false;
 Scope='Current source compiled as diagnostic libraries in normal and beta modes. Settings and detached controls only; no full regression or physical acceptance.';
 Inputs=$before;SnapshotRoot=$snapshot;LiveInputsUnchanged=$unchanged;Results=$results}|
 ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
Write-Output ('BETA_HOST_PRESENTATION_SOURCE_QA='+$qa)
