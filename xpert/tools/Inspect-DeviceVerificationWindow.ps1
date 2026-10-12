param([Parameter(Mandatory=$true)][int]$TargetProcessId,[Parameter(Mandatory=$true)][string]$OutputStem,[switch]$TestMissingManifestRefreshGate)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
$condition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$TargetProcessId)
$windows=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$condition)
if($TestMissingManifestRefreshGate){
 $process=Get-Process -Id $TargetProcessId
 $expectedRoot='D:\codex\HVXUnifiedDesktop\build\FreeValuePlanFix-20261006-r1'
 if($process.Path -ne ($expectedRoot+'\Xiongxin.OWTS.Desktop.exe') -or (Test-Path -LiteralPath ($expectedRoot+'\private\parent-manifest.xml'))){throw 'Only the verified missing-manifest gate may be exercised.'}
 if($windows.Count -ne 1){throw 'Expected only the main window before the gate check.'}
 $nameCondition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'刷新设备')
 $button=$windows.Item(0).FindFirst([System.Windows.Automation.TreeScope]::Descendants,$nameCondition)
 if($null -eq $button -or -not $button.Current.IsEnabled){throw 'Refresh device control is unavailable.'}
 Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class DeviceGateClick { [DllImport("user32.dll",SetLastError=true)] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l); }'
 if(-not [DeviceGateClick]::PostMessage([IntPtr]$button.Current.NativeWindowHandle,0xF5,[IntPtr]::Zero,[IntPtr]::Zero)){throw 'Unable to request the refresh gate check.'}
 Write-Output 'REFRESH_GATE_REQUESTED; no wait, no approval or connection action issued.'
 return
}
$rows=New-Object System.Collections.Generic.List[object]
for($windowIndex=0;$windowIndex -lt $windows.Count;$windowIndex++){
 $window=$windows.Item($windowIndex)
 $controls=$window.FindAll([System.Windows.Automation.TreeScope]::Subtree,[System.Windows.Automation.Condition]::TrueCondition)
 for($index=0;$index -lt $controls.Count;$index++){
  $current=$controls.Item($index).Current
  $rows.Add([pscustomobject]@{Window=$windowIndex;Type=$current.ControlType.ProgrammaticName;Name=$current.Name;Id=$current.AutomationId;Enabled=$current.IsEnabled;Offscreen=$current.IsOffscreen})
 }
 $rectangle=$window.Current.BoundingRectangle
 if(-not $rectangle.IsEmpty -and $rectangle.Width -gt 0 -and $rectangle.Height -gt 0){
  $bitmap=New-Object System.Drawing.Bitmap([int]$rectangle.Width,[int]$rectangle.Height)
  $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
  try{
   $graphics.CopyFromScreen([int]$rectangle.X,[int]$rectangle.Y,0,0,$bitmap.Size)
   $bitmap.Save($OutputStem+'-'+$windowIndex+'.png',[System.Drawing.Imaging.ImageFormat]::Png)
  }finally{$graphics.Dispose();$bitmap.Dispose()}
 }
}
$rows | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath ($OutputStem+'.json') -Encoding UTF8
$rows | Where-Object {$_.Name -ne ''} | Format-Table -AutoSize
