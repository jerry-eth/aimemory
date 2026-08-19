# GDI/USER/handle leak monitor for AiMemoryManager (M1 section 7)
# Samples every 60s for ~45 minutes to artifacts/gdi-monitor.csv
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class GdiMon {
    [DllImport("user32.dll")] public static extern int GetGuiResources(IntPtr hProcess, int flags);
}
"@
$out = "C:\Users\jerry\Desktop\memory\artifacts\gdi-monitor.csv"
"time,pid,gdi,user,handles,threads,wsMB,privateMB" | Out-File -Encoding utf8 $out
$end = (Get-Date).AddMinutes(45)
while ((Get-Date) -lt $end) {
    $p = Get-Process AiMemoryManager -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p) {
        $gdi = [GdiMon]::GetGuiResources($p.Handle, 0)
        $usr = [GdiMon]::GetGuiResources($p.Handle, 1)
        $line = "{0},{1},{2},{3},{4},{5},{6:N0},{7:N0}" -f (Get-Date -Format "HH:mm:ss"), $p.Id, $gdi, $usr, $p.HandleCount, $p.Threads.Count, ($p.WorkingSet64/1MB), ($p.PrivateMemorySize64/1MB)
    } else {
        $line = "{0},,,,,,," -f (Get-Date -Format "HH:mm:ss")
    }
    $line | Out-File -Append -Encoding utf8 $out
    Start-Sleep 60
}
Write-Output "monitor done"
