param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=Join-Path $root ('build\qa-r58-request-environment-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
New-Item -ItemType Directory -Path $output|Out-Null
$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$inputPaths=@('src\ModuleHost.Environment.cs','src\ModuleHost.FactoryPressure.cs','src\ArtEnvironmentReader.cs','src\EnvironmentalReadings.cs','tests\EnvironmentAutoRefreshSmoke.cs','tests\R58EnvironmentOwnedReleaseSmoke.cs','tests\R58RequestRejectionSmoke.cs')|ForEach-Object{Join-Path $root $_}
$inputPaths+=@(Get-ChildItem -LiteralPath (Join-Path $root 'modules-src\FactoryWorker') -Recurse -File -Filter '*.cs'|ForEach-Object FullName)
$inputPaths+=$PSCommandPath
$inventory=@($inputPaths|Sort-Object -Unique|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$tests=@()
foreach($case in @(
    @{Name='EnvironmentHandoff';Files=@('tests\EnvironmentAutoRefreshSmoke.cs','src\ModuleHost.Environment.cs','src\EnvironmentalReadings.cs')},
    @{Name='EnvironmentOwnedRelease';Files=@('tests\R58EnvironmentOwnedReleaseSmoke.cs','src\ArtEnvironmentReader.cs','src\EnvironmentalReadings.cs')},
    @{Name='RequestRejection';Files=@('tests\R58RequestRejectionSmoke.cs');Main='R58RequestRejectionSmoke'}
)) {
    $exe=Join-Path $output ($case.Name+'.exe')
    $files=@($case.Files|ForEach-Object{Join-Path $root $_})
    $options=@()
    if($case.ContainsKey('Main')){
        $files+=@(Get-ChildItem -LiteralPath (Join-Path $root 'modules-src\FactoryWorker') -Recurse -File -Filter '*.cs'|Sort-Object FullName|ForEach-Object FullName)
        # Compile the exact production verifier, not a duplicated test version.
        $hostText=[IO.File]::ReadAllText((Join-Path $root 'src\ModuleHost.FactoryPressure.cs'))
        $start=$hostText.IndexOf('        internal static bool IsVerifiedRequestRejection(',[StringComparison]::Ordinal)
        $end=$hostText.IndexOf('        internal static bool IsVerifiedPreNativeCancellation(',[StringComparison]::Ordinal)
        if($start -lt 0 -or $end -le $start){throw 'Missing bounded host request verifier.'}
        $hostVerifier=Join-Path $output 'RequestVerifier.production.cs'
        ('using System;using System.Reflection;namespace Xiongxin.OWTS.Desktop {internal sealed partial class ModuleHost {'+$hostText.Substring($start,$end-$start)+'}}')|Set-Content -LiteralPath $hostVerifier -Encoding UTF8
        $files+=$hostVerifier
        $options+=('/main:'+$case.Main)
    }
    $compile=@(& $compiler /nologo /langversion:latest /target:exe /platform:x64 /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:'+$exe) @options @files 2>&1|ForEach-Object{$_.ToString()})
    $compileExit=$LASTEXITCODE;$compile|Set-Content -LiteralPath (Join-Path $output ($case.Name+'-compile.txt')) -Encoding UTF8
    if($compileExit -ne 0){throw ($compile -join "`n")}
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$exe;$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $process=New-Object Diagnostics.Process;$process.StartInfo=$start
    if(-not $process.Start()){throw 'Offline runner did not start.'}
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    if(-not $process.WaitForExit(30000)){$process.Kill();throw 'Offline runner exceeded 30 seconds.'}
    $text=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult();$exit=$process.ExitCode;$process.Dispose()
    $text|Set-Content -LiteralPath (Join-Path $output ($case.Name+'-stdout.txt')) -Encoding UTF8
    $tests+=,[ordered]@{Name=$case.Name;CompileExit=$compileExit;ExitCode=$exit;Output=$text;ExecutableSHA256=(Get-FileHash -LiteralPath $exe).Hash}
    if($exit -ne 0){throw $text};$text|Write-Output
}
$unchanged=$true;foreach($entry in $inventory){if((Get-FileHash -LiteralPath $entry.Path).Hash -cne $entry.SHA256){$unchanged=$false}}
[ordered]@{Schema='HVX.R58.RequestEnvironmentBoundary.Offline/1';Utc=[DateTime]::UtcNow.ToString('o');Inputs=$inventory;SourceUnchanged=$unchanged;Tests=$tests;ProductStarted=$false;SDKCalled=$false;HardwareAccessed=$false;NetworkAccessed=$false;PhysicalAcceptance=$false;Uncovered=@('Actual ART timing, USB, cleanup and device handoff','Complete pressure/worker integration and real safety acceptance')}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'proof.json') -Encoding UTF8
Write-Output ('R58_BOUNDARY_PROOF='+$output)
if(-not $unchanged){throw 'Source changed during offline checks.'}
