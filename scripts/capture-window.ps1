<#
.SYNOPSIS
    Saves a PNG of a process's main window, DirectX content included, even when other windows cover it.
.DESCRIPTION
    Uses PrintWindow with PW_RENDERFULLCONTENT, so the window does not have to be in front and the
    desktop is left alone.
.EXAMPLE
    ./scripts/capture-window.ps1 -ProcessName Granary -Path shot.png
#>
param(
    [Parameter(Mandatory)] [string]$ProcessName,
    [Parameter(Mandatory)] [string]$Path
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class GranaryCapture {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    public const uint PW_RENDERFULLCONTENT = 2;
}
'@
[GranaryCapture]::SetProcessDPIAware() | Out-Null
$process = Get-Process -Name $ProcessName | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $process) { throw "No window found for process '$ProcessName'." }
$rect = New-Object GranaryCapture+RECT
[GranaryCapture]::GetWindowRect($process.MainWindowHandle, [ref]$rect) | Out-Null
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()
try {
    if (-not [GranaryCapture]::PrintWindow($process.MainWindowHandle, $hdc, [GranaryCapture]::PW_RENDERFULLCONTENT)) {
        throw "PrintWindow failed for '$ProcessName'."
    }
}
finally {
    $graphics.ReleaseHdc($hdc)
}
$bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap.Dispose()
Write-Host "Saved $Path ($width x $height)"
