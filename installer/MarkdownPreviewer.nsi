; ============================================================================
; MarkdownPreviewer.nsi
;
; Installer for the Markdown preview handler — a shell extension that renders
; Markdown in the Windows File Explorer preview pane.
;
; Build:
;   makensis /DSTAGE_DIR="..\artifacts\publish" MarkdownPreviewer.nsi
;
; Three things in here are load-bearing and easy to get wrong:
;
;   1. SetRegView 64. makensis produces a 32-bit executable, so every HKLM
;      write would otherwise land under WOW6432Node — invisible to 64-bit
;      Explorer. The handler would register and never be called.
;
;   2. The AppID on our CLSID must be the prevhost.exe surrogate. Without it
;      the shell loads the handler in-process and a fault takes Explorer down.
;
;   3. ThreadingModel must be Apartment. The .NET comhost writes "Both", and
;      WebView2 cannot run outside an STA.
; ============================================================================

Unicode True

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"
!include "WinVer.nsh"

;--------------------------------
; Product defines
;--------------------------------

!define PRODUCT_NAME       "Markdown Preview Handler"
!define PRODUCT_SHORTNAME  "MarkdownPreviewer"
; Keep in step with Directory.Build.props, assets\web\js\preview.js and CHANGELOG.md.
!define PRODUCT_VERSION    "1.3.0"
!define PRODUCT_PUBLISHER  "Addam Boord"
!define PRODUCT_URL        "https://github.com/Cadtastic/MarkdownPreviewer"
!define README_URL         "${PRODUCT_URL}/blob/main/README.md"

; Must match [Guid] on MarkdownPreviewHandler and RegistryKeys.PreviewHandlerClsid.
!define HANDLER_CLSID      "{5B54A6AB-8765-4A71-8732-EA187093A239}"

; IID_IPreviewHandler. The presence of this subkey under a file association is
; the signal the shell looks for.
!define SHELLEX_PREVIEW    "{8895b1c6-b41f-4c1c-a562-0d564250836f}"

; AppID of the 64-bit prevhost.exe surrogate host.
!define PREVHOST_APPID     "{6d2b5079-2f0b-48dd-ab7f-97cec514d30b}"

!define COMHOST_DLL        "MarkdownPreviewer.Shell.comhost.dll"
!define UNINSTALL_KEY      "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_SHORTNAME}"
!define SETTINGS_KEY       "SOFTWARE\${PRODUCT_SHORTNAME}"

; Where `dotnet publish` put the binaries. Override with /DSTAGE_DIR=...
!ifndef STAGE_DIR
  !define STAGE_DIR "..\artifacts\publish"
!endif

;--------------------------------
; Installer attributes
;--------------------------------

Name "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "..\artifacts\MarkdownPreviewer-${PRODUCT_VERSION}-x64-Setup.exe"
InstallDir "$PROGRAMFILES64\${PRODUCT_SHORTNAME}"
InstallDirRegKey HKLM "${SETTINGS_KEY}" "InstallDir"

; Writing to HKLM\SOFTWARE\Classes and Program Files both require elevation.
RequestExecutionLevel admin
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUnInstDetails show

;--------------------------------
; Variables
;--------------------------------

Var DotNetFound
Var WebView2Found
Var ReadmeOpened

;--------------------------------
; MUI configuration  (all defines BEFORE page insertions)
;--------------------------------

!define MUI_ABORTWARNING
!define MUI_ICON   "assets\installer.ico"
!define MUI_UNICON "assets\uninstaller.ico"

!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_BITMAP "assets\header.bmp"
!define MUI_HEADERIMAGE_RIGHT

!define MUI_WELCOMEFINISHPAGE_BITMAP   "assets\wizard.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "assets\wizard.bmp"

!define MUI_WELCOMEPAGE_TITLE "${PRODUCT_NAME} ${PRODUCT_VERSION}"
!define MUI_WELCOMEPAGE_TEXT  "This will install a shell extension that renders Markdown files in the File Explorer preview pane — code highlighting, Mermaid diagrams, LaTeX math, and automatic light/dark theming.$\r$\n$\r$\nExplorer must be restarted at the end of setup for the handler to load. Setup can do that for you.$\r$\n$\r$\nClose any Explorer windows you care about before continuing."

