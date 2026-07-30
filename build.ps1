<#
.SYNOPSIS
    Builds, publishes and packages the Markdown preview handler.

.DESCRIPTION
    Publishes the Shell project framework-dependent for win-x64 (COM hosting
    cannot be self-contained), stages the render assets, and optionally compiles
    the NSIS installer.

.PARAMETER Configuration
    Debug or Release. Default Release.

.PARAMETER Package
    Also run makensis to produce the setup executable.

.PARAMETER Test
    Run the unit tests before publishing.

.PARAMETER InstallLocal
    After publishing, register the freshly built handler from the publish folder
    and restart Explorer. Requires an elevated session. Intended for the inner
    development loop.

.EXAMPLE
    .\build.ps1 -Test -Package

.EXAMPLE
    .\build.ps1 -InstallLocal
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $Package,
    [switch] $Test,
    [switch] $InstallLocal
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$solution = Join-Path $root 'MarkdownPreviewer.sln'

function Write-Step {
    param([string] $Message)
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

Write-Step 'Restoring'
dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

Write-Step "Building ($Configuration|x64)"
dotnet build $solution --configuration $Configuration -p:Platform=x64 --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

if ($Test) {
    Write-Step 'Testing'
    dotnet test (Join-Path $root 'tests\MarkdownPreviewer.Tests\MarkdownPreviewer.Tests.csproj') `
        --configuration $Configuration -p:Platform=x64 --no-build --verbosity normal
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

Write-Step 'Publishing the shell extension'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Path $publish -Force | Out-Null

# SelfContained:false is mandatory, not a preference. .NET does not support COM
# hosting from a self-contained deployment; regsvr32 fails with 0x80008093.
dotnet publish (Join-Path $root 'src\MarkdownPreviewer.Shell\MarkdownPreviewer.Shell.csproj') `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained false `
    --output $publish `
    -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# --- sanity checks on the publish output -------------------------------------
$required = @(
    'MarkdownPreviewer.Shell.dll',
    'MarkdownPreviewer.Shell.comhost.dll',
    'MarkdownPreviewer.Shell.runtimeconfig.json',
    'MarkdownPreviewer.Domain.dll',
    'MarkdownPreviewer.Application.dll',
    'MarkdownPreviewer.Infrastructure.dll',
    'MarkdownPreviewer.Rendering.dll',
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.WinForms.dll',
    'assets\web\index.html',
    'assets\web\js\preview.js',
    'assets\web\js\mermaid.min.js',
    'assets\web\js\tex-mml-svg.js'
)

$missing = $required | Where-Object { -not (Test-Path (Join-Path $publish $_)) }
if ($missing) {
    throw "Publish output is incomplete. Missing:`n  $($missing -join "`n  ")"
}

# WebView2Loader.dll is a native asset; with a RID-specific publish it should land
# in the output root. Warn rather than fail — layouts have moved between SDKs.
if (-not (Test-Path (Join-Path $publish 'WebView2Loader.dll'))) {
    Write-Warning 'WebView2Loader.dll is not in the publish root. Check runtimes\win-x64\native\ and copy it up if needed.'
}

$size = (Get-ChildItem $publish -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ("Published {0:N1} MB to {1}" -f ($size / 1MB), $publish) -ForegroundColor Green

if ($Package) {
    Write-Step 'Packaging the installer'

    $makensis = @(
        "${env:ProgramFiles(x86)}\NSIS\makensis.exe",
        "$env:ProgramFiles\NSIS\makensis.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $makensis) {
        # No '?.': null-conditional access is PowerShell 7+, and this script
        # must also run under Windows PowerShell 5.1.
        $cmd = Get-Command makensis.exe -ErrorAction SilentlyContinue
        if ($cmd) { $makensis = $cmd.Source }
    }
    if (-not $makensis) {
        throw 'makensis.exe not found. Install NSIS 3.11 or add it to PATH.'
    }

    & $makensis /V3 "/DSTAGE_DIR=$publish" (Join-Path $root 'installer\MarkdownPreviewer.nsi')
    if ($LASTEXITCODE -ne 0) { throw 'makensis failed.' }

    Get-ChildItem $artifacts -Filter '*Setup.exe' |
        ForEach-Object { Write-Host ("Installer: {0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) -ForegroundColor Green }
}

if ($InstallLocal) {
    Write-Step 'Registering locally'
    & (Join-Path $root 'scripts\Register-MarkdownPreviewHandler.ps1') -InstallPath $publish -Scope Machine
    & (Join-Path $root 'scripts\Restart-Explorer.ps1')
}

Write-Host "`nDone." -ForegroundColor Green
