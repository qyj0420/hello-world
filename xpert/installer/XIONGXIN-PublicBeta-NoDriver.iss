#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef OutputDirPath
  #error OutputDirPath is required
#endif
#ifndef SetupFileName
  #error SetupFileName is required
#endif

[Setup]
; A new independent application-only lane. Never overwrite the historical lanes.
AppId={{2DAB4E38-ED9A-402F-AC41-663D0B14198D}
AppName=HV-DAC Xpert 1.1.2
AppVersion=1.1.2.0
VersionInfoVersion=1.1.2.0
VersionInfoProductVersion=1.1.2.0
AppComments=Application installer r57; device drivers are installed separately
DefaultDirName={autopf}\HV-DAC Xpert\Public Beta 1.1.2 20261011-NoDriver
DefaultGroupName=HV-DAC Xpert 1.1.2
UsePreviousAppDir=no
UsePreviousGroup=no
DisableDirPage=yes
OutputDir={#OutputDirPath}
OutputBaseFilename={#SetupFileName}
SetupIconFile=..\assets\shortcuts\256.ico
UninstallDisplayIcon={app}\Xiongxin.OWTS.Desktop.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
PrivilegesRequired=admin
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
DisableProgramGroupPage=yes
CloseApplications=no
RestartApplications=no
SetupLogging=yes
InfoBeforeFile=..\docs\PublicBeta_NoDriver_Installation_20261011.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Dirs]
; Only this dedicated image folder is user-writable. Never grant write access to {app}.
Name: "{app}\OEMpic"; Permissions: users-modify; Flags: uninsneveruninstall
Name: "{app}\说明书"
Name: "{app}\专业术语"

[Files]
Source: "{#PayloadDir}\Xiongxin.OWTS.Desktop.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\Xiongxin.OWTS.Desktop.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\module-manifest.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\release-validation.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\Installation_Readme.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\DesktopShortcut.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\assets\*"; DestDir: "{app}\assets"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
; Retain the original relative HTML tree used by the in-application help router.
Source: "{#PayloadDir}\docs\manual\*"; DestDir: "{app}\docs\manual"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\docs\international\*"; DestDir: "{app}\docs\international"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\说明书\*"; DestDir: "{app}\说明书"; Flags: ignoreversion
Source: "{#PayloadDir}\专业术语\*"; DestDir: "{app}\专业术语"; Flags: ignoreversion
#include "EncryptedModels.iss"
Source: "{#PayloadDir}\prerequisites\NDP48-x86-x64-AllOS-ENU.exe"; Flags: dontcopy

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"

[Icons]
Name: "{group}\HV-DAC Xpert 1.1.2"; Filename: "{app}\Xiongxin.OWTS.Desktop.exe"; WorkingDir: "{app}"; IconFilename: "{app}\DesktopShortcut.ico"
Name: "{group}\Uninstall HV-DAC Xpert 1.1.2"; Filename: "{uninstallexe}"
Name: "{autodesktop}\HV-DAC Xpert 1.1.2"; Filename: "{app}\Xiongxin.OWTS.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon; IconFilename: "{app}\DesktopShortcut.ico"
Name: "{group}\Operation manual (English)"; Filename: "{app}\说明书\HV-DAC_Xpert_Operation_Manual_EN.pdf"
Name: "{group}\操作说明书（中文）"; Filename: "{app}\说明书\HV-DAC_Xpert_Operation_Manual_ZH.pdf"
Name: "{group}\Electrical testing terminology (EN-ZH)"; Filename: "{app}\专业术语\Electrical_Testing_Terminology_EN-ZH.pdf"

[Code]
#include "InteractiveInstall.iss"

procedure InitializeWizard();
begin
  WizardForm.DirEdit.Text := ExpandConstant('{autopf}\HV-DAC Xpert\Public Beta 1.1.2 20261011-NoDriver');
end;
