param([Parameter(Mandatory=$true)][string]$Name,[Parameter(Mandatory=$true)][string]$OriginalHash)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if($Name -cnotin @('BenchModels.cs','PicoCaptureConfiguration.cs','LocationRangeAdvisor.cs','LocationRangeWindow.cs','RangeDecision.cs')){throw 'Unknown pure pressure contract.'}
$directory=Join-Path $project 'modules-src\PressureTest\vendor\Task39'
$path=Join-Path $directory $Name
$expected=$OriginalHash
if($Name -ceq 'BenchModels.cs'){
 $adaptation=Get-Content -LiteralPath (Join-Path $directory 'pressure-contract-adaptation.json') -Raw|ConvertFrom-Json
 if($adaptation.Schema -cne 'HVX.Pressure.Task39ContractAdaptation/1' -or $adaptation.Path -cne $Name -or
  $OriginalHash -cne '701D8B24A26DA1B8A35EDA41C512AD33FEE65155FA7CE0CE87867D077239D4C6' -or
  $adaptation.OriginalSHA256 -cne $OriginalHash -or
  $adaptation.AdaptedSHA256 -cne 'C6F8BD65144A1DCEA255F8383CB4AB0C61868174688D96762877F02958C1B868'){throw 'Unreviewed pressure data contract adaptation.'}
 $expected=$adaptation.AdaptedSHA256
}
if($Name -ceq 'LocationRangeAdvisor.cs'){
 # Reviewed DTO-only parity with the existing factory XML producer; advisor
 # arithmetic is unchanged. New fields default false/0 for legacy records.
 if($OriginalHash -cne '68C45BC2517735EEA2D04C12E9C5E0F386DC54624375104935F30FB81A4181C5'){throw 'Unexpected original range advisor contract.'}
 $expected='57F6C7CE5FC5E5C87011B227E02119BBF8E08EE086677EE60F0C73A9961F6C09'
}
if((Get-FileHash -LiteralPath $path).Hash -cne $expected){throw ('Pressure data contract changed: '+$Name)}
return $path
