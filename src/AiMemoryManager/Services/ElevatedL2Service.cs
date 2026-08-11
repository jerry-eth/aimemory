using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AiMemoryManager.Services;

public interface IL2Executor
{
    bool IsAvailable { get; }
    bool IsHelperTaskRegistered { get; }
    void RegisterHelperTask();                              // 触发一次性 UAC
    Task<long> PurgeStandbyListAsync(CancellationToken ct); // 返回估算释放字节数
}

public class ElevatedL2Service : IL2Executor
{
    public const string TaskName = "AiMemoryManager.L2Helper";
    private readonly string _helperPath;
    private readonly string _resultPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     "AiMemoryManager", "l2-result.json");

    public ElevatedL2Service(string helperPath) => _helperPath = helperPath;

    public bool IsAvailable => true;

    public bool IsHelperTaskRegistered
    {
        get
        {
            var p = Process.Start(new ProcessStartInfo("schtasks", $"/query /tn \"{TaskName}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
    }

    public void RegisterHelperTask()
    {
        // 唯一一次 UAC:以 runas 启动 Helper 自注册
        var p = Process.Start(new ProcessStartInfo(_helperPath, "--install")
        { UseShellExecute = true, Verb = "runas" })!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("计划任务注册失败,exit=" + p.ExitCode);
    }

    public async Task<long> PurgeStandbyListAsync(CancellationToken ct)
    {
        if (!IsHelperTaskRegistered) RegisterHelperTask();
        try { File.Delete(_resultPath); } catch { }
        var runStarted = DateTimeOffset.Now;    // 用于识别陈旧结果文件(上一次运行残留/删除失败)
        var run = Process.Start(new ProcessStartInfo("schtasks", $"/run /tn \"{TaskName}\"")
        { UseShellExecute = false, CreateNoWindow = true })!;
        run.WaitForExit();
        if (run.ExitCode != 0)
            throw new InvalidOperationException("schtasks /run 失败, exit=" + run.ExitCode);  // 任务未启动,干等只会超时

        // 轮询结果文件,最多 15 秒
        for (int i = 0; i < 150; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(_resultPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(_resultPath));
                    var root = doc.RootElement;
                    // 陈旧结果防护:time 早于本次 /run(留 5 秒容差)说明是残留文件,忽略并继续等新结果
                    bool stale = root.TryGetProperty("time", out var t)
                        && t.GetDateTimeOffset() < runStarted - TimeSpan.FromSeconds(5);
                    if (!stale)
                    {
                        int status = root.GetProperty("status").GetInt32();
                        if (status != 0)
                            throw new InvalidOperationException($"L2 清理失败,helper status={status}(NTSTATUS,可能特权未生效)");
                        return root.GetProperty("freedBytes").GetInt64();
                    }
                }
                catch (IOException) { /* 文件尚在写入,继续等 */ }
                catch (JsonException) { /* 读到未写完的部分 JSON,继续等 */ }
            }
            await Task.Delay(100, ct);
        }
        throw new TimeoutException("L2 清理结果等待超时");
    }
}

/// <summary>
/// Microsoft Store 兼容版的 L2 执行器。商店版不声明 allowElevation，
/// 因此不注册最高权限计划任务，也不尝试绕过商店能力限制。
/// </summary>
public sealed class UnavailableL2Executor : IL2Executor
{
    public bool IsAvailable => false;
    public bool IsHelperTaskRegistered => false;

    public void RegisterHelperTask() =>
        throw new NotSupportedException("当前商店版本未启用最高权限待机列表清理。");

    public Task<long> PurgeStandbyListAsync(CancellationToken ct) =>
        Task.FromException<long>(new NotSupportedException("当前商店版本未启用最高权限待机列表清理。"));
}
