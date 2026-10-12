param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $output.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Report engine output must be beneath project build.'}
for($walk=$output;$walk -and $walk.Length -ge $root.Length;$walk=[IO.Path]::GetDirectoryName($walk)) {
 if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked report build output is prohibited.'}
}
New-Item -ItemType Directory -Path $output -Force|Out-Null
$dll=Join-Path $output 'HVX.Report.Engine.dll'
& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /langversion:7.3 /deterministic+ /target:library /platform:x64 /optimize+ /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Xml.dll /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ('/out:'+$dll) (Join-Path $root 'modules-src\ReportEngine\src\AssemblyInfo.cs') (Join-Path $root 'modules-src\ReportEngine\src\ReportGenerator.cs') (Join-Path $root 'modules-src\ReportEngine\shared\BoundedDocx.cs') (Join-Path $root 'modules-src\ReportEngine\shared\ReportOutputSafety.cs')
if($LASTEXITCODE -ne 0){throw 'Report engine compilation failed.'}
Write-Output ('REPORT_ENGINE_SHA256='+(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash)
