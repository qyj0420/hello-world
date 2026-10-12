[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ModulesRoot,
      [Parameter(Mandatory=$true)][string]$BuildRoot,
      [Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$build=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
$modules=[IO.Path]::GetFullPath($ModulesRoot).TrimEnd('\')
$evidence=[IO.Path]::GetFullPath($EvidenceRoot).TrimEnd('\')
if(-not $build.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase) -or
   -not $modules.StartsWith($build+'\',[StringComparison]::OrdinalIgnoreCase) -or
   -not $evidence.StartsWith($build+'\',[StringComparison]::OrdinalIgnoreCase) -or
   $evidence.StartsWith($modules+'\',[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Protection evidence and original binaries must be outside the staged payload and within this candidate build.'
}
# Only build copies are transformed. SDKs, signed/pinned Task12 and Task17, and
# third-party libraries are never re-protected by this owned-calculation pass.
$plans=@(
    @{Profile='PdReview'; Paths=@('DataAnalysis/HVX.PdReview.V1.0.dll','PressureTest/HVX.PdReview.V1.0.dll')},
    @{Profile='DataBrowser'; Paths=@('DataBrowser/Xiongxin.OWTS.DataBrowser.dll')},
    @{Profile='ReportGenerator'; Paths=@('ReportGenerator/Xiongxin.OWTS.ReportGenerator.dll')},
    @{Profile='CalibrationTest'; Paths=@('CalibrationTest/Xiongxin.OWTS.CalibrationTest.dll')},
    @{Profile='CalibrationAcquisition'; Paths=@('CalibrationAcquisition/XIONGXIN.Calibration.Verification.exe')},
    @{Profile='PressureTest'; Paths=@('PressureTest/Xiongxin.OWTS.PressureTest.dll')},
    @{Profile='PressureRuntimePure'; Paths=@('PressureRuntime/Xiongxin.Pressure.Runtime.exe')},
    @{Profile='Task39InputPure'; Paths=@('Task39/Xiongxin.Task39.InputWorker.exe')},
    @{Profile='Task38LibraryPure'; Paths=@('Task38/HVX.OWTS.Task38.V1.0.38.dll')}
)
$records=New-Object 'System.Collections.Generic.List[object]'
foreach($plan in $plans){
    $paths=@($plan.Paths | ForEach-Object {Join-Path $modules $_})
    $inputHash=(Get-FileHash -LiteralPath $paths[0] -Algorithm SHA256).Hash
    foreach($path in $paths){if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $inputHash){throw 'Shared algorithm consumers differ before protection.'}}
    $folder=Join-Path $evidence $plan.Profile
    $protected=& (Join-Path $PSScriptRoot 'Protect-OwnedAlgorithms.ps1') -InputAssembly $paths[0] -OutputDirectory $folder -Profile $plan.Profile
    $receiptPath=Join-Path $folder 'receipt.json'
    $receipt=Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if($receipt.Schema -cne 'HVX.OwnedAlgorithmProtection/1' -or $receipt.InputSHA256 -cne $inputHash -or
       -not $receipt.ExistingMetadataPreserved -or -not $receipt.InputUnchanged -or $receipt.HardwareAccessed -or
       (Get-FileHash -LiteralPath $protected -Algorithm SHA256).Hash -cne $receipt.OutputSHA256) {throw 'Protection output does not match its conversion evidence.'}
    for($i=0;$i -lt $paths.Count;$i++){
        # Check again before replacing this candidate's original compiled copy.
        if((Get-FileHash -LiteralPath $paths[$i] -Algorithm SHA256).Hash -cne $inputHash){throw 'Staged input changed during protection.'}
        Copy-Item -LiteralPath $protected -Destination $paths[$i] -Force
        if((Get-FileHash -LiteralPath $paths[$i] -Algorithm SHA256).Hash -cne $receipt.OutputSHA256){throw 'Protected consumer copy mismatch.'}
        $records.Add([ordered]@{Path=('modules/'+$plan.Paths[$i]);Profile=$plan.Profile;InputSHA256=$inputHash;
            OutputSHA256=$receipt.OutputSHA256;ReceiptPath=$receiptPath.Substring($build.Length+1).Replace('\','/');
            ReceiptSHA256=(Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash})
    }
}
return $records.ToArray()
