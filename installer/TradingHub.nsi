; Instalador de Trading Hub para Windows: por usuario (no pide permisos de administrador), con accesos en el menú
; Inicio y el escritorio, desinstalador en «Aplicaciones instaladas» y la opción de abrirla al terminar.
;
; Lo arman build-windows.ps1 (en Windows) y build-windows.sh (en Mac o Linux, con «brew install makensis»):
;   makensis -DVERSION=1.1.0 -DARCH=x64 -DSRC=<carpeta con TradingHub.exe publicado> -DOUT=<instalador.exe> installer/TradingHub.nsi
;
; Los datos de la app (%APPDATA%\Trading Hub y Documentos\Trading Hub) no se tocan al actualizar ni al desinstalar.

Unicode true
!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"
!include "StrFunc.nsh"
${StrStr}
${UnStrStr}

!ifndef VERSION
  !define VERSION "1.1.0"
!endif
!ifndef ARCH
  !define ARCH "x64"
!endif
!ifndef SRC
  !error "Falta -DSRC=<carpeta con TradingHub.exe publicado>"
!endif
!ifndef OUT
  !define OUT "TradingHub-Setup-${ARCH}.exe"
!endif

!define NAME "Trading Hub"
!define EXE "TradingHub.exe"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\TradingHub"

Name "${NAME}"
OutFile "${OUT}"
; siempre en su propia carpeta: así actualizar y desinstalar pueden reemplazarla entera sin tocar nada más
InstallDir "$LOCALAPPDATA\Programs\Trading Hub"
RequestExecutionLevel user
SetCompressor /SOLID lzma
ManifestDPIAware true
BrandingText "Trading Hub ${VERSION}"
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${NAME}"
VIAddVersionKey "FileDescription" "Instalador de ${NAME}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "Licencia MIT con Commons Clause"

!define MUI_ICON "..\src\TradingHub.App\Assets\tradinghub.ico"
!define MUI_UNICON "..\src\TradingHub.App\Assets\tradinghub.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "Esto instala Trading Hub ${VERSION} en tu PC.$\r$\n$\r$\nSi ya la tenés, se actualiza: tus conexiones, gráficos, estrategias e indicadores quedan como están.$\r$\n$\r$\nTocá Instalar para seguir."
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Abrir Trading Hub"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Spanish"

; Si está abierta, Windows no deja reemplazar sus archivos: pide cerrarla (sin cerrarla a la fuerza, por si está operando)
Function WaitClosed
  ${Do}
    nsExec::ExecToStack 'tasklist /FI "IMAGENAME eq ${EXE}" /NH'
    Pop $0
    Pop $1
    ${StrStr} $2 $1 "${EXE}"
    ${If} $2 == ""
      ${Break}
    ${EndIf}
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "Trading Hub está abierta. Cerrala (si tenés operaciones o estrategias corriendo, primero dejalas como quieras) y tocá Reintentar." /SD IDCANCEL IDRETRY +2
    Abort
  ${Loop}
FunctionEnd

Function un.WaitClosed
  ${Do}
    nsExec::ExecToStack 'tasklist /FI "IMAGENAME eq ${EXE}" /NH'
    Pop $0
    Pop $1
    ${UnStrStr} $2 $1 "${EXE}"
    ${If} $2 == ""
      ${Break}
    ${EndIf}
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "Trading Hub está abierta. Cerrala y tocá Reintentar." /SD IDCANCEL IDRETRY +2
    Abort
  ${Loop}
FunctionEnd

; Borra la carpeta del programa solo si es la suya (se llama «Trading Hub» y tiene TradingHub.exe): aunque alguien la
; instale con /D en otra carpeta, nunca borra otra cosa
Function CleanInstallDir
  ${GetFileName} "$INSTDIR" $0
  ${If} $0 == "Trading Hub"
  ${AndIf} ${FileExists} "$INSTDIR\${EXE}"
    SetOutPath "$TEMP"
    RMDir /r "$INSTDIR"
  ${EndIf}
FunctionEnd

Function un.CleanInstallDir
  ${GetFileName} "$INSTDIR" $0
  ${If} $0 == "Trading Hub"
  ${AndIf} ${FileExists} "$INSTDIR\${EXE}"
    SetOutPath "$TEMP"
    RMDir /r "$INSTDIR"
  ${Else}
    ; otra carpeta: solo lo que es suyo
    Delete "$INSTDIR\Desinstalar Trading Hub.exe"
  ${EndIf}
FunctionEnd

Section "Trading Hub" SecMain
  Call WaitClosed
  ; una actualización reemplaza el programa entero (en su carpeta propia, nunca otra)
  Call CleanInstallDir
  SetOutPath "$INSTDIR"
  File /r "${SRC}\*.*"
  WriteUninstaller "$INSTDIR\Desinstalar Trading Hub.exe"

  CreateShortcut "$SMPROGRAMS\Trading Hub.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0
  CreateShortcut "$DESKTOP\Trading Hub.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0

  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName" "${NAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "Publisher" "Trading Hub"
  WriteRegStr HKCU "${UNINSTKEY}" "URLInfoAbout" "https://github.com/ralphtradesz/trading-hub"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\${EXE}"
  WriteRegStr HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString" '"$INSTDIR\Desinstalar Trading Hub.exe"'
  WriteRegStr HKCU "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\Desinstalar Trading Hub.exe" /S'
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINSTKEY}" "EstimatedSize" "$0"
SectionEnd

Section "Uninstall"
  Call un.WaitClosed
  Delete "$SMPROGRAMS\Trading Hub.lnk"
  Delete "$DESKTOP\Trading Hub.lnk"
  Call un.CleanInstallDir
  DeleteRegKey HKCU "${UNINSTKEY}"
SectionEnd
