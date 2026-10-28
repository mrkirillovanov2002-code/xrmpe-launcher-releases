{ Upgrade discovery belongs to Setup, not to the launcher/runtime. }
const
  UpgradeKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{EB475631-868E-40C0-B134-0F6DB93D551A}_is1';
var
  UpgradeReady: Boolean;
  UpgradeRegistered, UpgradeOtherScope, UpgradePortable, UpgradeOldDesktop: String;
  UpgradeOtherDesktop, UpgradeOtherPortable: String;
  UpgradeConfirmedDirectory: String;

function UpgradeNormal(const Value: String): String;
begin
  Result := RemoveBackslash(ExpandFileName(Value));
end;

function UpgradeIsCustom(const Directory: String): Boolean;
begin
  Result := False;
  { Discovery must not follow an arbitrary UNC shortcut over the network. }
  if Length(Directory) < 3 then Exit;
  if Directory[2] <> ':' then Exit;
  Result :=
    FileExists(AddBackslash(Directory) + 'Xrmpe.Launcher.exe') and
    FileExists(AddBackslash(Directory) + 'Xrmpe.Launcher.dll') and
    FileExists(AddBackslash(Directory) + 'Avalonia.Controls.dll');
end;

function UpgradeRegistry(const Root: Integer): String;
var Value: String;
begin
  Result := '';
  if not RegQueryStringValue(Root, UpgradeKey, 'Inno Setup: App Path', Value) then
    RegQueryStringValue(Root, UpgradeKey, 'InstallLocation', Value);
  if UpgradeIsCustom(Value) then Result := UpgradeNormal(Value);
end;

procedure UpgradeReadDesktop(const Directory: String; var Path, Root: String);
var Find: TFindRec; Shell, Link: Variant; Target: String;
begin
  if not FindFirst(AddBackslash(Directory) + '*.lnk', Find) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    repeat
      try
        Link := Shell.CreateShortcut(AddBackslash(Directory) + Find.Name);
        Target := Link.TargetPath;
        { Dot versus hyphen is intentional: never adopt the original game launcher. }
        if CompareText(ExtractFileName(Target), 'Xrmpe.Launcher.exe') = 0 then
          if UpgradeIsCustom(ExtractFileDir(Target)) then
          begin
            if Path = '' then
            begin
              Path := AddBackslash(Directory) + Find.Name;
              Root := UpgradeNormal(ExtractFileDir(Target));
            end;
            Log('Existing Custom Launcher link: ' + Find.Name + ' -> ' + Target);
          end;
      except
        Log('Unable to inspect desktop link: ' + Find.Name);
      end;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
end;

procedure UpgradeDiscover;
begin
  if UpgradeReady then Exit;
  UpgradeReady := True;
  if IsAdminInstallMode then
  begin
    UpgradeRegistered := UpgradeRegistry(HKLM64);
    if UpgradeRegistered = '' then UpgradeRegistered := UpgradeRegistry(HKLM32);
    UpgradeOtherScope := UpgradeRegistry(HKCU64);
    if UpgradeOtherScope = '' then UpgradeOtherScope := UpgradeRegistry(HKCU32);
  end
  else
  begin
    UpgradeRegistered := UpgradeRegistry(HKCU64);
    if UpgradeRegistered = '' then UpgradeRegistered := UpgradeRegistry(HKCU32);
    UpgradeOtherScope := UpgradeRegistry(HKLM64);
    if UpgradeOtherScope = '' then UpgradeOtherScope := UpgradeRegistry(HKLM32);
  end;
  UpgradeReadDesktop(ExpandConstant('{autodesktop}'), UpgradeOldDesktop, UpgradePortable);
  if IsAdminInstallMode then
    UpgradeReadDesktop(ExpandConstant('{userdesktop}'), UpgradeOtherDesktop, UpgradeOtherPortable)
  else
    UpgradeReadDesktop(ExpandConstant('{commondesktop}'), UpgradeOtherDesktop, UpgradeOtherPortable);
  Log('Upgrade registered=' + UpgradeRegistered + '; desktop=' + UpgradePortable + '; other scope=' + UpgradeOtherScope);
end;

function UpgradeDefaultDirectory(Param: String): String;
begin
  UpgradeDiscover;
  if UpgradeRegistered <> '' then Result := UpgradeRegistered
  else if UpgradeOtherScope <> '' then Result := UpgradeOtherScope
  else if UpgradePortable <> '' then Result := UpgradePortable
  else if UpgradeOtherPortable <> '' then Result := UpgradeOtherPortable
  else Result := ExpandConstant('{autopf}\xrMPE Launcher');
