using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AiMemoryManager.Models;
using AiMemoryManager.Native;

namespace AiMemoryManager.Services;

/// <summary>
/// 事务式跨盘迁移：复制、双重校验、暂存源目录、建立 junction、确认可访问后完成。
/// 任意中断都保留至少一个完整副本，并通过日志提供恢复/回退入口。
/// </summary>
public class MigrationService
{
    public const int LogCapacity = 50;
    private static readonly string[] RobocopyArgs = { "/E", "/COPY:DAT", "/DCOPY:DAT", "/XJ", "/R:1", "/W:1", "/NFL", "/NDL", "/NJH", "/NJS", "/NP" };

    private readonly INativeMemoryApi _native;
    private readonly string _logPath;
    private readonly Func<string[], int> _runner;
    private readonly LinkedList<MigrationLogEntry> _log = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public IReadOnlyList<MigrationLogEntry> Log { get { lock (_gate) return _log.ToList(); } }
    public IReadOnlyList<MigrationLogEntry> RecoverableLog => Log.Where(e => e.IsRecoverable).ToList();

    public MigrationService(INativeMemoryApi native, string logPath, Func<string[], int>? runner = null)
    {
        _native = native;
        _logPath = logPath;
        _runner = runner ?? DefaultRunner;
        Load();
    }

    public static string DefaultLogPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AiMemoryManager", "migration-log.json");