!define MUI_COMPONENTSPAGE_SMALLDESC

!define MUI_FINISHPAGE_NOAUTOCLOSE
!define MUI_FINISHPAGE_TEXT "The Markdown preview handler is installed.$\r$\n$\r$\nIn Explorer, turn the preview pane on with View > Show > Preview pane (or Alt+P), then select a .md file.$\r$\n$\r$\nIf the pane stays blank, run scripts\Test-MarkdownPreviewHandler.ps1 from the install folder — it checks every prerequisite and registry entry in the order the shell resolves them."
!define MUI_FINISHPAGE_RUN ""
!define MUI_FINISHPAGE_RUN_TEXT "Restart File Explorer now (required)"
!define MUI_FINISHPAGE_RUN_FUNCTION "RestartExplorer"
; The README opens on GitHub, not from the install folder, and through a custom
; function: MUI2 runs the finish page's Run action BEFORE its ShowReadme action,
; and the browser must be launched before Explorer is killed and relaunched. So
; RestartExplorer opens the README itself (when the box is ticked) and OpenReadme
; only fires for the readme-without-restart combination.
!define MUI_FINISHPAGE_SHOWREADME "${README_URL}"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Open the README (on GitHub)"
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION "OpenReadme"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
!define MUI_FINISHPAGE_LINK "Project home"
!define MUI_FINISHPAGE_LINK_LOCATION "${PRODUCT_URL}"

;--------------------------------
; Pages
;--------------------------------

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "resources\license.txt"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

;--------------------------------
; Language  (AFTER pages)
;--------------------------------

!insertmacro MUI_LANGUAGE "English"

;--------------------------------
; Version information  (AFTER the language file — ${LANG_ENGLISH} is defined
; by MUI_LANGUAGE; referencing it earlier is a compile error)
;--------------------------------

