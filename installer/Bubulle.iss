; Installateur de Bubulle (Inno Setup 6).
; Installation par utilisateur, sans droits administrateur : l'app peut ainsi se mettre à jour toute seule.
; Les réglages (%APPDATA%\Bubulle) ne sont ni créés ni supprimés par l'installateur.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\app"
#endif

[Setup]
AppId={{6B0E4F8A-2D4C-4E8B-9C11-B0B0B0B0B0B0}
AppName=Bubulle
AppVersion={#AppVersion}
AppPublisher=thino-yoshi
AppPublisherURL=https://github.com/thino-yoshi/bubulle-app
DefaultDirName={localappdata}\Programs\Bubulle
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=Bubulle-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Bubulle
UninstallDisplayIcon={app}\Bubulle.exe
CloseApplications=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le bureau"; GroupDescription: "Raccourcis :"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Bubulle"; Filename: "{app}\Bubulle.exe"
Name: "{userdesktop}\Bubulle"; Filename: "{app}\Bubulle.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Bubulle.exe"; Description: "Lancer Bubulle"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Retire le lancement au démarrage s'il avait été activé.
Filename: "schtasks.exe"; Parameters: "/Delete /TN Bubulle /F"; Flags: runhidden; RunOnceId: "RemoveStartupTask"