    public IReadOnlyList<string> GetBlockingProcesses(string folder)
    {
        var full = PathSafetyService.Normalize(folder);
        if (full is null) return Array.Empty<string>();
        var names = new List<string>();
        try
        {
            foreach (var p in _native.GetProcessSnapshots())
            {
                if (string.IsNullOrWhiteSpace(p.Path)) continue;
                var processPath = PathSafetyService.Normalize(p.Path);
                if (processPath is not null && PathSafetyService.IsSameOrDescendant(processPath, full))
                    names.Add(p.Name);
            }
        }
        catch { }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<MigrationLogEntry> MigrateAsync(string source, string targetRoot, CancellationToken ct = default)
    {
        await _operationGate.WaitAsync(ct);
        try { return await Task.Run(() => Migrate(source, targetRoot, ct), ct); }
        finally { _operationGate.Release(); }
    }

    public async Task<bool> RevertAsync(MigrationLogEntry entry, CancellationToken ct = default)
    {
        await _operationGate.WaitAsync(ct);
        try { return await Task.Run(() => Revert(entry, ct), ct); }
        finally { _operationGate.Release(); }
    }

    private MigrationLogEntry Migrate(string source, string targetRoot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sourceFull = PathSafetyService.Normalize(source);
        var rootFull = PathSafetyService.Normalize(targetRoot);
        if (sourceFull is null || rootFull is null) throw new InvalidOperationException("源或目标路径格式无效");
        if (!Directory.Exists(sourceFull)) throw new InvalidOperationException($"源目录不存在: {sourceFull}");
        if (PathSafetyService.IsProtectedForOperation(sourceFull, DiskCategory.UserFolder, migration: true))
            throw new InvalidOperationException($"系统或受保护路径禁止迁移: {sourceFull}");
        if (!Directory.Exists(rootFull)) throw new InvalidOperationException($"目标盘或目录不可用: {rootFull}");
        if (PathSafetyService.IsReparsePoint(rootFull)) throw new InvalidOperationException("目标目录是链接路径，无法安全迁移");

        var sourceRoot = Path.GetPathRoot(sourceFull);
        var targetRootDrive = Path.GetPathRoot(rootFull);
        if (PathSafetyService.IsDriveRoot(rootFull) &&
            string.Equals(sourceRoot, targetRootDrive, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目标盘不能与源目录位于同一磁盘");

        if (!PathSafetyService.IsSafeTarget(sourceFull, rootFull, out var target, out var targetError))
            throw new InvalidOperationException(targetError);
        if (Directory.Exists(target) || File.Exists(target))
            throw new InvalidOperationException($"目标已存在: {target}");

        var blocking = GetBlockingProcesses(sourceFull);
        if (blocking.Count > 0)
            throw new InvalidOperationException($"文件夹被运行中进程占用,无法迁移: {string.Join(", ", blocking)}");

        var sourceSnapshot = Snapshot(sourceFull, ct);
        if (!sourceSnapshot.IsComplete)
            throw new InvalidOperationException($"源目录存在无法读取的文件或链接（跳过 {sourceSnapshot.SkippedCount} 项），为避免数据丢失已停止迁移");
        EnsureFreeSpace(rootFull, sourceSnapshot.Bytes);
        var operationId = Guid.NewGuid().ToString("N");
        var backup = sourceFull + ".aimm-backup-" + operationId;
        var entry = new MigrationLogEntry(DateTimeOffset.Now, sourceFull, target, sourceFull, false)
        {
            OperationId = operationId,
            BackupPath = backup,
            State = MigrationState.Copying,
            SourceBytes = sourceSnapshot.Bytes,
            SourceFileCount = sourceSnapshot.Files,
        };
        AddOrUpdate(entry);

        try
        {
            ct.ThrowIfCancellationRequested();
            int copyCode = _runner(new[] { "robocopy" }.Concat(RobocopyArgs).Concat(new[] { sourceFull, target }).ToArray());
            if (copyCode >= 8)
                throw new InvalidOperationException($"robocopy 复制失败(退出码 {copyCode}),源目录未改动");
            if (!Directory.Exists(target)) throw new InvalidOperationException("复制完成但目标目录不存在");

            var targetSnapshot = Snapshot(target, ct);
            if (!targetSnapshot.IsComplete || targetSnapshot.Files != sourceSnapshot.Files || targetSnapshot.Bytes != sourceSnapshot.Bytes)
            {
                TryDeleteDirectory(target);
                throw new InvalidOperationException($"复制校验失败：文件数/字节数不一致（源 {sourceSnapshot.Files} 个/{sourceSnapshot.Bytes} 字节，目标 {targetSnapshot.Files} 个/{targetSnapshot.Bytes} 字节）");
            }
            entry = entry with { State = MigrationState.Copied, TargetBytes = targetSnapshot.Bytes, TargetFileCount = targetSnapshot.Files };
            AddOrUpdate(entry);

            // 复制期间若源目录发生变化，不能把旧快照挂回原路径。
            var sourceBeforeStage = Snapshot(sourceFull, ct);
            if (!sourceBeforeStage.IsComplete || sourceBeforeStage.Files != sourceSnapshot.Files || sourceBeforeStage.Bytes != sourceSnapshot.Bytes)
            {
                TryDeleteDirectory(target);
                throw new InvalidOperationException("源目录在复制期间发生变化，已取消迁移；源目录未改动");
            }

            // 使用同盘 Move 暂存源目录，避免先永久删除源；这样 mklink/UAC/断电时仍可恢复。
            if (Directory.Exists(backup)) throw new InvalidOperationException("发现同操作残留备份，已停止以避免覆盖数据");
            Directory.Move(sourceFull, backup);
            entry = entry with { State = MigrationState.SourceStaged };
            AddOrUpdate(entry);

            ct.ThrowIfCancellationRequested();
            int mk = _runner(new[] { "mklink", "/J", sourceFull, target });
            if (mk != 0)
                throw new InvalidOperationException($"创建 Junction 失败(mklink 退出码 {mk});数据仍保留在目标和备份目录，可从历史记录回退");
            if (!IsLinkCreated(sourceFull))
                throw new InvalidOperationException("Junction 创建后未能确认原路径可访问");
            entry = entry with { State = MigrationState.Linked };
            AddOrUpdate(entry);

            // 只有 junction 已确认，才清理源备份；目标目录是迁移后的唯一真实数据副本。
            TryDeleteDirectory(backup);
            if (Directory.Exists(backup))
                throw new InvalidOperationException("迁移链接已建立，但源备份无法清理；为安全起见保留未完成状态");

            entry = entry with { State = MigrationState.Completed, Error = null };
            AddOrUpdate(entry);
            return entry;
        }
        catch (OperationCanceledException)
        {
            var canceled = entry with { State = MigrationState.Failed, Error = "用户取消或操作被中断" };
            AddOrUpdate(canceled);
            throw;
        }
        catch (Exception ex)
        {
            var failed = entry with { State = MigrationState.Failed, Error = ex.Message };
            AddOrUpdate(failed);
            throw;
        }
    }

    private bool Revert(MigrationLogEntry supplied, CancellationToken ct)
    {
        if (!ValidateLogEntry(supplied)) return false;
        MigrationLogEntry entry = Find(supplied) ?? supplied;
        if (entry.Reverted || entry.State == MigrationState.Reverted) return true;
        AddOrUpdate(entry with { State = MigrationState.Reverting, Error = null });
        try
        {
            ct.ThrowIfCancellationRequested();
            var source = PathSafetyService.Normalize(entry.Source);
            var target = PathSafetyService.Normalize(entry.Target);
            var backup = PathSafetyService.Normalize(entry.BackupPath);
            if (source is null || target is null) return false;
            var hasBackup = backup is not null && Directory.Exists(backup);
            if (!Directory.Exists(target) && !hasBackup) return false;
            if (Directory.Exists(target) && PathSafetyService.IsReparsePoint(target)) return false;
            if (File.Exists(target)) return false;

            // 只删除确认过的 junction；普通目录绝不递归删除。
            if (Directory.Exists(source))
            {
                if (!PathSafetyService.IsReparsePoint(source))
                {
                    if (backup is null || !Directory.Exists(backup)) return false;
                }
                else Directory.Delete(source, recursive: false);
            }
            if (File.Exists(source + ".junction")) File.Delete(source + ".junction");

            if (backup is not null && Directory.Exists(backup))
            {
                if (Directory.Exists(source) || File.Exists(source)) return false;
                Directory.Move(backup, source);
                var restored = Snapshot(source, ct);
                if (!restored.IsComplete || (entry.SourceFileCount > 0 && restored.Files != entry.SourceFileCount)) return false;
            }
            else
            {
                if (Directory.Exists(source) || File.Exists(source)) return false;
                int code = _runner(new[] { "robocopy" }.Concat(RobocopyArgs).Concat(new[] { target, source }).ToArray());
                if (code >= 8 || !Directory.Exists(source)) return false;
                var restored = Snapshot(source, ct);
                var expected = Snapshot(target, ct);
                if (!restored.IsComplete || !expected.IsComplete || restored.Files != expected.Files || restored.Bytes != expected.Bytes) return false;
            }

            // 原路径数据已恢复并校验后，才删除迁移副本；目标盘已消失时无需删除。
            if (Directory.Exists(target))
            {
                if (PathSafetyService.IsReparsePoint(target)) return false;
                TryDeleteDirectory(target);
                if (Directory.Exists(target)) return false;
            }
            var done = entry with { Reverted = true, State = MigrationState.Reverted, Error = null };
            AddOrUpdate(done);
            return true;
        }
        catch
        {
            AddOrUpdate(entry with { State = MigrationState.Failed, Error = "回退未完成，数据副本已保留" });
            return false;
        }
    }

    private bool ValidateLogEntry(MigrationLogEntry entry)
    {
        var source = PathSafetyService.Normalize(entry.Source);
        var target = PathSafetyService.Normalize(entry.Target);
        if (source is null || target is null) return false;
        if (SystemPathGuard.IsProtected(source) || SystemPathGuard.IsProtected(target) || SystemPathGuard.IsProtected(entry.Junction)) return false;
        if (PathSafetyService.IsDriveRoot(source) || PathSafetyService.IsDriveRoot(target)) return false;
        if (PathSafetyService.IsSameOrDescendant(target, source) || PathSafetyService.IsSameOrDescendant(source, target)) return false;
        return true;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_logPath)) return;
            var entries = JsonSerializer.Deserialize<List<MigrationLogEntry>>(File.ReadAllText(_logPath)) ?? new();
            lock (_gate)
            {
                foreach (var entry in entries.Take(LogCapacity)) _log.AddLast(entry);
            }
        }
        catch { }
    }