VIProductVersion "${PRODUCT_VERSION}.0"
VIFileVersion "${PRODUCT_VERSION}.0"
VIAddVersionKey /LANG=${LANG_ENGLISH} "ProductName"     "${PRODUCT_NAME}"
VIAddVersionKey /LANG=${LANG_ENGLISH} "ProductVersion"  "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=${LANG_ENGLISH} "CompanyName"     "${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=${LANG_ENGLISH} "LegalCopyright"  "Copyright (c) 2026 ${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=${LANG_ENGLISH} "FileDescription" "${PRODUCT_NAME} Setup"
VIAddVersionKey /LANG=${LANG_ENGLISH} "FileVersion"     "${PRODUCT_VERSION}"

; ============================================================================
; Macros
; ============================================================================

; ── Associate one extension with our preview handler ──
; The shell resolves the ProgID chain BEFORE the extension key: if the extension
; has a ProgID (its default value, e.g. "md_auto_file" once a user picks a
; default app) and that ProgID carries its own preview-handler entry, the ProgID
; entry wins and the extension-level one is never consulted. So register at both.
;
; If another preview handler already owns the extension (at either level), the
; user is asked before it is replaced — silently stealing an association the
; user may have chosen on purpose would be rude. Declining leaves that
; extension entirely alone. Silent installs (/S) replace, matching the old
; behaviour.
;
; Registers: $R0 existing handler CLSID, $R1 its friendly name, $R2 the
; extension's ProgID, $R3 replace/keep verdict.
!macro RegisterPreviewExtension Extension
  ; What would the shell use today? Mirror its resolution order: ProgID first.
  StrCpy $R0 ""
  ReadRegStr $R2 HKLM "SOFTWARE\Classes\${Extension}" ""
  ${If} $R2 != ""
    ReadRegStr $R0 HKLM "SOFTWARE\Classes\$R2\shellex\${SHELLEX_PREVIEW}" ""
  ${EndIf}
  ${If} $R0 == ""
    ReadRegStr $R0 HKLM "SOFTWARE\Classes\${Extension}\shellex\${SHELLEX_PREVIEW}" ""
  ${EndIf}

  StrCpy $R3 "replace"
  ${If} $R0 != ""
  ${AndIf} $R0 != "${HANDLER_CLSID}"
    ; Name the incumbent: the shell's enumeration list first, then the CLSID's
    ; display name, then the raw CLSID so the prompt is never blank.
    ReadRegStr $R1 HKLM "SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers" $R0
    ${If} $R1 == ""
      ReadRegStr $R1 HKLM "SOFTWARE\Classes\CLSID\$R0" ""
    ${EndIf}
    ${If} $R1 == ""
      StrCpy $R1 "an unidentified handler ($R0)"
    ${EndIf}

    MessageBox MB_YESNO|MB_ICONQUESTION \
      "${Extension} files already have a preview handler:$\r$\n$\r$\n    $R1$\r$\n$\r$\nReplace it with ${PRODUCT_NAME}?$\r$\n$\r$\nChoosing No leaves ${Extension} previews unchanged." \
      /SD IDYES IDYES +2
      StrCpy $R3 "keep"
  ${EndIf}

  ${If} $R3 == "replace"
    DetailPrint "Associating ${Extension} with the Markdown preview handler..."
    WriteRegStr HKLM "SOFTWARE\Classes\${Extension}\shellex\${SHELLEX_PREVIEW}" "" "${HANDLER_CLSID}"
    ${If} $R2 != ""
      DetailPrint "  ...and its ProgID ($R2), which the shell resolves first."
      WriteRegStr HKLM "SOFTWARE\Classes\$R2\shellex\${SHELLEX_PREVIEW}" "" "${HANDLER_CLSID}"
    ${EndIf}
  ${Else}
    DetailPrint "Keeping $R1 as the preview handler for ${Extension}."
  ${EndIf}
!macroend

!macro UnregisterPreviewExtension Extension
  ; Only delete if it is still ours — another handler may have taken over since
  ; install, and clobbering it would be rude.
  ReadRegStr $0 HKLM "SOFTWARE\Classes\${Extension}\shellex\${SHELLEX_PREVIEW}" ""
  ${If} $0 == "${HANDLER_CLSID}"
    DeleteRegKey HKLM "SOFTWARE\Classes\${Extension}\shellex\${SHELLEX_PREVIEW}"
    ; Tidy up the now-empty shellex parent, but never the extension key itself.
    DeleteRegKey /ifempty HKLM "SOFTWARE\Classes\${Extension}\shellex"
  ${EndIf}
  ; Same for the ProgID entry, if the extension still has one and it is ours.
  ReadRegStr $1 HKLM "SOFTWARE\Classes\${Extension}" ""
  ${If} $1 != ""
    ReadRegStr $0 HKLM "SOFTWARE\Classes\$1\shellex\${SHELLEX_PREVIEW}" ""
    ${If} $0 == "${HANDLER_CLSID}"
      DeleteRegKey HKLM "SOFTWARE\Classes\$1\shellex\${SHELLEX_PREVIEW}"
      DeleteRegKey /ifempty HKLM "SOFTWARE\Classes\$1\shellex"
    ${EndIf}
  ${EndIf}
!macroend

; ============================================================================
; Sections
; ============================================================================

Section "Preview handler (required)" SEC_CORE
  SectionIn RO

  ; The preview host caches loaded handler DLLs, and it outlives the Explorer
  ; window that started it. Leaving it running means File commands fail with a
  ; sharing violation on upgrade.
  DetailPrint "Closing any running preview hosts..."
  nsExec::ExecToLog 'taskkill.exe /F /IM prevhost.exe'
  Pop $0
  Sleep 400

  SetOutPath "$INSTDIR"
  SetOverwrite on
  File /r "${STAGE_DIR}\*.*"

  ; Diagnostics and manual (un)registration, shipped alongside.
  SetOutPath "$INSTDIR\scripts"
  File "..\scripts\Register-MarkdownPreviewHandler.ps1"
  File "..\scripts\Test-MarkdownPreviewHandler.ps1"
  File "..\scripts\Restart-Explorer.ps1"

  SetOutPath "$INSTDIR"
  File "..\README.md"

  ; ── COM class registration ────────────────────────────────────────────────
  ; This setup executable is 32-bit (makensis always emits x86), so WOW64 file
  ; system redirection silently turns $SYSDIR\regsvr32.exe into the SysWOW64
  ; copy — the 32-bit regsvr32, which cannot load an x64 comhost DLL and would
  ; register into the WOW6432Node view even if it could. Redirection must be
  ; off so the real 64-bit System32\regsvr32.exe runs.
  DetailPrint "Registering the COM class..."
  ${DisableX64FSRedirection}
  ExecWait '"$SYSDIR\regsvr32.exe" /s "$INSTDIR\${COMHOST_DLL}"' $0
  ${EnableX64FSRedirection}
  ${If} $0 != 0
    DetailPrint "regsvr32 failed with 0x$0."
    MessageBox MB_OK|MB_ICONSTOP \
      "Registering the preview handler failed (regsvr32 returned 0x$0).$\r$\n$\r$\nThe usual cause is a missing .NET 8 Desktop Runtime (x64). COM hosting in .NET is framework-dependent by design, so the runtime must be present on this machine.$\r$\n$\r$\nInstall it from https://dotnet.microsoft.com/download/dotnet/8.0 and run setup again."
    Abort "COM registration failed."
  ${EndIf}

  ; ── Surrogate, threading model, display name ──────────────────────────────
  ; regsvr32 wrote the CLSID and InprocServer32; these three values are what
  ; turn a plain COM server into a well-behaved preview handler.
  DetailPrint "Configuring the out-of-process surrogate..."
  WriteRegStr HKLM "SOFTWARE\Classes\CLSID\${HANDLER_CLSID}" "" "${PRODUCT_NAME}"
  WriteRegStr HKLM "SOFTWARE\Classes\CLSID\${HANDLER_CLSID}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKLM "SOFTWARE\Classes\CLSID\${HANDLER_CLSID}" "AppID" "${PREVHOST_APPID}"
  WriteRegStr HKLM "SOFTWARE\Classes\CLSID\${HANDLER_CLSID}\InprocServer32" "ThreadingModel" "Apartment"

  ; The shell hosts preview handlers in a LOW-INTEGRITY prevhost by default.
  ; A low-IL process cannot write to %LOCALAPPDATA% (logs, WebView2 profile),
  ; and WebView2 does not support running at low integrity at all — the result
  ; is a blank pane and, once the wedged STA blocks the shell's next synchronous
  ; call, a hung Explorer. Opt out so prevhost hosts us at medium integrity.
  WriteRegDWORD HKLM "SOFTWARE\Classes\CLSID\${HANDLER_CLSID}" "DisableLowILProcessIsolation" 1

  ; ── The shell's enumeration list ──────────────────────────────────────────
  WriteRegStr HKLM "SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers" \
    "${HANDLER_CLSID}" "${PRODUCT_NAME}"

  ; ── Install location, for upgrades and the uninstaller ────────────────────
  WriteRegStr HKLM "${SETTINGS_KEY}" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "${SETTINGS_KEY}" "Version" "${PRODUCT_VERSION}"

  ; ── Default settings, only where absent ───────────────────────────────────
  ; An upgrade must not silently reset choices the user made. Each value is
  ; written only if it does not already exist.
  Call SeedDefaultSettings

  ; ── Add/Remove Programs ───────────────────────────────────────────────────
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr   HKLM "${UNINSTALL_KEY}" "DisplayName"     "${PRODUCT_NAME}"
  WriteRegStr   HKLM "${UNINSTALL_KEY}" "DisplayVersion"  "${PRODUCT_VERSION}"
  WriteRegStr   HKLM "${UNINSTALL_KEY}" "DisplayIcon"     "$INSTDIR\Uninstall.exe,0"
  WriteRegStr   HKLM "${UNINSTALL_KEY}" "Publisher"       "${PRODUCT_PUBLISHER}"
  WriteRegStr   HKLM "${UNINSTALL_KEY}" "URLInfoAbout"    "${PRODUCT_URL}"
  WriteRegStr   HKLM "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKLM "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKLM "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoRepair" 1

  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "EstimatedSize" "$0"
SectionEnd

Section "Associate .md and .markdown" SEC_PRIMARY_EXT
  !insertmacro RegisterPreviewExtension ".md"
  !insertmacro RegisterPreviewExtension ".markdown"
SectionEnd

Section "Associate other Markdown extensions" SEC_OTHER_EXT
  !insertmacro RegisterPreviewExtension ".mdown"
  !insertmacro RegisterPreviewExtension ".mkd"
  !insertmacro RegisterPreviewExtension ".mkdn"
  !insertmacro RegisterPreviewExtension ".mdwn"
  !insertmacro RegisterPreviewExtension ".mdtxt"
  !insertmacro RegisterPreviewExtension ".mdtext"
SectionEnd

Section "Start Menu shortcuts" SEC_SHORTCUTS
  CreateDirectory "$SMPROGRAMS\${PRODUCT_NAME}"
  CreateShortcut "$SMPROGRAMS\${PRODUCT_NAME}\Diagnose preview handler.lnk" \
    "$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" \
    '-NoExit -ExecutionPolicy Bypass -File "$INSTDIR\scripts\Test-MarkdownPreviewHandler.ps1"' \
    "$INSTDIR\assets\installer.ico" 0
  CreateShortcut "$SMPROGRAMS\${PRODUCT_NAME}\Uninstall.lnk" "$INSTDIR\Uninstall.exe"
SectionEnd

Section "-Finalise" SEC_FINALISE
  ; Tell the shell its association data changed, so it picks up the new handler
  ; without waiting for a full restart where it can.
  DetailPrint "Notifying the shell of association changes..."
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0x1000, p 0, p 0)'
SectionEnd

