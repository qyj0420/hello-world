[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$InputAssembly,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][ValidateSet('PdReview','DataBrowser','ReportGenerator','CalibrationTest','CalibrationAcquisition','HostPure','PressureTest','PressureRuntimePure','Task39InputPure','Task38LibraryPure')][string]$Profile
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$inputPath=[IO.Path]::GetFullPath($InputAssembly)
$outputPath=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
function Assert-ProjectBuildPath([string]$path) {
    if(-not $path.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Only project-local build copies may be protected.'}
    $walk=$path
    while($walk.Length -gt $project.Length){
        if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked build paths are prohibited.'}
        $walk=[IO.Path]::GetDirectoryName($walk)
    }
}
Assert-ProjectBuildPath $inputPath
Assert-ProjectBuildPath $outputPath
if(-not(Test-Path -LiteralPath $inputPath -PathType Leaf)){throw 'Input assembly is missing.'}
if(Test-Path -LiteralPath $outputPath){throw 'Use a fresh protection output directory.'}
$toolRoot='D:\PD_AI_LabVIEW2026_Project\Pulse400Hz_Extraction_DLL\tools\ConfuserEx-CLI-1.6.0'
$tool=Join-Path $toolRoot 'Confuser.CLI.exe'
if((Get-FileHash -LiteralPath $tool -Algorithm SHA256).Hash -cne '0790671F930BD1375FA06ECE29A776404CEE44695E083CACE46C905433644130'){throw 'Protection tool differs from the audited version.'}
$scopePath=Join-Path $PSScriptRoot 'owned-algorithm-protection.json'
$scope=Get-Content -LiteralPath $scopePath -Raw | ConvertFrom-Json
if($scope.Schema -cne 'HVX.OwnedAlgorithmProtectionScope/1'){throw 'Unknown protection scope schema.'}
$plan=$scope.Profiles.$Profile
$null=[Reflection.Assembly]::LoadFrom((Join-Path $toolRoot 'dnlib.dll'))
# dnlib reads PE/CLI metadata only. It never loads a product into the CLR or runs
# a module initializer, constructor, native SDK, hardware worker or model.
function Get-Contract($module){
    $rows=New-Object 'System.Collections.Generic.List[string]'
    $rows.Add('ASSEMBLY|'+$module.Assembly.FullName)
    foreach($t in $module.GetTypes()){
        $rows.Add('T|'+$t.FullName+'|'+$t.Attributes+'|'+$t.BaseType)
        foreach($m in $t.Methods){
            $params=@($m.ParamDefs | ForEach-Object {$_.Sequence.ToString()+':'+$_.Name+':'+$_.Attributes}) -join ','
            $rows.Add('M|'+$m.FullName+'|'+$m.Attributes+'|'+$params)
        }
        # Constants protection may remove compiler-generated RVA data fields.
        # These are not user types, serializer fields or reflection contracts.
        foreach($f in $t.Fields){if($t.FullName -cne '<PrivateImplementationDetails>'){$rows.Add('F|'+$f.FullName+'|'+$f.Attributes)}}
        foreach($p in $t.Properties){$rows.Add('P|'+$p.FullName+'|'+$p.Attributes)}
        foreach($e in $t.Events){$rows.Add('E|'+$e.FullName+'|'+$e.Attributes)}
    }
    foreach($r in $module.Resources){$rows.Add('R|'+$r.Name+'|'+$r.Attributes)}
    return @($rows.ToArray() | Sort-Object)
}
$before=[dnlib.DotNet.ModuleDefMD]::Load($inputPath)
try{
    if($before.Assembly.Name.String -cne $plan.AssemblyName -or $before.Assembly.PublicKey.Data.Length -ne 0){throw 'Wrong assembly identity or strong-named input; this pipeline never reads signing keys.'}
    $allTypes=@($before.GetTypes())
    $selected=New-Object 'System.Collections.Generic.List[string]'
    $selectedMethods=New-Object 'System.Collections.Generic.List[string]'
    $methodPatterns=New-Object 'System.Collections.Generic.List[string]'
    $exemptTypes=@()
    if($null -ne $plan.PSObject.Properties['UnprotectedContractTypes']){$exemptTypes=@($plan.UnprotectedContractTypes)}
    $targets=New-Object 'System.Collections.Generic.List[object]'
    $rootTypes=@($plan.Types)
    if($null -ne $plan.PSObject.Properties['AdditionalTypes']){$rootTypes+=@($plan.AdditionalTypes)}
    foreach($rootType in $rootTypes){
        $found=@($allTypes | Where-Object {$_.FullName -ceq $rootType})
        if($found.Count -ne 1){throw ('Exact protection target missing or duplicated: '+$rootType)}
        $family=@($allTypes | Where-Object {$_.FullName -ceq $rootType -or $_.FullName.StartsWith($rootType+'/',[StringComparison]::Ordinal)})
        $methods=0
        foreach($t in $family){
            if($t.FullName -cin $exemptTypes){continue}
            $count=@($t.Methods | Where-Object {$_.HasBody}).Count
            if($count -gt 0){$selected.Add($t.FullName);$methods+=$count;foreach($method in $t.Methods){if($method.HasBody){$selectedMethods.Add($method.FullName)}}}
        }
        if($methods -eq 0){throw ('Protection target has no managed method bodies: '+$rootType)}
        $targets.Add([ordered]@{Type=$rootType;ManagedBodies=$methods;FamilyTypes=$family.Count})
    }
    if($null -ne $plan.PSObject.Properties['Methods']){
        foreach($group in $plan.Methods){
            $type=@($allTypes|Where-Object {$_.FullName -ceq $group.Type})
            if($type.Count -ne 1){throw ('Missing mixed-type method owner: '+$group.Type)}
            foreach($name in $group.Names){
                $found=@($type[0].Methods|Where-Object {$_.Name.String -ceq $name -and $_.HasBody})
                if($found.Count -eq 0){throw ('Missing required calculation method: '+$group.Type+'::'+$name)}
                foreach($method in $found){$selectedMethods.Add($method.FullName)}
                $methodPatterns.Add("decl-type('"+$group.Type+"') and name('"+$name+"')")
            }
        }
    }
    $beforeContract=Get-Contract $before
}finally{$before.Dispose()}
$sourceHash=(Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash
$null=New-Item -ItemType Directory -Path (Join-Path $outputPath 'input')
$filename=[IO.Path]::GetFileName($inputPath)
Copy-Item -LiteralPath $inputPath -Destination (Join-Path $outputPath ('input\'+$filename))
$base=[Security.SecurityElement]::Escape((Join-Path $outputPath 'input'))
$dest=[Security.SecurityElement]::Escape((Join-Path $outputPath 'protected'))
$rules=@($selected | Sort-Object -Unique | ForEach-Object {
    if($_ -match "['\r\n]"){throw 'Unsafe metadata name in scope.'}
    # dnlib uses '/' for nested names. Confuser's declaring-type match uses the
    # same dnlib TypeDef.FullName, including compiler-generated nested bodies.
    $pattern=[Security.SecurityElement]::Escape("decl-type('"+$_+"')")
    ' <rule pattern="'+$pattern+'" preset="none" inherit="false"><protection id="ctrl flow"/><protection id="constants"/><protection id="anti tamper"/></rule>'
})
foreach($pattern in $methodPatterns){$rules+=' <rule pattern="'+[Security.SecurityElement]::Escape($pattern)+'" preset="none" inherit="false"><protection id="ctrl flow"/><protection id="constants"/><protection id="anti tamper"/></rule>'}
$rules=$rules -join "`r`n"
$moduleName=[Security.SecurityElement]::Escape($filename)
$probePath=[Security.SecurityElement]::Escape([IO.Path]::GetDirectoryName($inputPath))
$config=@"
<project outputDir="$dest" baseDir="$base" seed="HVX-owned-methods-v1" debug="false" xmlns="http://confuser.codeplex.com">
$rules
 <module path="$moduleName"/>
 <probePath>$probePath</probePath>
</project>
"@
$configFile=Join-Path $outputPath 'protection.crproj'
$config | Set-Content -LiteralPath $configFile -Encoding UTF8
$log=@(& $tool -n $configFile 2>&1 | ForEach-Object {$_.ToString()})
$code=$LASTEXITCODE
$log | Set-Content -LiteralPath (Join-Path $outputPath 'protection.log') -Encoding UTF8
if($code -ne 0){throw ('Protection failed; no unprotected fallback: '+$outputPath)}
$result=Join-Path $outputPath ('protected\'+$filename)
if(-not(Test-Path -LiteralPath $result -PathType Leaf)){throw 'Protection produced no assembly.'}
$after=[dnlib.DotNet.ModuleDefMD]::Load($result)
try{
    $afterContract=Get-Contract $after
    $methodMap=@{}
    foreach($type in $after.GetTypes()){foreach($method in $type.Methods){$methodMap[$method.FullName]=$method}}
    $methodCoverage=@(foreach($id in ($selectedMethods|Sort-Object -Unique)){
        if(-not $methodMap.ContainsKey($id)){throw ('Protected method missing: '+$id)}
        $method=$methodMap[$id];$rva=[uint32]$method.RVA
        $section=@($after.MetaData.PEImage.ImageSectionHeaders|Where-Object {$rva -ge [uint32]$_.VirtualAddress -and $rva -lt ([uint32]$_.VirtualAddress+[uint32]$_.VirtualSize)})
        $protected=$section.Count -eq 1 -and $section[0].DisplayName -notin @('.text','.rsrc','.reloc')
        if(-not $protected){throw ('Expected method was not moved into the encrypted section: '+$id)}
        [ordered]@{Method=$id;RVA=$rva;EncryptedSection=$true}
    })
}finally{$after.Dispose()}
$afterSet=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
foreach($line in $afterContract){$null=$afterSet.Add($line)}
$missing=@($beforeContract | Where-Object {-not $afterSet.Contains($_)})
$beforeContract | Set-Content -LiteralPath (Join-Path $outputPath 'metadata-before.txt') -Encoding UTF8
$afterContract | Set-Content -LiteralPath (Join-Path $outputPath 'metadata-after.txt') -Encoding UTF8
if($missing.Count -gt 0){$missing|Set-Content -LiteralPath (Join-Path $outputPath 'metadata-missing.txt') -Encoding UTF8;throw 'Existing metadata contract changed; reject protected copy.'}
$unchanged=(Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash -ceq $sourceHash
if(-not $unchanged){throw 'Build input changed during protection.'}
$resultHash=(Get-FileHash -LiteralPath $result -Algorithm SHA256).Hash
if($resultHash -ceq $sourceHash){throw 'Protection was a no-op.'}
$receipt=[ordered]@{
    Schema='HVX.OwnedAlgorithmProtection/1';Profile=$Profile;Input=$inputPath;InputSHA256=$sourceHash;Output=$result;OutputSHA256=$resultHash
    ScopeSHA256=(Get-FileHash -LiteralPath $scopePath -Algorithm SHA256).Hash
    ToolSHA256=(Get-FileHash -LiteralPath $tool -Algorithm SHA256).Hash;Protection='ConfuserEx 1.6.0; method encryption, control flow, constants; names retained'
    ExactRootCount=$targets.Count;SelectedTypeCount=@($selected | Sort-Object -Unique).Count;Targets=@($targets.ToArray())
    Methods=$methodCoverage;EncryptedMethodCount=$methodCoverage.Count;UnprotectedContractTypes=$exemptTypes
    ExistingMetadataRows=$beforeContract.Count;ExistingMetadataPreserved=$true;InputUnchanged=$true
    HardwareAccessed=$false;ProductEntryExecuted=$false;ModelInference=$false
    FullConfidentialityAccepted=$false;CompatibilityRegressionPassed=$false
    RequiredNext='Run scoped numerical/serialization/UI compatibility and static decompilation against these exact output bytes; do not treat this receipt alone as full product approval.'
}
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputPath 'receipt.json') -Encoding UTF8
Write-Output $result