    private MigrationLogEntry? Find(MigrationLogEntry entry)
    {
        lock (_gate)
            return _log.FirstOrDefault(e => e.OperationId == entry.OperationId) ??
                   _log.FirstOrDefault(e => string.Equals(e.Source, entry.Source, StringComparison.OrdinalIgnoreCase) && string.Equals(e.Target, entry.Target, StringComparison.OrdinalIgnoreCase));
    }

    private void AddOrUpdate(MigrationLogEntry entry)
    {
        lock (_gate)
        {
            var node = _log.First;
            while (node is not null)
            {
                if (node.Value.OperationId == entry.OperationId ||
                    (string.Equals(node.Value.Source, entry.Source, StringComparison.OrdinalIgnoreCase) && string.Equals(node.Value.Target, entry.Target, StringComparison.OrdinalIgnoreCase)))
                {
                    node.Value = entry;
                    PersistUnsafe();
                    return;
                }
                node = node.Next;
            }
            _log.AddFirst(entry);
            while (_log.Count > LogCapacity) _log.RemoveLast();
            PersistUnsafe();
        }
    }

    private void PersistUnsafe()
    {
        try { AtomicFile.WriteAllText(_logPath, JsonSerializer.Serialize(_log.ToList())); } catch { }
    }

    private static bool IsLinkCreated(string source)
    {
        if (File.Exists(source + ".junction")) return true; // 测试替身
        try { return Directory.Exists(source) && (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    private static void EnsureFreeSpace(string targetRoot, long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(targetRoot);
            if (string.IsNullOrWhiteSpace(root)) return;
            var drive = new DriveInfo(root);
            if (!drive.IsReady) throw new InvalidOperationException("目标盘不可用");
            const long safetyMargin = 64L * 1024 * 1024;
            var required = requiredBytes > long.MaxValue - safetyMargin ? long.MaxValue : requiredBytes + safetyMargin;
            if (drive.AvailableFreeSpace < required)
                throw new InvalidOperationException("目标盘可用空间不足");
        }
        catch (DriveNotFoundException) { throw new InvalidOperationException("目标盘不可用"); }
        catch (UnauthorizedAccessException) { throw new InvalidOperationException("无法读取目标盘空间或权限不足"); }
    }

    private sealed record SnapshotInfo(long Bytes, int Files, bool IsComplete, int SkippedCount);

    private static SnapshotInfo Snapshot(string dir, CancellationToken ct = default)
    {
        long bytes = 0;
        int files = 0;
        int skipped = 0;
        bool complete = true;
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = stack.Pop();
            IEnumerable<string> fileList;
            try { fileList = Directory.EnumerateFiles(current, "*", new EnumerationOptions { IgnoreInaccessible = true }); }
            catch { complete = false; skipped++; continue; }
            try
            {
                foreach (var file in fileList)
                {
                    ct.ThrowIfCancellationRequested();
                    try { bytes = checked(bytes + new FileInfo(file).Length); files++; }
                    catch { complete = false; skipped++; }
                }
            }
            catch { complete = false; skipped++; }
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(current, "*", new EnumerationOptions { IgnoreInaccessible = true }); }
            catch { complete = false; skipped++; continue; }
            try
            {
                foreach (var child in dirs)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        { complete = false; skipped++; continue; }
                        stack.Push(child);
                    }
                    catch { complete = false; skipped++; }
                }
            }
            catch { complete = false; skipped++; }
        }
        return new SnapshotInfo(bytes, files, complete, skipped);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

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
            foreach (var arg in args) psi.ArgumentList.Add(arg);
        }
        else
        {
            psi.FileName = args[0];
            foreach (var arg in args.Skip(1)) psi.ArgumentList.Add(arg);
        }
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动 {args[0]}");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(output, error);
        return process.ExitCode;
    }
}




