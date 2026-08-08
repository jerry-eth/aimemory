using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AiMemoryManager.Models;
using AiMemoryManager.Native;

namespace AiMemoryManager.Services;

/// <summary>
/// 文件夹跨盘迁移(FR-12.4):robocopy /E 复制 → 文件数校验 → 删源 → 记日志 → mklink /J 建 junction → 一键回退。
/// 外部命令经注入的 runner 执行(测试替身不跑真实 robocopy/mklink)。
/// 安全顺序:任何失败都发生在删源之前,保证用户数据不丢;只有校验通过后才永久删除源。
/// </summary>
public class MigrationService
{
    public const int LogCapacity = 50;

    private static readonly string[] RobocopyArgs = { "/E", "/NFL", "/NDL", "/NJH", "/NJS", "/NP" };

    private readonly INativeMemoryApi _native;
    private readonly string _logPath;
    private readonly Func<string[], int> _runner;
    private readonly LinkedList<MigrationLogEntry> _log = new();

    public IReadOnlyList<MigrationLogEntry> Log => _log.ToList();

    public MigrationService(INativeMemoryApi native, string logPath, Func<string[], int>? runner = null)
    {
        _native = native;
        _logPath = logPath;
        _runner = runner ?? DefaultRunner;
        try
        {
            if (File.Exists(_logPath))
                foreach (var e in JsonSerializer.Deserialize<List<MigrationLogEntry>>(File.ReadAllText(_logPath)) ?? new())
                    _log.AddLast(e);
        }
        catch { }   // 日志损坏回退为空
    }

