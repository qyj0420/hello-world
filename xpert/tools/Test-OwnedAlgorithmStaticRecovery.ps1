[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ProtectionDirectory,
      [string]$PackageAssembly='',
      [ValidatePattern('^static-recovery(?:-[A-Za-z0-9-]+)?$')][string]$OutputTag='static-recovery')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$directory=[IO.Path]::GetFullPath($ProtectionDirectory).TrimEnd('\')
if(-not $directory.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an isolated project build receipt.'}
$receipt=Get-Content -LiteralPath (Join-Path $directory 'receipt.json') -Raw|ConvertFrom-Json
if($receipt.Schema -cne 'HVX.OwnedAlgorithmProtection/1'){throw 'Unknown receipt.'}
$paths=@{Input=(Join-Path $directory ('input\'+[IO.Path]::GetFileName($receipt.Input)));Output=$receipt.Output}
foreach($side in @('Input','Output')){if((Get-FileHash -LiteralPath $paths[$side]).Hash -cne $receipt.($side+'SHA256')){throw 'Protection snapshot/output hash mismatch.'}}
if($PackageAssembly){
    $package=[IO.Path]::GetFullPath($PackageAssembly)
    if(-not $package.StartsWith($root+'\build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Only a candidate package copy may be inspected.'}
    if((Get-FileHash -LiteralPath $package).Hash -cne $receipt.OutputSHA256){throw 'Candidate consumer differs from protected output.'}
    $paths.Output=$package
}
$samples=@{
    HostPure=@('HVX.SignalProcessing.PdLinearHighPass','Xiongxin.OWTS.Data.LocationCycleReference')
    PressureTest=@('HVX.Controls.PressureTestAnalysis','Xiongxin.ZeroBench.LocationRangeAdvisor')
    PressureRuntimePure=@('Xiongxin.FactoryTest.FactoryAnalysis','Xiongxin.FactoryTest.FactoryPdSignal')
    Task39InputPure=@('Xiongxin.ZeroBench.SignalAnalysis','Xiongxin.ZeroBench.LocationRangeAdvisor')
    Task38LibraryPure=@('HVX.OWTS.Task38.CalibrationWindowing','HVX.OWTS.Task38.CalibrationIncidentQuality')
    PdReview=@('HVX.PdReview.ReviewCapacitorAnalysis','HVX.PD.LocationFiltering.LocationClusterFilterEngine','HVX.PD.LocationFiltering.LocationClusterStrengthFilterB')
    DataBrowser=@('HVX.SignalProcessing.PdLinearHighPass','CalibrationWaveform.CalibrationWaveformControl')
    ReportGenerator=@('HVX.Location.EvidenceCore','HVX.SignalProcessing.PdLinearHighPass')
    CalibrationTest=@('HVX.Controls.TdrReflectionAnalyzer','HVX.OWTS.Task38.CalibrationWindowSettings')
    CalibrationAcquisition=@('Xiongxin.Calibration.AcquisitionPlanning','HVX.OWTS.Task38.CalibrationWindowSettings')
}
if(-not $samples.ContainsKey($receipt.Profile)){throw 'No reviewed static sample profile.'}
$cli='D:\PD_AI_LabVIEW2026_Project\Pulse400Hz_Extraction_DLL\tools\ilspycmd\ilspycmd.dll'
$dotnet=(Get-Command dotnet -ErrorAction Stop).Source
$helperTokens=$null;$helperErrors=$null
$helperAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Internal12-Calibration.ps1'),[ref]$helperTokens,[ref]$helperErrors)
if($helperErrors.Count){throw 'Cannot parse bounded execution helpers.'}
foreach($name in @('Assert-RegressionUnlinkedPath','Invoke-RegressionExecutable')){
    $nodes=@($helperAst.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -eq $name)
    if($nodes.Count -ne 1){throw 'Missing/ambiguous bounded execution helper.'}
    . ([ScriptBlock]::Create($nodes[0].Extent.Text))
}
$output=Join-Path $directory $OutputTag
if(Test-Path -LiteralPath $output){throw 'Static evidence already exists; retain it without overwriting.'}
$null=New-Item -ItemType Directory -Path $output
$records=New-Object 'System.Collections.Generic.List[object]'
foreach($type in $samples[$receipt.Profile]){
    if($type -cnotin @($receipt.Targets|ForEach-Object {$_.Type})){throw 'Static target is not an explicitly protected root.'}
    foreach($side in @('Input','Output')){
        $run=Invoke-RegressionExecutable -Executable $dotnet -Arguments @($cli,'--disable-updatecheck','-t',$type,$paths[$side]) -LogPrefix (Join-Path $output ($side+'-'+$type)) -TimeoutMilliseconds 45000
        $file=$run.StdoutPath;$stderr=$run.StderrPath
        if($run.TimedOut -or -not $run.ProcessExitConfirmed -or $run.ExecutionFailure){throw ('Static decompiler did not complete: '+$type)}
        $text=Get-Content -LiteralPath $file -Raw
        if($null -eq $text){$text=''}
        # A non-empty dnlib Body or decompiler file can be random encrypted bytes
        # decoded as invalid IL. Never report it as recovered source merely for
        # existing; conversely one failure is not proof of absolute secrecy.
        $failures=[regex]::Matches($text,'(?i)InvalidMethodBody|Unknown opcode|Stack underflow|Error decoding local variables|Error decoding method|Could not decompile|Invalid branch target|ILSpy crashed|InvalidProgramException|BadImageFormatException').Count
        $records.Add([ordered]@{Type=$type;Side=$side;AssemblySHA256=(Get-FileHash -LiteralPath $paths[$side]).Hash;ExitCode=$run.ExitCode;ProcessExitConfirmed=$run.ProcessExitConfirmed;TimedOut=$run.TimedOut;Bytes=(Get-Item -LiteralPath $file).Length;OutputSHA256=(Get-FileHash -LiteralPath $file).Hash;InvalidIlDiagnosticCount=$failures;OutputFile=$file;StderrFile=$stderr})
        if($side -eq 'Input' -and ($run.ExitCode -ne 0 -or $text.Length -lt 100)){throw 'Unprotected reference did not decompile; the paired check is inconclusive.'}
    }
}
[ordered]@{Schema='HVX.OwnedAlgorithmStaticRecovery/1';Profile=$receipt.Profile;ProtectedSHA256=$receipt.OutputSHA256;PackageHashMatched=[bool]$PackageAssembly;ILSpySHA256=(Get-FileHash -LiteralPath $cli).Hash;Samples=$records.ToArray();ManualReviewRequired=$true;FullConfidentialityAccepted=$false;ProductExecuted=$false;HardwareAccessed=$false;ModelDecoded=$false;Limitation='Paired static decompiler sampling only. Inspect whether actual calculation bodies remain, not only diagnostic counts. Runtime memory recovery and native reverse engineering are not certified.'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
Write-Output ('STATIC_RECOVERY_EVIDENCE='+$output)
