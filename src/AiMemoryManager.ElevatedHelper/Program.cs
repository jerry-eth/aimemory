// 用法:
//   AiMemoryManager.ElevatedHelper.exe --install
//   AiMemoryManager.ElevatedHelper.exe --purge-standby --result <结果文件路径>
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

class Program
{
    const string TaskName = "AiMemoryManager.L2Helper";

    [DllImport("ntdll.dll")] static extern int NtSetSystemInformation(int InfoClass, ref int Info, int Length);
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int InfoClass, ref SYSTEM_PERFORMANCE_INFORMATION Info, int Length, out int ReturnLength);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr h, int access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool LookupPrivilegeValue(string? sys, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES tp, int len, IntPtr prev, IntPtr retLen);

    // LUID 为 8 字节、对齐 4;拆成两个 32 位字段,保证托管布局与原生一致(原生 LUID 紧跟在 int 后,偏移 4)。
    [StructLayout(LayoutKind.Sequential)]
    struct LUID { public uint LowPart; public int HighPart; }

    // 原生 TOKEN_PRIVILEGES:LUID 对齐 4 位于偏移 4;若用 long(对齐 8)会被填到偏移 8,结构错位导致 AdjustTokenPrivileges 读到错误 LUID。
    [StructLayout(LayoutKind.Sequential)]
    struct TOKEN_PRIVILEGES { public int Count; public uint LuidLow; public int LuidHigh; public int Attr; }

    // 已实测校验(见 task-6 报告):AvailablePages 偏移 = 44(4 个 long=32 + 3 个 int=12),
    // 读取值 × 页大小与 GlobalMemoryStatusEx 的 AvailPhys 完全一致(比值 1.000)。
    // 整体结构大小需 ≥ 312 字节,否则 NtQuerySystemInformation 返回长度不匹配,
    // 末尾 Reserved20..24 仅作填充,不参与读取。
    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_PERFORMANCE_INFORMATION
    {
        public long IdleProcessTime; public long IoReadTransferCount; public long IoWriteTransferCount; public long IoOtherTransferCount;
        public int IoReadOperationCount; public int IoWriteOperationCount; public int IoOtherOperationCount;
        public int AvailablePages; public int CommittedPages; public int CommitLimit; public int PeakCommitment;
        public int PageFaultCount; public int CopyOnWriteCount; public int TransitionCount; public int CacheTransitionCount;
        public int DemandZeroCount; public int PageReadCount; public int PageReadIoCount; public int CacheReadCount; public int CacheIoCount;
        public int DirtyPagesWriteCount; public int DirtyWriteIoCount; public int MappedPagesWriteCount; public int MappedWriteIoCount;
        public int PagedPoolPages; public int NonPagedPoolPages; public int PagedPoolAllocs; public int PagedPoolFrees;
        public int NonPagedPoolAllocs; public int NonPagedPoolFrees; public int FreeSystemPtes;
        public int ResidentSystemCodePage; public int TotalSystemDriverPages; public int TotalSystemCodePages;
        public int NonPagedPoolLookasideHits; public int PagedPoolLookasideHits; public int AvailablePagedPoolPages;
        public int ResidentSystemCachePage; public int ResidentPagedPoolPage; public int ResidentSystemDriverPage;
        public int CcFastReadNoWait; public int CcFastReadWait; public int CcFastReadResourceMiss; public int CcFastReadNotPossible;
        public int CcFastMdlReadNoWait; public int CcFastMdlReadWait; public int CcFastMdlReadResourceMiss; public int CcFastMdlReadNotPossible;
        public int CcMapDataNoWait; public int CcMapDataWait; public int CcMapDataNoWaitMiss; public int CcMapDataWaitMiss;
        public int CcPinMappedDataCount; public int CcPinReadNoWait; public int CcPinReadWait; public int CcPinReadNoWaitMiss; public int CcPinReadWaitMiss;
        public int CcCopyReadNoWait; public int CcCopyReadWait; public int CcCopyReadNoWaitMiss; public int CcCopyReadWaitMiss;
        public int CcMdlReadNoWait2; public int CcMdlReadWait2; public int CcMdlReadNoWaitMiss2; public int CcMdlReadWaitMiss2;
        public int LookasideHits; public int LookasideMisses; public int Reserved18; public int Reserved19;
        public int Reserved20; public int Reserved21; public int Reserved22; public int Reserved23; public int Reserved24;
    }

    static int Main(string[] args)
    {
        if (args.Contains("--install")) return Install();
        if (args.Contains("--purge-standby"))
        {
            var idx = Array.IndexOf(args, "--result");
            var resultPath = idx >= 0 ? args[idx + 1] : null;
            return Purge(resultPath);
        }
        Console.WriteLine("用法: --install | --purge-standby --result <path>");
        return 2;
    }

    static int Install()
    {
        // 由已提权进程注册"最高权限、手动触发"计划任务
        var exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo("schtasks",
            $"/create /tn \"{TaskName}\" /tr \"\\\"{exe}\\\" --purge-standby --result \\\"%PROGRAMDATA%\\\\AiMemoryManager\\\\l2-result.json\\\"\" /sc once /st 00:00 /rl HIGHEST /f")
        { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        var p = Process.Start(psi)!;
        p.WaitForExit();
        Console.WriteLine(p.ExitCode == 0 ? "计划任务注册成功" : "注册失败:" + p.StandardOutput.ReadToEnd());
        return p.ExitCode;
    }

    static int Purge(string? resultPath)
    {
        EnablePrivilege("SeProfileSingleProcessPrivilege");
        long before = AvailablePages();
        int command = 4; // MemoryPurgeStandbyList
        int status = NtSetSystemInformation(0x50 /*SystemMemoryListInformation*/, ref command, sizeof(int));
        long after = AvailablePages();
        long freedPages = Math.Max(0, after - before);
        if (resultPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new
            {
                status,
                freedBytes = freedPages * Environment.SystemPageSize,
                time = DateTimeOffset.Now
            }));
        }
        return status;
    }

    static long AvailablePages()
    {
        var spi = new SYSTEM_PERFORMANCE_INFORMATION();
        NtQuerySystemInformation(2 /*SystemPerformanceInformation*/, ref spi,
            Marshal.SizeOf<SYSTEM_PERFORMANCE_INFORMATION>(), out _);
        return spi.AvailablePages;
    }

    static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0020 | 0x0008, out var token))
            Console.Error.WriteLine($"EnablePrivilege: OpenProcessToken 失败,Win32Error={Marshal.GetLastWin32Error()}");
        if (!LookupPrivilegeValue(null, name, out LUID luid))
            Console.Error.WriteLine($"EnablePrivilege: LookupPrivilegeValue({name}) 失败,Win32Error={Marshal.GetLastWin32Error()}");
        var tp = new TOKEN_PRIVILEGES { Count = 1, LuidLow = luid.LowPart, LuidHigh = luid.HighPart, Attr = 0x00000002 /*SE_PRIVILEGE_ENABLED*/ };
        // AdjustTokenPrivileges 即使返回 true,也可能未实际赋予特权(ERROR_NOT_ALL_ASSIGNED=1300),必须检查 LastError
        bool ok = AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        int err = Marshal.GetLastWin32Error();
        if (!ok || err != 0)
            Console.Error.WriteLine($"EnablePrivilege: AdjustTokenPrivileges({name}) 未生效,ok={ok},Win32Error={err}" +
                (err == 1300 ? "(ERROR_NOT_ALL_ASSIGNED:令牌不持有该特权)" : ""));
    }
}
