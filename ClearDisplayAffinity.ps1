<#
.SYNOPSIS
  Detects and clears the Windows "display affinity" flag (WDA_EXCLUDEFROMCAPTURE / WDA_MONITOR)
  on top-level windows. Some apps set this flag on their own window so it renders as a black
  box in screen shares / recordings / remote-monitoring tools. This resets it to WDA_NONE so
  the window becomes capturable again.

.PARAMETER ListOnly
  Report affected windows without changing anything.

.PARAMETER Watch
  Continuously scan and clear every 2 seconds until Ctrl+C.

.EXAMPLE
  .\ClearDisplayAffinity.ps1
.EXAMPLE
  .\ClearDisplayAffinity.ps1 -ListOnly
.EXAMPLE
  .\ClearDisplayAffinity.ps1 -Watch
#>
param(
    [switch]$ListOnly,
    [switch]$Watch
)

$src = @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class WinAffinity
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint pdwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
"@

Add-Type -TypeDefinition $src -Language CSharp

$WDA_NONE = 0
$WDA_MONITOR = 1
$WDA_EXCLUDEFROMCAPTURE = 0x11

function AffinityName($a) {
    switch ($a) {
        0    { "WDA_NONE" }
        1    { "WDA_MONITOR" }
        17   { "WDA_EXCLUDEFROMCAPTURE" }
        default { "0x{0:X}" -f $a }
    }
}

function Scan-AndClear([bool]$listOnly) {
    $scanned = 0; $found = 0; $cleared = 0; $failed = 0

    $callback = {
        param($hWnd, $lParam)
        $script:scanned++

        $affinity = 0
        if (-not [WinAffinity]::GetWindowDisplayAffinity($hWnd, [ref]$affinity)) { return $true }
        if ($affinity -eq 0) { return $true }

        $script:found++

        $pid = 0
        [void][WinAffinity]::GetWindowThreadProcessId($hWnd, [ref]$pid)
        $procName = "unknown"
        try { $procName = (Get-Process -Id $pid -ErrorAction Stop).ProcessName + ".exe" } catch {}

        $sb = New-Object System.Text.StringBuilder 256
        [void][WinAffinity]::GetWindowText($hWnd, $sb, $sb.Capacity)
        $title = $sb.ToString()
        if ([string]::IsNullOrWhiteSpace($title)) { $title = "(no title)" }

        Write-Host ("[FOUND] PID {0} ({1}) - `"{2}`" - affinity={3}" -f $pid, $procName, $title, (AffinityName $affinity))

        if (-not $listOnly) {
            if ([WinAffinity]::SetWindowDisplayAffinity($hWnd, 0)) {
                Write-Host "        -> cleared"
                $script:cleared++
            } else {
                $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
                Write-Host "        -> FAILED to clear (Win32 error $err)"
                $script:failed++
            }
        }

        return $true
    }

    [void][WinAffinity]::EnumWindows($callback, [IntPtr]::Zero)

    Write-Host ""
    if ($listOnly) {
        Write-Host "Scanned $scanned windows. Found $found with capture-exclusion set."
    } else {
        Write-Host "Scanned $scanned windows. Found $found with capture-exclusion set. Cleared $cleared, failed $failed."
    }
}

if (-not $Watch) {
    Write-Host $(if ($ListOnly) { "Scanning for windows with capture-exclusion set (report only)..." } else { "Scanning and clearing capture-exclusion on windows..." })
    Write-Host ""
    Scan-AndClear -listOnly $ListOnly
} else {
    Write-Host "Watch mode: scanning every 2 seconds. Press Ctrl+C to stop."
    Write-Host ""
    while ($true) {
        Write-Host "--- $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ---"
        Scan-AndClear -listOnly $ListOnly
        Write-Host ""
        Start-Sleep -Seconds 2
    }
}
