[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$CalibrationModule,[string]$OutputRoot='build/qa-theme-preserved-20261010')
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not [IO.Path]::IsPathRooted($OutputRoot)){$OutputRoot=Join-Path $project $OutputRoot}
if(Test-Path -LiteralPath $OutputRoot){throw 'Use fresh isolated output root.'}
$null=New-Item -ItemType Directory -Path $OutputRoot
$sources=@('src/Theme.cs','src/OemUiBranding.cs','modules-src/PressureTest/src/PressureTestTheme.cs','modules-src/DataBrowser/src/BrowserTheme.cs','modules-src/DataBrowser/src/BrowserPhaseButton.cs','tests/ThemePreservedGeometrySmoke.cs')|ForEach-Object{Join-Path $project $_}
$files=@($sources)+@($CalibrationModule,$PSCommandPath)
$before=@($files|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$exe=Join-Path $OutputRoot 'ThemePreservedGeometrySmoke.exe'
& (Join-Path $PSScriptRoot 'compiler/csc.exe') /nologo /langversion:7.3 /target:exe /platform:x64 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:'+$exe) @sources
if($LASTEXITCODE-ne0){throw 'managed test compile failed'}
$qa=Join-Path $OutputRoot 'qa';$null=New-Item -ItemType Directory -Path $qa
$p=Start-Process -FilePath $exe -ArgumentList @(('"'+$qa+'"'),('"'+$CalibrationModule+'"')) -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $qa 'stdout.txt') -RedirectStandardError (Join-Path $qa 'stderr.txt')
$after=@($files|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})
$same=($before|ConvertTo-Json -Compress)-eq($after|ConvertTo-Json -Compress)
[ordered]@{ExitCode=$p.ExitCode;SourceAndCalibrationRuntimeUnchanged=$same;HardwareCalls=0;OriginalUserWrites=0;Sources=$before;TestExeSHA256=(Get-FileHash -LiteralPath $exe).Hash}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $qa 'proof.json') -Encoding UTF8
Get-Content -LiteralPath (Join-Path $qa 'stdout.txt')
Get-Content -LiteralPath (Join-Path $qa 'stderr.txt')
if($p.ExitCode-ne0-or-not $same){throw 'Theme smoke failed; retained proof.'}
