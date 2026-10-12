param([switch]$Apply)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$installedRoot='C:\Program Files\XIONGXIN\HV-DAC Xpert Frozen Internal 20261006'
$executable=Join-Path $installedRoot 'Xiongxin.OWTS.Desktop.exe'
$links=@(
 'C:\Users\Public\Desktop\HV-DAC Xpert Internal 20261006.lnk',
 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\XIONGXIN HV-DAC Xpert Frozen Internal 20261006\HV-DAC Xpert Internal.lnk'
)
$original=Join-Path $projectRoot 'assets\shortcuts\256.ico'
$expected='3B9F43CC1860C2C38F444A84D0AEED77A696A5C293D6DD9843A04A735A0F9285'
if((Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -cne $expected){throw 'Shortcut icon source changed.'}
if(-not (Test-Path -LiteralPath $executable -PathType Leaf)){throw 'Expected installed program is missing.'}
$shell=New-Object -ComObject WScript.Shell
$before=@(foreach($link in $links){
 if(-not (Test-Path -LiteralPath $link -PathType Leaf)){throw ('Shortcut missing: '+$link)}
 $shortcut=$shell.CreateShortcut($link)
 if([IO.Path]::GetFullPath($shortcut.TargetPath) -ine $executable){throw ('Unexpected shortcut target: '+$link)}
 [pscustomobject]@{Path=$link;Target=$shortcut.TargetPath;Arguments=$shortcut.Arguments;WorkingDirectory=$shortcut.WorkingDirectory;Icon=$shortcut.IconLocation;Description=$shortcut.Description;WindowStyle=$shortcut.WindowStyle;Hotkey=$shortcut.Hotkey;SHA256=(Get-FileHash -LiteralPath $link).Hash}
})
if(-not $Apply){$before|ConvertTo-Json -Depth 4;return}
$backupRoot=Join-Path $projectRoot ('build\shortcut-update-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backupRoot | Out-Null
for($i=0;$i -lt $before.Count;$i++){Copy-Item -LiteralPath $before[$i].Path -Destination (Join-Path $backupRoot ($i.ToString()+'.lnk'))}
$before|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $backupRoot 'before.json') -Encoding UTF8
$iconDirectory=Join-Path $installedRoot 'assets\shortcuts'
$installedIcon=Join-Path $iconDirectory ('shortcut-'+$expected.Substring(0,12)+'.ico')
if(Test-Path -LiteralPath $installedIcon){
 if((Get-FileHash -LiteralPath $installedIcon).Hash -cne $expected){throw 'Existing icon differs; not overwritten.'}
}else{
 New-Item -ItemType Directory -Path $iconDirectory -Force | Out-Null
 Copy-Item -LiteralPath $original -Destination $installedIcon
}
try{
 foreach($entry in $before){
  $shortcut=$shell.CreateShortcut($entry.Path);$shortcut.IconLocation=$installedIcon+',0';$shortcut.Save()
  $verified=$shell.CreateShortcut($entry.Path)
  if($verified.TargetPath -cne $entry.Target -or $verified.Arguments -cne $entry.Arguments -or $verified.WorkingDirectory -cne $entry.WorkingDirectory -or $verified.Description -cne $entry.Description -or $verified.WindowStyle -ne $entry.WindowStyle -or $verified.Hotkey -cne $entry.Hotkey -or $verified.IconLocation -ine ($installedIcon+',0')){throw 'Shortcut readback mismatch.'}
 }
}catch{
 for($i=0;$i -lt $before.Count;$i++){Copy-Item -LiteralPath (Join-Path $backupRoot ($i.ToString()+'.lnk')) -Destination $before[$i].Path -Force}
 throw
}
$receipt=[ordered]@{Updated=$links;Icon=$installedIcon;IconSHA256=$expected;OriginalLinks=$backupRoot;TargetsChanged=$false;ApplicationLaunched=$false;HardwareAccessed=$false}
$receipt|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $backupRoot 'result.json') -Encoding UTF8
$receipt|ConvertTo-Json -Depth 4
