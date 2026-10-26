; NexLimit - Windows installer (NSIS, Modern UI 2, 64-bit, admin: the WinDivert driver needs it)
; Build: makensis installer.nsi   (expects ..\dist with NexLimit.exe, WinDivert.dll, WinDivert64.sys, licenses\)
Target amd64-unicode
!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

!define APP      "NexLimit"
!define VERSION  "1.2.0"
!define EXE      "NexLimit.exe"
!define UNKEY    "Software\Microsoft\Windows\CurrentVersion\Uninstall\NexLimit"
!define DIST     "..\dist"

Name "${APP}"
OutFile "out\NexLimit-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\NexLimit"
InstallDirRegKey HKLM "Software\NexLimit" "InstallDir"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
BrandingText "${APP} ${VERSION}"
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APP}"
VIAddVersionKey "FileDescription" "${APP} Setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "CompanyName" "NexLimit"
VIAddVersionKey "LegalCopyright" "NexLimit contributors"

!define MUI_ICON   "..\res\nexlimit.ico"
!define MUI_UNICON "..\res\nexlimit.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "Install ${APP}"
!define MUI_WELCOMEPAGE_TEXT "See which apps use your internet, limit their download and upload speed, block apps, and set a daily or monthly data quota.$\r$\n$\r$\nIf NexLimit is running, setup closes it first. Your limits, quota and history are kept.$\r$\n$\r$\nClick Next to continue."
!define MUI_COMPONENTSPAGE_SMALLDESC
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Start NexLimit"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "NexLimit needs 64-bit Windows 10 or 11." /SD IDOK
    Abort
  ${EndIf}
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  ${If} $0 < 461808
    MessageBox MB_YESNO|MB_ICONEXCLAMATION "NexLimit needs Microsoft .NET Framework 4.7.2 or later, which this PC doesn't have yet.$\r$\n$\r$\nOpen Microsoft's download page now?" /SD IDNO IDNO +2
      ExecShell "open" "https://dotnet.microsoft.com/download/dotnet-framework/net48"
    Abort
  ${EndIf}
  SetRegView 64
FunctionEnd

Section "NexLimit (required)" SecCore
  SectionIn RO
  nsExec::Exec 'taskkill /F /T /IM ${EXE}'
  Sleep 1000
  SetOutPath "$INSTDIR"
  File "${DIST}\${EXE}"
  File "${DIST}\${EXE}.config"
  File "${DIST}\WinDivert.dll"
  File "${DIST}\WinDivert64.sys"
  SetOutPath "$INSTDIR\licenses"
  File /r "${DIST}\licenses\*.*"
  SetOutPath "$INSTDIR"
  CreateShortCut "$SMPROGRAMS\NexLimit.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0

  WriteRegStr HKLM "Software\NexLimit" "InstallDir" "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "${UNKEY}" "DisplayName" "${APP}"
  WriteRegStr HKLM "${UNKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNKEY}" "Publisher" "NexLimit"
  WriteRegStr HKLM "${UNKEY}" "DisplayIcon" "$INSTDIR\${EXE}"
  WriteRegStr HKLM "${UNKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKLM "${UNKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNKEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKLM "${UNKEY}" "EstimatedSize" $0
SectionEnd

Section "Desktop shortcut" SecDesktop
  CreateShortCut "$DESKTOP\NexLimit.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0
SectionEnd

Section "Start with Windows (in the tray)" SecStartup
  nsExec::Exec '"$INSTDIR\${EXE}" --autostart-on'
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecCore}    "NexLimit and the WinDivert packet driver."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "Put a NexLimit icon on the desktop."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecStartup} "Start NexLimit in the tray when you sign in, so limits, blocks and the quota are always on."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Function un.onInit
  SetRegView 64
FunctionEnd

Section "Uninstall"
  nsExec::Exec 'taskkill /F /T /IM ${EXE}'
  Sleep 1000
  nsExec::Exec '"$INSTDIR\${EXE}" --cleanup'
  Delete "$SMPROGRAMS\NexLimit.lnk"
  Delete "$DESKTOP\NexLimit.lnk"
  DeleteRegKey HKLM "${UNKEY}"
  DeleteRegKey HKLM "Software\NexLimit"
  Delete "$INSTDIR\${EXE}"
  Delete "$INSTDIR\${EXE}.config"
  Delete "$INSTDIR\WinDivert.dll"
  Delete /REBOOTOK "$INSTDIR\WinDivert64.sys"
  RMDir /r "$INSTDIR\licenses"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir /REBOOTOK "$INSTDIR"
  IfSilent done
  MessageBox MB_YESNO|MB_ICONQUESTION "Also delete NexLimit's settings, quota and usage history?" /SD IDNO IDNO done
    SetShellVarContext all
    RMDir /r "$APPDATA\NexLimit"
  done:
SectionEnd