;--------------------------------
; Component descriptions
;--------------------------------

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_CORE} \
    "The shell extension, render assets, and diagnostic scripts. Required."
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_PRIMARY_EXT} \
    "Show previews for .md and .markdown files."
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_OTHER_EXT} \
    "Also handle the less common spellings: .mdown, .mkd, .mkdn, .mdwn, .mdtxt, .mdtext."
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_SHORTCUTS} \
    "Start Menu entries for the diagnostic script and the uninstaller."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ============================================================================
; Functions
; ============================================================================

Function .onInit
  ; ── 64-bit only ──────────────────────────────────────────────────────────
  ; prevhost.exe is 64-bit on every supported Windows release, and the handler
  ; is built x64. A 32-bit machine cannot load it at all.
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP \
      "${PRODUCT_NAME} requires 64-bit Windows."
    Abort
  ${EndIf}

  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP \
      "${PRODUCT_NAME} requires Windows 10 or later."
    Abort
  ${EndIf}

  ; Everything from here writes to the 64-bit registry view. Without this a
  ; 32-bit makensis build silently redirects HKLM\SOFTWARE\Classes into
  ; WOW6432Node, where 64-bit Explorer will never look.
  SetRegView 64

  Call CheckDotNetDesktopRuntime
  Call CheckWebView2Runtime

  ${If} $DotNetFound == "0"
    MessageBox MB_YESNO|MB_ICONEXCLAMATION \
      ".NET 8 Desktop Runtime (x64) was not found.$\r$\n$\r$\nIt is required: .NET COM hosting cannot be deployed self-contained, so the shared runtime must be installed on this machine.$\r$\n$\r$\nOpen the download page now?" \
      IDYES dotnet_download IDNO dotnet_abort
    dotnet_download:
      ExecShell "open" "https://dotnet.microsoft.com/download/dotnet/8.0"
    dotnet_abort:
      Abort "Install the .NET 8 Desktop Runtime (x64), then run setup again."
  ${EndIf}

  ${If} $WebView2Found == "0"
    MessageBox MB_YESNO|MB_ICONEXCLAMATION \
      "The Microsoft Edge WebView2 runtime was not found.$\r$\n$\r$\nIt renders the Markdown. Setup can continue, but every preview will show an error until it is installed.$\r$\n$\r$\nOpen the download page now?" \
      IDYES webview_download IDNO webview_continue
    webview_download:
      ExecShell "open" "https://developer.microsoft.com/microsoft-edge/webview2/"
    webview_continue:
  ${EndIf}
