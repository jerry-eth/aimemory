using AiMemoryManager.Native;

namespace AiMemoryManager.Services;

public class ForegroundGuard
{
    private readonly INativeMemoryApi _native;
    private readonly Func<int> _selfPid;
    public bool IsFullscreenSettingEnabled { get; set; } = true;

    public ForegroundGuard(INativeMemoryApi native, Func<int> selfPid)
        => (_native, _selfPid) = (native, selfPid);

    public bool IsProtected(int pid) => pid == _selfPid() || pid == _native.GetForegroundPid();

    /// <summary>使用同一批刷新中已取得的前台 PID,避免逐进程重复调用 Win32。</summary>
    public bool IsProtected(int pid, int foregroundPid) => pid == _selfPid() || pid == foregroundPid;

    public bool ShouldSuppressAutoClean() =>
        IsFullscreenSettingEnabled && _native.IsFullscreenAppActive();
}
