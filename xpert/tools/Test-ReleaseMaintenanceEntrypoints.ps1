[CmdletBinding()]
param([string]$OutputDirectory='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path $root ('qa\release-maintenance-entrypoints-'+[Guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if(-not $output.StartsWith($root+'\qa\',[StringComparison]::OrdinalIgnoreCase)){throw 'Pure checks must remain below project qa.'}
$results=New-Object 'System.Collections.Generic.List[object]'
$inputs=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
function Check([bool]$Value,[string]$Name){$results.Add([ordered]@{Name=$Name;Passed=$Value});if(-not $Value){throw ('Pure release gate check failed: '+$Name)}}
function Read-Source([string]$Relative){$null=$inputs.Add($Relative);return [IO.File]::ReadAllText((Join-Path $root $Relative))}
function Guard-Preamble([string]$Relative,[string]$StopVariable){
    $text=Read-Source $Relative;$tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
    Check ($errors.Count -eq 0) ($Relative+' parses')
    $stop=@($ast.EndBlock.Statements|Where-Object {$_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left -is [Management.Automation.Language.VariableExpressionAst] -and $_.Left.VariablePath.UserPath -eq $StopVariable})
    Check ($stop.Count -eq 1) ($Relative+' has one safe I/O boundary')
    $prefix=$text.Substring(0,$stop[0].Extent.StartOffset)
    Check ($prefix -notmatch '\b(?:New-Item|Copy-Item|Start-Process|Get-FileHash|Test-Path)\b|\&\s*\(') ($Relative+' guard prefix contains no builder/file/device operations')
    return [ScriptBlock]::Create($prefix+"`r`n'GUARD_ACCEPTED'")
}
function Guard-Case([ScriptBlock]$Guard,[hashtable]$Arguments,[bool]$Accepted,[string]$Name){
    $actual=$false
    try{$value=@(& $Guard @Arguments);$actual=$value.Count -eq 1 -and $value[0] -ceq 'GUARD_ACCEPTED'}catch{}
    Check ($actual -eq $Accepted) $Name
}
$build=Guard-Preamble 'Build.ps1' 'root'
Guard-Case $build @{} $false 'Default Release without explicit maintenance profile rejects before I/O'
Guard-Case $build @{Hybrid=$true} $false 'Hybrid Release cannot omit protected maintenance'
Guard-Case $build @{Configuration='Debug';SkipModules=$true} $true 'Unpackaged Debug preview remains available'
Guard-Case $build @{Configuration='Release';V30BatchValidation=$true;Hybrid=$true;ProtectedRelease=$true;MaintenancePassword=$true} $true 'Complete Release profile reaches I/O boundary'
foreach($missing in @('V30BatchValidation','Hybrid','ProtectedRelease','MaintenancePassword')){
    $parameters=@{Configuration='Release';V30BatchValidation=$true;Hybrid=$true;ProtectedRelease=$true;MaintenancePassword=$true};$parameters[$missing]=$false
    Guard-Case $build $parameters $false ('Release rejects missing '+$missing)
}
foreach($invalid in @('SkipModules','PressureStandalone','FrozenInternal','R27Validation','V30Validation')){
    $parameters=@{Configuration='Release';V30BatchValidation=$true;Hybrid=$true;ProtectedRelease=$true;MaintenancePassword=$true};$parameters[$invalid]=$true
    Guard-Case $build $parameters $false ('Release rejects '+$invalid)
}
foreach($entry in @(@('Stage-Modules.ps1','output'),@('tools/Build-UiModules.ps1','projectRoot'))){
    $guard=Guard-Preamble $entry[0] $entry[1]
    Guard-Case $guard @{OutputRoot='never-accessed'} $true ($entry[0]+' default development preview remains unpackaged')
    Guard-Case $guard @{OutputRoot='never-accessed';Configuration='Release'} $false ($entry[0]+' Release without guard rejected')
    Guard-Case $guard @{OutputRoot='never-accessed';Configuration='Release';ProtectedRelease=$true} $false ($entry[0]+' protection alone rejected')
    Guard-Case $guard @{OutputRoot='never-accessed';Configuration='Release';ProtectedRelease=$true;MaintenancePassword=$true} $true ($entry[0]+' guarded Release reaches I/O boundary')
}
foreach($entry in @(@('Package.ps1',@{}),@('tools/Build-StandardSetup.ps1',@{PayloadDirectory='never-accessed'}),@('tools/Build-PreviewSetup.ps1',@{Archive='never-accessed'}),@('tools/Publish-FrozenInternal.ps1',@{BuildRoot='never-accessed';FreezeManifest='never-accessed';DeploymentProof='never-accessed';UiProof='never-accessed'}),@('tools/Publish-Verification117.ps1',@{BuildRoot='never-accessed';VerificationPath='never-accessed'}),@('tools/Publish-Verification118.ps1',@{BuildRoot='never-accessed';VerificationPath='never-accessed'}),@('tools/Publish-Verification119.ps1',@{BuildRoot='never-accessed';VerificationPath='never-accessed'}))){
    $guard=Guard-Preamble $entry[0] 'root';Guard-Case $guard $entry[1] $false ($entry[0]+' cannot generate another legacy delivery')
}
foreach($path in @('tools/Publish-R27Validation.ps1','tools/Publish-V30Validation.ps1')){
    $guard=Guard-Preamble $path 'root'
    foreach($mode in @('Capture','Validate','Publish')){Guard-Case $guard @{Mode=$mode;BuildRoot='never-accessed';FreezeEvidence='never-accessed'} ($mode -ne 'Publish') ($path+' '+$mode+' entry policy')}
}
$setup=Guard-Preamble 'tools/Build-V30BatchSetup.ps1' 'root'
Guard-Case $setup @{Mode='Compile';DeliveryBatch='06'} $false 'Historical password-free 06 cannot compile a new installer'
Guard-Case $setup @{Mode='SourceAudit';DeliveryBatch='06'} $true 'Historical source audit stays available'
Guard-Case $setup @{Mode='Validate';DeliveryBatch='08'} $true 'Historical 08 readonly validation stays available'
Guard-Case $setup @{Mode='Compile';DeliveryBatch='08'} $true '08 compile reaches later guarded payload/proof checks only'

$helper=Read-Source 'tools/ReleaseMaintenanceEvidence.ps1'
Check ($helper -notmatch '\b(?:Get-Content|Set-Content|Get-FileHash|Test-Path|New-Item|Copy-Item|Start-Process|Add-Type)\b|\[IO\.|Process\.|Assembly\.') 'Metadata/hash-chain helper has no I/O or executable loading'
. (Join-Path $root 'tools/ReleaseMaintenanceEvidence.ps1')
function New-EvidenceFixture {
    $a='A'*64;$b='B'*64;$c='C'*64;$d='D'*64
    $records=@();$staged=@();$transforms=@()
    foreach($name in @('ProjectCreation','ProjectManagement','DataBrowser','DataAnalysis','ReportGenerator','PressureTest','CalibrationTest')){
        $file=$name+'/Fixture.dll';$records+=[pscustomobject]@{Module=$name;File=$file;SHA256=$a}
        $protected=$name -in @('DataAnalysis','PressureTest')
        $staged+=[pscustomobject]@{Path='modules/'+$file;Sha256=$(if($protected){$b}else{$a})}
        if($protected){$transforms+=[pscustomobject]@{Path='modules/'+$file;InputSHA256=$a;OutputSHA256=$b}}
    }
    [pscustomobject]@{
        Build=[pscustomobject]@{ProtectedRelease=$true;MaintenancePasswordEnabled=$true;V30BatchValidationProfile=$true;Configuration='Release';ConfigurationProfile='Validation-V30-Batch-20261008';PressureStandalone=$false;HybridArchitecture=[pscustomobject]@{Included=$true};ExecutableSha256=$d;HostCompilation=[pscustomobject]@{Schema='HVX.HostCompilation/1';CompilerDefines=@('MAINTENANCE_PASSWORD_GUARD');OutputSHA256=$c};OwnedHostAlgorithmProtection=[pscustomobject]@{InputSHA256=$c;OutputSHA256=$d}}
        Modules=[pscustomobject]@{ProtectedRelease=$true;MaintenancePasswordEnabled=$true;Configuration='Release';Modules=$staged;OwnedAlgorithmProtection=$transforms}
        Ui=[pscustomobject]@{Schema='HVX.UiModuleCompilation/1';Configuration='Release';ProtectedRelease=$true;MaintenancePasswordEnabled=$true;CompilerDefines=@('MAINTENANCE_PASSWORD_GUARD');Modules=$records}
    }
}
function Evidence-Case([string]$Name,[ScriptBlock]$Mutate,[bool]$Accepted=$false){
    $fixture=New-EvidenceFixture;& $Mutate $fixture
    $actual=$false;try{Assert-ReleaseMaintenanceCompilation $fixture.Build $fixture.Modules $fixture.Ui;$actual=$true}catch{}
    Check ($actual -eq $Accepted) $Name
}
Evidence-Case 'Guarded original/protected mixed UI hash chains accepted' {} $true
Evidence-Case 'Host false maintenance flag rejected' {param($f) $f.Build.MaintenancePasswordEnabled=$false}
Evidence-Case 'Host string true does not create maintenance evidence' {param($f) $f.Build.MaintenancePasswordEnabled='true'}
Evidence-Case 'Missing host maintenance marker rejected' {param($f) $f.Build.PSObject.Properties.Remove('MaintenancePasswordEnabled')}
Evidence-Case 'Module false maintenance marker rejected' {param($f) $f.Modules.MaintenancePasswordEnabled=$false}
Evidence-Case 'UI false maintenance marker rejected' {param($f) $f.Ui.MaintenancePasswordEnabled=$false}
Evidence-Case 'Debug manifest cannot masquerade as release' {param($f) $f.Build.Configuration='Debug'}
Evidence-Case 'Nonhybrid candidate rejected' {param($f) $f.Build.HybridArchitecture.Included=$false}
Evidence-Case 'Standalone candidate rejected' {param($f) $f.Build.PressureStandalone=$true}
Evidence-Case 'Missing host compiler guard rejected' {param($f) $f.Build.HostCompilation.CompilerDefines=@('HYBRID_CORE')}
Evidence-Case 'Missing UI compiler guard rejected' {param($f) $f.Ui.CompilerDefines=@()}
Evidence-Case 'Duplicate compiler guard evidence rejected' {param($f) $f.Ui.CompilerDefines=@('MAINTENANCE_PASSWORD_GUARD','MAINTENANCE_PASSWORD_GUARD')}
Evidence-Case 'Old bool-only UI receipt rejected' {param($f) $f.Ui.PSObject.Properties.Remove('Schema')}
Evidence-Case 'Host protection input mismatch rejected' {param($f) $f.Build.OwnedHostAlgorithmProtection.InputSHA256='E'*64}
Evidence-Case 'Host protected executable mismatch rejected' {param($f) $f.Build.ExecutableSha256='E'*64}
Evidence-Case 'Missing one UI module rejected' {param($f) $f.Ui.Modules=@($f.Ui.Modules|Select-Object -First 6)}
Evidence-Case 'Duplicate UI identity rejected' {param($f) $f.Ui.Modules[0].Module='PressureTest'}
Evidence-Case 'Missing staged UI module rejected' {param($f) $f.Modules.Modules=@($f.Modules.Modules|Select-Object -First 6)}
Evidence-Case 'Duplicate staged UI path rejected' {param($f) $f.Modules.Modules+=$f.Modules.Modules[0]}
Evidence-Case 'Replacement of unchanged UI rejected' {param($f) $f.Modules.Modules[0].Sha256='E'*64}
Evidence-Case 'Protected UI input mismatch rejected' {param($f) $f.Modules.OwnedAlgorithmProtection[0].InputSHA256='E'*64}
Evidence-Case 'Protected UI output mismatch rejected' {param($f) $f.Modules.OwnedAlgorithmProtection[0].OutputSHA256='E'*64}
Evidence-Case 'Ambiguous UI protection chain rejected' {param($f) $f.Modules.OwnedAlgorithmProtection+=$f.Modules.OwnedAlgorithmProtection[0]}
Evidence-Case 'UI relative traversal rejected' {param($f) $f.Ui.Modules[0].File='ProjectCreation/../Fixture.dll'}
Evidence-Case 'Missing actual UI compile receipt rejected' {param($f) $f.Ui=$null}

$buildText=Read-Source 'Build.ps1';$stageText=Read-Source 'Stage-Modules.ps1';$uiText=Read-Source 'tools/Build-UiModules.ps1'
$publisher=Read-Source 'tools/Publish-V30BatchValidation.ps1';$setupText=Read-Source 'tools/Build-V30BatchSetup.ps1'
Check ($buildText.Contains('Configuration = $Configuration') -and $stageText.Contains('$stepArgs.Configuration=$Configuration')) 'Configuration flows from host through staging to UI'
Check ($buildText.Contains("if (`$MaintenancePassword) { `$args += '/define:MAINTENANCE_PASSWORD_GUARD' }") -and $uiText.Contains("if (`$MaintenancePassword) { `$common += '/define:MAINTENANCE_PASSWORD_GUARD' }")) 'Actual host/UI compiler argument arrays contain the maintenance define'
Check ($buildText.Contains('HostCompilation = $hostCompilation') -and $buildText.Contains('CompilerDefines=@($args')) 'Host receipt records actual compiler argument defines and original output hash'
Check ($uiText.Contains('CompilerDefines=@($common') -and $stageText.Contains('SourceUiRuntime = [ordered]@{')) 'UI compile receipt and staged receipt hash are explicit'
Check ($publisher.Contains('Assert-ReleaseMaintenanceCompilation $bm $mm $uiCompilation') -and $publisher.IndexOf('Assert-ReleaseMaintenanceCompilation $bm $mm $uiCompilation') -lt $publisher.IndexOf("if(`$Mode -eq 'Capture')")) 'Publish compilation gate runs before freeze/publish writes'
Check ($publisher.Contains("if(`$Mode -eq 'Publish' -and (-not `$maintenancePassword -or [string]::IsNullOrWhiteSpace(`$MaintenancePasswordProof)))")) 'New publisher always requires real maintenance access proof'
Check ($publisher.Contains("ManifestPath) ([string]`$mm.SourceUiRuntime.ManifestSHA256)") -and $publisher.Contains("`$buildEvidenceFiles+=[string]`$mm.SourceUiRuntime.ManifestPath")) 'UI receipt hash is checked and included in new freeze inventory'
Check ($setupText.Contains('HVX.ReleaseMaintenanceCompilation/1') -and $setupText.IndexOf('HVX.ReleaseMaintenanceCompilation/1') -lt $setupText.IndexOf('& $compiler')) 'New installer compile verifies guarded compilation declaration before compiler'
# Execute only the real metadata-only Compile condition, never the installer
# script. Its file/compiler/signature operations stay outside this block.
$setupTokens=$null;$setupErrors=$null
$setupAst=[Management.Automation.Language.Parser]::ParseInput($setupText,[ref]$setupTokens,[ref]$setupErrors)
$compileBlock=@($setupAst.EndBlock.Statements|Where-Object {$_ -is [Management.Automation.Language.IfStatementAst] -and $_.Extent.Text.Contains('HVX.ReleaseMaintenanceCompilation/1')})
Check ($compileBlock.Count -eq 1 -and $compileBlock[0].Extent.Text -notmatch '\b(?:Get-Content|Set-Content|Get-FileHash|Test-Path|New-Item|Copy-Item|Start-Process)\b|\&\s*\(') 'Actual installer Compile evidence block is metadata-only'
$compileGuard=[ScriptBlock]::Create('param($Mode,$release,$manifest) '+$compileBlock[0].Extent.Text+"`r`n'GUARD_ACCEPTED'")
$compileRelease=[pscustomobject]@{ExecutableSHA256=('D'*64);MaintenanceCompilation=[pscustomobject]@{Schema='HVX.ReleaseMaintenanceCompilation/1';Guard='MAINTENANCE_PASSWORD_GUARD';ProtectedHostSHA256=('D'*64);UiCompilationSHA256=('A'*64);HostCompilationSHA256=('C'*64);UiModuleCount=7}}
$compileModules=[pscustomobject]@{MaintenancePasswordEnabled=$true;ProtectedRelease=$true;Configuration='Release';SourceUiRuntime=[pscustomobject]@{ManifestSHA256=('A'*64)}}
Guard-Case $compileGuard @{Mode='Compile';release=$compileRelease;manifest=$compileModules} $true 'New guarded installer metadata reaches compiler boundary'
Guard-Case $compileGuard @{Mode='Compile';release=[pscustomobject]@{ExecutableSHA256=('D'*64)};manifest=$compileModules} $false 'Old 08 release without compilation evidence cannot create a new installer'
Guard-Case $compileGuard @{Mode='Validate';release=[pscustomobject]@{};manifest=[pscustomobject]@{}} $true 'Old metadata remains eligible for historical readonly validation'
$compileModules.MaintenancePasswordEnabled=$false
Guard-Case $compileGuard @{Mode='Compile';release=$compileRelease;manifest=$compileModules} $false 'Installer rejects false module maintenance marker'
$compileModules.MaintenancePasswordEnabled=$true;$compileModules.SourceUiRuntime.ManifestSHA256='E'*64
Guard-Case $compileGuard @{Mode='Compile';release=$compileRelease;manifest=$compileModules} $false 'Installer rejects unbound UI compile receipt'
$compileModules.SourceUiRuntime.ManifestSHA256='A'*64;$compileRelease.MaintenanceCompilation.Guard='OTHER'
Guard-Case $compileGuard @{Mode='Compile';release=$compileRelease;manifest=$compileModules} $false 'Installer rejects an unrelated compiler guard declaration'
$publisherTokens=$null;$publisherErrors=$null
$publisherAst=[Management.Automation.Language.Parser]::ParseInput($publisher,[ref]$publisherTokens,[ref]$publisherErrors)
$proofBlock=@($publisherAst.EndBlock.Statements|Where-Object {$_ -is [Management.Automation.Language.IfStatementAst] -and $_.Extent.Text.Contains('Every new publish requires actual guarded-program maintenance evidence.')})
Check ($proofBlock.Count -eq 1) 'New publish proof requirement is uniquely available as a pure condition'
$proofGuard=[ScriptBlock]::Create('param($Mode,$maintenancePassword,$MaintenancePasswordProof) '+$proofBlock[0].Extent.Text+"`r`n'GUARD_ACCEPTED'")
Guard-Case $proofGuard @{Mode='Publish';maintenancePassword=$false;MaintenancePasswordProof=''} $false 'Password-free new publish rejected'
Guard-Case $proofGuard @{Mode='Publish';maintenancePassword=$true;MaintenancePasswordProof=''} $false 'New publish without real access proof rejected'
Guard-Case $proofGuard @{Mode='Publish';maintenancePassword=$true;MaintenancePasswordProof='fixture-only'} $true 'Present access proof reaches later schema/hash/runtime verification'
Guard-Case $proofGuard @{Mode='Validate';maintenancePassword=$false;MaintenancePasswordProof=''} $true 'Historical readonly validation does not relabel a password-free build'
$null=Read-Source 'HVXUnifiedDesktop.csproj'
Check ((Read-Source 'HVXUnifiedDesktop.csproj').Contains('$(DefineConstants);MAINTENANCE_PASSWORD_GUARD')) 'Direct Release MSBuild remains guarded host-only output'
$null=$inputs.Add('tools/Test-ReleaseMaintenanceEntrypoints.ps1')
New-Item -ItemType Directory -Path $output -Force|Out-Null
$receipt=Join-Path $output 'verification.json'
[ordered]@{Schema='HVX.ReleaseMaintenanceEntrypoints.SourceOnly/1';CheckedUtc=[DateTime]::UtcNow.ToString('o');SourceOnly=$true;SyntheticMetadataOnly=$true;FullBuildRun=$false;PublisherRun=$false;ProductStarted=$false;SdkLoaded=$false;HardwareAccessed=$false;InstallerRun=$false;CorrectCredentialNotEntered=$true;Checks=$results.ToArray();Inputs=@($inputs|Sort-Object|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath (Join-Path $root $_) -Algorithm SHA256).Hash}})}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $receipt -Encoding UTF8
Write-Output ('RELEASE_MAINTENANCE_ENTRYPOINT_CHECKS='+$results.Count)
Write-Output ('RELEASE_MAINTENANCE_ENTRYPOINT_RECEIPT='+$receipt)
