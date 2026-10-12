# Pure metadata checks. Callers must hash-check the referenced receipts/files
# before calling this function. No product, assembly loading, device or I/O here.
function Assert-ReleaseMaintenanceCompilation($Build,$Modules,$Ui) {
    foreach($entry in @(@($Build,'ProtectedRelease'),@($Build,'MaintenancePasswordEnabled'),@($Build,'V30BatchValidationProfile'),@($Modules,'ProtectedRelease'),@($Modules,'MaintenancePasswordEnabled'),@($Ui,'ProtectedRelease'),@($Ui,'MaintenancePasswordEnabled'))){
        $value=$entry[0].PSObject.Properties[$entry[1]]
        if($null -eq $value -or $value.Value -isnot [bool] -or $value.Value -ne $true){throw ('Missing or false release maintenance evidence: '+$entry[1])}
    }
    if($Build.Configuration -cne 'Release' -or $Modules.Configuration -cne 'Release' -or $Ui.Configuration -cne 'Release' -or
       $Build.ConfigurationProfile -cne 'Validation-V30-Batch-20261008' -or $Build.PressureStandalone -isnot [bool] -or $Build.PressureStandalone -ne $false -or
       $Build.HybridArchitecture.Included -isnot [bool] -or $Build.HybridArchitecture.Included -ne $true){throw 'Only the complete Release maintenance profile can be published.'}
    if($Build.HostCompilation.Schema -cne 'HVX.HostCompilation/1' -or $Ui.Schema -cne 'HVX.UiModuleCompilation/1' -or
       @($Build.HostCompilation.CompilerDefines|Where-Object {$_ -ceq 'MAINTENANCE_PASSWORD_GUARD'}).Count -ne 1 -or
       @($Ui.CompilerDefines|Where-Object {$_ -ceq 'MAINTENANCE_PASSWORD_GUARD'}).Count -ne 1){throw 'Actual host/UI compiler define evidence is missing or ambiguous.'}
    $hostTransform=$Build.OwnedHostAlgorithmProtection
    if($Build.HostCompilation.OutputSHA256 -notmatch '^[A-Fa-f0-9]{64}$' -or
       $hostTransform.InputSHA256 -cne $Build.HostCompilation.OutputSHA256 -or $hostTransform.OutputSHA256 -cne $Build.ExecutableSha256){throw 'Guarded host compilation is not bound to its protected output.'}
    $required=@('ProjectCreation','ProjectManagement','DataBrowser','DataAnalysis','ReportGenerator','PressureTest','CalibrationTest')
    if(@($Ui.Modules).Count -ne $required.Count){throw 'Complete guarded UI compilation evidence is required.'}
    foreach($name in $required){
        $compiled=@($Ui.Modules|Where-Object {$_.Module -ceq $name})
        if($compiled.Count -ne 1 -or $compiled[0].SHA256 -notmatch '^[A-Fa-f0-9]{64}$'){throw ('Missing/ambiguous UI compilation: '+$name)}
        $file=[string]$compiled[0].File
        if($file -notmatch ('^'+[regex]::Escape($name)+'/[^/\\:]+\.dll$')){throw 'Invalid UI compile receipt file.'}
        $path='modules/'+$file
        $output=@($Modules.Modules|Where-Object {([string]$_.Path).Replace('\','/') -ceq $path})
        if($output.Count -ne 1){throw ('Missing/ambiguous staged UI: '+$name)}
        $transform=@($Modules.OwnedAlgorithmProtection|Where-Object {([string]$_.Path).Replace('\','/') -ceq $path})
        if($transform.Count -eq 0){
            if($output[0].Sha256 -cne $compiled[0].SHA256){throw ('Unguarded replacement of compiled UI: '+$name)}
        }elseif($transform.Count -eq 1){
            if($transform[0].InputSHA256 -cne $compiled[0].SHA256 -or $transform[0].OutputSHA256 -cne $output[0].Sha256){throw ('UI protection hash chain mismatch: '+$name)}
        }else{throw ('Ambiguous UI protection: '+$name)}
    }
}
