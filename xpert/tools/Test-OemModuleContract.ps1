param([Parameter(Mandatory=$true)][string]$ModuleRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ModuleRoot=[IO.Path]::GetFullPath($ModuleRoot)
$qa=Join-Path ([IO.Path]::GetDirectoryName($ModuleRoot)) ('qa\oem-modules-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded regression runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Get-RegressionTreeFiles','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw ('Missing runner helper '+$name)}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $ModuleRoot
Assert-RegressionUnlinkedPath $qa -AllowMissing
New-Item -ItemType Directory -Path $qa | Out-Null
function Inventory {
 @(Get-RegressionTreeFiles $ModuleRoot|Sort-Object FullName|ForEach-Object{[ordered]@{Path=$_.FullName.Substring($ModuleRoot.Length+1);SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
}
$before=@(Inventory);$startedUtc=[DateTime]::UtcNow.ToString('o');$results=New-Object 'System.Collections.Generic.List[object]'
function Receipt {
 $after=@(Inventory)
 [ordered]@{Schema='HVX.OemModules.Offline/1';ModuleRoot=$ModuleRoot;StartedUtc=$startedUtc;CompletedUtc=[DateTime]::UtcNow.ToString('o');HardwareAccessed=$false;UserProjectsWritten=$false;ProductEntryExecuted=$false;FullRegression=$false;
 RuntimeUnchanged=(($before|ConvertTo-Json -Depth 4 -Compress) -ceq ($after|ConvertTo-Json -Depth 4 -Compress));Runtime=$before;Tests=$results.ToArray()}|
 ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'verification-oem-modules.json') -Encoding UTF8
}
foreach($name in @('OemModuleContractSmoke','OwnedModuleDialogLayoutSmoke')) {
 $source=Join-Path $root ('tests\'+$name+'.cs');$exe=Join-Path $qa ($name+'.exe');$sourceHash=(Get-FileHash -LiteralPath $source).Hash
 $lines=@(& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /target:exe /platform:x64 /langversion:7.3 /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:Accessibility.dll ('/out:'+$exe) $source 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$lines|Set-Content -LiteralPath (Join-Path $qa ($name+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){$results.Add([ordered]@{Name=$name;CompileExit=$code;ExitCode=$null;Output=$lines});Receipt;throw ($name+' failed to compile: '+($lines -join "`n"))}
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments @($ModuleRoot,(Join-Path $qa ('case-'+$results.Count))) -LogPrefix (Join-Path $qa $name)
 $unchanged=(Get-FileHash -LiteralPath $source).Hash -ceq $sourceHash
 $results.Add([ordered]@{Name=$name;CompileExit=0;ExitCode=$run.ExitCode;Output=$run.Output;Milliseconds=$run.ElapsedMilliseconds;TimedOut=$run.TimedOut;ProcessExitConfirmed=$run.ProcessExitConfirmed;SourceSHA256=$sourceHash;SourceUnchanged=$unchanged;TestSHA256=(Get-FileHash -LiteralPath $exe).Hash})
 Receipt;$run.Output|Write-Output
 if($run.ExitCode -ne 0 -or -not $unchanged){throw ($name+' failed; evidence '+$qa)}
}
Write-Output ('OEM_MODULE_PROOF='+$qa)