FunctionEnd

Function un.onInit
  SetRegView 64
FunctionEnd

; ── .NET 8 Desktop Runtime detection ────────────────────────────────────────
;
; Enumerating the shared-framework directory rather than shelling out to
; `dotnet --list-runtimes`: the CLI is not necessarily on PATH (especially in a
; service or SCCM context), and a missing PATH entry is not the same fact as a
; missing runtime.
Function CheckDotNetDesktopRuntime
  StrCpy $DotNetFound "0"

  StrCpy $R0 "$PROGRAMFILES64\dotnet\shared\Microsoft.WindowsDesktop.App"
  ${IfNot} ${FileExists} "$R0\*.*"
    ; Fall back to the CLI in case dotnet lives somewhere non-default.
    nsExec::ExecToStack '"dotnet" --list-runtimes'
    Pop $0
    Pop $1
    ${If} $0 == 0
      Push $1
      Push "Microsoft.WindowsDesktop.App 8."
      Call StrContains
      Pop $2
      ${If} $2 != ""
        StrCpy $DotNetFound "1"
      ${EndIf}
    ${EndIf}
    Return
  ${EndIf}

  FindFirst $R1 $R2 "$R0\8.*"
  ${If} $R2 != ""
    StrCpy $DotNetFound "1"
    DetailPrint ".NET Desktop Runtime found: $R2"
  ${EndIf}
  FindClose $R1
