; Log Dug per-user installer. Built by .github/workflows/release.yml:
;   makensis /DVERSION=0.1.0 /DNUMERIC_VERSION=0.1.0.0 /DSOURCE_DIR=<publish dir> /DOUTPUT_FILE=<setup.exe> installer/logdug.nsi
; Installs to %LOCALAPPDATA%\Programs\LogDug without elevation, adds a Start menu shortcut, Explorer right-click entries, puts logdug on the
; user PATH and registers an uninstaller. Silent install/uninstall with /S (used by winget).

Unicode true
ManifestDPIAware true
RequestExecutionLevel user
SetCompressor /SOLID lzma

!ifndef VERSION
  !error "Pass /DVERSION=x.y.z"
!endif
!ifndef NUMERIC_VERSION
  !define NUMERIC_VERSION "0.0.0.0"
!endif

!define APP_NAME "Log Dug"
!define PUBLISHER "Adam Davies"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\LogDug"

!include "MUI2.nsh"

Name "${APP_NAME} ${VERSION}"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\LogDug"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"

VIProductVersion "${NUMERIC_VERSION}"
VIAddVersionKey "ProductName" "${APP_NAME}"
VIAddVersionKey "CompanyName" "${PUBLISHER}"
VIAddVersionKey "FileDescription" "${APP_NAME} installer"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "Apache-2.0"

!define MUI_FINISHPAGE_RUN "$INSTDIR\logdug.exe"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

; Adds or removes the install directory on the user PATH via PowerShell, which handles the registry
; value's length and type correctly and broadcasts the change.
!macro UserPath action
  nsExec::Exec `powershell -NoProfile -ExecutionPolicy Bypass -Command "$$d='$INSTDIR'; $$p=[Environment]::GetEnvironmentVariable('Path','User'); $$parts=@(($$p -split ';') | Where-Object { $$_ -and $$_ -ne $$d }); if ('${action}' -eq 'add') { $$parts += $$d }; [Environment]::SetEnvironmentVariable('Path', ($$parts -join ';'), 'User')"`
  Pop $0
!macroend

; Explorer right-click entries ("Open with Log Dug") for any file, any folder, and the empty background of a folder.
; Per-user (HKCU\Software\Classes), so no elevation is needed.
!macro ContextMenu action
  !if "${action}" == "add"
    WriteRegStr HKCU "Software\Classes\*\shell\LogDug" "" "Open with Log Dug"
    WriteRegStr HKCU "Software\Classes\*\shell\LogDug" "Icon" "$INSTDIR\logdug.exe,0"
    WriteRegStr HKCU "Software\Classes\*\shell\LogDug\command" "" '"$INSTDIR\logdug.exe" "%1"'
    WriteRegStr HKCU "Software\Classes\Directory\shell\LogDug" "" "Open with Log Dug"
    WriteRegStr HKCU "Software\Classes\Directory\shell\LogDug" "Icon" "$INSTDIR\logdug.exe,0"
    WriteRegStr HKCU "Software\Classes\Directory\shell\LogDug\command" "" '"$INSTDIR\logdug.exe" "%1"'
    WriteRegStr HKCU "Software\Classes\Directory\Background\shell\LogDug" "" "Open Log Dug here"
    WriteRegStr HKCU "Software\Classes\Directory\Background\shell\LogDug" "Icon" "$INSTDIR\logdug.exe,0"
    WriteRegStr HKCU "Software\Classes\Directory\Background\shell\LogDug\command" "" '"$INSTDIR\logdug.exe" "%V"'
  !else
    DeleteRegKey HKCU "Software\Classes\*\shell\LogDug"
    DeleteRegKey HKCU "Software\Classes\Directory\shell\LogDug"
    DeleteRegKey HKCU "Software\Classes\Directory\Background\shell\LogDug"
  !endif
!macroend

Section "Install"
  ; Replace a previous install's files rather than layering over them. Only a directory holding our
  ; uninstaller is cleared, so choosing an existing folder never deletes anything else in it.
  IfFileExists "$INSTDIR\uninstall.exe" 0 +2
    RMDir /r "$INSTDIR"
  SetOutPath "$INSTDIR"
  File /r "${SOURCE_DIR}\*.*"
  WriteUninstaller "$INSTDIR\uninstall.exe"

  CreateShortcut "$SMPROGRAMS\Log Dug.lnk" "$INSTDIR\logdug.exe" "" "$INSTDIR\logdug.exe" 0
  !insertmacro ContextMenu add
  !insertmacro UserPath add

  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\logdug.exe"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "URLInfoAbout" "https://github.com/adz/logdug"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  !insertmacro UserPath remove
  !insertmacro ContextMenu remove
  Delete "$SMPROGRAMS\Log Dug.lnk"
  RMDir /r "$INSTDIR"  ; $INSTDIR is the uninstaller's own directory here
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
SectionEnd
