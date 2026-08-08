using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AiMemoryManager.Models;

namespace AiMemoryManager.Native;

public class NativeMemoryApi : INativeMemoryApi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength; public uint dwMemoryLoad;
        public ulong ullTotalPhys; public ulong ullAvailPhys;
        public ulong ullTotalPageFile; public ulong ullAvailPageFile;
        public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int pquns);

    private const uint PROCESS_TERMINATE = 0x0001;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    public SystemMemoryInfo GetSystemMemory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new SystemMemoryInfo((long)m.ullTotalPhys, (long)m.ullAvailPhys);
    }

    public IReadOnlyList<ProcessSnapshot> GetProcessSnapshots()
    {
        var list = new List<ProcessSnapshot>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { /* 无权限读路径 */ }
                    list.Add(new ProcessSnapshot(
                        p.Id, p.ProcessName, path, p.WorkingSet64,
                        p.MainWindowHandle != IntPtr.Zero));
                }
                catch { /* 进程已退出,跳过 */ }
            }
        }
        return list;
    }

    public long EmptyWorkingSets(IReadOnlyCollection<int> pids)
    {
        long freed = 0;
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                long before = p.WorkingSet64;
                if (EmptyWorkingSet(p.Handle))
                {
                    p.Refresh();
                    long after = p.WorkingSet64;
                    if (before > after) freed += before - after;
                }
            }
            catch { /* 进程退出或无权限,跳过 */ }
        }
        return freed;
    }

    public int GetForegroundPid()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return -1;
        GetWindowThreadProcessId(hwnd, out int pid);
        return pid;
    }

    public bool IsFullscreenAppActive()
    {
        // QUNS_BUSY=2, QUNS_RUNNING_D3D_FULL_SCREEN=3
        return SHQueryUserNotificationState(out int state) == 0 && (state == 2 || state == 3);
    }

    public bool TryTerminateProcess(int pid, out int win32Error)
    {
        IntPtr h = OpenProcess(PROCESS_TERMINATE, false, pid);
        if (h == IntPtr.Zero)
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }
        try
        {
            if (!TerminateProcess(h, 1))
            {
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }
            win32Error = 0;
            return true;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public IReadOnlyList<string> GetWindowTitles(int pid)
    {
        var titles = new List<string>();
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out int owner);
            if (owner == pid && IsWindowVisible(hWnd) && GetWindowTextLength(hWnd) > 0)
            {
                var sb = new StringBuilder(GetWindowTextLength(hWnd) + 1);
                if (GetWindowText(hWnd, sb, sb.Capacity) > 0)
                    titles.Add(sb.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return titles;
    }
}
