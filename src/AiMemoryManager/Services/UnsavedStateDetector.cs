using AiMemoryManager.Native;

namespace AiMemoryManager.Services;

// FR-2.6: 窗口标题含 '*' 或 '•' 视为可能存在未保存工作(供 L3 二次确认标记高风险)
public class UnsavedStateDetector
{
    private static readonly char[] Marks = { '*', '•' };
    private readonly INativeMemoryApi _native;

    public UnsavedStateDetector(INativeMemoryApi native) => _native = native;

    public bool HasUnsavedSigns(int pid) =>
        _native.GetWindowTitles(pid).Any(t => t.IndexOfAny(Marks) >= 0);
}
