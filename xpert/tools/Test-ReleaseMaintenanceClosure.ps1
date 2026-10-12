[CmdletBinding()]
param([string]$OutputDirectory='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path $root ('qa\release-maintenance-closure-'+[Guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if(-not $output.StartsWith($root+'\qa\',[StringComparison]::OrdinalIgnoreCase)){throw 'Pure source checks must remain beneath project qa.'}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$results=New-Object System.Collections.Generic.List[object]
function Check([bool]$Condition,[string]$Name){
    $results.Add([ordered]@{Name=$Name;Passed=$Condition})
    if(-not $Condition){throw ('Source regression failed: '+$Name)}
}
function Read-Source([string]$Relative){return [IO.File]::ReadAllText((Join-Path $root $Relative))}
# Evaluate only the actual parameter/guard preamble. Stop before its first
# path assignment; never invoke builders, product entry points or SDKs.
function Guard-Preamble([string]$Relative,[string]$StopVariable){
    $path=Join-Path $root $Relative
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
    Check ($errors.Count -eq 0) ($Relative+' parses')
    $stop=@($ast.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.AssignmentStatementAst] -and
        $_.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $_.Left.VariablePath.UserPath -eq $StopVariable
    })
    Check ($stop.Count -eq 1) ($Relative+' has unique safe preamble boundary')
    $code=[IO.File]::ReadAllText($path).Substring(0,$stop[0].Extent.StartOffset)
    Check ($code -notmatch '\b(?:New-Item|Copy-Item|Start-Process|Get-FileHash|Test-Path)\b|\&\s*\(') ($Relative+' preamble has no build/file/device operations')
    return [ScriptBlock]::Create($code+"`r`n'GUARD_ACCEPTED'")
}
function Guard-Case([ScriptBlock]$Guard,[hashtable]$Arguments,[bool]$Accepted,[string]$Name){
    $actual=$false;$reason=''
    try{$value=@(& $Guard @Arguments);$actual=($value.Count -eq 1 -and $value[0] -ceq 'GUARD_ACCEPTED')}
    catch{$reason=$_.Exception.Message}
    Check ($actual -eq $Accepted) $Name
    if(-not $Accepted){Check (-not [string]::IsNullOrWhiteSpace($reason)) ($Name+' gives explicit rejection')}
}
$build=Guard-Preamble 'Build.ps1' 'root'
Guard-Case $build @{ProtectedRelease=$true} $false 'Protected host without maintenance is rejected'
Guard-Case $build @{V30BatchValidation=$true;Hybrid=$true;ProtectedRelease=$true} $false 'Protected complete profile without maintenance is rejected'
Guard-Case $build @{V30BatchValidation=$true;Hybrid=$true;ProtectedRelease=$true;MaintenancePassword=$true} $true 'Complete protected maintenance profile reaches build boundary'
Guard-Case $build @{V30BatchValidation=$true;Hybrid=$true;ProtectedRelease=$true;MaintenancePassword=$true;SkipModules=$true} $false 'Maintenance cannot skip module staging'
Guard-Case $build @{V30BatchValidation=$true;Hybrid=$true;ProtectedRelease=$true;PressureStandalone=$true} $false 'Historical password-free protected standalone is not a new release path'
Guard-Case $build @{V30BatchValidation=$true;Hybrid=$true;ProtectedRelease=$true;MaintenancePassword=$true;PressureStandalone=$true} $false 'Unsupported standalone maintenance combination stays explicitly blocked'
Guard-Case $build @{Configuration='Debug';SkipModules=$true} $true 'Unprotected debug remains a preview-only host path'
$stage=Guard-Preamble 'Stage-Modules.ps1' 'output'
Guard-Case $stage @{OutputRoot='not-used';ProtectedRelease=$true} $false 'Direct protected staging without maintenance is rejected before I/O'
Guard-Case $stage @{OutputRoot='not-used';ProtectedRelease=$true;MaintenancePassword=$true} $true 'Guarded staging reaches its I/O boundary'
Guard-Case $stage @{OutputRoot='not-used'} $true 'Unprotected module preview retains old semantics'
$ui=Guard-Preamble 'tools\Build-UiModules.ps1' 'projectRoot'
Guard-Case $ui @{OutputRoot='not-used';ProtectedRelease=$true} $false 'Direct protected UI build without maintenance is rejected before I/O'
Guard-Case $ui @{OutputRoot='not-used';ProtectedRelease=$true;MaintenancePassword=$true} $true 'Guarded UI build reaches its I/O boundary'
Guard-Case $ui @{OutputRoot='not-used'} $true 'Unprotected UI preview retains old semantics'
$buildText=Read-Source 'Build.ps1';$stageText=Read-Source 'Stage-Modules.ps1';$uiText=Read-Source 'tools\Build-UiModules.ps1'
Check ($buildText.Contains('ProtectedRelease = [bool]$ProtectedRelease; MaintenancePassword = [bool]$MaintenancePassword')) 'Host forwards both protection intentions to staging'
Check ($stageText.Contains('$stepArgs.MaintenancePassword=[bool]$MaintenancePassword') -and $stageText.Contains('$stepArgs.ProtectedRelease=[bool]$ProtectedRelease')) 'Staging forwards both protection intentions to UI modules'
Check ($buildText.Contains("if (`$MaintenancePassword) { `$args += '/define:MAINTENANCE_PASSWORD_GUARD' }")) 'Host maintenance flag reaches compiler define'
Check ($uiText.Contains("if (`$MaintenancePassword) { `$common += '/define:MAINTENANCE_PASSWORD_GUARD' }")) 'Shared UI compiler arguments include maintenance define'
Check ($uiText.Contains("`$pressureSources += Join-Path `$sourceRoot 'FactoryWorker\AdjacentHardwareRangePolicy.cs'")) 'Pressure UI compiles the same pure adjacent-range source as the worker'
[xml]$project=Read-Source 'HVXUnifiedDesktop.csproj'
$ns=New-Object Xml.XmlNamespaceManager($project.NameTable);$ns.AddNamespace('m','http://schemas.microsoft.com/developer/msbuild/2003')
$releaseDefines=@($project.SelectNodes('/m:Project/m:PropertyGroup/m:DefineConstants',$ns)|Where-Object{$_.ParentNode.Condition -ceq "'`$(Configuration)'=='Release'"})
Check ($releaseDefines.Count -eq 1 -and $releaseDefines[0].InnerText.Contains('MAINTENANCE_PASSWORD_GUARD')) 'Direct MSBuild Release host enables maintenance guard'

