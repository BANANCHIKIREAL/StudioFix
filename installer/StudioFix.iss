#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#ifndef BuildDir
  #define BuildDir "..\build"
#endif
#ifndef ReleaseDir
  #define ReleaseDir "..\build\release"
#endif
#ifndef InstallId
  #define InstallId "{{45A9FF78-1660-46AA-8114-F8BE149E93DE}"
#endif
#ifndef ShortcutName
  #define ShortcutName "StudioFix"
#endif
[Setup]
AppId={#InstallId}
AppName=StudioFix
AppVersion={#AppVersion}
AppPublisher=BANANCHIKIREAL
AppPublisherURL=https://github.com/BANANCHIKIREAL/StudioFix
AppSupportURL=https://github.com/BANANCHIKIREAL/StudioFix/issues
AppUpdatesURL=https://github.com/BANANCHIKIREAL/StudioFix/releases
DefaultDirName={localappdata}\Programs\StudioFix
DefaultGroupName=StudioFix
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
WizardStyle=modern dark includetitlebar
WizardSizePercent=100
SetupIconFile=..\src\StudioFix.ico
UninstallDisplayIcon={app}\StudioFix.exe
OutputDir={#ReleaseDir}
OutputBaseFilename=StudioFix-Setup
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=StudioFix Installer
VersionInfoCopyright=Copyright (c) 2026 BANANCHIKIREAL
[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: checkedonce
[Files]
Source: "{#BuildDir}\StudioFix.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publisher\LUCIDE-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
[Icons]
Name: "{userprograms}\{#ShortcutName}"; Filename: "{app}\StudioFix.exe"; WorkingDir: "{app}"; Comment: "{cm:AppComment}"
Name: "{userdesktop}\{#ShortcutName}"; Filename: "{app}\StudioFix.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Comment: "{cm:AppComment}"
[Run]
#ifndef TestInstall
Filename: "{app}\StudioFix.exe"; Parameters: "--cleanup-legacy"; Flags: runhidden waituntilterminated
#endif
Filename: "{app}\StudioFix.exe"; Description: "{cm:LaunchApp}"; Flags: postinstall nowait skipifsilent
[CustomMessages]
russian.DesktopIcon=Создать ярлык StudioFix на рабочем столе
english.DesktopIcon=Create a StudioFix desktop shortcut
russian.AppComment=Проверка фикса и запуск TikTok LIVE Studio
english.AppComment=Check the fix and launch TikTok LIVE Studio
russian.LaunchApp=Открыть StudioFix
english.LaunchApp=Open StudioFix
russian.FrameworkMissing=Нужен .NET Framework 4.8. Открыть официальную страницу Microsoft? После установки запусти установщик StudioFix снова.
english.FrameworkMissing=.NET Framework 4.8 is required. Open the official Microsoft download page? Run StudioFix Setup again after installing it.
[Code]
function InitializeSetup(): Boolean;
var
  Release: Cardinal;
  ErrorCode: Integer;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
  if not Result then
    if not WizardSilent then
      if MsgBox(CustomMessage('FrameworkMissing'), mbInformation, MB_YESNO) = IDYES then
        ShellExec('open', 'https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;
