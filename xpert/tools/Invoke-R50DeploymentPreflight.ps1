param([string]$ApplicationDirectory, [string]$PackageDirectory=$PSScriptRoot)
$ErrorActionPreference='Stop'

# Read-only: no product entry, connector, driver, SDK or device operation. The
# document predicate below calls the exact delivered host's pure XML validator.
function Read-R50SmallText([string]$Path,[long]$Limit) {
 $info=Get-Item -LiteralPath $Path -ErrorAction Stop
 if($info.PSIsContainer -or $info.Length -gt $Limit){throw 'Deployment document is oversized or not a file.'}
 return [IO.File]::ReadAllText($info.FullName)
}
function Get-R50ParentGate([Reflection.Assembly]$Assembly,[string]$AppDirectory,[string]$StatePath) {
 $manifest=Join-Path $AppDirectory 'private\parent-manifest.xml'
 $helper=Join-Path $AppDirectory 'private\ParentHost.exe'
 $footprint=(Test-Path -LiteralPath $manifest) -or (Test-Path -LiteralPath $helper) -or (Test-Path -LiteralPath $StatePath)
 if(-not $footprint){return [pscustomobject]@{Passed=$true;Mode='NoParentFootprint';Reason='';FirstBindingProvided=$false}}
 if(-not(Test-Path -LiteralPath $manifest -PathType Leaf)){return [pscustomobject]@{Passed=$false;Mode='ParentFootprint';Reason='Existing installation state requires the matching installation manifest.';FirstBindingProvided=$false}}
 if(-not(Test-Path -LiteralPath $StatePath -PathType Leaf)){return [pscustomobject]@{Passed=$false;Mode='ParentFootprint';Reason='Installation completion state is missing.';FirstBindingProvided=$false}}
 $type=$Assembly.GetType('Xiongxin.OWTS.Desktop.FirstHardwareProvisioning',$true)
 $method=$type.GetMethod('ValidateParentStateDocuments',[Reflection.BindingFlags]'NonPublic,Static')
 if($null -eq $method){throw 'The delivered pure installation validator is unavailable.'}
 $arguments=@((Read-R50SmallText $manifest (4MB)),(Read-R50SmallText $StatePath 16384),'',$false)
 $reason=[string]$method.Invoke($null,$arguments)
 return [pscustomobject]@{Passed=($reason.Length -eq 0);Mode='ExistingParentDocuments';Reason=$reason;FirstBindingProvided=$false}
}
function Test-R50RuntimeFiles([string]$AppDirectory,[object[]]$Files) {
 foreach($entry in $Files){
  $relative=[string]$entry.Path
  if([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative.Replace('/','\').Split('\') -contains '..'){throw 'Invalid delivery path.'}
  $file=[IO.Path]::GetFullPath((Join-Path $AppDirectory $relative))
  if(-not $file.StartsWith($AppDirectory.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Delivery path escaped its root.'}
  if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw ('Missing test file: '+$relative)}
  for($walk=$file;$walk.Length -ge $AppDirectory.Length;$walk=[IO.Path]::GetDirectoryName($walk)){
   if((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked deployment paths are not accepted.'}
  }
  if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne [string]$entry.SHA256){throw ('Different file; do not overwrite the old installation blindly: '+$relative)}
 }
}
function Invoke-R50Preflight([string]$AppDirectory,[string]$PackageRoot,[string]$StateFile) {
 $applicationRoot=[IO.Path]::GetFullPath($AppDirectory).TrimEnd('\')
 $packageRoot=[IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')
 $delivery=Read-R50SmallText (Join-Path $packageRoot 'delivery-manifest.json') (2MB)|ConvertFrom-Json
 # ASCII source also works in Windows PowerShell 5.1 on non-Chinese systems.
 $expectedProgram=([char]0x516c).ToString()+[char]0x6d4b+[char]0x7248+'V1.1.1.exe'
 if($delivery.Schema -ne 'HVX.R50.TestDelivery/1' -or $delivery.ProgramFileName -cne $expectedProgram){throw 'Unexpected test delivery manifest.'}
 $programEntries=@($delivery.RuntimeFiles|Where-Object Path -eq $delivery.ProgramFileName)
 if($programEntries.Count -ne 1 -or $programEntries[0].SHA256 -notmatch '^[A-F0-9]{64}$' -or $programEntries[0].SHA256 -cne $delivery.ProgramSHA256){throw 'The EXE must be uniquely hash-bound in the delivery manifest.'}
 Test-R50RuntimeFiles $applicationRoot @($delivery.RuntimeFiles)
 $program=Join-Path $applicationRoot $delivery.ProgramFileName
 $assembly=[Reflection.Assembly]::LoadFile($program)
 if(-not($assembly.GetManifestResourceNames() -contains 'HVX.Modules.Index.v1')){throw 'The test EXE has no embedded component index.'}
 $gate=Get-R50ParentGate $assembly $applicationRoot $StateFile
 return [pscustomobject][ordered]@{Schema='HVX.R50.DeploymentPreflight/1';RuntimeHashesPassed=$true;InstallationDocumentGatePassed=$gate.Passed;InstallationMode=$gate.Mode;Reason=$gate.Reason;
  ProductStarted=$false;SDKCalled=$false;HardwareAccessed=$false;FilesChanged=$false;DriverOrBindingVerified=$false;HardwareReady=$false;PhysicalAcceptance=$false;
  Notice='Only file hashes and the existing startup installation-document gate were checked. This is not device readiness or high-voltage permission.'}
}
if($MyInvocation.InvocationName -eq '.'){return}
if($PSVersionTable.PSVersion.Major -ne 5){throw 'Use Windows PowerShell: powershell.exe -NoProfile -File <this-script> -ApplicationDirectory <folder> -PackageDirectory <test-package>'}
if([string]::IsNullOrWhiteSpace($ApplicationDirectory)){throw 'Specify the folder containing the new test EXE. No files will be installed or changed.'}
$stateFile=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'XIONGXIN\HVXValidationV30Batch\install-state.xml'
$result=Invoke-R50Preflight $ApplicationDirectory $PackageDirectory $stateFile
$result|ConvertTo-Json -Depth 3
if(-not $result.InstallationDocumentGatePassed){throw 'Keep this test session offline. Do not delete state, forge a manifest, or disable the gate.'}
