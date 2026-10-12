param([Parameter(Mandatory=$true)][string]$BuildRoot,[Parameter(Mandatory=$true)][string]$QaRoot)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build=[IO.Path]::GetFullPath($BuildRoot)
$qa=[IO.Path]::GetFullPath($QaRoot)
if(-not $build.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase) -or
   -not $qa.StartsWith($build+'\qa\',[StringComparison]::OrdinalIgnoreCase)){throw 'Publisher fixture must remain under this build QA.'}
New-Item -ItemType Directory -Path $qa -Force | Out-Null
$cases=@(
    @{Name='hardware-true';HardwareAccessed=$true;PhysicalAcceptance=$false},
    @{Name='physical-true';HardwareAccessed=$false;PhysicalAcceptance=$true},
    @{Name='missing';HardwareAccessed=$null;PhysicalAcceptance=$null},
    @{Name='string-false';HardwareAccessed='false';PhysicalAcceptance='false'},
    @{Name='zero';HardwareAccessed=0;PhysicalAcceptance=0},
    @{Name='bool-false';HardwareAccessed=$false;PhysicalAcceptance=$false;InputFlag=$true;Expected='complete passing 1.1.9 regression'},
    @{Name='hardware-only-missing';HardwareAccessed=$null;PhysicalAcceptance=$false},
    @{Name='hardware-only-string';HardwareAccessed='false';PhysicalAcceptance=$false},
    @{Name='hardware-only-zero';HardwareAccessed=0;PhysicalAcceptance=$false},
    @{Name='physical-only-missing';HardwareAccessed=$false;PhysicalAcceptance=$null},
    @{Name='physical-only-string';HardwareAccessed=$false;PhysicalAcceptance='false'},
    @{Name='physical-only-zero';HardwareAccessed=$false;PhysicalAcceptance=0},
    @{Name='inputs-missing';HardwareAccessed=$false;PhysicalAcceptance=$false;Expected='complete frozen build/test input record'},
    @{Name='inputs-string';HardwareAccessed=$false;PhysicalAcceptance=$false;InputFlag='true';Expected='complete frozen build/test input record'},
    @{Name='inputs-number';HardwareAccessed=$false;PhysicalAcceptance=$false;InputFlag=1;Expected='complete frozen build/test input record'},
    @{Name='inputs-false';HardwareAccessed=$false;PhysicalAcceptance=$false;InputFlag=$false;Expected='complete frozen build/test input record'}
)
$checks=0
foreach($case in $cases) {
    $fixture=[ordered]@{Status='PASS';StartAtTest='';Tests=@();ProductBinaries=@()}
    if($case.Name -ne 'missing') {
        $fixture.Add('HardwareAccessed',$case.HardwareAccessed)
        $fixture.Add('PhysicalAcceptance',$case.PhysicalAcceptance)
    }
    if($case.ContainsKey('InputFlag')) {
        $fixture.Add('BuildTestInputsUnchanged',$case.InputFlag)
        # This deliberately incomplete proof must fail before any payload write.
        # A typed true exercises the next full-regression guard independently.
        $fixture.Add('BuildTestInputs',@(@{Path='fixture-only';SHA256=('0'*64)}))
    }
    $path=Join-Path $qa ($case.Name+'.json')
    $fixture | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path -Encoding UTF8
    $outputName='PublisherSafety-rejected-'+[Guid]::NewGuid().ToString('N')
    $rejection=''
    try {
        & (Join-Path $project 'tools\Publish-Verification119.ps1') -BuildRoot $build -VerificationPath $path -OutputName $outputName -IncludeModules | Out-Null
    } catch { $rejection=$_.Exception.Message }
    $expected=if($case.ContainsKey('Expected')){$case.Expected}else{'explicit hardware-free'}
    if($rejection -notlike ('*'+$expected+'*')){throw ('Wrong rejection for '+$case.Name+': '+$rejection)}
    $checks++
    if(Test-Path -LiteralPath (Join-Path $project ('build\'+$outputName))){throw 'Invalid proof created a delivery directory.'}
    $checks++
}
Write-Output ('PUBLISHER_SAFETY_PASS='+$checks+';HARDWARE=False;DELIVERY_WRITES=0')
