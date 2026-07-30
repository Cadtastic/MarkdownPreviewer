<#
.SYNOPSIS
    Restarts File Explorer and kills any lingering preview host, so a rebuilt
    handler DLL is actually reloaded.

.DESCRIPTION
    prevhost.exe caches the loaded handler for the life of the process, and it
    outlives the Explorer window that spawned it. Skipping it is the most common
    reason a developer swears their fix did nothing.
#>
[CmdletBinding(SupportsShouldProcess)]
param()

Set-StrictMode -Version Latest

foreach ($name in 'prevhost', 'dllhost', 'explorer') {
    $processes = Get-Process -Name $name -ErrorAction SilentlyContinue
    foreach ($process in $processes) {
        # dllhost hosts plenty of unrelated COM servers; only touch the ones
        # holding our DLL.
        if ($name -eq 'dllhost') {
            $holdsOurDll = $process.Modules |
                Where-Object { $_.ModuleName -like 'MarkdownPreviewer*' }
            if (-not $holdsOurDll) { continue }
        }

        if ($PSCmdlet.ShouldProcess("$($process.ProcessName) (PID $($process.Id))", 'Stop')) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

Start-Sleep -Milliseconds 800

if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
    Start-Process explorer.exe
}

Write-Host 'Explorer restarted; preview hosts cleared.' -ForegroundColor Green
