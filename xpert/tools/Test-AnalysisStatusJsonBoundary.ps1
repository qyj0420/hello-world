param()
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$qa=Join-Path $root ('build\AnalysisStatusJsonBoundaryQA-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
New-Item -ItemType Directory -Path $qa|Out-Null
$source=Join-Path $root 'modules-src\DataAnalysis\src\ReviewStatusJson.cs'
$test=Join-Path $root 'tests\AnalysisStatusJsonBoundarySmoke.cs'
$consumers=@('ReviewAnalysisEngine.cs','ReviewCalibrationInputScope.cs','ReviewAnalysisArchives.cs')|ForEach-Object{Join-Path $root ('modules-src\DataAnalysis\src\'+$_)}
$inputs=@($source,$test)+@($consumers)
$records=@($inputs|ForEach-Object{[ordered]@{Path=$_.Substring($root.Length+1);SHA256=(Get-FileHash -LiteralPath $_).Hash}})
foreach($path in $consumers){
 $text=Get-Content -LiteralPath $path -Raw -Encoding UTF8
 if($text -match 'MaxJsonLength\s*=\s*(int|Int32)\.MaxValue|RecursionLimit\s*=\s*256'){throw ('Unbounded consumer remains: '+$path)}
 if($text -notmatch 'ReviewStatusJson\.'){throw ('Consumer does not use shared policy: '+$path)}
}
$exe=Join-Path $qa 'AnalysisStatusJsonBoundarySmoke.exe'
$output=@(& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /langversion:7.3 /target:exe /platform:x64 /deterministic+ /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll ('/out:'+$exe) $source $test 2>&1|ForEach-Object{$_.ToString()})
$code=$LASTEXITCODE;$output|Set-Content -LiteralPath (Join-Path $qa 'compile.txt') -Encoding UTF8
if($code -ne 0){throw ('Boundary test compilation failed: '+($output -join "`n"))}
$p=Start-Process -FilePath $exe -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $qa 'stdout.txt') -RedirectStandardError (Join-Path $qa 'stderr.txt')
if(-not $p.WaitForExit(30000)){$p.Kill();$p.WaitForExit();throw 'Pure parser test timed out.'}
$p.WaitForExit()
$text=Get-Content -LiteralPath (Join-Path $qa 'stdout.txt') -Raw
$errorText=Get-Content -LiteralPath (Join-Path $qa 'stderr.txt') -Raw
[ordered]@{Schema='HVX.AnalysisStatusJson.SourceQA/1';ExitCode=$p.ExitCode;Inputs=$records;
 HardwareAccessed=$false;ProductEntryStarted=$false;SDKCalled=$false;
 Scope='Pure shared parser boundary test plus static consumer checks, not an algorithm/hardware acceptance test.';
 Output=$text;Error=$errorText}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
Write-Output $text;Write-Output ('ANALYSIS_STATUS_JSON_BOUNDARY_QA='+$qa)
if($p.ExitCode -ne 0){throw ('Boundary test failed: '+$errorText)}
