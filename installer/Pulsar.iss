#ifndef Stage
  #define Stage "..\build\installer-stage"
#endif
[Setup]
AppId={{DFB09F42-B316-45D4-92DC-9D9966086D99}
AppName=Пульсар
AppVersion=0.2.7
DefaultDirName={autopf}\Pulsar
DefaultGroupName=Пульсар
UsePreviousGroup=no
OutputDir=..\dist
OutputBaseFilename=Pulsar-Setup-0.2.7-x64
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\assets\brand\Pulsar.ico
WizardImageFile=..\assets\installer\wizard.bmp
WizardSmallImageFile=..\assets\installer\wizard-small.bmp
WizardImageStretch=yes
DisableWelcomePage=no
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\Pulsar.exe
InfoBeforeFile=setup-notes.txt

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "configure"; Description: "Настроить USB- и TAP-драйверы Archer T3U (обычный Wi-Fi через этот адаптер станет недоступен)"; Flags: checkedonce
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; Flags: checkedonce

[Files]
Source: "{#Stage}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,self-test.txt,*-test.txt,preview*.png"

[InstallDelete]
Type: files; Name: "{app}\PulseIt.exe"
Type: files; Name: "{app}\PulseIt.dll"
Type: files; Name: "{app}\PulseIt.deps.json"
Type: files; Name: "{app}\PulseIt.runtimeconfig.json"
Type: files; Name: "{autodesktop}\PulseIt.lnk"
Type: files; Name: "{commonprograms}\PulseIt\PulseIt.lnk"
Type: files; Name: "{commonprograms}\PulseIt\PulseIt quick start.lnk"
Type: files; Name: "{commonprograms}\PulseIt\Configure USB adapter.lnk"
Type: files; Name: "{commonprograms}\PulseIt\Restore USB Wi-Fi driver.lnk"
Type: files; Name: "{commonprograms}\PulseDrop\PulseDrop.lnk"
Type: files; Name: "{commonprograms}\PulseDrop\PulseDrop quick start.lnk"
Type: files; Name: "{commonprograms}\PulseDrop\Configure USB adapter.lnk"
Type: files; Name: "{commonprograms}\PulseDrop\Restore USB Wi-Fi driver.lnk"

; Удаление файлов предыдущих названий при обновлении того же приложения.
Type: files; Name: "{app}\PulseDrop.exe"
Type: files; Name: "{app}\PulseDrop.dll"
Type: files; Name: "{app}\PulseDrop.deps.json"
Type: files; Name: "{app}\PulseDrop.runtimeconfig.json"
Type: files; Name: "{group}\PulseDrop.lnk"
Type: files; Name: "{group}\PulseDrop quick start.lnk"
Type: files; Name: "{autodesktop}\PulseDrop.lnk"

[Icons]
Name: "{group}\Пульсар"; Filename: "{app}\Pulsar.exe"
Name: "{group}\Быстрый старт"; Filename: "{app}\QUICK-START.html"; IconFilename: "{app}\Pulsar.exe"
Name: "{group}\Настроить USB-адаптер"; Filename: "{app}\Pulsar.exe"; Parameters: "--configure-driver"
Name: "{group}\Восстановить драйвер Wi-Fi"; Filename: "{app}\Pulsar.exe"; Parameters: "--restore-driver"
Name: "{autodesktop}\Пульсар"; Filename: "{app}\Pulsar.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Pulsar.exe"; Description: "Открыть Пульсар"; Flags: nowait postinstall skipifsilent runascurrentuser
Filename: "{app}\QUICK-START.html"; Description: "Открыть инструкцию"; Flags: postinstall shellexec skipifsilent unchecked

[Code]
function RunDeviceSetup(Action: String): Boolean;
var ExitCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Setup-Devices.ps1') + '" -Action ' + Action + ' -Root "' + ExpandConstant('{app}') + '"',
    ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode);
  Result := Result and (ExitCode = 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('configure') then
    if not RunDeviceSetup('Configure') then
      RaiseException('Настройка адаптера не завершена. Проверьте C:\ProgramData\Pulsar\setup.log.');
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if MsgBox('Вернуть обычный Wi-Fi адаптера TP-Link перед удалением Пульсар? Сначала закройте приложение. Полученные файлы и резервные копии драйвера сохранятся.', mbConfirmation, MB_YESNO) = IDYES then
    if not RunDeviceSetup('Restore') then
      Result := MsgBox('Не удалось восстановить драйвер. Отменить удаление, чтобы сохранить средство восстановления?', mbError, MB_YESNO) = IDNO;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then RunDeviceSetup('Cleanup');
end;
