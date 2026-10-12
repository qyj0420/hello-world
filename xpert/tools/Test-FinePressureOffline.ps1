param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root=$project # Bounded runner's existing workspace reference.
Set-Location -LiteralPath $project
$qa=Join-Path $project ('build\fine-pressure-offline-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
New-Item -ItemType Directory -Path $qa | Out-Null
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot load bounded test process runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa
$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$results=New-Object 'System.Collections.Generic.List[object]'
foreach($name in @('UnifiedFinePressureRampSmoke','DeviceFinePressureSmoke','DeviceParameterProfilesSmoke')){
 $sourceRoot=if($name -eq 'UnifiedFinePressureRampSmoke'){'modules-src\FactoryWorker'}else{'src'}
 $sources=@(Get-ChildItem -LiteralPath (Join-Path $project $sourceRoot) -File -Recurse -Filter '*.cs'|Sort-Object FullName|ForEach-Object FullName)
 $sources+=Join-Path $project ('tests\'+$name+'.cs')
 $before=@($sources|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
 $exe=Join-Path $qa ($name+'.exe');$output=Join-Path $qa ($name+'-data')
 New-Item -ItemType Directory -Path $output | Out-Null
 $args=@('/nologo','/target:exe','/platform:x64','/langversion:latest',('/main:'+$name),('/out:'+$exe),'/define:V30_BATCH_VALIDATION_20261008',
 '/r:System.dll','/r:System.Core.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',
 '/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:Microsoft.CSharp.dll','/win32manifest:app.manifest',
 '/resource:assets\xiongxin.ico,Brand.ico','/resource:assets\xiongxin-original.png,Brand.png')
 $compile=@(& $compiler @args @sources 2>&1|ForEach-Object{$_.ToString()});$exit=$LASTEXITCODE
 $compile|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($exit -ne 0){throw ($name+' compile failed: '+($compile -join "`n"))}
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments @($output) -LogPrefix (Join-Path $qa $name)
 $after=@($sources|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
 $same=($before|ConvertTo-Json -Compress) -ceq ($after|ConvertTo-Json -Compress)
 $results.Add([ordered]@{Name=$name;ExitCode=$run.ExitCode;Output=$run.Output;SourceUnchanged=$same;Sources=$before;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash})
 [ordered]@{Schema='HVX.FinePressure.Offline/1';HardwareAccessed=$false;ProductEntryExecuted=$false;PhysicalAcceptance=$false;Tests=$results.ToArray()}|
 ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
 $run.Output|Write-Output
 if($run.ExitCode -ne 0 -or -not $same){throw ('Test failed or source changed: '+$name+'; '+$qa)}
}
Write-Output ('FINE_PRESSURE_OFFLINE_PROOF='+$qa)