$hostText=Read-Source 'src\ModuleHost.cs'
$signature='        public bool LaunchTask38(string page, out string error)'
$start=$hostText.IndexOf($signature,[StringComparison]::Ordinal)
$end=$hostText.IndexOf('        public bool LaunchTask38(string page)',$start+1,[StringComparison]::Ordinal)
Check ($start -ge 0 -and $end -gt $start) 'Legacy launch method source is uniquely available'
$method=$hostText.Substring($start,$end-$start)
Check ($method -notmatch '\b(?:Process\.Start|ProcessStartInfo|File\.|Directory\.|Task38\.ControlPanel)\b') 'Legacy launch has no process/file/device action'
# Compile only the exact isolated rejection method with a no-I/O diagnostic
# stub. This is a pure managed regression executable, not the desktop host.
$testSource=@'
using System;
internal sealed class PureDiagnostics { public int Count; public void Warning(string category,string message) { Count++; } }
internal sealed class SourceOnlyHost {
    private readonly PureDiagnostics diagnostics = new PureDiagnostics();
__METHOD__
    internal static int Main() {
        var host = new SourceOnlyHost();
        foreach (string page in new string[] { null, "", "pressure", "calibration", "unknown" }) {
            string error;
            if (host.LaunchTask38(page,out error) || String.IsNullOrWhiteSpace(error)) return 1;
        }
        if (host.diagnostics.Count != 5) return 2;
        Console.WriteLine("LEGACY_SECOND_CONTROLLER_REJECTED=5"); return 0;
    }
}
'@
$testSource=$testSource.Replace('__METHOD__',$method)
$testPath=Join-Path $output 'LegacyLaunchSourceOnly.cs';$exe=Join-Path $output 'LegacyLaunchSourceOnly.exe'
[IO.File]::WriteAllText($testPath,$testSource,(New-Object Text.UTF8Encoding($false)))
& (Join-Path $root 'tools\compiler\csc.exe') /nologo /target:exe /platform:x64 /r:System.dll ('/out:'+$exe) $testPath
Check ($LASTEXITCODE -eq 0) 'Exact legacy rejection method compiles in no-I/O test harness'
$testOutput=@(& $exe)
Check ($LASTEXITCODE -eq 0 -and $testOutput -contains 'LEGACY_SECOND_CONTROLLER_REJECTED=5') 'Pressure/calibration/invalid legacy launch calls are all rejected'
$report=[ordered]@{Schema='HVX.ReleaseMaintenanceClosure.SourceOnly/1';CheckedUtc=[DateTime]::UtcNow.ToString('o');SourceOnly=$true;FullBuildRun=$false;ProductStarted=$false;SdkLoaded=$false;HardwareAccessed=$false;InstallerRun=$false;CorrectCredentialNotEntered=$true;Checks=$results.ToArray();Sources=@('Build.ps1','Stage-Modules.ps1','tools/Build-UiModules.ps1','HVXUnifiedDesktop.csproj','src/ModuleHost.cs'|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath (Join-Path $root $_) -Algorithm SHA256).Hash}})}
$receipt=Join-Path $output 'verification.json'
$report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $receipt -Encoding UTF8
Write-Output ('RELEASE_MAINTENANCE_SOURCE_CHECKS='+$results.Count)
Write-Output ('RELEASE_MAINTENANCE_RECEIPT='+$receipt)
