; Inno Setup script for TTSApp (AI Audiobook Studio)
; 1. Run build\publish.ps1 first to produce the .\publish folder.
; 2. Install Inno Setup (https://jrsoftware.org/isinfo.php).
; 3. Open this file in Inno Setup Compiler and Build, or:  iscc build\installer.iss

#define AppName "AI Audiobook Studio"
#define AppExe "TTSApp.exe"
#define AppVersion "1.0.15"
#define Publisher "TTSApp"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={autopf}\TTSApp
DefaultGroupName={#AppName}
OutputBaseFilename=TTSApp-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
DisableProgramGroupPage=yes
WizardStyle=modern

[Files]
; Publish output produced by build\publish.ps1 (path is relative to this .iss file).
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
