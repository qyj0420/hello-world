param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $output.StartsWith($project+'\build\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Choose a new test directory beneath project/build.'}
for($walk=$output;$walk;$walk=[IO.Path]::GetDirectoryName($walk)){
 if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked test paths are prohibited.'}
}
$null=New-Item -ItemType Directory -Path $output
$checks=0
function Check([bool]$Passed,[string]$Description){if(-not $Passed){throw $Description};$script:checks++}
$installerPath=Join-Path $project 'installer/XIONGXIN-V30-Validation.iss'
$buildPath=Join-Path $project 'Build.ps1'
$sourcePaths=@($buildPath,$installerPath,(Join-Path $project 'installer/InteractiveInstall.iss'),(Join-Path $project 'tests/NeutralExecutableResourceSmoke.cs'))
$sourcePaths+=@(Get-ChildItem -LiteralPath (Join-Path $project 'src') -Filter '*.cs' -File | ForEach-Object FullName)
$hashes=@{};foreach($path in $sourcePaths){$hashes[$path]=(Get-FileHash -LiteralPath $path).Hash}
$installer=Get-Content -LiteralPath $installerPath -Raw
$buildText=Get-Content -LiteralPath $buildPath -Raw
$interaction=Get-Content -LiteralPath (Join-Path $project 'installer/InteractiveInstall.iss') -Raw
$setup=($installer -split '\[Setup\]',2)[1] -split '\[Languages\]',2 | Select-Object -First 1
Check ($setup -notmatch '(?im)^(AppName|AppPublisher|DefaultDirName|DefaultGroupName|SetupIconFile)=.*(XIONGXIN|雄新|KEHUI|xiongxin)') 'Neutral visible installer identity'
Check ($setup -match '(?m)^AppId=\{\{B6085D87-B11E-4AA0-9E49-CE1010300709\}') 'Keep isolated installer identity unchanged'
Check ($setup -match '(?m)^PrivilegesRequired=admin') 'Preserve administrator consent'
Check ($setup -match '(?m)^DefaultDirName=\{autopf\}\\HV-DAC Xpert\\Validation V30') 'Neutral isolated install path'
Check ($setup -notmatch '(?m)^SetupIconFile=') 'Neutral default installer icon'
Check ($installer -notmatch '(?im)^Name:.*IconFilename:.*(256\.ico|xiongxin|kehui)') 'No branded shortcut override'
Check ($installer -match 'Tasks: desktopicon') 'Desktop shortcut remains opt-in'
Check ($installer -match '#include "InteractiveInstall.iss"') 'Interactive installation remains included'
Check ($interaction -match 'not WizardSilent' -and $interaction -match 'MB_DEFBUTTON2') 'No silent installation or automatic consent'
Check ($buildText -notmatch '/win32icon:') 'Desktop compile does not implicitly select a customer icon'
Check ($buildText.Contains(',Brand.ico') -and $buildText.Contains(',Brand.png')) 'OEM reference assets retained, not destroyed'
Check ($buildText.Contains('V30 validation is unavailable')) 'Existing pressure source gate retained'

# Compile an isolated desktop shell only; do not stage SDKs, run the product,
# invoke Inno Setup, install anything or create user shortcuts.
$shellOutput=Join-Path $output 'shell'
$buildLog=@(& $buildPath -SkipModules -OutputDirectory $shellOutput 2>&1 | ForEach-Object {$_.ToString()})
$buildLog | Set-Content -LiteralPath (Join-Path $output 'compile.log') -Encoding UTF8
$desktop=Join-Path $shellOutput 'Xiongxin.OWTS.Desktop.exe'
Check (Test-Path -LiteralPath $desktop -PathType Leaf) 'Isolated neutral shell compiled'
$test=Join-Path $output 'NeutralExecutableResourceSmoke.exe'
& (Join-Path $PSScriptRoot 'compiler/csc.exe') /nologo /target:exe /platform:x64 /r:System.dll ('/out:'+$test) (Join-Path $project 'tests/NeutralExecutableResourceSmoke.cs')
if($LASTEXITCODE -ne 0){throw 'Resource test compilation failed.'}
$testOutput=@(& $test $desktop 2>&1 | ForEach-Object {$_.ToString()})
$testExit=$LASTEXITCODE
$testOutput | Set-Content -LiteralPath (Join-Path $output 'resources.log') -Encoding UTF8
Check ($testExit -eq 0 -and ($testOutput -join "`n").Contains('NEUTRAL_EXECUTABLE_RESOURCES_PASS=')) 'Actual executable metadata/resource inspection'
foreach($path in $sourcePaths){if((Get-FileHash -LiteralPath $path).Hash -ne $hashes[$path]){throw ('Source changed during test: '+$path)}}
$receipt=[ordered]@{Schema='HVX.NeutralPackaging.Source/1';Status='PASS';Checks=$checks;ResourceResult=$testOutput;ExecutableSHA256=(Get-FileHash -LiteralPath $desktop).Hash;SourceHashes=$hashes;ProductExecuted=$false;HardwareAccessed=$false;InstallerBuilt=$false;InstalledStateChanged=$false;Scope='Current V30 installer draft and desktop Win32 icon only; not complete Task41/OEM suite acceptance';CheckedUtc=[datetime]::UtcNow.ToString('o')}
$receipt|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
[pscustomobject]$receipt|Select-Object Status,Checks,ResourceResult,InstallerBuilt,HardwareAccessed
