param()
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$qa=Join-Path $root ('build\ManufacturerRampSaveQA-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
New-Item -ItemType Directory -Path $qa | Out-Null
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File|Sort-Object FullName)
$inputs=@($sources.FullName)+@((Join-Path $root 'tests\ManufacturerRampSaveSmoke.cs'))
$snapshot=Join-Path $qa 'inputs'
$records=@()
foreach($path in $inputs){
 $relative=$path.Substring($root.Length+1);$target=Join-Path $snapshot $relative
 New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force|Out-Null
 Copy-Item -LiteralPath $path -Destination $target
 $records+=@([ordered]@{Path=$relative;SHA256=(Get-FileHash -LiteralPath $target).Hash})
}
$compiler=Join-Path $PSScriptRoot 'compiler\csc.exe'
$exe=Join-Path $qa 'ManufacturerRampSaveSmoke.exe'
$argsCompile=@('/nologo','/langversion:7.3','/target:exe','/platform:x64','/deterministic+',
 '/define:MAINTENANCE_PASSWORD_GUARD','/main:ManufacturerRampSaveSmoke',('/out:'+$exe),
 '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',
 '/r:System.Xml.dll','/r:System.Xml.Linq.dll','/r:System.Security.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll')
$output=@(& $compiler @argsCompile @($sources|ForEach-Object{Join-Path $snapshot ('src\'+$_.Name)}) (Join-Path $snapshot 'tests\ManufacturerRampSaveSmoke.cs') 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE
$output|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($code -ne 0){throw ('Test compilation failed: '+($output -join "`n"))}
$fixture=Join-Path $qa 'fixture'
$p=Start-Process -FilePath $exe -ArgumentList ('"'+$fixture+'"') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $qa 'stdout.txt') -RedirectStandardError (Join-Path $qa 'stderr.txt')
if(-not $p.WaitForExit(30000)){$p.Kill();$p.WaitForExit();throw 'Fake-only settings test timed out.'}
$p.WaitForExit()
$text=Get-Content -LiteralPath (Join-Path $qa 'stdout.txt') -Raw
$errorText=Get-Content -LiteralPath (Join-Path $qa 'stderr.txt') -Raw
[ordered]@{Schema='HVX.ManufacturerRampSave.SourceQA/1';ExitCode=$p.ExitCode;Inputs=$records;
 HardwareAccessed=$false;SDKCalled=$false;ProductEntryStarted=$false;RealCredentialsRead=$false;UserSettingsWritten=$false;
 Scope='Guarded isolated test entry only. Expert grant injected into its detached test window; no real password checked. Temp fixture settings only.';
 Output=$text;Error=$errorText}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
Write-Output $text
Write-Output ('MANUFACTURER_RAMP_SAVE_QA='+$qa)
if($p.ExitCode -ne 0){throw ('Test failed: '+$errorText)}
