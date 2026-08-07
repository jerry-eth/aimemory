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

    public bool ShouldSuppressAutoClean() =>
        IsFullscreenSettingEnabled && _native.IsFullscreenAppActive();
}
