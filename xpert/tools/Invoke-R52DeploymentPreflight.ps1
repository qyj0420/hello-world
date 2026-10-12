param([string]$ApplicationDirectory, [string]$PackageDirectory=$PSScriptRoot)
$ErrorActionPreference='Stop'

# Read-only: no product entry, connector, driver, SDK or device operation. The
# document predicate below calls the exact delivered host's pure XML validator.
function Read-R52SmallText([string]$Path,[long]$Limit) {
 $info=Get-Item -LiteralPath $Path -ErrorAction Stop
 if($info.PSIsContainer -or $info.Length -gt $Limit){throw 'Deployment document is oversized or not a file.'}
 return [IO.File]::ReadAllText($info.FullName)
}
function Get-R52ParentGate([Reflection.Assembly]$Assembly,[string]$AppDirectory,[string]$StatePath) {
 $manifest=Join-Path $AppDirectory 'private\parent-manifest.xml'
 $helper=Join-Path $AppDirectory 'private\ParentHost.exe'
 $footprint=(Test-Path -LiteralPath $manifest) -or (Test-Path -LiteralPath $helper) -or (Test-Path -LiteralPath $StatePath)
 if(-not $footprint){return [pscustomobject]@{Passed=$true;Mode='NoParentFootprint';Reason='';FirstBindingProvided=$false}}
 if(-not(Test-Path -LiteralPath $manifest -PathType Leaf)){return [pscustomobject]@{Passed=$false;Mode='ParentFootprint';Reason='Existing installation state requires the matching installation manifest.';FirstBindingProvided=$false}}
 if(-not(Test-Path -LiteralPath $StatePath -PathType Leaf)){return [pscustomobject]@{Passed=$false;Mode='ParentFootprint';Reason='Installation completion state is missing.';FirstBindingProvided=$false}}
 $type=$Assembly.GetType('Xiongxin.OWTS.Desktop.FirstHardwareProvisioning',$true)
 $method=$type.GetMethod('ValidateParentStateDocuments',[Reflection.BindingFlags]'NonPublic,Static')
 if($null -eq $method){throw 'The delivered pure installation validator is unavailable.'}
 $arguments=@((Read-R52SmallText $manifest (4MB)),(Read-R52SmallText $StatePath 16384),'',$false)
 $reason=[string]$method.Invoke($null,$arguments)
 return [pscustomobject]@{Passed=($reason.Length -eq 0);Mode='ExistingParentDocuments';Reason=$reason;FirstBindingProvided=$false}
}
function Test-R52RuntimeFiles([string]$AppDirectory,[object[]]$Files) {
 $seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
 foreach($entry in $Files){
  $relative=[string]$entry.Path
  if([string]::IsNullOrWhiteSpace($relative) -or -not $seen.Add($relative.Replace('/','\')) -or [string]$entry.SHA256 -cnotmatch '^[A-F0-9]{64}$' -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative.Replace('/','\').Split('\') -contains '..'){throw 'Invalid delivery path.'}
  $file=[IO.Path]::GetFullPath((Join-Path $AppDirectory $relative))
  if(-not $file.StartsWith($AppDirectory.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Delivery path escaped its root.'}
  if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw ('Missing test file: '+$relative)}
  for($walk=$file;$walk.Length -ge $AppDirectory.Length;$walk=[IO.Path]::GetDirectoryName($walk)){
   if((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked deployment paths are not accepted.'}
  }
  if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne [string]$entry.SHA256){throw ('Different file; do not overwrite the old installation blindly: '+$relative)}
 }
}
function Get-R52ResourceHash([Reflection.Assembly]$Assembly,[string]$Name,[long]$Limit) {
 $stream=$Assembly.GetManifestResourceStream($Name)
 if($null -eq $stream){throw 'A required embedded resource is missing.'}
 try{
  if($stream.Length -le 0 -or $stream.Length -gt $Limit){throw 'Embedded metadata exceeded its bound.'}
  $sha=[Security.Cryptography.SHA256]::Create()
  try{return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$sha.Dispose()}
 }finally{$stream.Dispose()}
}
function Invoke-R52Preflight([string]$AppDirectory,[string]$PackageRoot,[string]$StateFile) {
 if($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSEdition -ne 'Desktop'){throw 'The protected .NET Framework EXE must be inspected using Windows PowerShell 5.1, not CoreCLR.'}
 $applicationRoot=[IO.Path]::GetFullPath($AppDirectory).TrimEnd('\')
 $packageRoot=[IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')
 $delivery=Read-R52SmallText (Join-Path $packageRoot 'delivery-manifest.json') (2MB)|ConvertFrom-Json
 # ASCII source also works in Windows PowerShell 5.1 on non-Chinese systems.
 $expectedProgram=([char]0x516c).ToString()+[char]0x6d4b+[char]0x7248+'V1.1.1_r52.exe'
 if($delivery.Schema -ne 'HVX.R52.TestDelivery/1' -or $delivery.ProgramFileName -cne $expectedProgram){throw 'Unexpected test delivery manifest.'}
 $programEntries=@($delivery.RuntimeFiles|Where-Object Path -eq $delivery.ProgramFileName)
 if($programEntries.Count -ne 1 -or $programEntries[0].SHA256 -notmatch '^[A-F0-9]{64}$' -or $programEntries[0].SHA256 -cne $delivery.ProgramSHA256){throw 'The EXE must be uniquely hash-bound in the delivery manifest.'}
 $required=@(($expectedProgram+'.config'),'assets/device-asset-catalog.v1.json')
 $models=@('5581BF7A60798CB8F349A3EE3A59C2F8C121D44AB98D23172453EC856D360D16','A32A19B489CD7FB0CF51F9D027C214E7636A5621D7663384D88925F2A4CEB32B')
 foreach($hash in $models){
  $relative='model-payload/'+$hash.ToLowerInvariant()+'.hxm'
  $modelEntries=@($delivery.RuntimeFiles|Where-Object {([string]$_.Path).Replace('\','/') -ceq $relative})
  if($modelEntries.Count -ne 1 -or $modelEntries[0].SHA256 -cne $hash){throw 'An approved separate encrypted model is missing from the runtime contract.'}
  $required+=,$relative
 }
 if(@($delivery.RuntimeFiles|Where-Object {([string]$_.Path).Replace('\','/').StartsWith('model-payload/',[StringComparison]::OrdinalIgnoreCase)}).Count -ne 2){throw 'Unexpected separate model inventory.'}
 foreach($relative in $required){
  if(@($delivery.RuntimeFiles|Where-Object {([string]$_.Path).Replace('\','/') -ceq $relative}).Count -ne 1){throw 'Required runtime component is not uniquely declared.'}
 }
 Test-R52RuntimeFiles $applicationRoot @($delivery.RuntimeFiles)
 $program=Join-Path $applicationRoot $delivery.ProgramFileName
 $assembly=[Reflection.Assembly]::LoadFile($program)
 if(-not($assembly.GetManifestResourceNames() -contains 'HVX.Modules.Index.v1')){throw 'The test EXE has no embedded component index.'}
 if((Get-Item -LiteralPath $program).VersionInfo.FileVersion -cne '1.1.1.0'){throw 'The test EXE version is not 1.1.1.0.'}
 $embedded=$delivery.EmbeddedModules
 if($embedded.BundleHash -cnotmatch '^[A-F0-9]{64}$' -or [long]$embedded.FileCount -le 0 -or [long]$embedded.FileCount -gt 4096 -or [long]$embedded.ExpandedBytes -le 0 -or [long]$embedded.ExpandedBytes -gt 1GB){throw 'Invalid bounded embedded component contract.'}
 if((Get-R52ResourceHash $assembly 'HVX.Modules.Index.v1' (2MB)) -cne $embedded.BundleHash){throw 'The embedded module index differs from the final build contract.'}
 if([string]$delivery.ResearchPinsSHA256 -cnotmatch '^[A-F0-9]{64}$' -or (Get-R52ResourceHash $assembly 'HVX.Research.Pins' (16MB)) -cne $delivery.ResearchPinsSHA256){throw 'The embedded research pins differ from the final build contract.'}
 $gate=Get-R52ParentGate $assembly $applicationRoot $StateFile
 return [pscustomobject][ordered]@{Schema='HVX.R52.DeploymentPreflight/1';RuntimeHashesPassed=$true;EmbeddedMetadataHashesPassed=$true;SeparateApprovedModelsPresent=$true;InstallationDocumentGatePassed=$gate.Passed;InstallationMode=$gate.Mode;Reason=$gate.Reason;
  ProductStarted=$false;SDKCalled=$false;HardwareAccessed=$false;FilesChanged=$false;DriverOrBindingVerified=$false;HardwareReady=$false;PhysicalAcceptance=$false;
  Notice='Only runtime/resource hashes and the existing startup installation-document gate were checked; no payload extraction or model inference was performed. This is not device readiness or high-voltage permission.'}
}
if($MyInvocation.InvocationName -eq '.'){return}
if($PSVersionTable.PSVersion.Major -ne 5){throw 'Use Windows PowerShell: powershell.exe -NoProfile -File <this-script> -ApplicationDirectory <folder> -PackageDirectory <test-package>'}
if([string]::IsNullOrWhiteSpace($ApplicationDirectory)){throw 'Specify the folder containing the new test EXE. No files will be installed or changed.'}
$stateFile=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'XIONGXIN\HVXValidationV30Batch\install-state.xml'
$result=Invoke-R52Preflight $ApplicationDirectory $PackageDirectory $stateFile
$result|ConvertTo-Json -Depth 3
if(-not $result.InstallationDocumentGatePassed){throw 'Keep this test session offline. Do not delete state, forge a manifest, or disable the gate.'}