FunctionEnd

; ── WebView2 Evergreen runtime detection ────────────────────────────────────
;
; The documented probe is the `pv` value under the WebView2 Edge Update client
; key. Checked in all three locations because a per-user install writes HKCU,
; and the WOW6432Node path is where a 64-bit machine records it.
Function CheckWebView2Runtime
  StrCpy $WebView2Found "0"

  ReadRegStr $0 HKLM "SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${If} $0 != ""
  ${AndIf} $0 != "0.0.0.0"
    StrCpy $WebView2Found "1"
    DetailPrint "WebView2 runtime found: $0"
    Return
  ${EndIf}

  ReadRegStr $0 HKLM "SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${If} $0 != ""
  ${AndIf} $0 != "0.0.0.0"
    StrCpy $WebView2Found "1"
    Return
  ${EndIf}

  ReadRegStr $0 HKCU "SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${If} $0 != ""
  ${AndIf} $0 != "0.0.0.0"
    StrCpy $WebView2Found "1"
  ${EndIf}
FunctionEnd

; ── Seed defaults without overwriting user choices ───────────────────────────
Function SeedDefaultSettings
  !macro SeedDword Name Value
    ClearErrors
    ReadRegDWORD $0 HKLM "${SETTINGS_KEY}" "${Name}"
    ${If} ${Errors}
      WriteRegDWORD HKLM "${SETTINGS_KEY}" "${Name}" ${Value}
    ${EndIf}
  !macroend

  !insertmacro SeedDword "Highlight"         1
  !insertmacro SeedDword "Mermaid"           1
  !insertmacro SeedDword "Math"              1
  !insertmacro SeedDword "SingleDollarMath"  0
  !insertmacro SeedDword "AllowRawHtml"      1
  !insertmacro SeedDword "AllowRemoteImages" 0
  !insertmacro SeedDword "TaskLists"         1
  !insertmacro SeedDword "ShowFrontMatter"   1
  !insertmacro SeedDword "FollowSystemTheme" 1
  !insertmacro SeedDword "FontScalePercent"  100
FunctionEnd

; ── Open the README on GitHub (finish page action) ──────────────────────────
Function OpenReadme
  ; RestartExplorer may already have opened it — see the finish-page defines.
  ${If} $ReadmeOpened != 1
    ExecShell "open" "${README_URL}"
  ${EndIf}
FunctionEnd

; ── Restart Explorer (finish page action) ────────────────────────────────────
Function RestartExplorer
  ; If the README box is also ticked, open it first: MUI2 would otherwise run
  ; this function, kill Explorer, and only then try to open the README while
  ; the shell is mid-relaunch.
  ${NSD_GetState} $mui.FinishPage.ShowReadme $0
  ${If} $0 = ${BST_CHECKED}
    ExecShell "open" "${README_URL}"
    StrCpy $ReadmeOpened 1
  ${EndIf}

  DetailPrint "Restarting File Explorer..."
  nsExec::ExecToLog 'taskkill.exe /F /IM prevhost.exe'
  Pop $0
  nsExec::ExecToLog 'taskkill.exe /F /IM explorer.exe'
  Pop $0
  Sleep 1200

  ; Windows normally relaunches the shell on its own; start it explicitly in
  ; case the restart policy says otherwise.
  Exec '"$WINDIR\explorer.exe"'