    public static string DefaultLogPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "AiMemoryManager", "migration-log.json");

    /// <summary>占用检测(FR-12.5):返回可执行文件路径位于 folder 下的运行中进程名。</summary>
    public IReadOnlyList<string> GetBlockingProcesses(string folder)
    {
        string full;
        try { full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return Array.Empty<string>(); }
        var names = new List<string>();
        foreach (var p in _native.GetProcessSnapshots())
        {
            if (string.IsNullOrEmpty(p.Path)) continue;
            string pp;
            try { pp = Path.GetFullPath(p.Path); }
            catch { continue; }
            if (pp.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                names.Add(p.Name);
        }
        return names;
    }

    public Task<MigrationLogEntry> MigrateAsync(string source, string targetRoot)
        => Task.Run(() => Migrate(source, targetRoot));

    public Task<bool> RevertAsync(MigrationLogEntry entry)
        => Task.Run(() => Revert(entry));

    private MigrationLogEntry Migrate(string source, string targetRoot)
    {
        // 执行端强制重查(FR-12.5):不依赖 UI/LLM 过滤,系统路径直接拒绝
        if (SystemPathGuard.IsProtected(source))
            throw new InvalidOperationException($"系统路径禁止迁移: {source}");
        if (!Directory.Exists(source))
            throw new InvalidOperationException($"源目录不存在: {source}");

        // ① 占用检测:有进程从源目录运行则拒绝迁移
        var blocking = GetBlockingProcesses(source);
        if (blocking.Count > 0)
            throw new InvalidOperationException($"文件夹被运行中进程占用,无法迁移: {string.Join(", ", blocking)}");

        string target = Path.Combine(targetRoot, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)));
        // 执行端强制重查(评审修复):解析后的完整目标路径同样不得落在受保护目录下,拒绝在 robocopy 启动之前
        if (SystemPathGuard.IsProtected(target))
            throw new InvalidOperationException($"目标为系统路径,禁止迁移: {target}");
        if (Directory.Exists(target) || File.Exists(target))
            throw new InvalidOperationException($"目标已存在: {target}");

        // ② robocopy 复制(退出码 <8 为成功)
        int code = _runner(new[] { "robocopy" }.Concat(RobocopyArgs).Concat(new[] { source, target }).ToArray());
        if (code >= 8)
            throw new InvalidOperationException($"robocopy 复制失败(退出码 {code}),源目录未改动");

        // ③ 文件数校验:源 vs 目标递归计数,不等→中止并清理半成品副本,源目录保持原样
        if (CountFiles(source) != CountFiles(target))
        {
            try { Directory.Delete(target, recursive: true); } catch { }
            throw new InvalidOperationException("复制校验失败:源与目标文件数不一致,已中止迁移,源目录未改动");
        }

        // ④ 永久删除源 — 有意为之(非回收站):大迁移回收站装不下,
        //    UI(T12)在执行前已弹出明确警告;此刻副本已校验完整,删源不丢数据
        Directory.Delete(source, recursive: true);

        // ⑤ 先记日志(原子写,容量 50,新在前):必须在 mklink 之前落盘,
        //    否则 mklink 失败时源已删、数据在 target 却无日志,一键回退通道丢失
        var entry = new MigrationLogEntry(DateTimeOffset.Now, source, target, source, false);
        _log.AddFirst(entry);
        while (_log.Count > LogCapacity) _log.RemoveLast();
        Persist();

        // ⑥ 原位置建 junction;失败仅缺链接,日志已在,UI 可提示手动 mklink 或一键回退
        int mk = _runner(new[] { "mklink", "/J", source, target });
        if (mk != 0)
            throw new InvalidOperationException($"创建 Junction 失败(mklink 退出码 {mk});数据已完整迁移至 {target},可在历史中一键回退");

        return entry;
    }

    private bool Revert(MigrationLogEntry entry)
    {
        // 执行端强制重查(评审修复):日志是磁盘可编辑 JSON,被篡改/损坏的条目
        // 不得导致永久删除或写回系统路径;任一路径受保护即拒绝,不做任何动作
        if (SystemPathGuard.IsProtected(entry.Target) || SystemPathGuard.IsProtected(entry.Junction))
            return false;
        try
        {
            // ① 删 junction:真实环境 junction 是目录(reparse point),
            //    Directory.Delete(junction, false) 只删链接本身、不递归目标;
            //    兼容 File/Directory 两种存在形式。
            //    测试替身里 mklink 假实现写的是 <junction>.junction 标记文件,
            //    junction 本体不存在,跳过即可(顺手清掉标记文件)。
            if (Directory.Exists(entry.Junction))
                Directory.Delete(entry.Junction, recursive: false);
            else if (File.Exists(entry.Junction))
                File.Delete(entry.Junction);
            if (File.Exists(entry.Junction + ".junction")) File.Delete(entry.Junction + ".junction");

            // ② robocopy /E 移回原位置
            int code = _runner(new[] { "robocopy" }.Concat(RobocopyArgs).Concat(new[] { entry.Target, entry.Junction }).ToArray());
            if (code >= 8) return false;

            // ③ 删除迁移副本(永久,与迁移删源同理)
            if (Directory.Exists(entry.Target))
                Directory.Delete(entry.Target, recursive: true);

            // ④ 日志标记 Reverted
            var node = _log.Find(entry);
            if (node != null)
            {
                node.Value = entry with { Reverted = true };
                Persist();
            }
            return true;
        }
        catch { return false; }   // 任一步失败 → false,不抛
    }

    private void Persist()
    {
        try { AtomicFile.WriteAllText(_logPath, JsonSerializer.Serialize(_log.ToList())); } catch { }
    }

    /// <summary>递归文件计数:跳过 reparse point 防循环,无权限项跳过。</summary>
    private static int CountFiles(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        int count = 0;
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(d); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException) { continue; }
            try { foreach (var _ in files) count++; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException) { }

            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(d); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException) { continue; }
            try
            {
                foreach (var s in subs)
                {
                    try
                    {
                        if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue;
                        stack.Push(s);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException) { }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException) { }
        }
        return count;
    }

    /// <summary>默认 runner:robocopy 直接跑进程;mklink 是 cmd 内建命令,经 cmd /c 调用。</summary>
    private static int DefaultRunner(string[] args)
    {
        var psi = new ProcessStartInfo
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (args[0].Equals("mklink", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "cmd";
            psi.ArgumentList.Add("/c");
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        else
        {
            psi.FileName = args[0];
            foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        // 先排空 stdout/stderr 再等退出:否则子进程写满管道缓冲区会阻塞,WaitForExit 死锁
        var drainOut = p.StandardOutput.ReadToEndAsync();
        var drainErr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return p.ExitCode;
    }
}
