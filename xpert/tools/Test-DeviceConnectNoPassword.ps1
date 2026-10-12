param([Parameter(Mandatory=$true)][string]$BuildRoot,[string[]]$Names=@('SettingsNoRecheckSmoke','SettingsDeviceAutoConnectSmoke','SettingsAccessSmoke','PlusDataSettingsSmoke','SettingsFeedbackSmoke','SettingsIntegritySmoke','NoiseToleranceSettingsSmoke','EndpointManufacturerPolicySmoke','Internal04Smoke','DetectionDeviceSelectionSmoke','HomeDeviceConnectionSmoke','HomeDeviceActionsSmoke'))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$BuildRoot=[IO.Path]::GetFullPath($BuildRoot)
if(-not $BuildRoot.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an isolated build under this project.'}
$qa=Join-Path $BuildRoot ('qa\password-free-source-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot load bounded test runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Get-RegressionTreeFiles','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
New-Item -ItemType Directory -Path $qa | Out-Null
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -File -Filter '*.cs' | Sort-Object FullName | ForEach-Object FullName)
function SourceInventory {
 @($sources|ForEach-Object{[ordered]@{Path=$_.Substring($root.Length+1).Replace('\','/');SHA256=(Get-FileHash -LiteralPath $_).Hash}})
}
function RuntimeInventory {
 $files=@(Get-Item -LiteralPath (Join-Path $BuildRoot 'Xiongxin.OWTS.Desktop.exe'),(Join-Path $BuildRoot 'Xiongxin.OWTS.Desktop.exe.config'))
 $files+=@(Get-RegressionTreeFiles (Join-Path $BuildRoot 'modules'))
 @($files|Sort-Object FullName|ForEach-Object{[ordered]@{Path=$_.FullName.Substring($BuildRoot.Length+1).Replace('\','/');SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
}
$sourceBefore=@(SourceInventory);$runtimeBefore=@(RuntimeInventory)
$profileArgs=@()
$buildManifest=Get-Content -LiteralPath (Join-Path $BuildRoot 'build-manifest.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($buildManifest.PSObject.Properties['R27ValidationProfile'] -and $buildManifest.R27ValidationProfile){$profileArgs=@('/define:R27_VALIDATION_20261007')}
if($buildManifest.PSObject.Properties['V30ValidationProfile'] -and $buildManifest.V30ValidationProfile){$profileArgs=@('/define:V30_VALIDATION_20261007')}
if($buildManifest.PSObject.Properties['V30BatchValidationProfile'] -and $buildManifest.V30BatchValidationProfile){$profileArgs=@('/define:V30_BATCH_VALIDATION_20261008')}
if($buildManifest.PSObject.Properties['PressureStandalone'] -and $buildManifest.PressureStandalone){$profileArgs+=('/define:PRESSURE_STANDALONE_20261008')}
# When present, use the actual host compilation profile, including the isolated
# public-beta version and maintenance guards. A later version must not be tested
# through a historical profile merely because both are V30 batch builds.
if($buildManifest.PSObject.Properties['HostCompilation'] -and $buildManifest.HostCompilation.PSObject.Properties['CompilerDefines']){
 $defines=@($buildManifest.HostCompilation.CompilerDefines)
 if($defines.Count -eq 0 -or @($defines|Where-Object {$_ -isnot [string] -or $_ -cnotmatch '^[A-Z][A-Z0-9_]*$'}).Count){throw 'Invalid actual host compilation defines.'}
 $profileArgs=@($defines|ForEach-Object {'/define:'+$_})
}
$startedUtc=[DateTime]::UtcNow.ToString('o')
$results=New-Object 'System.Collections.Generic.List[object]'
foreach($name in $Names){
 if($name -notmatch '^[A-Za-z0-9]+$'){throw 'Invalid smoke test name.'}
 $exe=Join-Path $qa ($name+'.exe');$output=Join-Path $qa ($name+'-output');New-Item -ItemType Directory -Path $output | Out-Null
 $testSource=Join-Path $root ('tests\'+$name+'.cs');$testSourceHash=(Get-FileHash -LiteralPath $testSource).Hash
 $compile=@(& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /target:exe /platform:x64 /langversion:7.3 /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Xml.Linq.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Security.dll /r:Microsoft.CSharp.dll /win32manifest:app.manifest /resource:assets\xiongxin.ico,Brand.ico /resource:assets\xiongxin-original.png,Brand.png @profileArgs ('/main:'+$name) ('/out:'+$exe) $testSource @sources 2>&1 | ForEach-Object {$_.ToString()})
 $code=$LASTEXITCODE;$compile|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ($name+' compile failed: '+($compile -join "`n"))}
 $arguments=if($name -in @('PlusDataSettingsSmoke','EndpointManufacturerPolicySmoke','Internal04Smoke','Task39HostContractSmoke','Task12ModelRoutingSmoke','ModuleNavigationPerformanceSmoke','ModuleAuditSmoke','CalibrationNavigationSmoke','PressureZeroReferenceBridgeSmoke','PressureFailureCleanupHostSmoke')){@($BuildRoot,$output)}else{@($output)}
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments $arguments -LogPrefix (Join-Path $qa $name)
 $results.Add([ordered]@{Name=$name;CompileExit=0;ExitCode=$run.ExitCode;Output=$run.Output;TimedOut=$run.TimedOut;ProcessExitConfirmed=$run.ProcessExitConfirmed;Milliseconds=$run.ElapsedMilliseconds;SourceSHA256=$testSourceHash;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash})
 $sourcesUnchanged=(($sourceBefore|ConvertTo-Json -Compress) -ceq (@(SourceInventory)|ConvertTo-Json -Compress))
 $runtimeUnchanged=(($runtimeBefore|ConvertTo-Json -Compress) -ceq (@(RuntimeInventory)|ConvertTo-Json -Compress))
 [ordered]@{Schema='HVX.PasswordFree.Source/2';BuildRoot=$BuildRoot;StartedUtc=$startedUtc;CompletedUtc=[DateTime]::UtcNow.ToString('o');HardwareAccessed=$false;UserSettingsWritten=$false;CompilerDefines=@($profileArgs|ForEach-Object {$_.Substring(8)});SourceInventory=$sourceBefore;SourceUnchanged=$sourcesUnchanged;Runtime=$runtimeBefore;RuntimeUnchanged=$runtimeUnchanged;Tests=$results.ToArray()}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
 $run.Output|Write-Output
 if($run.ExitCode -ne 0){throw ($name+' failed; '+$qa)}
 if(-not $sourcesUnchanged -or -not $runtimeUnchanged -or (Get-FileHash -LiteralPath $testSource).Hash -cne $testSourceHash){throw 'Tested inputs changed during the source regression.'}
}
Write-Output ('PASSWORD_FREE_SOURCE_PROOF='+$qa)
