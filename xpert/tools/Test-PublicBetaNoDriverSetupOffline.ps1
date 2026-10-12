[CmdletBinding()]
param([string]$OutputRoot='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$builder=Join-Path $PSScriptRoot 'Build-PublicBetaNoDriverSetup.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($builder,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'New installer builder does not parse.'}
foreach($name in @('Relative','Bool','RuntimeInventoryHash','AssertDeferredReceipt')){
    $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -ceq $name)
    if($node.Count -ne 1){throw 'Ambiguous offline helper.'}
    . ([ScriptBlock]::Create($node[0].Extent.Text))
}
$checks=1
foreach($path in @('../old','a/../b','/root','C:\Windows','\\server\share','x:y','a//b','a/','a/CON.txt','a/NUL','a/b.','a/b ')){
    $rejected=$false;try{$null=Relative $path}catch{$rejected=$true}
    if(-not $rejected){throw ('Unsafe path was accepted: '+$path)};$checks++
}
foreach($path in @('assets/shortcuts/256.ico','说明书/HV-DAC_Xpert_Operation_Manual_ZH.pdf','专业术语/Electrical_Testing_Terminology_EN-ZH.pdf')){
    if((Relative $path) -cne $path){throw 'A valid payload path changed.'};$checks++
}
foreach($object in @([pscustomobject]@{},[pscustomobject]@{Enabled='true'},[pscustomobject]@{Enabled=$false})){
    $rejected=$false;try{Bool $object 'Enabled' $true}catch{$rejected=$true}
    if(-not $rejected){throw 'Missing/string/false evidence was accepted.'};$checks++
}
Bool ([pscustomobject]@{Enabled=$true}) 'Enabled' $true;$checks++
$policy=@($ast.ParamBlock.Parameters|Where-Object {$_.Name.VariablePath.UserPath -ceq 'ConfidentialityPolicy'})
if($policy.Count -ne 1 -or $policy[0].DefaultValue.Extent.Text -cne "'Required'"){throw 'Full confidentiality must remain the default policy.'};$checks++
$sampleRuntime=@([pscustomobject]@{Path='modules/example.dll';SHA256=('A'*64)},[pscustomobject]@{Path='model-payload/example.hxm';SHA256=('B'*64)})
$runtimeHash=RuntimeInventoryHash $sampleRuntime
if($runtimeHash -cnotmatch '^[A-F0-9]{64}$' -or (RuntimeInventoryHash @($sampleRuntime[1],$sampleRuntime[0])) -cne $runtimeHash){throw 'Runtime inventory binding is order dependent.'};$checks++
foreach($badRuntime in @(@(),@($sampleRuntime[0],$sampleRuntime[0]),@($sampleRuntime[0],[pscustomobject]@{Path='MODULES/example.dll';SHA256=('A'*64)}),@([pscustomobject]@{Path='modules/x.dll';SHA256='bad'}))){
    $rejected=$false;try{$null=RuntimeInventoryHash $badRuntime}catch{$rejected=$true}
    if(-not $rejected){throw 'Invalid or duplicate runtime identity was accepted.'};$checks++
}
$testBuild='D:\example\v112';$testFreeze=[datetime]::UtcNow.AddMinutes(-2)
$receipt=[pscustomobject][ordered]@{Schema='HVX.AlgorithmConfidentialityDeferral/1';Status='DEFERRED_BY_USER';RecordedUtc=[datetime]::UtcNow.AddMinutes(-1).ToString('o');
    Version='1.1.2.0';Profile='PublicBeta-NoDriver-1.1.2-20261011';BuildRoot=$testBuild;ExecutableSHA256=('C'*64);FreezeSHA256=('D'*64);
    RuntimeInventorySHA256=$runtimeHash;RuntimeFiles=2;UserDecision='Full confidentiality review explicitly deferred by the user; source will be provided separately for further encryption.';
    FullConfidentialityAccepted=$false;ReviewCompleted=$false;NotAConfidentialityPass=$true;HardwareAccessed=$false}
