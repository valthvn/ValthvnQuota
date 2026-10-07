; Inno Setup — ValthvnQuotaSetup-{version}-{arch}.exe
; CI: pass absolute /DPublishDir (Inno resolves relative paths from this script's folder).

#ifndef MyAppName
  #define MyAppName "ValthvnQuota"
#endif
#ifndef MyAppExeName
  #define MyAppExeName "ValthvnQuota.exe"
#endif
#ifndef PublishDir
  #define PublishDir "..\src\ValthvnQuota.App\bin\x64\Release\net9.0-windows10.0.19041.0\win-x64\publish"
#endif
#ifndef MyAppVersion
  ; Single source of truth: read the version stamped into the published ValthvnQuota.exe
  ; (csproj <Version>; CI overrides this with /DMyAppVersion=<tag>). GetVersionNumbersString
  ; returns "X.Y.Z.0", so strip the trailing ".0".
  #define _VersionFull GetVersionNumbersString(AddBackslash(PublishDir) + MyAppExeName)
  #define MyAppVersion Copy(_VersionFull, 1, Len(_VersionFull) - 2)
#endif
#ifndef TargetArch
  #define TargetArch "x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#if TargetArch == "arm64"
  #define ArchAllowed "arm64"
  #define ArchInstallMode "arm64"
#else
  #define ArchAllowed "x64compatible"
  #define ArchInstallMode "x64compatible"
#endif

[Setup]
AppId={{C4725E88-5182-4DA4-B972-88E4112BA64E}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=valthvn
AppPublisherURL=https://github.com/valthvn/ValthvnQuota
AppSupportURL=https://github.com/valthvn/ValthvnQuota/issues
AppUpdatesURL=https://github.com/valthvn/ValthvnQuota/releases
DefaultDirName={autopf}\ValthvnQuota
DefaultGroupName=ValthvnQuota
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=ValthvnQuotaSetup-{#MyAppVersion}-{#TargetArch}
SetupIconFile=..\src\ValthvnQuota.App\Assets\ValthvnQuota.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchInstallMode}
MinVersion=10.0.19041
PrivilegesRequired=lowest

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
