using AiMemoryManager.Models;

namespace AiMemoryManager.Native;

public interface INativeMemoryApi
{
    SystemMemoryInfo GetSystemMemory();
    IReadOnlyList<ProcessSnapshot> GetProcessSnapshots();
    long EmptyWorkingSets(IReadOnlyCollection<int> pids); // 返回估算释放字节数
    int GetForegroundPid();
    bool IsFullscreenAppActive();
}
