; =====================================================================
; Knox Studio - Inno Setup Installer Script
; Developed by Furkan Çentek (rootcf)
; Open-source under GNU AGPLv3
; =====================================================================

#define AppName "Knox Studio"
#define AppPublisher "Furkan Çentek (rootcf)"
#define AppURL "https://github.com/rootcf"
#define AppExeName "Knox.App.exe"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef Arch
  #define Arch "x64"
#endif

#ifndef PubDir
  #define PubDir "..\dist\publish-" + Arch
#endif

[Setup]
AppId={{9E5D3F2A-1C4B-4E6A-9B77-4E0A5D6C7B81}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} v{#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename=Knox-Studio-Installer-v{#AppVersion}-{#Arch}
SetupIconFile=..\assets\icons\windows\knox.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardImageFile=..\assets\installer\setup_side.bmp
WizardSmallImageFile=..\assets\installer\setup_header.bmp
WizardSizePercent=100,100
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ChangesAssociations=yes

#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "associatefiles"; Description: "Associate .knox and .knoxproj project files"; GroupDescription: "File Associations:"

[Files]
; Main published application files
Source: "{#PubDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; App icon for file associations
Source: "..\assets\icons\windows\knox.ico"; DestDir: "{app}"; Flags: ignoreversion
; Installer graphical assets
Source: "..\assets\installer\setup_topbar.bmp"; Flags: dontcopy

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; File Association for .knox project files
Root: HKA; Subkey: "Software\Classes\.knox"; ValueType: string; ValueName: ""; ValueData: "KnoxStudio.Project"; Flags: uninsdeletevalue; Tasks: associatefiles
Root: HKA; Subkey: "Software\Classes\.knoxproj"; ValueType: string; ValueName: ""; ValueData: "KnoxStudio.Project"; Flags: uninsdeletevalue; Tasks: associatefiles
Root: HKA; Subkey: "Software\Classes\KnoxStudio.Project"; ValueType: string; ValueName: ""; ValueData: "Knox Studio Project"; Flags: uninsdeletekey; Tasks: associatefiles
Root: HKA; Subkey: "Software\Classes\KnoxStudio.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\knox.ico,0"; Tasks: associatefiles
Root: HKA; Subkey: "Software\Classes\KnoxStudio.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Tasks: associatefiles

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  TopBarPanel: TPanel;
  TopBarImg: TBitmapImage;

procedure StyleControls;
begin
  // Set dark theme and Segoe UI fonts
  WizardForm.Font.Name := 'Segoe UI';
  
  WizardForm.WelcomeLabel1.Font.Name := 'Segoe UI';
  WizardForm.WelcomeLabel1.Font.Size := 14;
  WizardForm.WelcomeLabel1.Font.Style := [fsBold];
  
  WizardForm.FinishedHeadingLabel.Font.Name := 'Segoe UI';
  WizardForm.FinishedHeadingLabel.Font.Size := 14;
  WizardForm.FinishedHeadingLabel.Font.Style := [fsBold];
  
  WizardForm.PageNameLabel.Font.Name := 'Segoe UI';
  WizardForm.PageNameLabel.Font.Size := 10;
  WizardForm.PageNameLabel.Font.Style := [fsBold];
  
  WizardForm.PageDescriptionLabel.Font.Name := 'Segoe UI';
  WizardForm.PageDescriptionLabel.Font.Size := 9;
end;

procedure InitializeWizard;
begin
  WizardForm.Bevel.Visible := False;
  WizardForm.Bevel1.Visible := False;
  
  // Custom Top Header Bar (Rockstar Social Club / Studio Launcher style)
  ExtractTemporaryFile('setup_topbar.bmp');
  
  TopBarPanel := TPanel.Create(WizardForm);
  TopBarPanel.Parent := WizardForm;
  TopBarPanel.Left := 0;
  TopBarPanel.Top := 0;
  TopBarPanel.Width := WizardForm.ClientWidth;
  TopBarPanel.Height := ScaleY(56);
  TopBarPanel.BevelOuter := bvNone;
  TopBarPanel.Color := $0C0D0E;
  
  TopBarImg := TBitmapImage.Create(TopBarPanel);
  TopBarImg.Parent := TopBarPanel;
  TopBarImg.Left := 0;
  TopBarImg.Top := 0;
  TopBarImg.Width := TopBarPanel.Width;
  TopBarImg.Height := TopBarPanel.Height;
  TopBarImg.Stretch := True;
  TopBarImg.Bitmap.LoadFromFile(ExpandConstant('{tmp}\setup_topbar.bmp'));
  
  // Push inner content slightly down so top bar doesn't overlap
  WizardForm.OuterNotebook.Top := TopBarPanel.Height;
  WizardForm.OuterNotebook.Height := WizardForm.ClientHeight - TopBarPanel.Height - ScaleY(50);
  
  // Adjust bottom navigation buttons
  WizardForm.BackButton.Top := WizardForm.ClientHeight - ScaleY(40);
  WizardForm.NextButton.Top := WizardForm.ClientHeight - ScaleY(40);
  WizardForm.CancelButton.Top := WizardForm.ClientHeight - ScaleY(40);
  
  StyleControls;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  // Keep top bar aligned with form width
  if Assigned(TopBarPanel) then
  begin
    TopBarPanel.Width := WizardForm.ClientWidth;
    if Assigned(TopBarImg) then
      TopBarImg.Width := TopBarPanel.Width;
  end;
end;



