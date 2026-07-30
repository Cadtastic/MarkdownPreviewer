<#
.SYNOPSIS
    Diagnoses why the Markdown preview pane is not working.

.DESCRIPTION
    Checks, in the order the shell itself resolves them, every prerequisite and
    registry entry the handler depends on, and reports the first thing that is
    wrong. Written because a preview handler's native failure mode is total
    silence: no error, no event log entry, just an empty pane.

.EXAMPLE
    .\Test-MarkdownPreviewHandler.ps1 -Extension .md
#>
[CmdletBinding()]
param(
    [Parameter()]
    [string] $Extension = '.md'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

$Clsid = '{5B54A6AB-8765-4A71-8732-EA187093A239}'
$PreviewHandlerShellExId = '{8895b1c6-b41f-4c1c-a562-0d564250836f}'
$PrevHostAppId = '{6d2b5079-2f0b-48dd-ab7f-97cec514d30b}'

$issues = [System.Collections.Generic.List[string]]::new()

function Write-Check {
    param([string] $Label, [bool] $Ok, [string] $Detail = '')

    $mark = if ($Ok) { '[ ok ]' } else { '[FAIL]' }
    $colour = if ($Ok) { 'Green' } else { 'Red' }
    Write-Host "$mark $Label" -ForegroundColor $colour
    if ($Detail) { Write-Host "       $Detail" -ForegroundColor DarkGray }
}

Write-Host "`n== Prerequisites ==" -ForegroundColor Cyan

# .NET 8 Desktop Runtime, x64. Required because COM hosting cannot be
# self-contained.
$runtimeRoot = "$env:ProgramFiles\dotnet\shared\Microsoft.WindowsDesktop.App"
$desktopRuntimes = if (Test-Path $runtimeRoot) {
    Get-ChildItem $runtimeRoot -Directory | Where-Object { $_.Name -like '8.*' }
} else { @() }

$hasRuntime = @($desktopRuntimes).Count -gt 0
Write-Check '.NET 8 Desktop Runtime (x64)' $hasRuntime `
    $(if ($hasRuntime) { "Found: $((@($desktopRuntimes).Name) -join ', ')" } else { 'Install the .NET 8 Desktop Runtime, x64.' })
if (-not $hasRuntime) { $issues.Add('Install the .NET 8 Desktop Runtime (x64).') }

# WebView2 Evergreen runtime.
$webView2Keys = @(
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
    'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
    'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
)
$webView2Version = $null
foreach ($key in $webView2Keys) {
    if (Test-Path $key) {
        $value = (Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue).pv
        if ($value) { $webView2Version = $value; break }
    }
}
Write-Check 'Microsoft Edge WebView2 runtime' ([bool]$webView2Version) `
    $(if ($webView2Version) { "Version $webView2Version" } else { 'Install the Evergreen WebView2 Runtime.' })
if (-not $webView2Version) { $issues.Add('Install the Evergreen WebView2 Runtime.') }

Write-Host "`n== COM registration ==" -ForegroundColor Cyan

$clsidFound = $false
foreach ($root in 'HKLM:\SOFTWARE\Classes', 'HKCU:\SOFTWARE\Classes') {
    $path = "$root\CLSID\$Clsid"
    if (-not (Test-Path $path)) { continue }

    $clsidFound = $true
    Write-Check "CLSID present in $root" $true

    $clsidProps = Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue
    $appId = if ($clsidProps -and $clsidProps.PSObject.Properties['AppID']) { $clsidProps.AppID } else { $null }
    $appIdOk = $appId -eq $PrevHostAppId
    Write-Check '  AppID points at the prevhost surrogate' $appIdOk "Found: '$appId'"
    if (-not $appIdOk) { $issues.Add('AppID is missing or wrong; re-run registration.') }

    # Low-IL prevhost cannot write %LOCALAPPDATA% and cannot run WebView2:
    # symptom is a blank pane, no log file, and eventually a hung Explorer.
    $lowIl = if ($clsidProps -and $clsidProps.PSObject.Properties['DisableLowILProcessIsolation']) {
        $clsidProps.DisableLowILProcessIsolation
    } else { $null }
    $lowIlOk = $lowIl -eq 1
    Write-Check '  DisableLowILProcessIsolation is 1' $lowIlOk "Found: '$lowIl'"
    if (-not $lowIlOk) { $issues.Add('Set DisableLowILProcessIsolation=1 (DWORD) on the CLSID; WebView2 cannot run in the default low-integrity prevhost.') }

    $inproc = "$path\InprocServer32"
    if (Test-Path $inproc) {
        $props = Get-ItemProperty -LiteralPath $inproc
        $dll = $props.'(default)'
        $dllOk = $dll -and (Test-Path -LiteralPath $dll)
        Write-Check '  InprocServer32 points at an existing DLL' $dllOk "Found: '$dll'"
        if (-not $dllOk) { $issues.Add("InprocServer32 points at a missing file: '$dll'") }

        $threadingOk = $props.ThreadingModel -eq 'Apartment'
        Write-Check '  ThreadingModel is Apartment' $threadingOk "Found: '$($props.ThreadingModel)'"
        if (-not $threadingOk) { $issues.Add('ThreadingModel must be Apartment; re-run registration.') }

        if ($dllOk) {
            $assets = Join-Path (Split-Path -Parent $dll) 'assets\web\index.html'
            $assetsOk = Test-Path -LiteralPath $assets
            Write-Check '  Render assets present' $assetsOk $assets
            if (-not $assetsOk) { $issues.Add("Render assets missing: '$assets'") }
        }
    }
    else {
        Write-Check '  InprocServer32 subkey' $false
        $issues.Add('InprocServer32 is missing; regsvr32 did not run or failed.')
    }
}

if (-not $clsidFound) {
    Write-Check 'CLSID registered' $false "Not found under HKLM or HKCU for $Clsid"
    $issues.Add('The COM class is not registered. Run Register-MarkdownPreviewHandler.ps1.')
}

Write-Host "`n== Shell association for '$Extension' ==" -ForegroundColor Cyan

# The shell resolves the ProgID first, so an editor that claimed .md and happens
# to register its own preview handler will win. Report the whole chain.
$progId = $null
foreach ($root in 'HKCU:\SOFTWARE\Classes', 'HKLM:\SOFTWARE\Classes') {
    $path = "$root\$Extension"
    if (Test-Path $path) {
        $candidate = (Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue).'(default)'
        if ($candidate) { $progId = $candidate; break }
    }
}
Write-Host "       ProgID for $Extension : $(if ($progId) { $progId } else { '<none>' })" -ForegroundColor DarkGray

$associationFound = $false
$searchPaths = @()
foreach ($root in 'HKCU:\SOFTWARE\Classes', 'HKLM:\SOFTWARE\Classes') {
    if ($progId) { $searchPaths += "$root\$progId\shellex\$PreviewHandlerShellExId" }
    $searchPaths += "$root\$Extension\shellex\$PreviewHandlerShellExId"
}

foreach ($path in $searchPaths) {
    if (-not (Test-Path $path)) { continue }
    $value = (Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue).'(default)'
    $isOurs = $value -eq $Clsid
    Write-Check "$(if ($isOurs) { 'Our handler' } else { 'ANOTHER handler' }) at $path" $isOurs "CLSID: $value"
    if ($isOurs) { $associationFound = $true }
    elseif ($value) { $issues.Add("A different preview handler ($value) is registered at '$path' and may take precedence.") }
}

if (-not $associationFound) {
    Write-Check "Preview handler associated with $Extension" $false
    $issues.Add("No association for $Extension. Re-run registration; if an editor owns the ProgID, pass -ProgId '$progId'.")
}

Write-Host "`n== Shell settings ==" -ForegroundColor Cyan

$advanced = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
# StrictMode throws on absent properties, and this value is absent by default;
# probe the property bag instead of dotting into it.
$advancedProps = Get-ItemProperty -LiteralPath $advanced -ErrorAction SilentlyContinue
$showPreviewHandlers = if ($advancedProps -and $advancedProps.PSObject.Properties['ShowPreviewHandlers']) {
    $advancedProps.ShowPreviewHandlers
} else { $null }
# Absent means "on"; only an explicit 0 disables it.
$previewHandlersOn = $showPreviewHandlers -ne 0
Write-Check "'Show preview handlers in preview pane' is enabled" $previewHandlersOn `
    $(if ($previewHandlersOn) { '' } else { 'Enable it in Folder Options > View.' })
if (-not $previewHandlersOn) { $issues.Add("Enable 'Show preview handlers in preview pane' in Folder Options > View.") }

Write-Host "`n== Diagnostics ==" -ForegroundColor Cyan
$logPath = Join-Path $env:LOCALAPPDATA 'MarkdownPreviewer\logs\preview.log'
if (Test-Path -LiteralPath $logPath) {
    Write-Host "       Log: $logPath" -ForegroundColor DarkGray
    Write-Host '       Last 15 lines:' -ForegroundColor DarkGray
    Get-Content -LiteralPath $logPath -Tail 15 | ForEach-Object { Write-Host "         $_" -ForegroundColor DarkGray }
}
else {
    Write-Host '       Logging is off. Enable it with:' -ForegroundColor DarkGray
    Write-Host "         New-Item 'HKCU:\SOFTWARE\MarkdownPreviewer' -Force | Out-Null" -ForegroundColor DarkGray
    Write-Host "         Set-ItemProperty 'HKCU:\SOFTWARE\MarkdownPreviewer' -Name LogLevel -Value 0 -Type DWord" -ForegroundColor DarkGray
    Write-Host '       Then restart Explorer and select a .md file.' -ForegroundColor DarkGray
}

Write-Host ''
if ($issues.Count -eq 0) {
    Write-Host 'All checks passed. If the pane is still blank, restart Explorer (Stop-Process -Name explorer -Force).' -ForegroundColor Green
}
else {
    Write-Host "$($issues.Count) issue(s) found:" -ForegroundColor Red
    $i = 1
    foreach ($issue in $issues) {
        Write-Host "  $i. $issue" -ForegroundColor Yellow
        $i++
    }
}
