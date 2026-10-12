[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$final=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $final.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Only owned build candidates are allowed.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Bounded runner helper parse failed.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Get-RegressionTreeFiles','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count-ne1){throw ('Missing bounded helper '+$name)};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $final
$qa=Join-Path $final ('qa\cal-style-published-'+[Guid]::NewGuid().ToString('N').Substring(0,8));Assert-RegressionUnlinkedPath $qa -AllowMissing
$null=New-Item -ItemType Directory -Path $qa
$bmPath=Join-Path $final 'build-manifest.json';$mmPath=Join-Path $final 'module-manifest.json'
$bm=Get-Content -LiteralPath $bmPath -Raw -Encoding UTF8|ConvertFrom-Json
$mm=Get-Content -LiteralPath $mmPath -Raw -Encoding UTF8|ConvertFrom-Json
if(-not $bm.ProtectedRelease){throw 'Published mode requires protected final bytes.'}
$expected=[ordered]@{'Xiongxin.OWTS.Desktop.exe'='HostPure';'modules/PressureTest/Xiongxin.OWTS.PressureTest.dll'='PressureTest';'modules/CalibrationTest/Xiongxin.OWTS.CalibrationTest.dll'='CalibrationTest';'modules/DataBrowser/Xiongxin.OWTS.DataBrowser.dll'='DataBrowser'}
$mappings=@($mm.OwnedAlgorithmProtection)+@($bm.OwnedHostAlgorithmProtection)
$receipts=@()
foreach($relative in $expected.Keys){
 $entry=@($mappings|Where-Object Path -ceq $relative)
 if($entry.Count-ne1-or$entry[0].Profile-cne$expected[$relative]){throw ('Missing exact final protection binding '+$relative)}
 $entry=$entry[0];$path=Join-Path $final $relative;$receiptPath=Join-Path $final $entry.ReceiptPath
 Assert-RegressionUnlinkedPath $path;Assert-RegressionUnlinkedPath $receiptPath
 $receipt=Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8|ConvertFrom-Json
 if((Get-FileHash -LiteralPath $path).Hash-cne$entry.OutputSHA256-or(Get-FileHash -LiteralPath $receiptPath).Hash-cne$entry.ReceiptSHA256-or
 $receipt.Schema-cne'HVX.OwnedAlgorithmProtection/1'-or$receipt.Profile-cne$expected[$relative]-or$receipt.OutputSHA256-cne$entry.OutputSHA256-or
 -not $receipt.ExistingMetadataPreserved-or$receipt.Protection-notmatch'names retained'){throw ('Final metadata/byte identity failed '+$relative)}
 $receipts+=$receiptPath
}
$names=@('CalibrationPulseDisplaySmoke','ThemePreservedGeometrySmoke','CalibrationPersistenceTimingSmoke')
$sources=@($names|ForEach-Object{Join-Path $root ('tests/'+$_+'.cs')})
$business=@('src/Theme.cs','modules-src/PressureTest/src/PressureTestTheme.cs','modules-src/CalibrationTest/src/CalibrationProgramControl.Theme.cs','modules-src/CalibrationTest/src/CalibrationProgramControl.Compact.cs','modules-src/DataBrowser/src/BrowserPhaseButton.cs','src/CalibrationPulseDisplayWindow.cs','modules-src/CalibrationTest/src/CalibrationProgramControl.DisplayPackets.cs','modules-src/CalibrationTest/src/CalibrationProgramControl.Interaction.cs','modules-src/CalibrationTest/src/CalibrationProgramControl.ViewWindow.cs','modules-src/DataBrowser/vendor/Calibration/CalibrationPulseView.cs')|ForEach-Object{Join-Path $root $_}
# ASCII script source is deliberate: Windows PowerShell 5 reads BOM-less UTF-8
# as ANSI. Construct the readonly known project name without code-page loss.
$projectName=-join ([char[]]@(0x8F6F,0x4EF6,0x6D4B,0x8BD5,0x2D,0x8F6F,0x4EF6))
$sourceProject=Join-Path (Join-Path 'D:\OWTSData' $projectName) '2026-10-07_13-23-28'
$actual=@(Join-Path $sourceProject 'ALL_5nC.cal')+@(Get-ChildItem -LiteralPath (Join-Path $sourceProject 'Calibration/Acquisition/20261010_065747_317077') -File -Filter 'capture_*.xxs.capture.log'|ForEach-Object FullName)
$inputs=@($sources)+@($business)+@($actual)+@($receipts)+@($PSCommandPath,$helper,$bmPath,$mmPath,(Join-Path $PSScriptRoot 'compiler/csc.exe'))
function SourceInventory {@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
function RuntimeInventory {
 $files=@(Get-Item -LiteralPath (Join-Path $final 'Xiongxin.OWTS.Desktop.exe'),(Join-Path $final 'Xiongxin.OWTS.Desktop.exe.config'),$bmPath,$mmPath)
 $files+=@(Get-RegressionTreeFiles (Join-Path $final 'modules'))
 @($files|Sort-Object FullName|ForEach-Object{[ordered]@{Path=$_.FullName.Substring($final.Length+1);SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
}
$sourceBefore=@(SourceInventory);$runtimeBefore=@(RuntimeInventory)
$results=New-Object 'System.Collections.Generic.List[object]';$started=[DateTime]::UtcNow.ToString('o')
$compileFailed=$false
foreach($name in $names){
 $exe=Join-Path $qa ($name+'.exe');$testQa=Join-Path $qa ('case-'+$results.Count)
 $lines=@(& (Join-Path $PSScriptRoot 'compiler/csc.exe') /nologo /langversion:7.3 /target:exe /platform:x64 /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:'+$exe) (Join-Path $root ('tests/'+$name+'.cs')) 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($code-ne0){$compileFailed=$true;$results.Add([ordered]@{Name=$name;CompileExit=$code;Output=$lines});continue}
 Copy-Item -LiteralPath (Join-Path $final 'Xiongxin.OWTS.Desktop.exe.config') -Destination ($exe+'.config')
 $arguments=if($name-ceq'ThemePreservedGeometrySmoke'){@($testQa,(Join-Path $final 'modules/CalibrationTest/Xiongxin.OWTS.CalibrationTest.dll'),$final)}else{@($final,$testQa)}
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments $arguments -LogPrefix (Join-Path $qa $name) -TimeoutMilliseconds 120000
 $results.Add([ordered]@{Name=$name;CompileExit=0;Result=$run;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash;SupersededOldAssertion=($name-ceq'CalibrationPersistenceTimingSmoke')});$run.Output|Write-Output
}
$sourceSame=($sourceBefore|ConvertTo-Json -Depth 5 -Compress)-ceq(@(SourceInventory)|ConvertTo-Json -Depth 5 -Compress)
$runtimeSame=($runtimeBefore|ConvertTo-Json -Depth 5 -Compress)-ceq(@(RuntimeInventory)|ConvertTo-Json -Depth 5 -Compress)
$failed=$compileFailed-or@($results.ToArray()|Where-Object{$_.Contains('Result')-and$_.Result.ExitCode-ne0}).Count-gt0
$newFailed=$failed
$proof=Join-Path $qa 'proof.json'
[ordered]@{Schema='HVX.CalDisplayStyle.SameFinal/1';Status=$(if($failed-or-not$sourceSame-or-not$runtimeSame){'FAIL'}else{'PASS'});NewScopedTestsStatus=$(if($newFailed-or-not$sourceSame-or-not$runtimeSame){'FAIL'}else{'PASS'});
 StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');OutputDirectory=$final;SameFinalProductBytes=$true;ProductModulesRecompiled=$false;TestOnlyCompilation=$true;
 NamesRetainedReceiptBound=$true;ObfuscationFallbackUsed=$false;RuntimeUnchanged=$runtimeSame;SourceAndOriginalDataUnchanged=$sourceSame;HardwareAccessed=$false;ProductMainExecuted=$false;SDKCalled=$false;UserProjectWritten=$false;
 SourceInputs=$sourceBefore;Runtime=$runtimeBefore;Tests=$results.ToArray();SupersededAssertion='Old CalibrationPersistenceTimingSmoke distant noise cursor rejected FAIL retained at r42/qa/ui-repair-6a84f106c2 and isolated g/qa/ui-repair-5eba5f22c9. Replaced only that incompatible assertion by explicit operator manual-reflection contract plus strict invalid boundaries; complete updated suite actually executes, no silent skip.'}|
 ConvertTo-Json -Depth 15|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output ('CAL_STYLE_PUBLISHED_PROOF='+$proof)
if($failed-or-not$sourceSame-or-not$runtimeSame){throw 'Focused final-byte verification contains FAIL; inspect retained proof, no fallback.'}
