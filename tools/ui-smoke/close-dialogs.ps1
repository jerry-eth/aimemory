Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WinEnum {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
}
"@
$closed = 0
$cb = [WinEnum+EnumWindowsProc]{
    param($hWnd, $lParam)
    if ([WinEnum]::IsWindowVisible($hWnd)) {
        $sb = New-Object System.Text.StringBuilder 256
        [void][WinEnum]::GetWindowText($hWnd, $sb, 256)
        $t = $sb.ToString()
        if ($t -match '白名单文件|插入磁盘') {
            Write-Output "closing: '$t' hwnd=$hWnd"
            [void][WinEnum]::PostMessage($hWnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)  # WM_CLOSE
            $script:closed++
        }
    }
    return $true
}
[void][WinEnum]::EnumWindows($cb, [IntPtr]::Zero)
Write-Output "closed=$closed"
