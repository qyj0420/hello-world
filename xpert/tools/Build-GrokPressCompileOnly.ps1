[CmdletBinding()]
param(
    [string]$ImportedSourceRoot=(Join-Path $PSScriptRoot '..\vendor\factory-press'),
    [Parameter(Mandatory=$true)][string]$OutputRoot
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$source=[IO.Path]::GetFullPath($ImportedSourceRoot).TrimEnd('\')
$output=[IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if(-not $output.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Use a fresh owned compile-only output.'}
function AssertUnlinked([string]$path){
    for($walk=[IO.Path]::GetFullPath($path);$walk;$walk=[IO.Path]::GetDirectoryName($walk)){
        if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw ('Linked source/output rejected: '+$walk)}
    }
}
AssertUnlinked $source;AssertUnlinked $output
$sources=@()
foreach($part in @('Xiongxin.Owts.Contracts','Xiongxin.Owts.Acquisition','Xiongxin.Owts.HvPressTest')){
    $sources+=@(Get-ChildItem -LiteralPath (Join-Path $source $part) -Recurse -File -Filter '*.cs' |
        Where-Object {$_.Name -notin @('Program.cs','FactoryPressForm.cs','WaveformPlotPanel.cs')} | ForEach-Object FullName)
}
$sources=@($sources|Sort-Object -Unique)
$inputs=@($sources|ForEach-Object{AssertUnlinked $_;[ordered]@{Path=$_.Substring($source.Length+1).Replace('\','/');SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})
$null=New-Item -ItemType Directory -Path $output
$compiler=Join-Path $PSScriptRoot 'compiler/csc.exe'
$framework=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$library=Join-Path $output 'Grok.Pressure.CompileOnly.dll'
$arguments=@('/nologo','/noconfig','/nostdlib+','/langversion:7.3','/deterministic+','/target:library','/platform:x64','/optimize+','/debug-',('/out:'+$library))
foreach($name in @('mscorlib.dll','System.dll','System.Core.dll','System.Xml.dll','System.Xml.Linq.dll','System.Web.Extensions.dll','System.Runtime.Serialization.dll')){$arguments+='/r:'+(Join-Path $framework $name)}
$response=Join-Path $output 'compile.rsp'
@(@($arguments)+@($sources)|ForEach-Object{'"'+$_.Replace('"','\"')+'"'})|Set-Content -LiteralPath $response -Encoding UTF8
$lines=@(& $compiler ('@'+$response) 2>&1|ForEach-Object{$_.ToString()});$code=$LASTEXITCODE
$lines|Set-Content -LiteralPath (Join-Path $output 'compile.log') -Encoding UTF8
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $source $inputFile.Path) -Algorithm SHA256).Hash -cne $inputFile.SHA256){throw 'Imported source changed during compile.'}}
[ordered]@{
    Schema='HVX.GrokPress.CompileOnly/1';ImportedSourceRoot=$source;SourceZipSHA256='BA7024DAD8E9190C2A46F21B5FD32BE8A168E8F5FFB64548B8EF15106668CD86'
    CompletedUtc=[DateTime]::UtcNow.ToString('o');SourceCount=$sources.Count;SourceInputs=$inputs;InputUnchanged=$true
    FrameworkTarget='.NET Framework 4.8';CompilerSHA256=(Get-FileHash -LiteralPath $compiler).Hash;ExitCode=$code
    Status=$(if($code -eq 0){'COMPILE_ONLY_PASS'}else{'COMPILE_ONLY_FAILED'})
    LibrarySHA256=$(if(Test-Path -LiteralPath $library){(Get-FileHash -LiteralPath $library).Hash}else{''})
    ProductEntryExecuted=$false;ProductionNativeLoaded=$false;SDKCalled=$false;HardwareAccessed=$false;IntegratedIntoDesktop=$false
}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'compile-only-manifest.json') -Encoding UTF8
if($code -ne 0){$lines|Out-Host;throw ('Grok compatibility compilation failed: '+$output)}
Write-Output ('GROK_COMPILE_ONLY='+$library)
