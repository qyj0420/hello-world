param([Parameter(Mandatory=$true)][string]$QaRoot)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$qa=[IO.Path]::GetFullPath($QaRoot)
if(-not $qa.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Publisher inventory fixtures must remain under project build.'}
. (Join-Path $project 'tools\VerifiedRuntimeInventory.ps1')
$fixture=Join-Path $qa ('inventory-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixture 'assets'),(Join-Path $fixture 'modules\example') -Force | Out-Null
$names=@('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config','build-manifest.json','module-manifest.json','assets\brand.png','modules\example\module.dll')
foreach($name in $names){[IO.File]::WriteAllText((Join-Path $fixture $name),'synthetic:'+ $name)}
$entries=@($names | ForEach-Object {[pscustomobject]@{Path=$_;SHA256=(Get-FileHash -LiteralPath (Join-Path $fixture $_)).Hash}})
$checks=0
if((Assert-VerifiedRuntimeInventory $fixture $entries) -ne 6){throw 'Valid closed inventory failed.'};$checks++
foreach($name in $names) {
    $path=Join-Path $fixture $name
    [IO.File]::WriteAllText($path,'changed after preflight')
    $rejected=$false;try{Assert-VerifiedRuntimeInventory $fixture $entries | Out-Null}catch{$rejected=$_.Exception.Message -like '*changed*'}
    if(-not $rejected){throw ('Changed copied file was accepted: '+$name)};$checks++
    [IO.File]::WriteAllText($path,'synthetic:'+ $name)
}
foreach($relative in @('modules\example\extra.dll','assets\extra.png')) {
    $extra=Join-Path $fixture $relative;[IO.File]::WriteAllText($extra,'unverified')
    $rejected=$false;try{Assert-VerifiedRuntimeInventory $fixture $entries | Out-Null}catch{$rejected=$_.Exception.Message -like '*not exhaustive*'}
    if(-not $rejected){throw ('Extra runtime file was accepted: '+$relative)};$checks++
    # Remove only this exact synthetic file within the freshly owned fixture.
    Remove-Item -LiteralPath $extra
}
$duplicate=@($entries);$duplicate[5]=$entries[0]
$rejected=$false;try{Assert-VerifiedRuntimeInventory $fixture $duplicate | Out-Null}catch{$rejected=$_.Exception.Message -like '*Invalid verified runtime path*'}
if(-not $rejected){throw 'Duplicate inventory entry was accepted.'};$checks++
$invalid=@($entries);$invalid[5]=[pscustomobject]@{Path='..\escape.dll';SHA256=$entries[5].SHA256}
$rejected=$false;try{Assert-VerifiedRuntimeInventory $fixture $invalid | Out-Null}catch{$rejected=$_.Exception.Message -like '*Invalid verified runtime path*'}
if(-not $rejected){throw 'Escaping inventory entry was accepted.'};$checks++
if((Assert-VerifiedRuntimeInventory $fixture $entries -StrictCandidateRoot $true) -ne 6){throw 'Valid strict candidate failed.'};$checks++
foreach($extraName in @('injected.dll','unknown-runtime')) {
    $extra=Join-Path $fixture $extraName
    if($extraName -eq 'unknown-runtime'){New-Item -ItemType Directory -Path $extra | Out-Null}else{[IO.File]::WriteAllText($extra,'unverified')}
    $rejected=$false;try{Assert-VerifiedRuntimeInventory $fixture $entries -StrictCandidateRoot $true | Out-Null}catch{$rejected=$_.Exception.Message -like '*Unexpected candidate root*'}
    if(-not $rejected){throw ('Unlisted candidate root item accepted: '+$extraName)};$checks++
    Remove-Item -LiteralPath $extra
}
# Each attack owns a separate fixture. Keep payloads for diagnosis, restore only
# the attributes that this test set, and never remove a junction recursively.
function New-OwnedInventoryFixture([string]$Name) {
    $owned=[IO.Path]::GetFullPath((Join-Path $qa $Name))
    if(-not $owned.StartsWith($qa+'\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $owned)) { throw 'The isolated inventory fixture must be new and below QA.' }
    New-Item -ItemType Directory -Path (Join-Path $owned 'assets'),(Join-Path $owned 'modules\example') -Force | Out-Null
    foreach($name in $names){[IO.File]::WriteAllText((Join-Path $owned $name),'synthetic:'+ $name)}
    return $owned
}
$attributeChecks=0;$reparseChecks=0;$reparseSkipped=0
foreach($surface in @('assets','modules\example')) {
    foreach($attribute in @([IO.FileAttributes]::Hidden,[IO.FileAttributes]::System)) {
        $owned=New-OwnedInventoryFixture ('attribute-file-'+$attributeChecks)
        $extra=Join-Path $owned ($surface+'\not-in-proof.dll')
        [IO.File]::WriteAllText($extra,'unlisted runtime with a filtered attribute')
        $originalAttributes=[IO.File]::GetAttributes($extra)
        try {
            [IO.File]::SetAttributes($extra,($originalAttributes -bor $attribute))
            if(-not ([IO.File]::GetAttributes($extra) -band $attribute)){throw 'The filtered file attribute was not actually applied.'}
            $rejected=$false;try{Assert-VerifiedRuntimeInventory $owned $entries | Out-Null}catch{$rejected=$_.Exception.Message -like '*not exhaustive*'}
            if(-not $rejected){throw ('Filtered extra runtime file was accepted: '+$surface+';'+$attribute)}
            $attributeChecks++;$checks++
        } finally { [IO.File]::SetAttributes($extra,$originalAttributes) }
    }
    $owned=New-OwnedInventoryFixture ('hidden-parent-'+$attributeChecks)
    $hiddenParent=Join-Path $owned ($surface+'\hidden-parent')
    New-Item -ItemType Directory -Path $hiddenParent | Out-Null
    [IO.File]::WriteAllText((Join-Path $hiddenParent 'not-in-proof.dll'),'ordinary file below a hidden parent')
    $originalAttributes=[IO.File]::GetAttributes($hiddenParent)
    try {
        [IO.File]::SetAttributes($hiddenParent,($originalAttributes -bor [IO.FileAttributes]::Hidden))
        if(-not ([IO.File]::GetAttributes($hiddenParent) -band [IO.FileAttributes]::Hidden)){throw 'The hidden parent attribute was not actually applied.'}
        $rejected=$false;try{Assert-VerifiedRuntimeInventory $owned $entries | Out-Null}catch{$rejected=$_.Exception.Message -like '*not exhaustive*'}
        if(-not $rejected){throw ('Runtime under a hidden parent was accepted: '+$surface)}
        $attributeChecks++;$checks++
    } finally { [IO.File]::SetAttributes($hiddenParent,$originalAttributes) }

    $owned=New-OwnedInventoryFixture ('hidden-reparse-'+$reparseChecks+'-'+$reparseSkipped)
    $link=Join-Path $owned ($surface+'\hidden-junction')
    # The target is a new empty directory owned by this invocation, outside the
    # fixture inventory but still strictly inside its private QA root.
    $target=Join-Path $qa ('empty-junction-target-'+$reparseChecks+'-'+$reparseSkipped)
    New-Item -ItemType Directory -Path $target | Out-Null
    $junctionCreated=$false
    try { New-Item -ItemType Junction -Path $link -Value $target -ErrorAction Stop | Out-Null; $junctionCreated=$true }
    catch {
        # Capability failures are explicitly not counted as attack passes.
        if($_.Exception -isnot [UnauthorizedAccessException] -and $_.Exception -isnot [PlatformNotSupportedException] -and $_.Exception -isnot [NotSupportedException]) { throw }
        $reparseSkipped++
        Write-Output ('PUBLISHER_INVENTORY_SKIP=hidden-junction;surface='+$surface+';reason=permission_or_filesystem_capability;not_counted_as_pass=true')
    }
    if($junctionCreated) {
        $originalAttributes=[IO.File]::GetAttributes($link)
        try {
            [IO.File]::SetAttributes($link,($originalAttributes -bor [IO.FileAttributes]::Hidden))
            $actualAttributes=(Get-Item -LiteralPath $link -Force).Attributes
            if(-not ($actualAttributes -band [IO.FileAttributes]::Hidden) -or -not ($actualAttributes -band [IO.FileAttributes]::ReparsePoint)){throw 'The hidden junction attack was not actually created.'}
            $rejected=$false;try{Assert-VerifiedRuntimeInventory $owned $entries | Out-Null}catch{$rejected=$_.Exception.Message -like '*Linked runtime path*'}
            if(-not $rejected){throw ('Hidden linked runtime directory was accepted: '+$surface)}
            $reparseChecks++;$checks++
        } finally { [IO.File]::SetAttributes($link,$originalAttributes) }
    }
}
if((Assert-VerifiedRuntimeInventory $fixture $entries -StrictCandidateRoot $true) -ne 6){throw 'Normal closed inventory stopped being accepted after attribute/reparse checks.'};$checks++
$proofPath=$fixture+'.proof.json';$copyPath=$fixture+'.proof-copy.json'
[IO.File]::WriteAllText($proofPath,'{"Status":"PASS","HardwareAccessed":false}')
$snapshot=Read-VerificationProofSnapshot $proofPath
[IO.File]::WriteAllText($proofPath,'{"Status":"FAIL","HardwareAccessed":true}')
[IO.File]::WriteAllBytes($copyPath,$snapshot.Bytes)
if($snapshot.Value.Status -ne 'PASS' -or $snapshot.Value.HardwareAccessed -ne $false -or (Get-FileHash -LiteralPath $copyPath).Hash -ne $snapshot.SHA256){throw 'Published proof did not preserve the validated byte snapshot.'};$checks++
if((Get-FileHash -LiteralPath $proofPath).Hash -eq $snapshot.SHA256){throw 'The source proof mutation fixture did not change.'};$checks++
Write-Output ('PUBLISHER_INVENTORY_PASS='+$checks+';HARDWARE=False;REAL_DELIVERY_WRITES=0;FILTERED_ATTRIBUTE_ATTACKS='+$attributeChecks+';HIDDEN_JUNCTION_ATTACKS='+$reparseChecks+';HIDDEN_JUNCTION_SKIPPED='+$reparseSkipped+';QA='+$fixture)
