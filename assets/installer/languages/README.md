# Knox Studio Installer - Multi-Language Support
To add a new language to Knox Studio Setup, create a JSON file in this directory (e.g. `it.json`, `ja.json`, `ru.json`).
The installer will automatically detect and list it in the language selection dialog.

### Example JSON format (`sample_language.json`):
```json
{
  "Code": "sample",
  "Name": "Sample Language",
  "NativeName": "Sample (Native)",
  "Strings": {
    "AppSubtitle": "INSTALLER",
    "IntroTitle": "Knox Studio v0.37.1",
    "IntroSubtitle": "Next-Generation Open-Source Digital Audio Workstation",
    "Feat1": "• 64-bit ultra-low latency professional audio engine",
    "Feat2": "• Comprehensive MIDI editing, virtual instruments & effects",
    "Feat3": "• Modern, hardware-accelerated fluid studio interface",
    "Feat4": "• Completely free and open-source (GNU AGPLv3)",
    "IntroDesc": "Click NEXT to proceed with installation.",
    "Cancel": "CANCEL",
    "Next": "NEXT >",
    "Back": "< BACK",
    "LicenseTitle": "License Agreement (GNU AGPLv3)",
    "LicenseSubtitle": "Please review the license terms before proceeding.",
    "AgreeLicense": "I have read and agree to the terms of the license agreement.",
    "AgreeNext": "I AGREE >",
    "ConfigTitle": "Installation Options",
    "ConfigSubtitle": "Select the destination path and system integrations.",
    "PathLabel": "Destination Folder:",
    "Browse": "Browse...",
    "BrowseDialogTitle": "Select Installation Directory",
    "DesktopShortcut": "Create Desktop shortcut",
    "StartMenuShortcut": "Create Start Menu shortcut",
    "AssociateFiles": "Associate .knox and .knoxproj project files",
    "StartInstall": "START INSTALLATION",
    "ProgressTitle": "Installing Knox Studio...",
    "StatusExtracting": "Extracting components...",
    "InstallingBadge": "INSTALLING",
    "FinishTitle": "Installation Completed Successfully!",
    "FinishDesc": "Knox Studio is now installed on your system.",
    "LaunchNow": "Launch Knox Studio now",
    "Finish": "FINISH"
  }
}
```