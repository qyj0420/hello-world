[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$BuildRoot,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$source=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$build=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$deliveryBase='D:\codex\HVXUnifiedDesktop\deliverables'
if(-not $build.StartsWith($source+'\build\',[StringComparison]::OrdinalIgnoreCase) -or -not $output.StartsWith($deliveryBase+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an isolated r58 build and a new project delivery folder.'}
function Unlinked([string]$path){for($walk=[IO.Path]::GetFullPath($path);$walk;$walk=[IO.Path]::GetDirectoryName($walk)){if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked export path prohibited.'}}}
function Hash([string]$path){Unlinked $path;return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
function Within([string]$base,[string]$relative){if($relative -match '(^[/\\]|:|\\|(^|/)\.\.?(/|$))'){throw 'Unsafe relative delivery path.'};$path=[IO.Path]::GetFullPath((Join-Path $base $relative));if(-not $path.StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Escaping delivery path.'};Unlinked $path;return $path}
function CopyVerified([string]$from,[string]$to,[string]$expected=''){
    Unlinked $from;Unlinked $to;$digest=Hash $from
    if($expected -and $digest -cne $expected){throw ('Source mismatch: '+$from)}
    $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($to)) -Force
    Copy-Item -LiteralPath $from -Destination $to
    if((Hash $to) -cne $digest){throw 'Copy verification failed.'}
}
Unlinked $build;Unlinked $output
$zipPath=$output+'.zip'
if((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath $zipPath)){throw 'Delivery folder or ZIP already exists; choose a fresh name.'}
$buildReceipt=Get-Content -LiteralPath (Join-Path $build 'r58-desktop-test-build.json') -Raw -Encoding UTF8|ConvertFrom-Json
$proofs=@(Get-ChildItem -LiteralPath (Join-Path $build 'qa') -Filter 'proof.json' -File -Recurse|Where-Object{$_.Directory.Name -like 'desktop-offline-*'}|Sort-Object LastWriteTimeUtc -Descending)
if(-not $proofs.Count){throw 'Offline verification proof is required.'}
$verificationPath=$proofs[0].FullName
$verification=Get-Content -LiteralPath $verificationPath -Raw -Encoding UTF8|ConvertFrom-Json
if(-not $buildReceipt.CompleteDesktopExecutable -or $buildReceipt.CustomerInstaller -or -not $buildReceipt.SourceUnchanged -or (Hash (Join-Path $build 'Xiongxin.OWTS.Desktop.exe')) -cne $buildReceipt.ExecutableSHA256){throw 'Not a verified complete desktop build.'}
if($verification.Status -cne 'STATIC_PASS' -or $verification.ExecutableSHA256 -cne $buildReceipt.ExecutableSHA256 -or $verification.HardwareAccessed){throw 'Offline verification is required before export.'}
$null=New-Item -ItemType Directory -Path $output
foreach($name in @('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config','HvxAcquisitionNative.dll','r58-desktop-test-build.json','r58-desktop-test-module-manifest.json')){CopyVerified (Join-Path $build $name) (Join-Path $output $name)}
CopyVerified $verificationPath (Join-Path $output 'r58-desktop-offline-verification.json')
foreach($folder in @('assets','docs/international','model-payload')){
    $directory=Within $build $folder
    foreach($file in @(Get-ChildItem -LiteralPath $directory -File -Recurse)){
        $relative=$file.FullName.Substring($build.Length+1).Replace('\','/')
        CopyVerified $file.FullName (Within $output $relative)
    }
}
$null=New-Item -ItemType Directory -Path (Join-Path $output 'OEMpic')
foreach($pair in @(@('modules/DataAnalysis/Task12/ONNX_RUNTIME_LICENSE.txt','ONNX_RUNTIME_LICENSE.txt'),@('modules/r71/LICENSE.txt','PYTHON_LICENSE.txt'),@('modules/r71/licenses/numpy-METADATA.txt','NUMPY_LICENSE_METADATA.txt'))){CopyVerified (Within $build $pair[0]) (Within $output ('licenses/'+$pair[1]))}
CopyVerified (Join-Path $source 'dependencies/Protection/ConfuserEx-LICENSE.txt') (Join-Path $output 'licenses/ConfuserEx-LICENSE.txt')
# Copy the previously supplied neutral bilingual documentation without rewriting it.
$manualManifest=Join-Path $source 'dependencies/Documentation/manual4.1-files.json'
if((Hash $manualManifest) -cne 'E34629581AF8529BE97AC70B42C4BEDDA5C8B8CF5666DF32CFEA04741D1AD4A4'){throw 'Documentation manifest changed.'}
$manual=Get-Content -LiteralPath $manualManifest -Raw -Encoding UTF8|ConvertFrom-Json
$manualBase='C:\Users\qyj04\Documents\ChatGPT\说明书\output\软件帮助文件_中英客户版_修订4.1_20261010_07'
foreach($entry in $manual.Files){if($entry.Path -clike 'docs/manual/*'){CopyVerified (Within $manualBase $entry.Path) (Within $output $entry.Path) $entry.SHA256}}
$aliases=@{
    '软件操作使用说明书_中性中文版.pdf'='HV-DAC_Xpert_Operation_Manual_ZH.pdf'
    '软件操作使用说明书_中性中文版.docx'='HV-DAC_Xpert_Operation_Manual_ZH.docx'
    'Software_Operation_Manual_Neutral_EN.pdf'='HV-DAC_Xpert_Operation_Manual_EN.pdf'
    'Software_Operation_Manual_Neutral_EN.docx'='HV-DAC_Xpert_Operation_Manual_EN.docx'
}
foreach($name in $aliases.Keys){CopyVerified (Join-Path $output ('docs/manual/'+$name)) (Join-Path $output ('说明书/'+$aliases[$name]))}
$glossary='C:\Users\qyj04\Documents\ChatGPT\说明书\output\专业术语表_中英对照_20261010\电缆局部放电检测_专业术语表_中英对照.pdf'
CopyVerified $glossary (Join-Path $output '专业术语/Electrical_Testing_Terminology_EN-ZH.pdf') 'CA7106DB9E9AE69DC1F41780EA757902A7817C3927444A86C7BF69ECAED6EA50'
$readme=@'
HV-DAC Xpert V1.1.2 r58 单 LOC 分析热修复测试版（2026-10-12）

启动：请完整解压 ZIP，双击 Xiongxin.OWTS.Desktop.exe。不要单独复制 EXE。
请先结束原有试验，按现场规程确认安全后关闭旧版本，再启动本版，避免两个程序同时占用设备。
这是完整桌面测试程序，不是安装器；不附带设备驱动安装包。需要 Windows x64、.NET Framework 4.8，以及设备已正确安装的官方驱动。

本版修正单 LOC 分析的重复进程启动、重复证据整理、分析结果预算、旧存档空源加载及 F-T 显示。E/E+ 算法规则不变，采用串行复用的算法进程；每条完整输入和结果身份仍校验，取消或失败不静默重试。
硬件保持已冻结的 r58 代码，不包含 Grok 加压逻辑迁移。等待新版加压/停止/量程规则后另行整合；本包不要用于声称硬件故障已修复或已通过实机验收。
保留独立的 30% / 45% / 50% 评分设置与顺序校验。
现有算法保护和维护权限保留；EXE、主窗口和自有窗口沿用项目中性/OEM 图标。
中英文说明书、专业术语及加密模型随包提供。OEMpic 是自定义图片目录。

重要：本版沿用本机 V1.1.2 的已有参数目录，不是独立参数沙箱；运行时更改设置会影响同一参数配置。不会随包导出个人设备参数或密码。
若其他电脑已有安装状态但本测试目录不匹配安装身份，原有保护可能只允许离线分析；本版不绕过该保护。
日志目录：%LOCALAPPDATA%\XIONGXIN\HVXUnifiedDesktop\PublicBeta-NoDriver-1.1.2-20261011\diagnostics

已执行：完整桌面与模块编译、嵌入文件 SHA256 校验、入口/架构/版本/权限与模块契约离线检查。
未执行：启动产品、调用设备 SDK、实机加压或物理安全验收。请由现场操作员在安全条件下测试；此版本尚不能表述为所有历史问题已通过实机验证。
本次不包含安装器发布审核、代码签名或全部历史界面回归。
'@
[IO.File]::WriteAllText((Join-Path $output 'README_测试说明.txt'),$readme,(New-Object Text.UTF8Encoding($true)))
$files=@(Get-ChildItem -LiteralPath $output -File -Recurse|Sort-Object FullName|ForEach-Object{Unlinked $_.FullName;[ordered]@{Path=$_.FullName.Substring($output.Length+1).Replace('\','/');Bytes=$_.Length;SHA256=(Hash $_.FullName)}})
[ordered]@{Schema='HVX.R58.DesktopTestDelivery/1';CreatedUtc=[DateTime]::UtcNow.ToString('o');Executable='Xiongxin.OWTS.Desktop.exe';ExecutableSHA256=$buildReceipt.ExecutableSHA256;FileVersion='1.1.2.0';Files=$files;BuilderSHA256=(Hash $PSCommandPath);CustomerInstaller=$false;DriverInstallerIncluded=$false;HardwareAccessed=$false;PhysicalAcceptance=$false}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $output 'delivery-files.json') -Encoding UTF8
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($output,$zipPath,[IO.Compression.CompressionLevel]::Optimal,$false)
$archive=[IO.Compression.ZipFile]::OpenRead($zipPath)
try{
    $expected=@{};foreach($file in @(Get-ChildItem -LiteralPath $output -File -Recurse)){$expected.Add($file.FullName.Substring($output.Length+1).Replace('\','/'),$file.FullName)}
    $seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $archive.Entries){if(-not $entry.Name){continue};if(-not $seen.Add($entry.FullName) -or -not $expected.ContainsKey($entry.FullName)){throw 'Unexpected or duplicate ZIP entry.'};$stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create();try{$digest=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$stream.Dispose();$sha.Dispose()};if($digest -cne (Hash $expected[$entry.FullName])){throw 'ZIP contents mismatch.'}}
    if($seen.Count -ne $expected.Count){throw 'ZIP omitted delivery files.'}
}finally{$archive.Dispose()}
[ordered]@{Schema='HVX.R58.DesktopTestArchive/1';Zip=$zipPath;SHA256=(Hash $zipPath);Bytes=(Get-Item -LiteralPath $zipPath).Length;Files=$expected.Count;ArchiveContentsVerified=$true;ExecutableSHA256=$buildReceipt.ExecutableSHA256}|ConvertTo-Json|Set-Content -LiteralPath ($zipPath+'.receipt.json') -Encoding UTF8
Write-Output ('DESKTOP_EXE='+ (Join-Path $output 'Xiongxin.OWTS.Desktop.exe'))
Write-Output ('DESKTOP_ZIP='+$zipPath)
Write-Output ('ZIP_SHA256='+(Hash $zipPath))
