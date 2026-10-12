param([Parameter(Mandatory=$true)][string]$Archive)
$ErrorActionPreference = 'Stop'
throw 'Unprotected preview installer compilation is retired. Debug previews are development output, not deliverable installers.'
$root = Split-Path -Parent $PSScriptRoot
$payload = [IO.Path]::GetFullPath($Archive)
if (-not $payload.StartsWith((Join-Path $root 'deliverables\'), [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $payload -PathType Leaf)) { throw 'Select a generated release archive.' }
$output = [IO.Path]::ChangeExtension($payload, '.Setup.exe')
if (Test-Path -LiteralPath $output) { throw 'Setup already exists.' }
$hashFile = Join-Path $root 'build\setup-payload.sha256'
(Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash | Set-Content -LiteralPath $hashFile -Encoding ASCII
& (Join-Path $root 'tools\compiler\csc.exe') /nologo /target:winexe /platform:x64 /optimize+ /debug- /langversion:7.3 `
    ('/out:' + $output) ('/win32icon:' + (Join-Path $root 'assets\xiongxin.ico')) ('/win32manifest:' + (Join-Path $root 'app.manifest')) `
    ('/resource:' + (Join-Path $root 'assets\xiongxin.ico') + ',Brand.ico') ('/resource:' + $payload + ',Payload.zip') ('/resource:' + $hashFile + ',Payload.sha256') `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    (Join-Path $PSScriptRoot 'PreviewSetup.cs')
if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed.' }
Write-Output $output