AssertDeferredReceipt $receipt $testBuild $receipt.Profile '1.1.2.0' ('C'*64) ('D'*64) $runtimeHash 2 $testFreeze;$checks++
foreach($change in @(@('Status','PASS'),@('Profile','PublicBeta-NoDriver-20261011'),@('Version','1.1.1.0'),@('BuildRoot','D:\example\old'),
    @('ExecutableSHA256',('E'*64)),@('FreezeSHA256',('E'*64)),@('RuntimeInventorySHA256',('E'*64)),@('RuntimeFiles',1),@('UserDecision',''),
    @('FullConfidentialityAccepted',$true),@('ReviewCompleted',$true),@('NotAConfidentialityPass',$false),@('HardwareAccessed',$true),
    @('RecordedUtc',[datetime]::UtcNow.AddDays(1).ToString('o')),@('RecordedUtc',$testFreeze.AddMinutes(-1).ToString('o')))){
    $bad=$receipt|ConvertTo-Json|ConvertFrom-Json;$bad.($change[0])=$change[1]
    $rejected=$false;try{AssertDeferredReceipt $bad $testBuild $receipt.Profile '1.1.2.0' ('C'*64) ('D'*64) $runtimeHash 2 $testFreeze}catch{$rejected=$true}
    if(-not $rejected){throw ('Invalid confidentiality deferral was accepted: '+$change[0])};$checks++
}
$bad=$receipt|ConvertTo-Json|ConvertFrom-Json;$bad.PSObject.Properties.Remove('FullConfidentialityAccepted')
$rejected=$false;try{AssertDeferredReceipt $bad $testBuild $receipt.Profile '1.1.2.0' ('C'*64) ('D'*64) $runtimeHash 2 $testFreeze}catch{$rejected=$true}
if(-not $rejected){throw 'Missing non-acceptance flag was accepted.'};$checks++
$audit=@(& $builder -Mode SourceAudit)|Out-String|ConvertFrom-Json
if($audit.Status -cne 'SOURCE_AUDIT_PASS' -or $audit.Version -cne '1.1.2.0' -or $audit.Profile -cne 'PublicBeta-NoDriver-1.1.2-20261011' -or
    $audit.DefaultConfidentialityPolicy -cne 'Required' -or $audit.DocumentationAndIconFiles -ne 91 -or
    $audit.DriverInstallerIncluded -or $audit.InstallerCompiled -or $audit.InstallerExecuted -or $audit.ProductExecuted -or $audit.HardwareAccessed){throw 'New lane static audit failed.'};$checks++
$old=Join-Path $project 'build/public-beta-1.1.1-20261011-r52'
$oldRejected=$null
if(Test-Path -LiteralPath $old -PathType Container){
    try{& $builder -Mode Validate -BuildRoot $old|Out-Null;$oldRejected=$false}catch{
        if($_.Exception.Message -notlike 'Wrong actual build/version/profile*'){throw};$oldRejected=$true
    }
    if(-not $oldRejected){throw 'A shared historical profile was accepted as a new isolated lane.'};$checks++
}
if([string]::IsNullOrWhiteSpace($OutputRoot)){$OutputRoot=Join-Path $project ('build/qa-public-beta-no-driver-setup-'+[Guid]::NewGuid().ToString('N').Substring(0,10))}
$output=[IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Use a new isolated QA directory.'}
for($walk=$output;$walk.Length -ge $project.Length;$walk=[IO.Path]::GetDirectoryName($walk)){
    if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked QA directory is prohibited.'}
    if($walk -ieq $project){break}
}
foreach($p in $audit.InputHashes.PSObject.Properties){if((Get-FileHash -LiteralPath $p.Name -Algorithm SHA256).Hash -cne $p.Value){throw 'An audited input changed.'}}
$null=New-Item -ItemType Directory -Path $output
[ordered]@{Schema='HVX.PublicBetaNoDriver.SetupOffline/1';Status='PASS';CheckedUtc=[DateTime]::UtcNow.ToString('o');Checks=$checks;
    HistoricalSharedProfileRejected=$oldRejected;SourceAudit=$audit;Inputs=@([ordered]@{Path=$builder;SHA256=(Get-FileHash -LiteralPath $builder -Algorithm SHA256).Hash},
      [ordered]@{Path=$PSCommandPath;SHA256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash});
    InstallerCompiled=$false;InstallerExecuted=$false;ProductExecuted=$false;WorkerStarted=$false;SDKCalled=$false;HardwareAccessed=$false;PhysicalAcceptance=$false;
    Remaining=@('Final protected build and its actual freeze/maintenance/deployment/current-source evidence are required before staging; full confidentiality review remains deferred, not passed.',
      'This source audit does not execute ISCC or validate a Windows installation, OEM folder ACLs on disk, device drivers or hardware.')}|
    ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $output 'proof.json') -Encoding UTF8
Write-Output ('NO_DRIVER_SETUP_OFFLINE_PROOF='+$output)
Write-Output ('PASS checks='+$checks)
