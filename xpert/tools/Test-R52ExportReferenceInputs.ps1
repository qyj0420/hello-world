param([string]$OutputRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($PSVersionTable.PSVersion.Major -lt 7){throw 'Use PowerShell 7 for this isolated exporter helper test.'}
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$exporter=Join-Path $PSScriptRoot 'Export-R52TestDelivery.ps1'
if([string]::IsNullOrWhiteSpace($OutputRoot)){$OutputRoot=Join-Path $project ('build\qa-r52-reference-inputs-'+[Guid]::NewGuid().ToString('N').Substring(0,10))}
$qa=[IO.Path]::GetFullPath($OutputRoot)
if(-not $qa.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $qa)){throw 'Use a new isolated project build QA directory.'}
$inputs=@($exporter,$PSCommandPath)|ForEach-Object{[ordered]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}}
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($exporter,[ref]$tokens,[ref]$parseErrors)
if(@($parseErrors).Count){throw 'Exporter has syntax errors.'}
foreach($name in @('Assert-R52ReferencePath','Assert-R52ReferenceInput')){
 $node=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true))
 if($node.Count -ne 1){throw 'Expected exactly one owned reference helper.'}
 # Import only these two source-defined read-only helpers, never exporter body.
 . ([scriptblock]::Create($node[0].Extent.Text))
}
$endNode=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.ForEachStatementAst] -and $n.Extent.Text.Contains('A bound input changed before export completion')},$true))
if($endNode.Count -ne 1){throw 'Final snapshot guard must be uniquely present in the exporter.'}
$endGuard=[scriptblock]::Create($endNode[0].Extent.Text)
$snapshots=New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
$referenceSnapshots=New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
New-Item -ItemType Directory -Path $qa|Out-Null
$fixture=Join-Path $qa 'reference.loc'
[IO.File]::WriteAllBytes($fixture,[byte[]](1,2,3,4,5))
$hash=(Get-FileHash -LiteralPath $fixture).Hash
$checks=New-Object System.Collections.Generic.List[object]
function Check([bool]$Condition,[string]$Name){$checks.Add([ordered]@{Name=$Name;Passed=$Condition});if(-not $Condition){throw $Name}}
function Reject([scriptblock]$Action,[string]$Name){$rejected=$false;try{& $Action}catch{$rejected=$true};Check $rejected $Name}
Assert-R52ReferenceInput $fixture $hash
Check ($referenceSnapshots.Count -eq 1 -and $snapshots.Count -eq 1) 'Valid fully qualified reference is hash-bound once.'
Assert-R52ReferenceInput $fixture.Replace('\','/') $hash
Assert-R52ReferenceInput $fixture.ToUpperInvariant() $hash
Check ($referenceSnapshots.Count -eq 1 -and $snapshots.Count -eq 1) 'Slash and case aliases are deduplicated.'
Reject {Assert-R52ReferenceInput $fixture ('B'*64)} 'Conflicting duplicate hash is rejected.'
Check ($referenceSnapshots[$fixture] -ceq $hash) 'Rejected conflict cannot replace the original binding.'
Reject {Assert-R52ReferenceInput 'relative.loc' $hash} 'Relative reference is rejected.'
Reject {Assert-R52ReferenceInput 'D:reference.loc' $hash} 'Drive-relative reference is rejected.'
Reject {Assert-R52ReferenceInput ($qa+'\..\reference.loc') $hash} 'Parent traversal is rejected before read.'
Reject {Assert-R52ReferenceInput '\\server\share\reference.loc' $hash} 'UNC network path is rejected before read.'
Reject {Assert-R52ReferenceInput ('\\?\'+$fixture) $hash} 'Device namespace path is rejected.'
Reject {Assert-R52ReferenceInput ($fixture+':stream') $hash} 'Alternate data stream is rejected.'
Reject {Assert-R52ReferenceInput $qa $hash} 'Directory reference is rejected.'
Reject {Assert-R52ReferenceInput (Join-Path $qa 'absent.loc') $hash} 'Missing reference is rejected.'
Reject {Assert-R52ReferenceInput $fixture $hash.ToLowerInvariant()} 'Lowercase noncanonical hash is rejected.'
Reject {Assert-R52ReferenceInput $fixture 'invalid'} 'Malformed hash is rejected.'
$other=Join-Path $qa 'other.loc';[IO.File]::WriteAllBytes($other,[byte[]](9,8,7))
Reject {Assert-R52ReferenceInput $other ('B'*64)} 'Different bytes reject an otherwise new reference.'
Check (-not $referenceSnapshots.ContainsKey($other)) 'Rejected new reference never enters the snapshot.'
$snapshots[$fixture]='C'*64
Reject {Assert-R52ReferenceInput $fixture $hash} 'Project and reference inventory conflicting bindings are rejected.'
$snapshots[$fixture]=$hash
& $endGuard
Check $true 'Actual source final snapshot guard accepts unchanged inputs.'
[IO.File]::WriteAllBytes($fixture,[byte[]](1,2,3,4,6))
Reject {& $endGuard} 'Actual source final snapshot guard rejects post-validation changes.'
Check ((Get-FileHash -LiteralPath $exporter).Hash -ceq $inputs[0].SHA256) 'Exporter source was not rewritten by this test.'
$mismatches=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.Path).Hash -cne $_.SHA256}).Count
[ordered]@{Schema='HVX.R52.ExportReferenceInputs.SourceQA/1';CompletedUtc=[DateTime]::UtcNow.ToString('o');Status='PASS';ExitCode=0;Inputs=@($inputs);SourceUnchanged=($mismatches -eq 0);InputMismatchCount=$mismatches;Checks=$checks.ToArray();CheckCount=$checks.Count;
 Scope='Only the two source-defined reference helpers and the final snapshot foreach guard were invoked against synthetic QA files. Exporter body and real external references were not invoked.';
 ProductStarted=$false;SDKCalled=$false;HardwareAccessed=$false;ExternalReferenceFilesCopied=$false;ExternalDataModified=$false;PhysicalAcceptance=$false}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $qa 'verification.json') -Encoding UTF8
if($mismatches){throw 'A helper test input changed.'}
Write-Output ('R52_REFERENCE_INPUTS_PASS='+$checks.Count+';PROOF='+ (Join-Path $qa 'verification.json'))
