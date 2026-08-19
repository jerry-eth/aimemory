Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WinEnum2 {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
"@
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$cb = [WinEnum2+EnumWindowsProc]{
    param($hWnd, $lParam)
    if ([WinEnum2]::IsWindowVisible($hWnd)) {
        $sb = New-Object System.Text.StringBuilder 256
        [void][WinEnum2]::GetWindowText($hWnd, $sb, 256)
        $cls = New-Object System.Text.StringBuilder 256
        [void][WinEnum2]::GetClassName($hWnd, $cls, 256)
        $procId = 0
        [void][WinEnum2]::GetWindowThreadProcessId($hWnd, [ref]$procId)
        $proc = (Get-Process -Id $procId -ErrorAction SilentlyContinue).ProcessName
        Write-Output ("hwnd=" + $hWnd + " class=[" + $cls.ToString() + "] proc=" + $proc + " title=[" + $sb.ToString() + "]")
    }
    return $true
}
[void][WinEnum2]::EnumWindows($cb, [IntPtr]::Zero)