FunctionEnd

; ── StrContains: needle in haystack ─────────────────────────────────────────
; Stack in:  haystack, needle       Stack out: matched substring or ""
Function StrContains
  Exch $R0            ; needle
  Exch
  Exch $R1            ; haystack
  Push $R2
  Push $R3
  Push $R4
  Push $R5

  StrCpy $R2 -1
  StrLen $R3 $R0
  StrLen $R4 $R1
  StrCpy $R5 ""

  loop:
    IntOp $R2 $R2 + 1
    StrCpy $R5 $R1 $R3 $R2
    ${If} $R5 == $R0
      Goto done
    ${EndIf}
    IntCmp $R2 $R4 notfound notfound loop

  notfound:
    StrCpy $R5 ""

  done:
    StrCpy $R0 $R5
    Pop $R5
    Pop $R4
    Pop $R3
    Pop $R2
    Pop $R1
    Exch $R0
FunctionEnd

; ============================================================================
; Uninstaller
; ============================================================================

Section "Uninstall"
  SetRegView 64

  DetailPrint "Closing preview hosts..."
  nsExec::ExecToLog 'taskkill.exe /F /IM prevhost.exe'
  Pop $0
  Sleep 400

  ; ── Extension associations ────────────────────────────────────────────────
  !insertmacro UnregisterPreviewExtension ".md"
  !insertmacro UnregisterPreviewExtension ".markdown"
  !insertmacro UnregisterPreviewExtension ".mdown"
  !insertmacro UnregisterPreviewExtension ".mkd"
  !insertmacro UnregisterPreviewExtension ".mkdn"
  !insertmacro UnregisterPreviewExtension ".mdwn"
  !insertmacro UnregisterPreviewExtension ".mdtxt"
  !insertmacro UnregisterPreviewExtension ".mdtext"

  ; ── Shell enumeration list ────────────────────────────────────────────────
  DeleteRegValue HKLM "SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers" "${HANDLER_CLSID}"

  ; ── COM class ─────────────────────────────────────────────────────────────
  ${If} ${FileExists} "$INSTDIR\${COMHOST_DLL}"
    DetailPrint "Unregistering the COM class..."
    ; Same WOW64 dance as the installer: force the 64-bit regsvr32.
    ${DisableX64FSRedirection}
    ExecWait '"$SYSDIR\regsvr32.exe" /s /u "$INSTDIR\${COMHOST_DLL}"' $0
    ${EnableX64FSRedirection}
  ${EndIf}
  ; Remove the key regardless: if regsvr32 could not run, a stale CLSID pointing
  ; at a deleted DLL makes every future preview attempt fail slowly.
  DeleteRegKey HKLM "SOFTWARE\Classes\CLSID\${HANDLER_CLSID}"

  ; ── Files ─────────────────────────────────────────────────────────────────
  RMDir /r "$INSTDIR\assets"
  RMDir /r "$INSTDIR\scripts"
  Delete "$INSTDIR\*.dll"
  Delete "$INSTDIR\*.json"
  Delete "$INSTDIR\*.exe"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"

  RMDir /r "$SMPROGRAMS\${PRODUCT_NAME}"

  ; ── Registry ──────────────────────────────────────────────────────────────
  DeleteRegKey HKLM "${UNINSTALL_KEY}"
  ; Machine-wide settings go; per-user preferences under HKCU are deliberately
  ; left alone so a reinstall keeps them.
  DeleteRegKey HKLM "${SETTINGS_KEY}"

  ; ── Cached browser profile ────────────────────────────────────────────────
  ; Ours alone — created by WebView2EnvironmentProvider under our own folder.
  RMDir /r "$LOCALAPPDATA\${PRODUCT_SHORTNAME}\WebView2"
  RMDir /r "$LOCALAPPDATA\${PRODUCT_SHORTNAME}\logs"
  RMDir "$LOCALAPPDATA\${PRODUCT_SHORTNAME}"

  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0x1000, p 0, p 0)'

  DetailPrint "Restart File Explorer to complete removal."
SectionEnd
