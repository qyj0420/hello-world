param([Parameter(Mandatory=$true)][string]$ModulePath,[Parameter(Mandatory=$true)][string]$QaRoot,[Parameter(Mandatory=$true)][string]$PopplerDirectory)
$ErrorActionPreference='Stop'
$module=[IO.Path]::GetFullPath($ModulePath)
$qa=[IO.Path]::GetFullPath($QaRoot)
$assembly=[Reflection.Assembly]::LoadFrom($module)
$service=$assembly.GetType('HVX.ReportGenerator.ReportDocumentService',$true)
$convert=$service.GetMethod('TryConvertWithOfficeOrWps',[Reflection.BindingFlags]'Static,NonPublic')
foreach($name in @('no-appendix','with-appendix')) {
    $docx=Join-Path $qa ($name+'.docx')
    $pdf=Join-Path $qa ($name+'.pdf')
    if(Test-Path -LiteralPath $pdf){throw 'Do not overwrite prior report QA.'}
    [object[]]$arguments=@([string]$docx,[string]$pdf,[bool]$false,$null,$null)
    $success=$convert.Invoke($null,$arguments)
    if(-not $success){throw ('Office/WPS conversion unavailable: '+$arguments[4])}
    Write-Output ('PROVIDER='+$arguments[3])
    & (Join-Path $PopplerDirectory 'pdfinfo.exe') $pdf
    & (Join-Path $PopplerDirectory 'pdftoppm.exe') -r 90 -png $pdf (Join-Path $qa ($name+'-page'))
    if($LASTEXITCODE -ne 0){throw 'PDF rendering failed.'}
}