end;

function UpgradeDesktopFolder(Param: String): String;
begin
  UpgradeDiscover;
  if UpgradeOldDesktop <> '' then Result := ExtractFileDir(UpgradeOldDesktop)
  else Result := ExpandConstant('{autodesktop}');
end;

function UpgradeDesktopName(Param: String): String;
begin
  UpgradeDiscover;
  if UpgradeOldDesktop <> '' then Result := ChangeFileExt(ExtractFileName(UpgradeOldDesktop), '')
  else Result := 'xrMPE Launcher';
end;

function UpgradeNeedsDesktopIcon: Boolean;
begin
  UpgradeDiscover;
  if UpgradeOldDesktop <> '' then Result := True
  else if UpgradeOtherDesktop <> '' then Result := False
  else Result := WizardIsTaskSelected('desktopicon');
end;

function UpgradeExistingDirectory: String;
begin
  UpgradeDiscover;
  Result := UpgradeRegistered;
  if Result = '' then Result := UpgradeOtherScope;
  if Result = '' then Result := UpgradePortable;
  if Result = '' then Result := UpgradeOtherPortable;
end;

function UpgradeConfirmDirectory: Boolean;
var Previous, Selected: String;
begin
  Result := True;
  Previous := UpgradeExistingDirectory;
  Selected := UpgradeNormal(WizardDirValue);
  if (Previous = '') or (CompareText(Previous, Selected) = 0) then Exit;
  Result := MsgBox('Найдена прежняя копия:' + #13#10 + Previous + #13#10#13#10 +
    'Вы выбрали другую папку. Прежняя копия останется на диске, а существующий ярлык будет направлен в новую папку.' + #13#10#13#10 +
    'Создать отдельную установку? Для обновления на месте вернитесь и выберите прежнюю папку.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  if Result then UpgradeConfirmedDirectory := Selected;
end;

function UpgradePrepare: String;
var Previous, Selected: String;
begin
  Result := '';
  UpgradeDiscover;
  if (UpgradeRegistered = '') and (UpgradeOtherScope = '') and
    (UpgradePortable = '') and (UpgradeOtherPortable <> '') then
  begin
    Result := 'Прежний ярлык найден в другом режиме пользователей. Запустите Setup с /CURRENTUSER для личного ярлыка или с /ALLUSERS для общего. Вторая копия автоматически не создаётся.';
    Exit;
  end;
  if (UpgradeRegistered = '') and (UpgradeOtherScope <> '') then
  begin
    Result := 'Прежняя установка найдена в другом режиме прав. Для установки для всех пользователей запустите Setup с /ALLUSERS, для своей учётной записи — с /CURRENTUSER. Вторая регистрация автоматически не создаётся.';
    Exit;
  end;
  Previous := UpgradeExistingDirectory;
  Selected := UpgradeNormal(WizardDirValue);
  if (Previous <> '') and (CompareText(Previous, Selected) <> 0) and
    (CompareText(UpgradeConfirmedDirectory, Selected) <> 0) and
    (ExpandConstant('{param:ALLOWSEPARATE|0}') <> '1') then
    Result := 'Найдена прежняя копия: ' + Previous + '. Выберите эту же папку для обновления. Бесшумная отдельная установка требует явного /ALLOWSEPARATE=1; старая копия не удаляется.';
  Log('Upgrade selected=' + Selected + '; old=' + Previous + '; desktop=' + UpgradeOldDesktop);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Code: Integer; Arguments: String;
begin
  if CurStep <> ssPostInstall then Exit;
  ExtractTemporaryFile('Repair-LauncherShortcuts.ps1');
  Arguments := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
    AddQuotes(ExpandConstant('{tmp}\Repair-LauncherShortcuts.ps1')) +
    ' -LauncherPath ' + AddQuotes(ExpandConstant('{app}\Xrmpe.Launcher.exe'));
  if not ExecAsOriginalUser(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Arguments, '', SW_HIDE, ewWaitUntilTerminated, Code) then Code := -1;
  if Code <> 0 then
  begin
    Log('Existing desktop aliases could not be repaired; code=' + IntToStr(Code));
    SuppressibleMsgBox('Лаунчер установлен, но не удалось обновить дополнительные личные ярлыки. Основной ярлык установленной копии готов. Подробности: журнал Setup.', mbError, MB_OK, IDOK);
  end;
end;
