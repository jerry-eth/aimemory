using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AiMemoryManager.Services;

public interface IL2Executor
{
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
        var run = Process.Start(new ProcessStartInfo("schtasks", $"/run /tn \"{TaskName}\"")
        { UseShellExecute = false, CreateNoWindow = true })!;
        run.WaitForExit();

        // 轮询结果文件,最多 15 秒
        for (int i = 0; i < 150; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(_resultPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(_resultPath));
                    return doc.RootElement.GetProperty("freedBytes").GetInt64();
                }
                catch (IOException) { /* 文件尚在写入,继续等 */ }
            }
            await Task.Delay(100, ct);
        }
        throw new TimeoutException("L2 清理结果等待超时");
    }
}
