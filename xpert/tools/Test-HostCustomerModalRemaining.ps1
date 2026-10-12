param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$qa=[IO.Path]::GetFullPath($OutputRoot)
if(-not $qa.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $qa)){throw 'Use a new project-local proof directory.'}
$helper=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
$hostSources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -File -Filter '*.cs'|Sort-Object Name|ForEach-Object FullName)
$test=Join-Path $root 'tests\HostCustomerModalRemainingOfflineSmoke.cs'
$diagnostics=Join-Path $root 'src\Diagnostics.cs';$csc=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputs=@($hostSources)+@($test,$PSCommandPath,$helper)+@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'compiler') -File|Where-Object Extension -in @('.exe','.dll','.config','.rsp')|ForEach-Object FullName)
$inputs=@($inputs|Sort-Object -Unique|ForEach-Object{Assert-RegressionUnlinkedPath $_;[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$started=[DateTime]::UtcNow.ToString('o');New-Item -ItemType Directory -Path $qa|Out-Null
$common=@('/nologo','/langversion:latest','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Accessibility.dll')
# Compile the complete current host as a library. Never load this library or
# execute its Program/Main, forms, profiles, discovery, worker or SDK paths.
$hostLibrary=Join-Path $qa 'HostCustomerModalUnderTest.dll'
$hostLines=@(& $csc @common /target:library ('/out:'+$hostLibrary) @hostSources 2>&1|ForEach-Object{$_.ToString()})
$hostExit=$LASTEXITCODE;$hostLines|Set-Content -LiteralPath (Join-Path $qa 'host.compile.txt') -Encoding UTF8
$exe=Join-Path $qa 'HostCustomerModalRemainingOfflineSmoke.exe';$testExit=$null;$run=$null
if($hostExit -eq 0){
 # The fixture supplies an isolated AppPaths; only production Diagnostics runs.
 $testLines=@(& $csc @common /target:exe /main:HostCustomerModalRemainingOfflineSmoke ('/out:'+$exe) $test $diagnostics 2>&1|ForEach-Object{$_.ToString()})
 $testExit=$LASTEXITCODE;$testLines|Set-Content -LiteralPath (Join-Path $qa 'test.compile.txt') -Encoding UTF8
 if($testExit -eq 0){$run=Invoke-RegressionExecutable -Executable $exe -Arguments @($root,(Join-Path $qa 'synthetic-diagnostics')) -LogPrefix (Join-Path $qa 'test') -TimeoutMilliseconds 30000}
}
$mismatches=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256})
$passed=$hostExit -eq 0 -and $testExit -eq 0 -and $null -ne $run -and $run.ExitCode -eq 0 -and $mismatches.Count -eq 0
$checks=0;if($run -and ($run.Output -join "`n") -match 'HOST_CUSTOMER_MODAL_REMAINING_PASS=(\d+);'){$checks=[int]$Matches[1]}else{$passed=$false}
$proof=Join-Path $qa 'proof.json'
[ordered]@{Schema='HVX.HostCustomerModalRemaining.CurrentSource/1';StartedUtc=$started;CompletedUtc=[DateTime]::UtcNow.ToString('o');Status=$(if($passed){'PASS'}else{'FAIL'});Inputs=$inputs;InputMismatchCount=$mismatches.Count;SourceUnchanged=($mismatches.Count -eq 0);HostCompileExit=$hostExit;HostLibrary=$hostLibrary;HostLibrarySHA256=$(if(Test-Path -LiteralPath $hostLibrary){(Get-FileHash -LiteralPath $hostLibrary).Hash}else{$null});TestCompileExit=$testExit;Execution=$run;CheckCount=$checks;TestSHA256=$(if(Test-Path -LiteralPath $exe){(Get-FileHash -LiteralPath $exe).Hash}else{$null});ProductEntryExecuted=$false;HostLibraryLoaded=$false;WindowsShown=$false;ProfilesRead=$false;WorkerEntryExecuted=$false;SDKCalled=$false;HardwareAccessed=$false;UserProjectsWritten=$false;FinalDeliveryBytes=$false;PhysicalAcceptance=$false;Scope='Current complete host compile-only; three ordinary customer presentation source checks and actual Diagnostics with a test-only path provider. No modal rendering or full workflow acceptance.'}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $proof -Encoding UTF8
if($run){$run.Output|Write-Output};Write-Output ('HOST_CUSTOMER_MODAL_PROOF='+$proof)
if(-not $passed){throw 'Host customer modal offline regression failed; evidence retained.'}
