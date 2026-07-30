<#
.SYNOPSIS
    Registers the Markdown preview handler with the Windows Shell.

.DESCRIPTION
    regsvr32 on the generated .comhost.dll writes the CLSID and InprocServer32
    entries, but a preview handler needs three more things that COM hosting knows
    nothing about:

      1. An AppID pointing at the prevhost.exe surrogate, so the handler runs
         out-of-process. Without it the shell loads us in-process and a crash takes
         Explorer with it.
      2. ThreadingModel = Apartment. The .NET comhost writes "Both"; preview
         handlers are documented as Apartment, and WebView2 requires an STA.
      3. A shellex association per file extension, plus an entry in the
         PreviewHandlers list the shell enumerates.

    This script does all of it, and -Unregister reverses it exactly.

.PARAMETER InstallPath
    Directory containing MarkdownPreviewer.Shell.comhost.dll and assets\web.

.PARAMETER Scope
    Machine (HKLM, requires elevation) or User (HKCU, no elevation).

.PARAMETER ProgId
    Additionally register under a specific ProgID. Use when an editor has claimed
    the .md association and its ProgID shadows the bare extension key.

.EXAMPLE
    .\Register-MarkdownPreviewHandler.ps1 -InstallPath 'C:\Program Files\MarkdownPreviewer'

.EXAMPLE
    .\Register-MarkdownPreviewHandler.ps1 -Unregister -Scope User
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter()]
    [string] $InstallPath,

    [Parameter()]
    [ValidateSet('Machine', 'User')]
    [string] $Scope = 'Machine',

    [Parameter()]
    [string[]] $Extensions = @(
        '.md', '.markdown', '.mdown', '.mkd', '.mkdn', '.mdwn', '.mdtxt', '.mdtext'
    ),

    [Parameter()]
    [string] $ProgId,

    [Parameter()]
    [switch] $Unregister,

    [Parameter()]
    [switch] $SkipComRegistration
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- constants ---------------------------------------------------------------

# IID_IPreviewHandler. The presence of this subkey under a file association is
# what tells the shell "there is a preview handler here".
$PreviewHandlerShellExId = '{8895b1c6-b41f-4c1c-a562-0d564250836f}'

# Our handler's CLSID. Must match [Guid] on MarkdownPreviewHandler and
# RegistryKeys.PreviewHandlerClsid.
$Clsid = '{5B54A6AB-8765-4A71-8732-EA187093A239}'
$FriendlyName = 'Markdown Preview Handler'

# AppID of the 64-bit prevhost.exe surrogate. (A 32-bit handler on 64-bit Windows
# would use {534A1E02-D58F-44f0-B58B-36CBED287C7C}; we build x64 only.)
$PrevHostAppId = '{6d2b5079-2f0b-48dd-ab7f-97cec514d30b}'

$ComHostDllName = 'MarkdownPreviewer.Shell.comhost.dll'

# --- helpers -----------------------------------------------------------------

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-RootPaths {
    param([string] $Scope)

    if ($Scope -eq 'Machine') {
        return [pscustomobject]@{
            Classes         = 'HKLM:\SOFTWARE\Classes'
            PreviewHandlers = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers'
            Settings        = 'HKLM:\SOFTWARE\MarkdownPreviewer'
        }
    }

    return [pscustomobject]@{
        Classes         = 'HKCU:\SOFTWARE\Classes'
        PreviewHandlers = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers'
        Settings        = 'HKCU:\SOFTWARE\MarkdownPreviewer'
    }
}

function New-RegistryKeyPath {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -Path $Path -Force | Out-Null
    }
}

