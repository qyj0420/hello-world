param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$qa=Join-Path $root ('build\qa-r51-feedback-languages-'+[Guid]::NewGuid().ToString('N').Substring(0,10))
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Cannot parse bounded test runner.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
 if($node.Count -ne 1){throw 'Missing bounded runner helper.'}
 . ([ScriptBlock]::Create($node[0].Extent.Text))
}
Assert-RegressionUnlinkedPath $qa -AllowMissing
New-Item -ItemType Directory -Path $qa|Out-Null
$source=@('src\UiLanguage.cs','src\InternationalText.cs','src\InternationalText.Generated.cs','src\Localization.cs','src\InputOperationFeedback.cs'|ForEach-Object{Join-Path $root $_})
$tests=@('InputOperationFeedbackSmoke','R51LanguageCatalogAudit')
$inputs=$source+@($tests|ForEach-Object{Join-Path $root ('tests\'+$_+'.cs')})+@($PSCommandPath)
function Inventory {@($inputs|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}})}
$before=@(Inventory);$results=@()
foreach($test in $tests){
 $exe=Join-Path $qa ($test+'.exe')
 $compiled=@(& (Join-Path $PSScriptRoot 'compiler\csc.exe') /nologo /target:exe /langversion:7.3 /platform:x64 /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.dll /r:System.Web.Extensions.dll ('/main:'+$test) ('/out:'+$exe) @source (Join-Path $root ('tests\'+$test+'.cs')) 2>&1|ForEach-Object{$_.ToString()})
 $code=$LASTEXITCODE;$compiled|Set-Content -LiteralPath (Join-Path $qa ($test+'.compile.txt')) -Encoding UTF8
 if($code -ne 0){throw ($compiled -join "`n")}
 $arguments=@();if($test -eq 'R51LanguageCatalogAudit'){$arguments=@(Join-Path $qa 'language-catalog-audit.json')}
 $run=Invoke-RegressionExecutable -Executable $exe -Arguments $arguments -LogPrefix (Join-Path $qa $test) -TimeoutMilliseconds 30000
 $results+=@([ordered]@{Name=$test;Result=$run});$run.Output|Write-Output
}
$unchanged=(($before|ConvertTo-Json -Compress) -ceq (@(Inventory)|ConvertTo-Json -Compress))
$passed=$unchanged -and @($results|Where-Object {$_.Result.ExitCode -ne 0}).Count -eq 0
[ordered]@{Schema='HVX.R51.FeedbackLanguages/1';Passed=$passed;SourceUnchanged=$unchanged;HardwareAccessed=$false;ProductStarted=$false;FullTranslationVerified=$false;Inputs=$before;Results=$results}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
Write-Output ('FEEDBACK_LANGUAGE_PROOF='+$qa)
if(-not $passed){throw 'Feedback/language regression failed.'}
