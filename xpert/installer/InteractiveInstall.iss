// Included from [Code]. No hardware actions, exclusion rules or silent consent.
function InitializeSetup(): Boolean;
begin
  Result := not WizardSilent;
  if not Result then
    MsgBox('This installer requires interactive review. Restart without silent options.', mbError, MB_OK);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpInfoBefore then
    Result := MsgBox('Please review the installation information before continuing.' + #13#10 +
      'Keep security software enabled. This installer does not disable protection or add exclusions.' + #13#10 +
      'Installing the software does not confirm safe hardware operation or replace on-site safety checks.' + #13#10 +
      'Continue with installation?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var ExitCode: Integer;
begin
  Result := '';
  if not IsDotNetInstalled(net48, 0) then
  begin
    if MsgBox('.NET Framework 4.8 is required. Open its installer now? Review and accept its terms in that window.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then
    begin
      Result := '.NET Framework installation was not authorized. No application installation has started.';
      Exit;
    end;
    ExtractTemporaryFile('NDP48-x86-x64-AllOS-ENU.exe');
    if not Exec(ExpandConstant('{tmp}\NDP48-x86-x64-AllOS-ENU.exe'),
      '/norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode) then
      Result := 'Unable to open the .NET Framework 4.8 installer.'
    else if ExitCode = 3010 then
      NeedsRestart := True
    else if ExitCode <> 0 then
      Result := '.NET Framework installation was cancelled or failed. Exit code: ' + IntToStr(ExitCode);
    if (Result = '') and not NeedsRestart and not IsDotNetInstalled(net48, 0) then
      Result := '.NET Framework 4.8 was not detected after installation.';
  end;
end;