function Remove-RegistryKeyIfPresent {
    param([string] $Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-Regsvr32 {
    param([string] $DllPath, [switch] $Uninstall)

    $arguments = @('/s')
    if ($Uninstall) { $arguments += '/u' }
    $arguments += "`"$DllPath`""

    $process = Start-Process -FilePath "$env:SystemRoot\System32\regsvr32.exe" `
        -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden

    if ($process.ExitCode -ne 0) {
        throw @"
regsvr32 failed with exit code 0x$('{0:X8}' -f $process.ExitCode) for:
  $DllPath

The usual causes, in order of likelihood:
  * The .NET 8 Desktop Runtime (x64) is not installed. COM hosting is
    framework-dependent by design — self-contained COM hosts are not supported by
    .NET and fail here with 0x80008093.
  * The DLL was published self-contained. Rebuild with SelfContained=false.
  * The command is not running elevated (machine scope requires it).
"@
    }
}

# --- validation --------------------------------------------------------------

if ($Scope -eq 'Machine' -and -not (Test-Elevated)) {
    throw 'Machine-scope registration writes to HKLM and needs an elevated PowerShell session. Use -Scope User to register for the current user only.'
}

$roots = Get-RootPaths -Scope $Scope

if (-not $Unregister) {
    if ([string]::IsNullOrWhiteSpace($InstallPath)) {
        $InstallPath = $PSScriptRoot
    }

    $InstallPath = (Resolve-Path -LiteralPath $InstallPath).Path
    $comHostPath = Join-Path $InstallPath $ComHostDllName

    if (-not (Test-Path -LiteralPath $comHostPath)) {
        throw "Could not find $ComHostDllName in '$InstallPath'. Build the Shell project (Release|x64) and point -InstallPath at its output, or at the install directory."
    }

    $indexPath = Join-Path $InstallPath 'assets\web\index.html'
    if (-not (Test-Path -LiteralPath $indexPath)) {
        Write-Warning "Render assets were not found at '$indexPath'. The handler will register but every preview will show an error until the assets are present."
    }
}

# --- unregister --------------------------------------------------------------

if ($Unregister) {
    Write-Host "Unregistering the Markdown preview handler ($Scope scope)..." -ForegroundColor Cyan

    foreach ($extension in $Extensions) {
        $path = Join-Path $roots.Classes "$extension\shellex\$PreviewHandlerShellExId"
        if ($PSCmdlet.ShouldProcess($path, 'Remove preview handler association')) {
            Remove-RegistryKeyIfPresent -Path $path
        }
    }

    if ($ProgId) {
        $path = Join-Path $roots.Classes "$ProgId\shellex\$PreviewHandlerShellExId"
        if ($PSCmdlet.ShouldProcess($path, 'Remove preview handler association')) {
            Remove-RegistryKeyIfPresent -Path $path
        }
    }

    if (Test-Path -LiteralPath $roots.PreviewHandlers) {
        if ($PSCmdlet.ShouldProcess($roots.PreviewHandlers, "Remove $Clsid from the preview handler list")) {
            Remove-ItemProperty -LiteralPath $roots.PreviewHandlers -Name $Clsid -ErrorAction SilentlyContinue
        }
    }

    if (-not $SkipComRegistration) {
        $candidate = if ($InstallPath) { Join-Path $InstallPath $ComHostDllName } else { $null }
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            if ($PSCmdlet.ShouldProcess($candidate, 'regsvr32 /u')) {
                Invoke-Regsvr32 -DllPath $candidate -Uninstall
            }
        }
        else {
            # The binary may already be gone (uninstall order). Clean the CLSID by hand.
            $clsidPath = Join-Path $roots.Classes "CLSID\$Clsid"
            if ($PSCmdlet.ShouldProcess($clsidPath, 'Remove CLSID registration')) {
                Remove-RegistryKeyIfPresent -Path $clsidPath
            }
        }
    }

    Write-Host 'Unregistered. Restart File Explorer for the change to take effect.' -ForegroundColor Green
    return
}

# --- register ----------------------------------------------------------------

Write-Host "Registering the Markdown preview handler from '$InstallPath' ($Scope scope)..." -ForegroundColor Cyan

# 1. COM class registration (CLSID + InprocServer32), done by the .NET comhost.
if (-not $SkipComRegistration) {
    if ($PSCmdlet.ShouldProcess($comHostPath, 'regsvr32')) {
        Invoke-Regsvr32 -DllPath $comHostPath
        Write-Host '  [ok] COM class registered.' -ForegroundColor DarkGray
    }
}

# 2. Surrogate + threading model + display name on the CLSID key.
$clsidPath = Join-Path $roots.Classes "CLSID\$Clsid"
if ($PSCmdlet.ShouldProcess($clsidPath, 'Configure surrogate and threading model')) {
    New-RegistryKeyPath -Path $clsidPath
    Set-ItemProperty -LiteralPath $clsidPath -Name '(default)'  -Value $FriendlyName
    Set-ItemProperty -LiteralPath $clsidPath -Name 'DisplayName' -Value $FriendlyName
    Set-ItemProperty -LiteralPath $clsidPath -Name 'AppID'       -Value $PrevHostAppId

    # Prevhost hosts preview handlers at LOW integrity by default; a low-IL
    # process cannot write %LOCALAPPDATA% and WebView2 cannot run at low IL at
    # all. Opt out so the handler runs at medium integrity.
    Set-ItemProperty -LiteralPath $clsidPath -Name 'DisableLowILProcessIsolation' -Value 1 -Type DWord

    $inprocPath = Join-Path $clsidPath 'InprocServer32'
    New-RegistryKeyPath -Path $inprocPath
    # regsvr32 writes "Both"; the shell documents Apartment for preview handlers,
    # and WebView2 will not run outside an STA.
    Set-ItemProperty -LiteralPath $inprocPath -Name 'ThreadingModel' -Value 'Apartment'

    Write-Host '  [ok] Surrogate AppID and Apartment threading model set.' -ForegroundColor DarkGray
}

# 3. File-extension associations.
$targets = @($Extensions)
if ($ProgId) { $targets += $ProgId }

foreach ($target in $targets) {
    $path = Join-Path $roots.Classes "$target\shellex\$PreviewHandlerShellExId"
    if ($PSCmdlet.ShouldProcess($path, 'Associate preview handler')) {
        New-RegistryKeyPath -Path $path
        Set-ItemProperty -LiteralPath $path -Name '(default)' -Value $Clsid
    }
}
Write-Host "  [ok] Associated with: $($targets -join ', ')" -ForegroundColor DarkGray

# 4. The shell's enumeration list.
if ($PSCmdlet.ShouldProcess($roots.PreviewHandlers, 'Add to the preview handler list')) {
    New-RegistryKeyPath -Path $roots.PreviewHandlers
    Set-ItemProperty -LiteralPath $roots.PreviewHandlers -Name $Clsid -Value $FriendlyName
    Write-Host '  [ok] Added to the PreviewHandlers list.' -ForegroundColor DarkGray
}

# 5. Default settings, only where absent — never stomp a user's choices on upgrade.
if ($PSCmdlet.ShouldProcess($roots.Settings, 'Seed default settings')) {
    New-RegistryKeyPath -Path $roots.Settings
    $defaults = @{
        Highlight         = 1
        Mermaid           = 1
        Math              = 1
        SingleDollarMath  = 0
        AllowRawHtml      = 0
        TaskLists         = 1
        ShowFrontMatter   = 1
        FollowSystemTheme = 1
        FontScalePercent  = 100
    }

    $existing = Get-Item -LiteralPath $roots.Settings
    foreach ($name in $defaults.Keys) {
        if ($existing.GetValue($name, $null) -eq $null) {
            Set-ItemProperty -LiteralPath $roots.Settings -Name $name -Value $defaults[$name] -Type DWord
        }
    }
}

Write-Host ''
Write-Host 'Registered successfully.' -ForegroundColor Green
Write-Host 'Restart File Explorer to load the handler:' -ForegroundColor Yellow
Write-Host '    Stop-Process -Name explorer -Force' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Then in Explorer: View > Show > Preview pane, and select a .md file.' -ForegroundColor Yellow
