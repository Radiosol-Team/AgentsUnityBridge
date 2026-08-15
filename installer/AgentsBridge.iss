#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif

[Setup]
AppId={{AA856F53-06DD-489D-9817-4584291BD80B}
AppName=Unity Agents Bridge
AppVersion={#AppVersion}
AppPublisher=Radiosol Team
AppPublisherURL=https://github.com/Radiosol-Team/AgentsUnityBridge
AppSupportURL=https://github.com/Radiosol-Team/AgentsUnityBridge/issues
AppUpdatesURL=https://github.com/Radiosol-Team/AgentsUnityBridge/releases
DefaultDirName={localappdata}\Programs\AgentsBridge
DefaultGroupName=Unity Agents Bridge
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=AgentsBridge-win-x64-setup
SetupIconFile=..\src\AgentsBridge.Desktop\Assets\agentsbridge-logo.ico
UninstallDisplayIcon={app}\Unity Agents Bridge.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=yes
ChangesEnvironment=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Unity Agents Bridge"; Filename: "{app}\Unity Agents Bridge.exe"
Name: "{autodesktop}\Unity Agents Bridge"; Filename: "{app}\Unity Agents Bridge.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Unity Agents Bridge.exe"; Parameters: "--start-daemon"; Description: "Launch Unity Agents Bridge"; Flags: nowait
