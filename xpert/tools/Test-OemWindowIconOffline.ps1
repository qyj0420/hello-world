param([string]$ExistingStagedShortcut='')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-oem-window-icon-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$runner=Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded offline runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'};. ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing;New-Item -ItemType Directory -Path $qa|Out-Null
$csc=Join-Path $PSScriptRoot 'compiler\csc.exe';$neutral=Join-Path $root 'assets\shortcuts\256.ico'
$sources=@('src/OemUiBranding.cs','src/Theme.cs','src/R2RoundedButton.cs','tests/OemWindowIconSmoke.cs')
$inputs=@($sources)+@('Build.ps1','tools/Build-UiModules.ps1','src/MainForm.cs','installer/XIONGXIN-PublicBeta-NoDriver.iss','tools/Build-PublicBetaNoDriverSetup.ps1',$PSCommandPath,$runner,$csc,$neutral)
$inputs+=@(Get-ChildItem -LiteralPath 'src' -File -Filter '*.cs'|ForEach-Object FullName)
if($ExistingStagedShortcut){Assert-RegressionUnlinkedPath $ExistingStagedShortcut;$inputs+=$ExistingStagedShortcut}
$before=@($inputs|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=[IO.Path]::GetFullPath($_);SHA256=(Get-FileHash -LiteralPath $_).Hash}})
# Actual shell build, with no module staging, worker, hybrid/native build,
# product invocation, installer or installed-state/config access.
$buildLog=@(& .\Build.ps1 -Configuration Debug -SkipModules -OutputDirectory (Join-Path $qa 'shell') 2>&1|ForEach-Object{$_.ToString()})
$buildLog|Set-Content -LiteralPath (Join-Path $qa 'desktop.compile.txt') -Encoding UTF8
$desktop=Join-Path $qa 'shell\Xiongxin.OWTS.Desktop.exe'
if(-not (Test-Path -LiteralPath $desktop -PathType Leaf)){throw 'Offline shell compilation did not produce an inspectable file.'}
$common=@('/nologo','/langversion:7.3','/platform:x64','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll')
$library=Join-Path $qa 'NeutralWindowOwnedUi.dll'
& $csc @common /target:library ('/out:'+$library) ('/resource:'+$neutral+',HVX.NeutralWindow.ico') 'src/OemUiBranding.cs'
if($LASTEXITCODE -ne 0){throw 'Owned UI icon fixture compilation failed.'}
$runs=@()
foreach($mode in @('entry','own')) {
 $exe=Join-Path $qa ($mode+'.exe');$args=@($common)+@('/target:exe','/main:OemWindowIconSmoke',('/out:'+$exe))
 if($mode -eq 'entry'){$args+=('/resource:'+$neutral+',HVX.NeutralWindow.ico')}
 & $csc @args @sources
 if($LASTEXITCODE -ne 0){throw 'Offline icon test compilation failed.'}
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments @($root,$qa,$desktop,$library,$mode) -LogPrefix (Join-Path $qa $mode) -TimeoutMilliseconds 30000
 $runs+=,[ordered]@{Name=$mode;Result=$run};$run.Output|Write-Output
}
$shortcutProof=$null
if($ExistingStagedShortcut){$matches=(Get-FileHash -LiteralPath $ExistingStagedShortcut).Hash -ceq (Get-FileHash -LiteralPath $neutral).Hash;$shortcutProof=[ordered]@{Path=[IO.Path]::GetFullPath($ExistingStagedShortcut);SHA256=(Get-FileHash -LiteralPath $ExistingStagedShortcut).Hash;ByteIdentical=$matches};if(-not $matches){throw 'Existing staged shortcut differs from original artwork.'}}
$changed=@($before|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count
$passed=@($runs|Where-Object{$_.Result.ExitCode -ne 0}).Count -eq 0
$proof=Join-Path $qa 'verification.json'
[ordered]@{Schema='HVX.OemWindowIcon.Offline/1';UTC=[DateTime]::UtcNow.ToString('o');Status=$(if($passed -and $changed -eq 0){'PASS'}else{'FAIL'});Inputs=$before;InputMismatchCount=$changed;
 DesktopPath=$desktop;DesktopSHA256=(Get-FileHash -LiteralPath $desktop).Hash;NeutralOriginalSHA256=(Get-FileHash -LiteralPath $neutral).Hash;ExistingStagedShortcut=$shortcutProof;Runs=$runs;
 ProductStarted=$false;MainFormCreated=$false;WorkerStarted=$false;SDKCalled=$false;HardwareAccessed=$false;UserProjectsWritten=$false;UserSettingsWritten=$false;InstallerBuilt=$false;
 Scope='Actual Build.ps1 Debug shell compile only, Win32 icon group/image bytes via data-only resource mapping, exact managed resource, fake Form ApplyDialogIcon, OEM priority and entry/owned-assembly fallback; no product execution.'}|
 ConvertTo-Json -Depth 12|Set-Content -LiteralPath $proof -Encoding UTF8
Write-Output ('OEM_WINDOW_ICON_PROOF='+$proof)
if(-not $passed -or $changed -ne 0){throw ('Icon regression failed or source changed: '+$qa)}
