param([Parameter(Mandatory=$true)][string]$QaRoot)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$qa=[IO.Path]::GetFullPath($QaRoot)
if (-not $qa.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Guard fixtures must remain below project build.' }
if (Test-Path -LiteralPath $qa) { throw 'Guard fixture must use a new directory.' }
. (Join-Path $project 'tools\VerifiedRuntimeInventory.ps1')
$checks=0
foreach($extension in @('.cs','.c','.cpp','.cxx','.h','.hpp','.hxx','.obj','.lib','.exp','.pdb','.map','.ilk','.ipynb','.pyc','.pyo','.pem','.snk','.pfx','.key','.log','.tmp','.ps1','.cmd','.bat','.md','.py')) {
    $fixture=Join-Path $qa ('private-'+$checks)
    New-Item -ItemType Directory -Path $fixture -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixture ('not-for-customers'+$extension)),'offline fixture')
    $rejected=$false
    try { Assert-PublicModuleFiles -ModulesRoot $fixture } catch { $rejected=$_.Exception.Message -like '*private source*' }
    if (-not $rejected) { throw ('Private runtime material was accepted: '+$extension) }
    $checks++
}
$public=Join-Path $qa 'public\Hybrid'
New-Item -ItemType Directory -Path $public -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $public 'hvx_offline_verify.py'),'explicit public offline tool')
Assert-PublicModuleFiles -ModulesRoot (Split-Path -Parent $public)
$checks++
$hiddenPrivateChecks=0
foreach($extension in @('.cpp','.key','.py')) {
    foreach($hiddenScope in @('File','Parent')) {
        $fixture=Join-Path $qa ('hidden-private-'+$hiddenPrivateChecks)
        $parent=Join-Path $fixture 'hidden-owner'
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        $file=Join-Path $parent ('not-for-customers'+$extension)
        [IO.File]::WriteAllText($file,'private hidden offline fixture')
        $attributePath=if($hiddenScope -eq 'File'){$file}else{$parent}
        $originalAttributes=[IO.File]::GetAttributes($attributePath)
        try {
            [IO.File]::SetAttributes($attributePath,($originalAttributes -bor [IO.FileAttributes]::Hidden))
            if(-not ([IO.File]::GetAttributes($attributePath) -band [IO.FileAttributes]::Hidden)){throw 'The private hidden fixture was not actually hidden.'}
            $rejected=$false
            try { Assert-PublicModuleFiles -ModulesRoot $fixture } catch { $rejected=$_.Exception.Message -like '*private source*' }
            if(-not $rejected){throw ('Hidden private runtime material was accepted: '+$extension+';'+$hiddenScope)}
            $hiddenPrivateChecks++;$checks++
        } finally { [IO.File]::SetAttributes($attributePath,$originalAttributes) }
    }
}
Write-Output ('HYBRID_PUBLISHING_GUARDS_PASS='+$checks+';hiddenPrivateAttacks='+$hiddenPrivateChecks+';hardware=false;deliveryWrites=0')
