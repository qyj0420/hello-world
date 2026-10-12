param([Parameter(Mandatory=$true)][string]$OutputDirectory,[switch]$Baseline)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $out.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $out)){throw 'Use a fresh QA directory under this project build.'}
New-Item -ItemType Directory -Path $out | Out-Null
$inputs=@(Get-ChildItem -LiteralPath "$root/modules-src/ReportEngine/src","$root/modules-src/ReportEngine/shared" -Filter *.cs -File | Sort-Object FullName)
$before=@($inputs | ForEach-Object {[pscustomobject]@{Path=$_.FullName;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$csc=Join-Path $root 'tools/compiler/csc.exe'
$refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
& $csc /nologo /langversion:7.3 /target:library /platform:x64 ('/out:'+$out+'\HVX.Report.Engine.dll') @refs @($inputs.FullName)
if($LASTEXITCODE){throw 'Engine compile failed.'}
& $csc /nologo /target:exe /platform:x64 ('/out:'+$out+'\soffice.exe') "$root/tests/ReportOutputConverterFixture.cs"
if($LASTEXITCODE){throw 'Fixture compile failed.'}
& $csc /nologo /langversion:7.3 /target:exe /platform:x64 ('/out:'+$out+'\ReportOutputSafetySmoke.exe') @refs ('/r:'+$out+'\HVX.Report.Engine.dll') "$root/tests/ReportOutputSafetySmoke.cs"
if($LASTEXITCODE){throw 'Test compile failed.'}
$args=@('"'+$out+'"','"'+$root+'\dependencies\Report\Templates\EPFT_OWTS_Report_Template_v1.0.docx"','"'+$out+'\soffice.exe"')
if($Baseline){$args+='baseline'}
$p=Start-Process -FilePath "$out/ReportOutputSafetySmoke.exe" -ArgumentList $args -WindowStyle Hidden -PassThru -RedirectStandardOutput "$out/stdout.txt" -RedirectStandardError "$out/stderr.txt"
if(-not $p.WaitForExit(60000)){$p.Kill();$p.WaitForExit();throw 'Offline test timed out.'}
$p.WaitForExit();Get-Content -LiteralPath "$out/stdout.txt","$out/stderr.txt"
$unchanged=@($before|Where-Object {(Get-FileHash -LiteralPath $_.Path).Hash -ne $_.SHA256}).Count -eq 0
[ordered]@{Schema='HVX.ReportOutputSafety.1';Utc=[DateTime]::UtcNow.ToString('o');Baseline=[bool]$Baseline;ExitCode=$p.ExitCode;SourceUnchanged=$unchanged;HardwareAccess=$false;RealProjectAccess=$false;Inputs=$before;EngineSHA256=(Get-FileHash -LiteralPath "$out/HVX.Report.Engine.dll").Hash} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$out/verification.json" -Encoding UTF8
if($p.ExitCode -ne 0 -or -not $unchanged){throw 'Report output regression failed; evidence retained.'}
