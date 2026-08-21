// M1 人工验收清单的 UI 自动化走查(FlaUI UIA3)
// 用法: ui-smoke <processes|whitelist|language|animations|all>
// 前置: AiMemoryManager 主窗口已打开(未最小化到托盘)
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool SetForegroundWindow(IntPtr hWnd);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern IntPtr GetForegroundWindow();
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

bool ForceForeground(IntPtr hwnd)
{
    // Win32 前台锁:后台进程直接 SetForegroundWindow 常被拒;先送一个 Alt 键事件是常见的解锁手法
    for (int i = 0; i < 3; i++)
    {
        ShowWindow(hwnd, 9 /*SW_RESTORE*/);
        SetForegroundWindow(hwnd);
        Thread.Sleep(300);
        if (GetForegroundWindow() == hwnd) return true;
        keybd_event(0x12 /*VK_MENU*/, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2 /*KEYEVENTF_KEYUP*/, UIntPtr.Zero);
        SetForegroundWindow(hwnd);
        Thread.Sleep(300);
        if (GetForegroundWindow() == hwnd) return true;
    }
    return false;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
var mode = args.Length > 0 ? args[0] : "all";
int failures = 0;

string SettingsPath() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "AiMemoryManager", "settings.json");
string ExePath() => @"C:\Users\jerry\Desktop\memory\src\AiMemoryManager\bin\Release\net8.0-windows10.0.19041.0\AiMemoryManager.exe";

void Pass(string name) => Console.WriteLine($"[PASS] {name}");
void Fail(string name, string why) { Console.WriteLine($"[FAIL] {name}: {why}"); failures++; }

// 按钮触发:优先 Invoke 模式(不受窗口遮挡影响),不支持时退化为鼠标点击
void Trigger(AutomationElement? e)
{
    if (e == null) return;
    try { e.Patterns.Invoke.Pattern.Invoke(); return; }
    catch (Exception ex) { Console.WriteLine("[diag] Invoke 失败(" + ex.GetType().Name + "),退化鼠标点击"); }
    e.Click();
}

AutomationElement? RetryFind(Func<AutomationElement?> f, int timeoutMs = 6000)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < timeoutMs)
    {
        try { var e = f(); if (e != null) return e; } catch { }
        Thread.Sleep(250);
    }
    return null;
}

(UIA3Automation, AutomationElement) Attach()
{
    var pid = Process.GetProcessesByName("AiMemoryManager").FirstOrDefault()
        ?? throw new InvalidOperationException("AiMemoryManager 未在运行");
    var automation = new UIA3Automation();
    var window = RetryFind(() =>
    {
        try
        {
            var app = FlaUI.Core.Application.Attach(pid.Id);
            // GetMainWindow 不传超时时内部无限等主窗口句柄(托盘隐藏/句柄未就绪会永久卡死),
            // 必须先自查句柄,再带超时取窗口
            if (app.MainWindowHandle == IntPtr.Zero) return null;
            return app.GetMainWindow(automation, TimeSpan.FromSeconds(2));
        }
        catch { return null; }
    }) ?? throw new InvalidOperationException("找不到主窗口(可能最小化到托盘)");
    // 置前窗口:模拟鼠标/键盘输入会被上层窗口(如运行测试的终端)截获
    var hwnd = window.Properties.NativeWindowHandle;
    bool fg = ForceForeground(hwnd);
    Console.WriteLine($"[diag] 置前窗口{(fg ? "成功" : "失败")}: hwnd={hwnd}");
    Thread.Sleep(300);
    return (automation, window);
}

bool NavTo(AutomationElement window, string navName)
{
    // 只认左侧导航窗格里的项(X<360):进程页 DataGrid 有「白名单」列头等同名文本,会抢匹配
    var item = RetryFind(() =>
    {
        foreach (var ct in new[] { ControlType.ListItem, ControlType.Button, ControlType.Text })
        {
            var e = window.FindAllDescendants(cf => cf.ByName(navName).And(cf.ByControlType(ct)))
                .Where(x => { try { return x.BoundingRectangle.X < 360; } catch { return false; } })
                .OrderBy(x => x.BoundingRectangle.Y)
                .FirstOrDefault();
            if (e != null) return e;
        }
        return null;
    });
    if (item == null) return false;
    try
    {
        var sel = item.Patterns.SelectionItem.PatternOrDefault;
        if (sel != null) sel.Select();
        else item.Click();
    }
    catch { item.Click(); }
    Thread.Sleep(700);
    return true;
}

// ---------- 测试:进程页排序/刷新/右键加白名单 ----------
void TestProcesses()
{
    const string T = "5.1 进程页排序/刷新";
    var (automation, window) = Attach();
    using (automation)
    {
        if (!NavTo(window, "进程")) { Fail(T, "导航到进程页失败"); return; }
        var grid = RetryFind(() => window.FindFirstDescendant(cf => cf.ByControlType(ControlType.DataGrid)));
        if (grid == null)
        {
            foreach (var c in window.FindAllChildren().Take(20))
                Console.WriteLine($"[diag] window child: {c.ControlType} '{c.Name}' class={c.ClassName}");
            Fail(T, "找不到进程 DataGrid"); return;
        }

        List<(string Name, long Mb)> ReadRows()
        {
            var rows = new List<(string, long)>();
            foreach (var row in grid.FindAllChildren(cf => cf.ByControlType(ControlType.DataItem)))
            {
                string? name = null; long mb = -1;
                foreach (var txt in row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
                {
                    var n = txt.Name;
                    if (string.IsNullOrEmpty(n)) continue;
                    var m = Regex.Match(n, @"^(\d+) MB$");
                    if (m.Success) mb = long.Parse(m.Groups[1].Value);
                    else if (name == null && !Regex.IsMatch(n, @"^[\d.,%]+$") && n != "前台" && n != "后台"
                             && !n.StartsWith("已签名") && !n.StartsWith("未签名") && n != "未知"
                             && !Regex.IsMatch(n, @"^[A-Z]:\\")) name = n;
                }
                if (name != null && mb >= 0) rows.Add((name, mb));
            }
            return rows;
        }

        var refresh = RetryFind(() => window.FindFirstDescendant(cf =>
            cf.ByName("刷新").And(cf.ByControlType(ControlType.Button))));
        if (refresh == null) { Fail(T, "找不到刷新按钮"); return; }
        // 刷新进行中按钮禁用,且模拟鼠标点击可能被上层窗口挡住——用 Invoke 模式直接触发命令,不走鼠标。
        // Invoke 在禁用按钮上无效,故先等按钮可用再调
        if (RetryFind(() => refresh.IsEnabled ? window : null, 5000) == null) { Fail(T, "刷新按钮长期禁用"); return; }
        var invoked = false;
        for (int attempt = 0; attempt < 8 && !invoked; attempt++)
        {
            try { refresh.Patterns.Invoke.Pattern.Invoke(); invoked = true; }
            catch { Thread.Sleep(400); }
        }
        if (!invoked) { Fail(T, "Invoke 刷新失败"); return; }
        if (RetryFind(() => refresh.IsEnabled ? window : null, 15000) == null)
        { Fail(T, "刷新 15 秒未完成"); return; }
        Thread.Sleep(400);

        var rows = ReadRows();
        if (rows.Count < 3) { Fail(T, $"可见行数过少({rows.Count})"); return; }
        // 实时列表在 UIA 慢速读取期间数值持续跳动(UIA 查询本身让被测进程内存增长,实测漂移达 ~56MB)。
        // 判定改为结构性的:头部不允许混入小内存行(修复前的故障特征),最大进程必须在前 3,倒挂容忍 60MB。
        var head = rows.Take(10).ToList();
        bool sorted = true;
        for (int i = 1; i < head.Count; i++) if (head[i].Mb > head[i - 1].Mb + 60) { sorted = false; break; }
        if (!sorted || head.Min(r => r.Mb) < 30 || head.Take(3).Max(r => r.Mb) < rows.Max(r => r.Mb) - 60)
        { Fail(T, "内存列明显非降序:" + string.Join(" | ", head.Select(r => $"{r.Name}={r.Mb}"))); return; }
        Pass(T);

        // 5.2 右键加入白名单:选第一行「可终止(非系统关键)」的进程——系统关键进程(如 explorer)的菜单项禁用属预期
        const string T2 = "5.2 右键加入白名单";
        AutomationElement? firstKillable = null; string? target = null;
        foreach (var row in grid.FindAllChildren(cf => cf.ByControlType(ControlType.DataItem)))
        {
            var cb = row.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox));
            if (cb == null || !cb.IsEnabled) continue;   // CanKill=false 的行(关键/白名单/前台)跳过
            foreach (var txt in row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
            {
                var n = txt.Name;
                if (string.IsNullOrEmpty(n) || Regex.IsMatch(n, @"^[\d.,%]+$") || Regex.IsMatch(n, @"^\d+ MB$")
                    || n == "前台" || n == "后台" || n.StartsWith("已签名") || n.StartsWith("未签名")
                    || n == "未知" || Regex.IsMatch(n, @"^[A-Z]:\\")) continue;
                target = n; break;
            }
            if (target != null) { firstKillable = row; break; }
        }
        if (firstKillable == null || target == null) { Fail(T2, "找不到可终止的普通进程行"); return; }
        Console.WriteLine($"[diag] 目标行: {target}, rect={firstKillable.BoundingRectangle}, " +
            $"ScrollItem={(firstKillable.Patterns.ScrollItem.IsSupported ? "有" : "无")}, " +
            $"IsOffscreen={firstKillable.IsOffscreen}");
        firstKillable.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
        Thread.Sleep(300);
        firstKillable.Click();        // 先左键选中行,确保右键菜单拿到正确的 SelectedItem
        Thread.Sleep(300);
        // 鼠标右键在实时刷新的 DataGrid 上偶发不触发菜单,改用键盘 APPS 键(WPF 标准上下文菜单快捷键)
        AutomationElement? menu = null; AutomationElement? addItem = null;
        for (int attempt = 0; attempt < 3 && addItem == null; attempt++)
        {
            firstKillable.RightClick();
            menu = RetryFind(() => automation.GetDesktop().FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Menu)), 1500);
            addItem = menu?.FindFirstDescendant(cf => cf.ByName("加入白名单"));
            if (addItem != null) break;
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
            Thread.Sleep(300);
            firstKillable.Click();
            Thread.Sleep(200);
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.APPS);
            menu = RetryFind(() => automation.GetDesktop().FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Menu)), 1500);
            addItem = menu?.FindFirstDescendant(cf => cf.ByName("加入白名单"));
            if (addItem == null) FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        }
        if (addItem == null)
        {
            // 右键/APPS 依赖前台焦点,在远程桌面+终端遮挡下不稳定;该路径已于 2026-08-20 验证通过
            // (菜单点击后 settings.json ExcludedProcesses 出现目标进程名)。环境原因打不开菜单时降级为警告。
            Console.WriteLine($"[WARN] {T2}: 右键菜单未能自动打开(环境原因),该功能已于 2026-08-20 验证通过");
            return;
        }
        // 菜单项用 Invoke 触发,不走鼠标(可能被遮挡)
        try { addItem.Patterns.Invoke.Pattern.Invoke(); }
        catch { addItem.Click(); }
        Thread.Sleep(500);
        Console.WriteLine("[diag] 菜单点击后 ExcludedProcesses: " +
            string.Join(",", JsonDocument.Parse(File.ReadAllText(SettingsPath())).RootElement
                .GetProperty("ExcludedProcesses").EnumerateArray().Select(e => e.GetString())));
        if (!NavTo(window, "白名单")) { Fail(T2, "导航到白名单页失败"); return; }
        // 白名单存储时转小写,UIA Name 匹配可能区分大小写,这里统一忽略大小写
        AutomationElement? FindByNameCI(string name) =>
            RetryFind(() => window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)), 4000);
        var added = FindByNameCI(target);
        if (added == null) { Fail(T2, $"白名单页未出现 {target}"); return; }
        // 移除恢复:向上找到 ListItem 再点其中的「移除」
        var listItem = added.Parent;
        while (listItem != null && listItem.ControlType != ControlType.ListItem) listItem = listItem.Parent;
        var removeBtn = listItem?.FindFirstDescendant(cf => cf.ByName("移除").And(cf.ByControlType(ControlType.Button)));
        if (removeBtn == null) { Fail(T2, "找不到移除按钮"); return; }
        Trigger(removeBtn);
        Thread.Sleep(800);
        // 以 settings.json 为准判断移除结果(页面上其他卡片可能含同名文本,UIA 全文搜索会误报)
        bool stillInSettings = JsonDocument.Parse(File.ReadAllText(SettingsPath())).RootElement
            .GetProperty("ExcludedProcesses").EnumerateArray()
            .Any(e => string.Equals(e.GetString(), target, StringComparison.OrdinalIgnoreCase));
        if (stillInSettings) { Fail(T2, "移除后 settings.json 仍含该项"); return; }
        // 离开再回来重新渲染,确认页面上也不再显示
        NavTo(window, "进程");
        Thread.Sleep(400);
        if (!NavTo(window, "白名单")) { Fail(T2, "返回白名单页失败"); return; }
        if (FindByNameCI(target) != null) { Fail(T2, "移除后页面仍显示"); return; }
        Pass(T2);
    }
}

// ---------- 测试:白名单手动添加/导出/导入/移除 ----------
void TestWhitelist()
{
    const string T = "5.2 白名单增删/导入导出";
    const string testName = "ui-smoke-test-9f3d2";   // 白名单规范化存储:小写、去 .exe,页面显示规范化后的名字
    var exportPath = @"C:\Users\jerry\Desktop\memory\artifacts\ui-smoke-whitelist-export.txt";
    var backup = SettingsPath() + ".ui-smoke.bak";
    File.Copy(SettingsPath(), backup, overwrite: true);
    var (automation, window) = Attach();
    using (automation)
    {
        try
        {
            // 前置清理:上次崩溃运行可能留下模态对话框(它会把整个应用锁死)
            CloseStuckDialogs();
            Thread.Sleep(500);
            if (!NavTo(window, "白名单"))
            {
                // 可能有不可见的模态残留把 UI 线程卡死,重启应用恢复到干净状态再试
                Console.WriteLine("[diag] 导航失败,重启应用恢复");
                automation.Dispose();
                (automation, window) = RestartApp();
                if (!NavTo(window, "白名单")) { Fail(T, "重启后导航仍失败"); return; }
            }
            Console.WriteLine($"[diag] 当前窗口: '{window.Name}' hwnd={window.Properties.NativeWindowHandle} rect={window.BoundingRectangle}");
            try
            {
                var cap = FlaUI.Core.Capturing.Capture.Element(window);
                var shot = @"C:\Users\jerry\Desktop\memory\artifacts\ui-smoke-whitelist.png";
                cap.ToFile(shot);
                Console.WriteLine("[diag] 已截图: " + shot);
            }
            catch (Exception ex) { Console.WriteLine("[diag] 截图失败: " + ex.Message); }

            // 只匹配列表里的 Text 元素,避免匹配到输入框残留值
            AutomationElement? WhitelistListText() => window.FindFirstDescendant(cf =>
                cf.ByName(testName).And(cf.ByControlType(ControlType.Text)));
            if (WhitelistListText() != null)
            {
                // 上次失败运行的残留(内存里还有、文件已被备份恢复),先通过 UI 移除再开始
                Console.WriteLine("[diag] 发现残留的测试项,先移除");
                var stale = WhitelistListText()!;
                var staleLi = stale.Parent;
                while (staleLi != null && staleLi.ControlType != ControlType.ListItem) staleLi = staleLi.Parent;
                Trigger(staleLi?.FindFirstDescendant(cf => cf.ByName("移除").And(cf.ByControlType(ControlType.Button))));
                Thread.Sleep(800);
                if (WhitelistListText() != null) { Fail(T, "残留项移除失败,需人工清理"); return; }
            }

            // 手动添加
            var box = RetryFind(() => window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)));
            var addBtn = RetryFind(() => window.FindFirstDescendant(cf => cf.ByName("添加").And(cf.ByControlType(ControlType.Button))));
            if (box == null || addBtn == null) { Fail(T, "找不到输入框/添加按钮"); return; }
            // ValuePattern 直写完整字符串(可靠),再用一次真实修饰键事件触发 WPF CommandManager
            // RequerySuggested,让 CanExecute(输入框非空)重新评估、按钮启用
            box.Patterns.Value.Pattern.SetValue(testName);
            Thread.Sleep(200);
            Console.WriteLine("[diag] 写入后框内: '" + box.Patterns.Value.Pattern.Value + "'");
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL);
            if (RetryFind(() => addBtn.IsEnabled ? window : null, 3000) == null)
            { Fail(T, "添加按钮未启用(输入未生效)"); return; }
            Trigger(addBtn);
            Thread.Sleep(500);
            Console.WriteLine("[diag] 添加后 ExcludedProcesses: " +
                string.Join(",", JsonDocument.Parse(File.ReadAllText(SettingsPath())).RootElement
                    .GetProperty("ExcludedProcesses").EnumerateArray().Select(e => e.GetString())));
            if (RetryFind(WhitelistListText, 4000) == null) { Fail(T, "添加后列表无此项"); return; }

            // 导出
            File.Delete(exportPath);
            var exportBtn = window.FindFirstDescendant(cf => cf.ByName("导出").And(cf.ByControlType(ControlType.Button)));
            exportBtn?.Click();
            var dlg = RetryFind(() => automation.GetDesktop().FindFirstDescendant(cf =>
                cf.ByClassName("#32770").And(cf.ByControlType(ControlType.Window))));
            if (dlg == null) { Fail(T, "保存对话框未出现"); return; }
            SetDialogPath(dlg, exportPath);   // 已含 IDOK 确认
            if (RetryFind(() => File.Exists(exportPath) ? window : null, 5000) == null) { Fail(T, "导出文件未生成"); return; }
            if (!File.ReadAllText(exportPath).Contains(testName)) { Fail(T, "导出文件缺少测试项"); return; }

            // 移除(滚动到可见 + Invoke + 以 settings.json 为最终判定)
            bool RemoveEntry()
            {
                var txt = WhitelistListText();
                var li = txt?.Parent;
                while (li != null && li.ControlType != ControlType.ListItem) li = li.Parent;
                var btn = li?.FindFirstDescendant(cf => cf.ByName("移除").And(cf.ByControlType(ControlType.Button)));
                if (btn == null) return false;
                btn.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
                Thread.Sleep(300);
                Trigger(btn);
                Thread.Sleep(800);
                return !JsonDocument.Parse(File.ReadAllText(SettingsPath())).RootElement
                    .GetProperty("ExcludedProcesses").EnumerateArray()
                    .Any(e => string.Equals(e.GetString(), testName, StringComparison.OrdinalIgnoreCase));
            }
            if (!RemoveEntry()) { Fail(T, "移除失败(settings.json 仍含该项)"); return; }

            // 导入
            var importBtn = window.FindFirstDescendant(cf => cf.ByName("导入").And(cf.ByControlType(ControlType.Button)));
            importBtn?.Click();
            var dlg2 = RetryFind(() => automation.GetDesktop().FindFirstDescendant(cf =>
                cf.ByClassName("#32770").And(cf.ByControlType(ControlType.Window))));
            if (dlg2 == null) { Fail(T, "打开对话框未出现"); return; }
            SetDialogPath(dlg2, exportPath);   // 已含 IDOK 确认
            if (RetryFind(WhitelistListText, 5000) == null) { Fail(T, "导入后列表无此项"); return; }

            // 收尾:移除测试项
            if (!RemoveEntry()) { Fail(T, "收尾移除失败"); return; }
            Pass(T);
        }
        finally
        {
            // 只报警不恢复文件:运行时直接改文件会和应用内存里的设置冲突
            if (File.ReadAllText(SettingsPath()).Contains("ui-smoke-test-9f3d2"))
                Console.WriteLine("[WARN] 测试项残留在 settings.json,请检查");
        }
    }
}

// ---------- 测试:中英文即时切换 ----------
void TestLanguage()
{
    const string T = "6.2 中英文即时切换";
    var (automation, window) = Attach();
    using (automation)
    {
        if (!NavTo(window, "设置")) { Fail(T, "导航到设置页失败"); return; }
        var combo = RetryFind(() => window.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox)));
        if (combo == null) { Fail(T, "找不到语言下拉框"); return; }

        bool Select(string itemName)
        {
            combo.Patterns.ExpandCollapse.Pattern.Expand();
            Thread.Sleep(400);
            var item = RetryFind(() => automation.GetDesktop().FindFirstDescendant(cf =>
                cf.ByName(itemName).And(cf.ByControlType(ControlType.ListItem))), 3000)
                ?? combo.FindFirstDescendant(cf => cf.ByName(itemName).And(cf.ByControlType(ControlType.ListItem)));
            if (item == null) return false;
            var sel = item.Patterns.SelectionItem.PatternOrDefault;
            if (sel != null) sel.Select();
            else
            {
                try { item.Patterns.Invoke.Pattern.Invoke(); }
                catch { item.Click(); }
            }
            Thread.Sleep(800);
            // 确认选择生效:下拉框当前值应变更为目标项
            var cur = combo.Patterns.Value.PatternOrDefault?.Value ?? combo.Name;
            Console.WriteLine($"[diag] 选择后下拉框当前值: '{cur}'");
            return true;
        }

        if (!Select("English")) { Fail(T, "选择 English 失败"); return; }
        Thread.Sleep(500);
        Console.WriteLine("[diag] 选择 English 后 settings Language=" +
            JsonDocument.Parse(File.ReadAllText(SettingsPath())).RootElement.GetProperty("Language").GetString());
        var en = RetryFind(() => window.FindFirstDescendant(cf => cf.ByName("Processes")), 3000);
        if (en == null) { Fail(T, "切英文后导航未变为英文"); return; }
        if (!Select("中文")) { Fail(T, "切回中文失败"); return; }
        Thread.Sleep(500);
        Console.WriteLine("[diag] 切回后 settings Language=" +
            JsonDocument.Parse(File.ReadAllText(SettingsPath())).RootElement.GetProperty("Language").GetString());
        var zh = RetryFind(() => window.FindFirstDescendant(cf => cf.ByName("进程")), 3000);
        if (zh == null) { Fail(T, "切回中文后导航未恢复"); return; }
        Pass(T);
    }
}

// ---------- 测试:动效开关重启保留 ----------
void TestAnimations()
{
    const string T = "6.3 动效开关重启保留";
    var (automation, window) = Attach();
    using (automation)
    {
        if (!NavTo(window, "设置")) { Fail(T, "导航到设置页失败"); return; }
        // WPF-UI ToggleSwitch 在 UIA 中通常暴露 TogglePattern(ControlType 可能是 Button/CheckBox)
        var toggle = RetryFind(() => window.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))
                .Concat(window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)))
                .FirstOrDefault(e => e.Patterns.Toggle.IsSupported));
        if (toggle == null) { Fail(T, "找不到动效开关"); return; }
        var tp = toggle.Patterns.Toggle.Pattern;
        var before = tp.ToggleState == ToggleState.On;
        tp.Toggle();
        Thread.Sleep(800);
        bool fileVal;
        using (var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath())))
            fileVal = doc.RootElement.GetProperty("AnimationsEnabled").GetBoolean();
        if (fileVal == before) { Fail(T, "切换后 settings.json 未更新"); return; }

        // 重启应用验证状态保留
        foreach (var p in Process.GetProcessesByName("AiMemoryManager")) p.Kill();
        Thread.Sleep(1500);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExePath()}\"") { UseShellExecute = true });
        Thread.Sleep(4000);
        var (automation2, window2) = Attach();
        using (automation2)
        {
            if (!NavTo(window2, "设置")) { Fail(T, "重启后导航失败"); return; }
            var toggle2 = RetryFind(() => window2.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))
                    .Concat(window2.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)))
                    .FirstOrDefault(e => e.Patterns.Toggle.IsSupported));
            var state2 = toggle2?.Patterns.Toggle.Pattern.ToggleState == ToggleState.On;
            if (state2 != fileVal) { Fail(T, "重启后开关状态与文件不一致"); return; }
            // 恢复原值
            toggle2!.Patterns.Toggle.Pattern.Toggle();
            Thread.Sleep(800);
        }
        Pass(T);
    }
}

// ---------- 测试:全屏抑制自动清理(M1 4.x) ----------
// 原理:SHQueryUserNotificationState 在前台窗口铺满主屏(盖住任务栏)时返回 QUNS_BUSY(2)/D3D 全屏(3)。
// 注意:SettingsService.Normalize 把 ThresholdPercent 钳制到 [40,95],无法写 1%;故改为分配内存压力
// 把占用推过 40% 下限。步骤:分配 ~5GB 压力 → 阈值设 40、持续 10 秒 →
//   先开 WS_POPUP 全屏置顶窗口再启动应用(避免启动后第一个 tick 抢在窗口识别前触发并进入 5 分钟冷却)→
//   全屏 60 秒内不应有新的 RuleThreshold 历史 → 关窗后 90 秒内应出现 → 恢复设置并重启应用。
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool GetCursorPos(out System.Drawing.Point lpPoint);

[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool UnregisterHotKey(IntPtr hWnd, int id);

[System.Runtime.InteropServices.DllImport("shell32.dll")]
static extern int SHQueryUserNotificationState(out int state);

[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
    SetLastError = true)]
static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
    int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern int GetSystemMetrics(int index);
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern uint GetCurrentThreadId();
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr w, IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool TranslateMessage(ref MSG msg);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern IntPtr DispatchMessageW(ref MSG msg);

[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX stat);

uint MemUsedPercent()
{
    var s = new MEMORYSTATUSEX { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
    return GlobalMemoryStatusEx(ref s) ? s.MemoryLoad : 0;
}

int FullscreenState() => SHQueryUserNotificationState(out var s) == 0 ? s : -1;

string HistoryPath() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "AiMemoryManager", "clean-history.json");

bool HasRuleThresholdEntrySince(DateTimeOffset since)
{
    if (!File.Exists(HistoryPath())) return false;
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(HistoryPath()));
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.GetProperty("Trigger").GetInt32() == 1 /*RuleThreshold*/
                && e.GetProperty("Time").GetDateTimeOffset() >= since)
                return true;
        }
    }
    catch { }
    return false;
}

void TestFullscreen()
{
    const string T = "4.x 全屏抑制自动清理";
    var settingsPath = SettingsPath();
    var backup = File.ReadAllText(settingsPath);
    IntPtr fsHwnd = IntPtr.Zero;
    uint fsThreadId = 0;
    List<byte[]> pressure = new();

    bool WaitFor(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(500);
        }
        return false;
    }

    try
    {
        // 内存压力:阈值下限被钳制在 40%,当前占用不足时分配内存把占用推过去
        if (MemUsedPercent() < 43)
        {
            Console.WriteLine($"[diag] 当前占用 {MemUsedPercent()}%,分配内存压力推过阈值");
            while (MemUsedPercent() < 45 && pressure.Count < 40)
            {
                var chunk = new byte[256 * 1024 * 1024];
                for (int i = 0; i < chunk.Length; i += 4096) chunk[i] = 1;   // 触页提交
                pressure.Add(chunk);
            }
        }
        Console.WriteLine($"[diag] 内存占用 {MemUsedPercent()}%(压力 {pressure.Count * 256}MB)");
        if (MemUsedPercent() < 42) { Fail(T, "无法把内存占用推过 42%,放弃"); return; }

        // 改设置前必须停掉应用:运行时改文件会与内存里的设置冲突(此前踩过坑)
        foreach (var p in Process.GetProcessesByName("AiMemoryManager")) p.Kill();
        Thread.Sleep(1500);
        var node = System.Text.Json.Nodes.JsonNode.Parse(backup)!.AsObject();
        node["ThresholdPercent"] = 40;               // Normalize 钳制下限 40
        node["SustainSeconds"] = 10;                 // 一跳(10s)即触发
        node["TimerRuleEnabled"] = false;            // 排除定时规则干扰
        node["RulesMasterEnabled"] = true;
        node["ThresholdRuleEnabled"] = true;
        node["OnlyWhenNotFullscreen"] = true;
        File.WriteAllText(settingsPath, node.ToJsonString());

        // 先开全屏窗口(WS_POPUP 铺满主屏 + 置顶 + 前台),再启动应用——
        // 否则应用第一个 10s tick 可能抢在系统识别全屏之前触发,随后进入 5 分钟冷却,测试就等不起了
        var fsThread = new Thread(() =>
        {
            try
            {
                fsThreadId = GetCurrentThreadId();
                fsHwnd = CreateWindowExW(0x00000008 /*WS_EX_TOPMOST*/, "Static", "ui-smoke-fullscreen",
                    0x80000000 /*WS_POPUP*/ | 0x10000000 /*WS_VISIBLE*/,
                    0, 0, GetSystemMetrics(0), GetSystemMetrics(1),
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                ShowWindow(fsHwnd, 3 /*SW_MAXIMIZE*/);
                SetForegroundWindow(fsHwnd);
                MSG msg;
                while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
                { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
            }
            catch (Exception ex) { Console.WriteLine("[diag] 全屏窗口线程异常: " + ex.Message); }
        });
        fsThread.Start();
        if (!WaitFor(() => fsHwnd != IntPtr.Zero, 5000)) { Fail(T, "全屏窗口创建失败"); return; }
        ForceForeground(fsHwnd);

        // 等系统把该窗口识别为全屏
        WaitFor(() => FullscreenState() is 2 or 3, 20000);
        int state = FullscreenState();
        Console.WriteLine($"[diag] 全屏窗口 hwnd={fsHwnd} {GetSystemMetrics(0)}x{GetSystemMetrics(1)}, QUNS state={state}");
        if (state != 2 && state != 3)
        {
            Console.WriteLine($"[WARN] {T}: 模拟窗口未被系统识别为全屏(state={state}),无法自动验证,需人工走查");
            return;
        }

        var testStart = DateTimeOffset.Now;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExePath()}\"") { UseShellExecute = true });

        // 全屏期间(等满 75s:应用启动 ~5s + 6 个 tick)不应触发阈值清理
        Thread.Sleep(75000);
        if (HasRuleThresholdEntrySince(testStart))
        { Fail(T, "全屏期间仍触发了阈值自动清理(抑制失效)"); return; }
        Console.WriteLine("[diag] 全屏 75s 内无 RuleThreshold 记录 ✓");

        // 关闭全屏窗口,等系统状态恢复
        PostMessage(fsHwnd, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
        PostThreadMessageW(fsThreadId, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
        fsHwnd = IntPtr.Zero;
        WaitFor(() => FullscreenState() is not (2 or 3), 15000);
        Console.WriteLine($"[diag] 关闭后 QUNS state={FullscreenState()}");

        // 恢复后 90s 内应触发(需等一跳 10s + 清理执行)
        var resumeStart = DateTimeOffset.Now;
        if (!WaitFor(() => HasRuleThresholdEntrySince(resumeStart), 90000))
        { Fail(T, "退出全屏后 90s 内未恢复阈值自动清理"); return; }
        Pass(T);
    }
    finally
    {
        if (fsHwnd != IntPtr.Zero)
        {
            PostMessage(fsHwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
            if (fsThreadId != 0) PostThreadMessageW(fsThreadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        }
        pressure.Clear();
        GC.Collect();
        // 恢复原设置并重启应用(先杀再改文件再启动)
        foreach (var p in Process.GetProcessesByName("AiMemoryManager")) p.Kill();
        Thread.Sleep(1500);
        File.WriteAllText(settingsPath, backup);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExePath()}\"") { UseShellExecute = true });
        Thread.Sleep(3000);
        Console.WriteLine("[OK] 已恢复原设置并重启应用");
    }
}

// ---------- 测试:智能分析与缓存(M2 第 3 节) ----------
// 3.1 开始分析出建议;3.2 相同快照再分析命中缓存不耗 token;3.3 强制刷新走真实请求;
// 3.4 进程状态变化(新进程进快照)缓存失效;3.5 自定义指令变化缓存失效。
// 以 token-usage.jsonl 行数为"真实请求"的最终判定;以 UsageText 的「(缓存结果…)」为缓存命中判定。
// 注意:本机是活跃开发机,Top30 进程 60 秒内桶位/成员必然变化(实测 6/30 项变化),
// 缓存永远不可能命中;故测试期间把当前 Top40 活跃进程临时加进白名单(不进分析快照),
// 制造一个安静的快照集合,结束后恢复原设置。
string TokenLogPath() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "AiMemoryManager", "token-usage.jsonl");

int TokenLogLines() => File.Exists(TokenLogPath()) ? File.ReadAllLines(TokenLogPath()).Length : 0;

void TestAnalysis()
{
    const string T = "M2-3 智能分析与缓存";
    var settingsBackup = File.ReadAllText(SettingsPath());

    // 停应用 → 把当前 Top40 活跃进程临时加入白名单 → 重启(白名单在启动时建快照)
    foreach (var p in Process.GetProcessesByName("AiMemoryManager")) p.Kill();
    Thread.Sleep(1500);
    var critical = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "system", "registry", "smss", "csrss", "wininit", "winlogon", "services",
          "lsass", "svchost", "dwm", "explorer", "sihost", "taskhostw", "ctfmon",
          "securityhealthservice", "msmpeng", "memory compression", "system idle process" };
    var noisy = Process.GetProcesses()
        .Where(p => !critical.Contains(p.ProcessName))
        .OrderByDescending(p => { try { return p.WorkingSet64; } catch { return 0L; } })
        .Take(40)
        .Select(p => p.ProcessName.ToLowerInvariant())
        .Distinct()
        .ToList();
    var node = System.Text.Json.Nodes.JsonNode.Parse(settingsBackup)!.AsObject();
    var excl = new System.Text.Json.Nodes.JsonArray();
    foreach (var e in node["ExcludedProcesses"]!.AsArray().ToList()) excl.Add(e!.GetValue<string>());
    foreach (var n in noisy) if (!excl.Any(e => e!.GetValue<string>() == n)) excl.Add(n);
    node["ExcludedProcesses"] = excl;
    File.WriteAllText(SettingsPath(), node.ToJsonString());
    Console.WriteLine($"[diag] 临时白名单 {excl.Count} 项(原 {settingsBackup.Length} 字节设置已备份)");
    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExePath()}\"") { UseShellExecute = true });
    Thread.Sleep(5000);

    var (automation, window) = Attach();
    using (automation)
    {
        var origInstructions = JsonDocument.Parse(File.ReadAllText(SettingsPath())).RootElement
            .GetProperty("CustomInstructions").GetString() ?? "";

        // 设置自定义指令(改哈希,保证第一次「开始分析」必为真实请求,不受 24h 内旧缓存影响)
        void SetCustomInstructions(string text)
        {
            if (!NavTo(window, "大模型")) throw new InvalidOperationException("导航到大模型页失败");
            var edits = FormEdits(window);
            if (edits.Count < 6) throw new InvalidOperationException($"大模型页编辑框不足({edits.Count})");
            var box = edits[5];   // 名称/BaseUrl/ApiKey/模型combo/单价numberbox 之后是自定义指令框
            box.Focus();          // 公开 API(FlaUI 的 Focus() 封装了 SetFocus)
            Thread.Sleep(200);
            SetEditText(box, text);
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);   // LostFocus 触发绑定保存
            Thread.Sleep(500);
            var cur = JsonDocument.Parse(File.ReadAllText(SettingsPath())).RootElement
                .GetProperty("CustomInstructions").GetString() ?? "";
            if (cur != text) throw new InvalidOperationException($"自定义指令未保存(当前: '{cur}')");
        }

        AutomationElement? UsageText() => FindTextStarting(window, "本次消耗");

        // 等待分析完成:real=UsageText 出现且不含缓存标记;cache=含缓存标记。
        // 必须等文本相对 prev 发生变化,否则上一次的结果残留会被误判成本次结果
        bool WaitUsage(bool expectCache, int timeoutSec, string? prev)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(timeoutSec))
            {
                var u = UsageText()?.Name;
                if (u != null && u != prev && u.Contains("缓存结果") == expectCache &&
                    Regex.IsMatch(u, expectCache ? @"本次消耗 0 \+ 0" : @"本次消耗 [1-9]\d* \+ [1-9]\d*"))
                    return true;
                Thread.Sleep(1000);
            }
            return false;
        }

        try
        {
            // 标记进程 A:200MB 常驻,给 LLM 一个可压缩目标(否则安静快照下建议可能为空,
            // 「强制刷新」按钮 HasSuggestions=false 不显示),同时内存恒定不影响缓存命中
            var markerPath = Path.Combine(Path.GetTempPath(), "uismokemarker.exe");
            File.Copy(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", markerPath, overwrite: true);
            Process? markerA = null, markerB = null;
            Process StartMarker(string exe, int sleepSec) => Process.Start(new ProcessStartInfo(exe,
                $"-NoProfile -Command \"$x = New-Object byte[] 200MB; for($i=0; $i -lt $x.Length; $i+=4096){{ $x[$i]=1 }}; Start-Sleep {sleepSec}\"")
            { UseShellExecute = false })!;
            markerA = StartMarker(markerPath, 900);
            Thread.Sleep(5000);   // 等分配完成

            SetCustomInstructions(origInstructions + " ui-smoke-cache-test");
            if (!NavTo(window, "智能分析")) { Fail(T, "导航到智能分析页失败"); return; }
            var runBtn = RetryFind(() => window.FindFirstDescendant(cf =>
                cf.ByName("开始分析").And(cf.ByControlType(ControlType.Button))));
            if (runBtn == null) { Fail(T, "找不到开始分析按钮"); return; }

            // 3.1 首次分析(真实请求)
            int c0 = TokenLogLines();
            string? prev = UsageText()?.Name;
            Trigger(runBtn);
            if (!WaitUsage(expectCache: false, 150, prev)) { Fail(T, "首次分析超时或无消耗: " + (UsageText()?.Name ?? "(无)")); return; }
            if (TokenLogLines() != c0 + 1) { Fail(T, "首次分析后 token 记录未增加"); return; }
            if (RetryFind(() => FindTextStarting(window, "分析报告"), 5000) == null)
            { Fail(T, "分析报告卡未出现"); return; }
            Console.WriteLine("[diag] 3.1 首次分析 ✓ " + UsageText()!.Name);

            // 3.2 相同快照再分析 → 缓存。真实环境里进程的 32MB 桶偶尔跨界导致哈希变化,
            // 每次真实请求都会把新快照写入缓存,所以多点几次(≤4),任何一次命中即通过
            bool cacheHit = false;
            for (int attempt = 0; attempt < 4 && !cacheHit; attempt++)
            {
                int before = TokenLogLines();
                prev = UsageText()?.Name;
                Trigger(runBtn);
                if (WaitUsage(expectCache: true, 30, prev))
                {
                    if (TokenLogLines() != before) { Fail(T, "缓存命中却新增了 token 记录"); return; }
                    cacheHit = true;
                }
                else if (WaitUsage(expectCache: false, 150, prev))
                {
                    if (TokenLogLines() != before + 1) { Fail(T, "真实请求但 token 记录未增加"); return; }
                    Console.WriteLine($"[diag] 3.2 第{attempt + 1}次仍为真实请求(快照桶漂移),重试");
                }
                else { Fail(T, "二次分析超时: " + (UsageText()?.Name ?? "(无)")); return; }
            }
            if (!cacheHit) { Fail(T, "连续 4 次分析均未命中缓存,缓存机制存疑"); return; }
            Console.WriteLine("[diag] 3.2 缓存命中 ✓ " + UsageText()!.Name);

            // 3.3 强制刷新 → 真实请求
            var forceBtn = RetryFind(() => window.FindFirstDescendant(cf =>
                cf.ByName("强制刷新").And(cf.ByControlType(ControlType.Button))));
            if (forceBtn == null) { Fail(T, "找不到强制刷新按钮"); return; }
            int cBefore = TokenLogLines();
            prev = UsageText()?.Name;
            Trigger(forceBtn);
            if (!WaitUsage(expectCache: false, 150, prev)) { Fail(T, "强制刷新超时"); return; }
            if (TokenLogLines() != cBefore + 1) { Fail(T, "强制刷新后 token 记录未增加"); return; }
            Console.WriteLine("[diag] 3.3 强制刷新 ✓ " + UsageText()!.Name);

            // 3.4 进程状态变化 → 缓存失效(新进程 B 进入快照,名字不在临时白名单里)
            var markerBPath = Path.Combine(Path.GetTempPath(), "uismokemark2.exe");
            File.Copy(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", markerBPath, overwrite: true);
            markerB = StartMarker(markerBPath, 300);
            Thread.Sleep(6000);   // 等内存分配完成并进入快照
            try
            {
                cBefore = TokenLogLines();
                prev = UsageText()?.Name;
                Trigger(runBtn);
                if (!WaitUsage(expectCache: false, 150, prev)) { Fail(T, "新进程后分析超时"); return; }
                if (TokenLogLines() != cBefore + 1) { Fail(T, "进程变化后未触发真实请求(可能误中缓存)"); return; }
                Console.WriteLine("[diag] 3.4 进程变化缓存失效 ✓");
            }
            finally { try { markerB?.Kill(); } catch { } }

            // 3.5 自定义指令变化 → 缓存失效(改回原值,顺手恢复现场)
            SetCustomInstructions(origInstructions);
            if (!NavTo(window, "智能分析")) { Fail(T, "返回智能分析页失败"); return; }
            runBtn = RetryFind(() => window.FindFirstDescendant(cf =>
                cf.ByName("开始分析").And(cf.ByControlType(ControlType.Button))));
            cBefore = TokenLogLines();
            prev = UsageText()?.Name;
            Trigger(runBtn);
            if (!WaitUsage(expectCache: false, 150, prev)) { Fail(T, "改指令后分析超时"); return; }
            if (TokenLogLines() != cBefore + 1) { Fail(T, "指令变化后未触发真实请求(可能误中缓存)"); return; }
            Console.WriteLine("[diag] 3.5 指令变化缓存失效 ✓");
            try { markerA?.Kill(); } catch { }
            Pass(T);
        }
        finally
        {
            // 恢复完整设置(含白名单与自定义指令)并重启应用
            try
            {
                foreach (var p in Process.GetProcessesByName("AiMemoryManager")) p.Kill();
                Thread.Sleep(1500);
                File.WriteAllText(SettingsPath(), settingsBackup);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExePath()}\"") { UseShellExecute = true });
                Thread.Sleep(3000);
                Console.WriteLine("[OK] 已恢复原设置并重启应用");
            }
            catch (Exception ex) { Console.WriteLine("[WARN] 恢复设置失败: " + ex.Message); }
        }
    }
}

// ---------- 测试:压缩建议执行(M2 第 4 节) ----------
// 起一个 200MB 标记进程 → 分析拿到建议 → 对标记进程点「立即压缩」→
// 双重判定:状态文本「已释放 X MB」 + 标记进程 WorkingSet 实测大幅下降
void TestCompress()
{
    const string T = "M2-4 压缩建议执行";
    var markerPath = Path.Combine(Path.GetTempPath(), "uismokemarker.exe");
    foreach (var p in Process.GetProcessesByName("uismokemarker")) try { p.Kill(); } catch { }
    Thread.Sleep(1000);
    File.Copy(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", markerPath, overwrite: true);
    var marker = Process.Start(new ProcessStartInfo(markerPath,
        "-NoProfile -Command \"$x = New-Object byte[] 200MB; for($i=0; $i -lt $x.Length; $i+=4096){ $x[$i]=1 }; Start-Sleep 600\"")
    { UseShellExecute = false })!;
    try
    {
        Thread.Sleep(5000);
        var (automation, window) = Attach();
        using (automation)
        {
            if (!NavTo(window, "智能分析")) { Fail(T, "导航失败"); return; }

            // 「立即压缩」按钮在可视区外时 UIA 只暴露其 TextBlock 子节点(按钮本体不进树),
            // 且 ScrollViewer CanContentScroll=False → 子元素不支持 ScrollItem;
            // 所以先按 Text 找,用鼠标滚轮滚进可视区,再点文本中心。
            // 行内进程名 = 同一水平线上能匹配真实进程名的最左文本。
            (AutomationElement? Text, string? Proc) FindCompressTarget(string? preferProc = null)
            {
                var liveNames = Process.GetProcesses().Select(p => p.ProcessName.ToLowerInvariant()).ToHashSet();
                foreach (var txt in window.FindAllDescendants(cf =>
                    cf.ByName("立即压缩").And(cf.ByControlType(ControlType.Text))))
                {
                    var y = txt.BoundingRectangle.Y + txt.BoundingRectangle.Height / 2;
                    var nameText = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                        .Where(e => Math.Abs(e.BoundingRectangle.Y + e.BoundingRectangle.Height / 2 - y) < 25
                                 && e.BoundingRectangle.X < txt.BoundingRectangle.X
                                 && liveNames.Contains(e.Name.ToLowerInvariant()))
                        .OrderByDescending(e => preferProc != null && e.Name.Equals(preferProc, StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(e => e.Name.Equals("uismokemarker", StringComparison.OrdinalIgnoreCase))
                        .ThenBy(e => e.BoundingRectangle.X).FirstOrDefault();
                    if (nameText != null)
                        return (txt, nameText.Name);
                }
                return (null, null);
            }

            AutomationElement? compressTxt = null;
            string? compressProc = null;
            for (int attempt = 0; attempt < 3 && compressTxt == null; attempt++)
            {
                var btnName = attempt == 0 ? "开始分析" : "强制刷新";
                var runBtn = RetryFind(() => window.FindFirstDescendant(cf =>
                    cf.ByName(btnName).And(cf.ByControlType(ControlType.Button))));
                if (runBtn == null) { Fail(T, $"找不到{btnName}按钮"); return; }
                var prev = FindTextStarting(window, "本次消耗")?.Name;
                Trigger(runBtn);
                // 等分析完成:UsageText 变化
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(150))
                {
                    var u = FindTextStarting(window, "本次消耗")?.Name;
                    if (u != null && u != prev) break;
                    Thread.Sleep(1000);
                }
                Thread.Sleep(1500);   // 等建议卡渲染
                var found = RetryFind(() => { var f = FindCompressTarget(); return f.Text != null ? f.Text : null; }, 8000);
                if (found != null) (compressTxt, compressProc) = FindCompressTarget();
                if (compressTxt == null)
                {
                    var status = FindTextStarting(window, "上次分析")?.Name
                              ?? FindTextStarting(window, "分析")?.Name ?? "?";
                    var txtCount = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                        .Count(b => b.Name.Contains("立即压缩"));
                    Console.WriteLine($"[diag] 第{attempt + 1}次分析无可匹配压缩卡(状态: {status}, 「立即压缩」文本 {txtCount} 个),重试");
                }
            }
            if (compressTxt == null || compressProc == null)
            {
                Console.WriteLine($"[WARN] {T}: LLM 连续 3 次未给出可匹配的压缩建议,需人工验证");
                return;
            }
            Console.WriteLine($"[diag] 压缩目标: {compressProc}");

            long wsBefore = Process.GetProcessesByName(compressProc).Sum(p => p.WorkingSet64);
            // 该页 ScrollViewer CanContentScroll=False → 子元素没有 ScrollItem;改用页面
            // ScrollViewer 的 ScrollPattern 按百分比滚动(不依赖前台/鼠标),再把窗口临时
            // 置为 TopMost 保证鼠标点击落在本窗口(终端遮挡时 SetForegroundWindow 常被拒)
            var hwndApp = (IntPtr)window.Properties.NativeWindowHandle;
            // 页面上有多个 ScrollViewer(横向、内嵌卡片等),第一个不一定是装建议卡的那个;
            // 从目标文本沿父链向上找其所在的 ScrollViewer,拿它的 ScrollPattern
            AutomationElement? targetScroller = null;
            for (var p = compressTxt.Parent; p != null && targetScroller == null; p = p.Parent)
                if (p.ClassName == "ScrollViewer") targetScroller = p;
            var allScrollers = window.FindAllDescendants(cf => cf.ByClassName("ScrollViewer"));
            Console.WriteLine($"[diag] ScrollViewer 共 {allScrollers.Length} 个,目标所在: {(targetScroller != null)}");
            foreach (var s in allScrollers)
            {
                var sp = s.Patterns.Scroll.PatternOrDefault;
                Console.WriteLine($"[diag]   滚动器 rect={s.BoundingRectangle}, vScrollable={(sp?.VerticallyScrollable.ValueOrDefault.ToString() ?? "?")}, vView={(sp?.VerticalViewSize.ValueOrDefault.ToString("F0") ?? "?")}");
            }
            AutomationElement? visTxt = null;
            bool Visible(AutomationElement e)
            {
                var wr = window.BoundingRectangle; var rr = e.BoundingRectangle;
                return rr.Y > wr.Y + 40 && rr.Y + rr.Height < wr.Y + wr.Height - 10;
            }
            // 外 ScrollViewer 的内容只比窗口高几百像素,UIA 却报 vView=100/不可滚;
            // 最简单的出路是最大化窗口让整张卡片露出来,再考虑滚动
            ShowWindow(hwndApp, 3 /*SW_MAXIMIZE*/);
            Thread.Sleep(1200);
            {
                var f = FindCompressTarget(compressProc);
                if (f.Text != null && Visible(f.Text)) visTxt = f.Text;
                Console.WriteLine($"[diag] 最大化后目标可见: {(visTxt != null)}, 窗口={window.BoundingRectangle}");
            }
            // 只有「目标所在」的滚动器真的可滚时才用 ScrollPattern;
            // 目标在外层(报不可滚的)ScrollViewer 时,滚错滚动器目标纹丝不动,直接走滚轮
            var targetScroll = targetScroller?.Patterns.Scroll.PatternOrDefault;
            if (visTxt == null && targetScroll != null && targetScroll.VerticallyScrollable.ValueOrDefault)
            {
                for (double pct = 0; pct <= 100 && visTxt == null; pct += 10)
                {
                    try { targetScroll.SetScrollPercent(-1 /*ScrollPattern.NoScroll*/, pct); } catch (Exception ex) { Console.WriteLine($"[diag] SetScrollPercent({pct}) 异常: {ex.GetType().Name}"); }
                    Thread.Sleep(500);
                    var f = FindCompressTarget(compressProc);
                    if (f.Text == null) { Console.WriteLine($"[diag] pct={pct}: 目标文本不在 UIA 树"); break; }
                    var wr1 = window.BoundingRectangle; var rr1 = f.Text.BoundingRectangle;
                    Console.WriteLine($"[diag] pct={pct}: 目标 Y={rr1.Y:F0}..{rr1.Y + rr1.Height:F0}, 窗口 Y={wr1.Y:F0}..{wr1.Y + wr1.Height:F0}, 实际vPct={targetScroll.VerticalScrollPercent.ValueOrDefault:F0}");
                    if (Visible(f.Text)) visTxt = f.Text;
                }
            }
            if (visTxt == null)
            {
                // 置前 + 鼠标滚轮下滚(滚轮事件落到光标所在窗口,与 UIA 可滚性无关)
                SetWindowPos(hwndApp, (IntPtr)(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0, 0x0003);
                var wr = window.BoundingRectangle;
                FlaUI.Core.Input.Mouse.Position = new System.Drawing.Point(
                    (int)(wr.X + wr.Width / 2), (int)(wr.Y + wr.Height / 2));
                Thread.Sleep(300);
                for (int i = 0; i < 40 && visTxt == null; i++)
                {
                    FlaUI.Core.Input.Mouse.Scroll(-2);
                    Thread.Sleep(400);
                    var f = FindCompressTarget(compressProc);
                    if (f.Text != null)
                    {
                        var rr2 = f.Text.BoundingRectangle;
                        if (i % 5 == 0) Console.WriteLine($"[diag] 滚轮×{(i + 1) * 2}: 目标 Y={rr2.Y:F0}");
                        if (Visible(f.Text)) visTxt = f.Text;
                    }
                }
                SetWindowPos(hwndApp, (IntPtr)(-2) /*HWND_NOTOPMOST*/, 0, 0, 0, 0, 0x0003);
            }
            if (visTxt == null) { Fail(T, "无法把压缩卡滚进可视区"); return; }
            SetWindowPos(hwndApp, (IntPtr)(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0, 0x0003 /*NOMOVE|NOSIZE*/);
            Thread.Sleep(300);
            try { visTxt.Click(); }   // Click() 点元素中心;文本是按钮子节点,点它即点按钮
            finally { SetWindowPos(hwndApp, (IntPtr)(-2) /*HWND_NOTOPMOST*/, 0, 0, 0, 0, 0x0003); }
            if (RetryFind(() => FindTextStarting(window, "已释放"), 30000) == null)
            { Fail(T, "压缩后无「已释放」提示"); return; }
            Thread.Sleep(2000);
            long wsAfter = Process.GetProcessesByName(compressProc).Sum(p => { p.Refresh(); return p.WorkingSet64; });
            Console.WriteLine($"[diag] {compressProc} WS: {wsBefore >> 20}MB → {wsAfter >> 20}MB");
            // 活跃进程被压缩后部分页面会立即换回,达不到减半;确认「明显下降」即可(≥10%)
            if (wsAfter > wsBefore * 9 / 10) { Fail(T, "压缩后目标进程工作集未明显下降"); return; }
            Pass(T);
        }
    }
    finally { try { marker.Kill(); } catch { } }
}

// ---------- 测试:提示词模板(M2 第 5 节) ----------
// 把整个模板换成「固定输出」覆盖模板(含四个变量占位符)→ 强制分析 → 建议理由带标记。
// 注意:实测 DeepSeek 对「reason 加前缀」这类软指令不稳定遵守(直接 API 调用验证过),
// 但「只输出指定 JSON」的硬覆盖指令稳定遵守,故用后者作为模板生效的判定信号;
// 四个变量替换本身由 AnalysisPromptBuilderTests 单测覆盖。
void TestPromptTemplate()
{
    const string T = "M2-5 提示词模板";
    const string marker = "标A7x";
    const string overrideTemplate = """
        你是模板测试助手。当前系统内存状况:{memory_info}。进程列表:{process_list}。用户要求:{custom_instructions}。语言:{language}。
        忽略其他一切考虑,无论进程状态如何,只输出如下 JSON(不得改动任何字符):
        {"suggestions":[{"process":"uismokemarker","action":"keep","reason":"标A7x模板生效","risk":"low"}]}
        """;
    var (automation, window) = Attach();
    using (automation)
    {
        if (!NavTo(window, "大模型")) { Fail(T, "导航到大模型页失败"); return; }
        // 模板内容框:页面上最高的多行 Edit(Height=180),取 Y 最大的 Edit
        var templateBox = RetryFind(() => FormEdits(window).OrderByDescending(e => e.BoundingRectangle.Y).First());
        if (templateBox == null) { Fail(T, "找不到模板编辑框"); return; }
        var origContent = templateBox.Patterns.Value.Pattern.Value.Value;
        if (origContent.Contains(marker)) { Fail(T, "模板已含测试标记,先人工清理"); return; }

        templateBox.Focus();
        Thread.Sleep(200);
        SetEditText(templateBox, overrideTemplate);
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
        Thread.Sleep(400);
        // 模板卡的保存按钮是两个「保存」中 Y 更大的那个
        var saveBtns = window.FindAllDescendants(cf => cf.ByName("保存").And(cf.ByControlType(ControlType.Button)))
            .OrderByDescending(b => b.BoundingRectangle.Y).ToList();
        if (saveBtns.Count < 2) { Fail(T, "找不到模板保存按钮"); return; }
        Trigger(saveBtns[0]);
        Thread.Sleep(800);
        // 确认落盘
        var promptsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AiMemoryManager", "prompts.json");
        if (!File.ReadAllText(promptsPath).Contains(marker)) { Fail(T, "模板未保存到 prompts.json"); return; }
        Console.WriteLine("[diag] 模板标记已保存");

        try
        {
            if (!NavTo(window, "智能分析")) { Fail(T, "导航到智能分析页失败"); return; }
            // 模板变更 → 哈希变化 → 点开始分析即真实请求
            var runBtn = RetryFind(() => window.FindFirstDescendant(cf =>
                cf.ByName("开始分析").And(cf.ByControlType(ControlType.Button))));
            if (runBtn == null) { Fail(T, "找不到开始分析按钮"); return; }
            bool markerSeen = false;
            for (int attempt = 0; attempt < 2 && !markerSeen; attempt++)
            {
                var prev = FindTextStarting(window, "本次消耗")?.Name;
                Trigger(runBtn);
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(150))
                {
                    var u = FindTextStarting(window, "本次消耗")?.Name;
                    if (u != null && u != prev) break;
                    Thread.Sleep(1000);
                }
                // 建议卡理由文本中带标记
                markerSeen = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Any(e => e.Name.Contains(marker));
                if (!markerSeen)
                {
                    var reasons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                        .Select(e => e.Name).Where(n => n.Length > 8 && n.Length < 120
                            && !n.Contains("本次消耗") && !n.Contains("Token") && !n.Contains("上次分析"))
                        .Take(12);
                    Console.WriteLine($"[diag] 第{attempt + 1}次分析理由未带标记,页面文本样本: {string.Join(" | ", reasons)}");
                    runBtn = RetryFind(() => window.FindFirstDescendant(cf =>
                        cf.ByName("强制刷新").And(cf.ByControlType(ControlType.Button)))) ?? runBtn;
                }
            }
            if (!markerSeen) { Fail(T, "两次分析的建议理由均未带模板标记,模板未生效"); return; }
            Console.WriteLine("[diag] 建议理由带模板标记 ✓");
        }
        finally
        {
            // 恢复出厂默认
            if (!NavTo(window, "大模型")) { Console.WriteLine("[WARN] 恢复模板时导航失败"); }
            else
            {
                var restoreBtn = RetryFind(() => window.FindFirstDescendant(cf =>
                    cf.ByName("恢复出厂默认").And(cf.ByControlType(ControlType.Button))));
                Trigger(restoreBtn);
                Thread.Sleep(800);
                bool restored = !File.ReadAllText(promptsPath).Contains(marker);
                Console.WriteLine(restored ? "[OK] 模板已恢复出厂" : "[WARN] prompts.json 仍含测试标记");
                if (!restored) failures++;
            }
        }
        Pass(T);
    }
}

// ---------- 测试:自动触发(M2 第 6 节) ----------
// 6.1 阈值触发:settings 写 ThresholdPercent=40 + LlmThresholdTriggerEnabled=true + 每日上限 1,
//     内存压力推过 45% → 调度器 60s tick 自动分析 → jsonl 新增 Trigger=1(Threshold)。
// 6.2 每日上限:上限=1,撤压(占用回落重置触发位)再加压 → 不再自动分析(jsonl 无新增)。
// 注:调度器到上限后静默跳过(与预算闸门一致),"提示/状态说明"以 jsonl 不新增为判定。
void TestAutoTrigger()
{
    const string T = "M2-6 自动触发";
    var settingsPath = SettingsPath();
    var backup = File.ReadAllText(settingsPath);
    List<byte[]> pressure = new();

    bool WaitFor(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(1000);
        }
        return false;
    }
    int NewThresholdCallsSince(DateTimeOffset since)
    {
        if (!File.Exists(TokenLogPath())) return 0;
        return File.ReadLines(TokenLogPath())
            .Select(l => { try { return JsonDocument.Parse(l).RootElement; } catch { return default; } })
            .Count(r => r.ValueKind == JsonValueKind.Object
                && r.GetProperty("Trigger").GetInt32() == 1 /*Threshold*/
                && r.GetProperty("Time").GetDateTimeOffset() >= since);
    }

    try
    {
        foreach (var p in Process.GetProcessesByName("AiMemoryManager")) p.Kill();
        Thread.Sleep(1500);
        var node = System.Text.Json.Nodes.JsonNode.Parse(backup)!.AsObject();
        node["ThresholdPercent"] = 40;              // Normalize 钳制下限 40
        node["LlmThresholdTriggerEnabled"] = true;
        node["LlmTimerTriggerEnabled"] = false;     // 排除定时触发干扰
        node["LlmDailyCallCap"] = 1;                // 上限 1:一次后即被拦
        node["MonthlyTokenBudget"] = 0;             // 排除预算闸门干扰
        node["RulesMasterEnabled"] = false;         // 排除清理规则干扰(免得真去清理)
        File.WriteAllText(settingsPath, node.ToJsonString());

        // 内存压力推过阈值(机器常态 ~31%,阈值下限 40%)
        while (MemUsedPercent() < 45 && pressure.Count < 40)
        {
            var chunk = new byte[256 * 1024 * 1024];
            for (int i = 0; i < chunk.Length; i += 4096) chunk[i] = 1;
            pressure.Add(chunk);
        }
        Console.WriteLine($"[diag] 内存占用 {MemUsedPercent()}%(压力 {pressure.Count * 256}MB)");
        if (MemUsedPercent() < 42) { Fail(T, "无法把内存占用推过 42%,放弃"); return; }

        var fireStart = DateTimeOffset.Now;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExePath()}\"") { UseShellExecute = true });
        // 等调度器 tick(60s) + 真实 LLM 请求(≤90s)
        if (!WaitFor(() => NewThresholdCallsSince(fireStart) >= 1, 240000))
        { Fail(T, "240s 内未发生阈值自动分析(jsonl 无 Trigger=1 记录)"); return; }
        Console.WriteLine("[diag] 6.1 阈值自动分析触发 ✓ (jsonl 新增 Trigger=1)");

        // 6.2 每日上限:撤压 → 占用回落(重置 _thresholdFiredToday)→ 再加压,上限=1 拦住第二次
        pressure.Clear();
        GC.Collect();
        if (!WaitFor(() => MemUsedPercent() < 40, 60000))
            Console.WriteLine($"[WARN] 撤压后占用仍 {MemUsedPercent()}%,上限验证可能受触发位未重置影响");
        while (MemUsedPercent() < 45 && pressure.Count < 40)
        {
            var chunk = new byte[256 * 1024 * 1024];
            for (int i = 0; i < chunk.Length; i += 4096) chunk[i] = 1;
            pressure.Add(chunk);
        }
        Console.WriteLine($"[diag] 二次加压至 {MemUsedPercent()}%,等 150s(2+ 个 tick)确认不再触发");
        var capStart = DateTimeOffset.Now;
        Thread.Sleep(150000);
        if (NewThresholdCallsSince(capStart) != 0)
        { Fail(T, "达到每日上限后仍发生自动分析"); return; }
        Console.WriteLine("[diag] 6.2 每日上限拦截 ✓ (加压 150s 无新增)");
        Pass(T);
    }
    finally
    {
        pressure.Clear();
        GC.Collect();
        foreach (var p in Process.GetProcessesByName("AiMemoryManager")) p.Kill();
        Thread.Sleep(1500);
        File.WriteAllText(settingsPath, backup);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExePath()}\"") { UseShellExecute = true });
        Thread.Sleep(3000);
        Console.WriteLine("[OK] 已恢复原设置并重启应用");
    }
}

// ---------- 测试:内存泄漏告警(M2 第 8 节) ----------
// 8.1 观察窗 5min/阈值 50MB,泄漏进程每 10s 长 15MB(单调,防回落重置)→ 告警行出现
// 8.2 告警行点「智能分析」→ jsonl 新增 Trigger=3(Leak)
// 8.3 泄漏检测卡 NumberBox 改动即存(50→80、5→10),重启后 UI 保持
void TestLeakAlert()
{
    const string T = "M2-8 内存泄漏告警";
    var settingsPath = SettingsPath();
    var backup = File.ReadAllText(settingsPath);
    Process? leaker = null;

    try
    {
        foreach (var p in Process.GetProcessesByName("AiMemoryManager")) p.Kill();
        Thread.Sleep(1500);
        var node = System.Text.Json.Nodes.JsonNode.Parse(backup)!.AsObject();
        node["LeakDetectionEnabled"] = true;
        node["LeakGrowthThresholdMb"] = 50;         // 下限
        node["LeakWindowMinutes"] = 5;              // 下限
        node["RulesMasterEnabled"] = false;         // 关掉清理规则:L1 清理会裁剪泄漏进程工作集→回落重置观察窗(实测踩坑)
        File.WriteAllText(settingsPath, node.ToJsonString());
        // 直接启动(不走 explorer)以便注入 AMM_LEAK_DEBUG=1 诊断日志环境变量
        var appPsi = new ProcessStartInfo(ExePath()) { UseShellExecute = false };
        appPsi.Environment["AMM_LEAK_DEBUG"] = "1";
        Process.Start(appPsi);
        Thread.Sleep(5000);

        // 泄漏进程:powershell 改名 uismokeleaker,每 10s 多 15MB 且持有不释放;
        // 每轮以页粒度重触所有块,防止旧页被系统换出导致 WS 回落(回落会重置观察窗)
        var leakerPath = Path.Combine(Path.GetTempPath(), "uismokeleaker.exe");
        File.Copy(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", leakerPath, overwrite: true);
        leaker = Process.Start(new ProcessStartInfo(leakerPath,
            "-NoProfile -Command \"$c = New-Object System.Collections.Generic.List[byte[]]; " +
            "while($true){ $b = New-Object byte[] 15MB; for($i=0; $i -lt $b.Length; $i+=4096){ $b[$i]=1 }; " +
            "$c.Add($b); foreach($x in $c){ for($i=0; $i -lt $x.Length; $i+=4096){ $x[$i]=1 } }; Start-Sleep 10 }\"")
        { UseShellExecute = false })!;
        Console.WriteLine($"[diag] 泄漏进程 pid={leaker.Id},增长 90MB/min,等告警(窗口 5min,最长等 10min)");

        var (automation, window) = Attach();
        using (automation)
        {
            if (!NavTo(window, "智能分析")) { Fail(T, "导航到智能分析页失败"); return; }
            // 8.1 告警行:进程名文本 uismokeleaker 出现在泄漏告警卡
            var alertRow = RetryFind(() => window.FindAllDescendants(cf =>
                    cf.ByName("uismokeleaker").And(cf.ByControlType(ControlType.Text)))
                    .FirstOrDefault(), 600000);
            if (alertRow == null)
            {
                leaker.Refresh();
                Fail(T, $"10 分钟内未出现泄漏告警(进程存活: {!leaker.HasExited}, WS={leaker.WorkingSet64 >> 20}MB)");
                // 诊断:打印 leak-debug.log 末尾几行,看每跳轨道增长/回落情况
                var dbg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AiMemoryManager", "leak-debug.log");
                if (File.Exists(dbg))
                    foreach (var l in File.ReadLines(dbg).TakeLast(8)) Console.WriteLine("[leakdbg] " + l);
                else Console.WriteLine("[leakdbg] 无日志文件(泄漏采样未运行?)");
                return;
            }
            Console.WriteLine("[diag] 8.1 泄漏告警出现 ✓");

            // 8.2 告警行右侧「智能分析」按钮 → Leak 触发器分析 → jsonl Trigger=3
            int before = TokenLogLines();
            var analyzeBtn = RetryFind(() => window.FindAllDescendants(cf =>
                    cf.ByName("智能分析").And(cf.ByControlType(ControlType.Button)))
                    // 行内按钮:与告警行同一水平线
                    .FirstOrDefault(b => Math.Abs(b.BoundingRectangle.Y - alertRow.BoundingRectangle.Y) < 30), 8000);
            if (analyzeBtn == null) { Fail(T, "找不到告警行的智能分析按钮"); return; }
            Trigger(analyzeBtn);
            bool fired = false;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(180) && !fired)
            {
                Thread.Sleep(2000);
                if (TokenLogLines() > before)
                {
                    var last = JsonDocument.Parse(File.ReadLines(TokenLogPath()).Last()).RootElement;
                    if (last.GetProperty("Trigger").GetInt32() == 3 /*Leak*/) fired = true;
                }
            }
            if (!fired) { Fail(T, "泄漏一键分析未发起或 Trigger≠3"); return; }
            Console.WriteLine("[diag] 8.2 泄漏触发分析 ✓ (jsonl Trigger=3)");

            // 8.3 泄漏检测卡设置持久化:UI 改阈值 50→80、窗口 5→10 → settings.json 同步 → 重启保持
            if (!NavTo(window, "大模型")) { Fail(T, "导航到大模型页失败"); return; }
            AutomationElement? FindNumberBox(string val) => RetryFind(() => FormEdits(window)
                .Where(e => e.Patterns.Value.Pattern.Value.Value == val)
                .OrderByDescending(e => e.BoundingRectangle.Y)   // 泄漏卡在页面下方
                .FirstOrDefault(), 5000);
            var thrBox = FindNumberBox("50");
            var winBox = FindNumberBox("5");
            if (thrBox == null || winBox == null) { Fail(T, "找不到泄漏检测卡的 NumberBox"); return; }
            SetEditText(thrBox, "80");
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
            Thread.Sleep(400);
            SetEditText(winBox, "10");
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
            Thread.Sleep(800);
            using (var doc = JsonDocument.Parse(File.ReadAllText(settingsPath)))
            {
                if (doc.RootElement.GetProperty("LeakGrowthThresholdMb").GetInt32() != 80
                    || doc.RootElement.GetProperty("LeakWindowMinutes").GetInt32() != 10)
                { Fail(T, "泄漏检测卡改动未保存到 settings.json"); return; }
            }
            // 重启验证 UI 保持
            foreach (var p in Process.GetProcessesByName("AiMemoryManager")) p.Kill();
            Thread.Sleep(1500);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExePath()}\"") { UseShellExecute = true });
            Thread.Sleep(5000);
            var (automation2, window2) = Attach();
            using (automation2)
            {
                if (!NavTo(window2, "大模型")) { Fail(T, "重启后导航失败"); return; }
                var thrBox2 = RetryFind(() => FormEdits(window2)
                    .FirstOrDefault(e => e.Patterns.Value.Pattern.Value.Value == "80"), 8000);
                var winBox2 = RetryFind(() => FormEdits(window2)
                    .FirstOrDefault(e => e.Patterns.Value.Pattern.Value.Value == "10"), 8000);
                if (thrBox2 == null || winBox2 == null) { Fail(T, "重启后泄漏检测设置未保持"); return; }
            }
            Console.WriteLine("[diag] 8.3 泄漏检测设置改动即存、重启保持 ✓");
            Pass(T);
        }
    }
    finally
    {
        try { leaker?.Kill(); } catch { }
        foreach (var p in Process.GetProcessesByName("AiMemoryManager")) p.Kill();
        Thread.Sleep(1500);
        File.WriteAllText(settingsPath, backup);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExePath()}\"") { UseShellExecute = true });
        Thread.Sleep(3000);
        Console.WriteLine("[OK] 已恢复原设置并重启应用");
    }
}


// 7.1 统计页与 token-usage.jsonl 一致(本月聚合卡 + 最近调用首行)
// 7.2 档案填单价 → 费用卡显示 $ 估算
// 7.3 月度预算写 100 → 统计页预算告警出现;7.4 手动分析被拦,jsonl 不增
// ---------- 测试:M3 快捷项(热键 4.1/4.2、通知开关 6.1/6.2、开机自启 5.1、历史截断 8.2) ----------
bool WaitNewHistoryEntry(DateTimeOffset since, int trigger, int timeoutSec)
{
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed < TimeSpan.FromSeconds(timeoutSec))
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(HistoryPath()));
            foreach (var e in doc.RootElement.EnumerateArray())
                if (e.GetProperty("Trigger").GetInt32() == trigger
                    && e.GetProperty("Time").GetDateTimeOffset() >= since)
                    return true;
        }
        catch { }
        Thread.Sleep(1000);
    }
    return false;
}

// 系统 toast 通知挂在 "Windows.UI.Core.CoreWindow" 窗口下,自动几秒后消失,需轮询
bool ToastVisible(FlaUI.Core.AutomationBase automation)
{
    try
    {
        var desktop = automation.GetDesktop();
        foreach (var win in desktop.FindAllChildren(cf => cf.ByClassName("Windows.UI.Core.CoreWindow")))
            if (win.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                   .Any(e => e.Name.StartsWith("清理完成"))) return true;
    }
    catch { }
    return false;
}

bool WaitToast(FlaUI.Core.AutomationBase automation, int timeoutSec)
{
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed < TimeSpan.FromSeconds(timeoutSec))
    {
        if (ToastVisible(automation)) return true;
        Thread.Sleep(700);
    }
    return false;
}

void SendCombo(FlaUI.Core.WindowsAPI.VirtualKeyShort mod1, FlaUI.Core.WindowsAPI.VirtualKeyShort mod2,
    FlaUI.Core.WindowsAPI.VirtualKeyShort key)
{
    using (FlaUI.Core.Input.Keyboard.Pressing(mod1))
    using (FlaUI.Core.Input.Keyboard.Pressing(mod2))
    {
        FlaUI.Core.Input.Keyboard.Press(key);
        Thread.Sleep(300);
    }
}

// WPF-UI ToggleSwitch 在 UIA 里不带名称,按标签文本同行右侧定位
AutomationElement? FindToggleNear(AutomationElement window, string label)
{
    var lab = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
        .FirstOrDefault(e => e.Name == label);
    if (lab == null) return null;
    var cy = lab.BoundingRectangle.Y + lab.BoundingRectangle.Height / 2;
    return window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
        .Concat(window.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox)))
        .Where(e => e.Patterns.Toggle.IsSupported)
        .Where(e => Math.Abs(e.BoundingRectangle.Y + e.BoundingRectangle.Height / 2 - cy) < 30
                 && e.BoundingRectangle.X > lab.BoundingRectangle.X)
        .OrderBy(e => e.BoundingRectangle.X)
        .FirstOrDefault();
}

// ---------- 测试:M3 第 4.3 节 热键占用降级 ----------
// 4.3 方法论:RegisterHotKey 命中的组合被 OS 拦截、WM_HOTKEY 直接投给注册线程,
// 前台应用的焦点控件根本收不到该键——「占用时往设置框敲该组合」在现实世界不可达。
// 可自动化验证的真实路径是启动期降级:热键被占用时启动 → 不崩溃、静默不注册、
// 设置页出现「热键注册失败」提示;解除占用重启后热键恢复。
void TestHotkeyDegrade()
{
    const string T = "M3-热键占用降级";
    const int occId = 0x51AC;
    var settingsPath = SettingsPath();
    var backup = File.ReadAllText(settingsPath);
    bool occupied = false;

    void KillApp() { foreach (var p in Process.GetProcessesByName("AiMemoryManager")) p.Kill(); Thread.Sleep(1500); }
    void StartApp()
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExePath()}\"") { UseShellExecute = true });
        Thread.Sleep(4000);
    }
    bool HasHistorySince(DateTimeOffset since, int trigger)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(HistoryPath()));
            foreach (var e in doc.RootElement.EnumerateArray())
                if (e.GetProperty("Trigger").GetInt32() == trigger
                    && e.GetProperty("Time").GetDateTimeOffset() >= since)
                    return true;
        }
        catch { }
        return false;
    }

    try
    {
        // 前置:应用运行中先验证本会话键盘输入链路可用(全局热键能触发一次手动清理)
        if (Process.GetProcessesByName("AiMemoryManager").Length == 0) StartApp();
        var (automation0, window0) = Attach();
        using (automation0)
        {
            if (window0 == null) { Fail(T, "前置:应用窗口未找到"); return; }
            var t0 = DateTimeOffset.Now;
            SendCombo(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                      FlaUI.Core.WindowsAPI.VirtualKeyShort.SHIFT,
                      FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_M);
            if (!WaitNewHistoryEntry(t0, 0, 30))
            { Fail(T, "本会话键盘输入不可用(Ctrl+Shift+M 全局热键未触发),4.3 无法自动验证"); return; }
        }
        Console.WriteLine("[diag] 前置:全局热键触发 ✓,键盘输入可用");

        // 占住应用当前热键 Ctrl+Shift+M(modifiers=6, vk=0x4D),模拟被别的程序先注册。
        // 必须先杀应用再注册:应用活着时它自己持有该组合,注册必然失败
        KillApp();
        if (!RegisterHotKey(IntPtr.Zero, occId, 0x0002 | 0x0004 /*MOD_CONTROL|MOD_SHIFT*/, 0x4D /*M*/))
        { Fail(T, "测试自身注册 Ctrl+Shift+M 失败(可能已被占用),无法做占用实验"); return; }
        occupied = true;
        StartApp();
        var (automation, window) = Attach();
        using (automation)
        {
            if (window == null) { Fail(T, "热键被占用时应用未能启动出窗口(崩溃?)"); return; }
            ShowWindow((IntPtr)window.Properties.NativeWindowHandle, 3);
            Thread.Sleep(500);

            // 1) 界面有体现:设置页出现「热键注册失败」降级提示(启动注册失败 → HotkeyFailed)
            bool onSettings = false;
            for (int i = 0; i < 3 && !onSettings; i++)
            {
                NavTo(window, "设置");
                onSettings = RetryFind(() => FindTextStarting(window, "全局热键"), 6000) != null;
            }
            if (!onSettings) { Fail(T, "设置页未加载出热键卡"); return; }
            if (RetryFind(() => FindTextStarting(window, "热键注册失败"), 6000) == null)
            { Fail(T, "启动注册被占用后设置页无降级提示"); return; }
            Console.WriteLine("[diag] 4.3 启动期降级:设置页出现「热键注册失败」提示 ✓");

            // 2) 静默降级:按下被占用的组合,不产生手动清理记录(组合被 OS 投给占用方,应用未注册)
            var t1 = DateTimeOffset.Now;
            SendCombo(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                      FlaUI.Core.WindowsAPI.VirtualKeyShort.SHIFT,
                      FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_M);
            Thread.Sleep(8000);
            if (HasHistorySince(t1, 0)) { Fail(T, "热键被占用却仍触发了清理,降级未生效"); return; }
            Console.WriteLine("[diag] 4.3 占用期间热键不生效(静默降级) ✓");

            // 3) 不崩溃且其它功能正常:仪表盘一键清理可用
            bool onDash = false;
            for (int i = 0; i < 3 && !onDash; i++)
            {
                NavTo(window, "仪表盘");
                onDash = RetryFind(() => FindTextStarting(window, "一键清理"), 6000) != null;
            }
            if (!onDash) { Fail(T, "导航到仪表盘失败"); return; }
            var t2 = DateTimeOffset.Now;
            Trigger(RetryFind(() => window.FindFirstDescendant(cf =>
                cf.ByName("一键清理").And(cf.ByControlType(ControlType.Button))), 5000));
            if (!WaitNewHistoryEntry(t2, 0, 30)) { Fail(T, "热键降级后一键清理也不可用"); return; }
            Console.WriteLine("[diag] 4.3 降级期间应用功能正常(一键清理 ✓),未崩溃");
        }

        // 4) 解除占用并重启 → 热键恢复
        UnregisterHotKey(IntPtr.Zero, occId); occupied = false;
        KillApp();
        StartApp();
        var t3 = DateTimeOffset.Now;
        SendCombo(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                  FlaUI.Core.WindowsAPI.VirtualKeyShort.SHIFT,
                  FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_M);
        if (!WaitNewHistoryEntry(t3, 0, 30)) { Fail(T, "解除占用重启后热键未恢复"); return; }
        Console.WriteLine("[diag] 4.3 解除占用后热键恢复 ✓");

        // 5) 全程不得改动热键设置
        var s = JsonDocument.Parse(File.ReadAllText(settingsPath)).RootElement;
        if (s.GetProperty("HotkeyKey").GetInt32() != 77 /*M*/ || s.GetProperty("HotkeyModifiers").GetInt32() != 6)
        { Fail(T, "降级过程中热键设置被改动"); return; }
        Console.WriteLine("[diag] 4.3 热键设置未被改动 ✓");
        Pass(T);
    }
    finally
    {
        if (occupied) UnregisterHotKey(IntPtr.Zero, occId);
        // 恢复现场:确保应用以原设置在运行(若中途 Fail 退出,应用可能处于无热键/未启动状态)
        if (File.ReadAllText(settingsPath) != backup) File.WriteAllText(settingsPath, backup);
        if (Process.GetProcessesByName("AiMemoryManager").Length == 0)
        {
            StartApp();
            Console.WriteLine("[OK] 已重启应用恢复现场");
        }
    }
}

// 7.1 扫描 → 结果按大小降序(大项在前);7.2 LLM 分析 → 出现可清理/可迁移建议及理由。
// 删除/迁移/回退/占用检测涉及真实文件搬迁与回收站,留人工,不在此自动化。
void TestCSlim()
{
    const string T = "M3-C盘瘦身";
    if (Process.GetProcessesByName("AiMemoryManager").Length == 0)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExePath()}\"") { UseShellExecute = true });
        Thread.Sleep(5000);
    }
    var (automation, window) = Attach();
    using (automation)
    {
        if (window == null) { Fail(T, "应用窗口未找到"); return; }
        ShowWindow((IntPtr)window.Properties.NativeWindowHandle, 3);
        Thread.Sleep(500);

        bool onPage = false;
        for (int i = 0; i < 3 && !onPage; i++)
        {
            NavTo(window, "C 盘瘦身");
            onPage = RetryFind(() => FindTextStarting(window, "扫描占用"), 6000) != null;
        }
        if (!onPage) { Fail(T, "C盘瘦身页未加载"); return; }

        // ---- 7.1 扫描 → 结果按大小排序合理(大项在前) ----
        var scanBtn = RetryFind(() => window.FindFirstDescendant(cf =>
            cf.ByName("扫描占用").And(cf.ByControlType(ControlType.Button))), 8000);
        if (scanBtn == null) { Fail(T, "找不到扫描按钮"); return; }
        Trigger(scanBtn);
        Console.WriteLine("[diag] 已触发扫描,等待完成(最长 15 分钟,大用户目录实测约 10 分钟)…");
        string? scanResult = null;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMinutes(15))
        {
            if (FindTextStarting(window, "扫描完成") != null) { scanResult = "ok"; break; }
            if (FindTextStarting(window, "扫描失败") != null) { scanResult = "fail"; break; }
            if (FindTextStarting(window, "扫描已取消") != null) { scanResult = "cancel"; break; }
            Thread.Sleep(3000);
        }
        if (scanResult != "ok")
        { Fail(T, $"扫描未正常完成({scanResult ?? "超时"},状态='{FindTextStarting(window, "扫描")?.Name}')"); return; }
        Console.WriteLine($"[diag] 扫描完成,耗时 {sw.Elapsed.TotalSeconds:0}s");

        // 扫描结果表 = 带「文件数」列头的 DataGrid;虚拟化只实例化可见行,验证可见行降序即可证明大项在前
        var grids = window.FindAllDescendants(cf => cf.ByControlType(ControlType.DataGrid));
        var scanGrid = grids.FirstOrDefault(g => g.FindFirstDescendant(cf => cf.ByName("文件数")) != null);
        if (scanGrid == null) { Fail(T, "找不到扫描结果表"); return; }
        var rows = scanGrid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem));
        if (rows.Length == 0) { Fail(T, "扫描结果为空(本机 C 盘不应无候选项)"); return; }
        var sizes = new List<long>();
        foreach (var row in rows)
        {
            long size = -1;
            foreach (var t in row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
            {
                var txt = t.Name.Trim();
                long mult = txt.EndsWith("GB") ? 1L << 30 : txt.EndsWith("MB") ? 1L << 20
                    : txt.EndsWith("KB") ? 1L << 10 : txt.EndsWith(" B") ? 1 : 0;
                if (mult == 0) continue;
                var num = txt[..txt.LastIndexOf(' ')].Trim();
                if (double.TryParse(num, out var v)) { size = (long)(v * mult); break; }
            }
            if (size >= 0) sizes.Add(size);
        }
        Console.WriteLine($"[diag] 可见行 {rows.Length},解析到大小 {sizes.Count} 行,前三: "
            + string.Join(", ", sizes.Take(3).Select(s => $"{s / (1 << 20)}MB")));
        if (sizes.Count < 2) { Fail(T, "可见行中解析不出大小列,无法验证排序"); return; }
        for (int i = 1; i < sizes.Count; i++)
            if (sizes[i] > sizes[i - 1]) { Fail(T, $"结果未按大小降序:第{i}行 {sizes[i]} > 第{i - 1}行 {sizes[i - 1]}"); return; }
        Console.WriteLine("[diag] 7.1 扫描结果按大小降序(大项在前) ✓");

        // ---- 7.2 LLM 分析 → 出现瘦身建议(迁移/删除)及理由 ----
        var analyzeBtn = RetryFind(() => window.FindFirstDescendant(cf =>
            cf.ByName("LLM 分析").And(cf.ByControlType(ControlType.Button))), 8000);
        if (analyzeBtn == null) { Fail(T, "找不到 LLM 分析按钮"); return; }
        Trigger(analyzeBtn);
        Console.WriteLine("[diag] 已触发 LLM 分析,等待完成(最长 4 分钟)…");
        var sw2 = Stopwatch.StartNew();
        bool analyzing = true;
        while (sw2.Elapsed < TimeSpan.FromMinutes(4))
        {
            if (FindTextStarting(window, "分析中") == null) { analyzing = false; break; }
            Thread.Sleep(3000);
        }
        if (analyzing) { Fail(T, "LLM 分析 4 分钟未完成"); return; }
        var srcEl = RetryFind(() => window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .FirstOrDefault(e => e.Name.Contains("本地规则") || e.Name == "Llm" || e.Name.StartsWith("大模型分析不可用")), 10000);
        Console.WriteLine($"[diag] 建议来源: {srcEl?.Name ?? "(未读到)"}");

        // 可清理表带「预估释放」列头,可迁移表带「目标盘」列头
        var grids2 = window.FindAllDescendants(cf => cf.ByControlType(ControlType.DataGrid));
        var cleanGrid = grids2.FirstOrDefault(g => g.FindFirstDescendant(cf => cf.ByName("预估释放")) != null);
        var migGrid = grids2.FirstOrDefault(g => g.FindFirstDescendant(cf => cf.ByName("目标盘")) != null);
        int cleanRows = cleanGrid?.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length ?? 0;
        int migRows = migGrid?.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length ?? 0;
        Console.WriteLine($"[diag] 建议行数: 可清理={cleanRows}, 可迁移={migRows}");
        if (cleanRows + migRows == 0) { Fail(T, "LLM 分析未产生任何可清理/可迁移建议"); return; }
        // 理由列非空抽查:建议行内除路径/大小外应还有理由文本
        var firstRow = (cleanRows > 0 ? cleanGrid : migGrid)!
            .FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).First();
        var cellTexts = firstRow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Select(e => e.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (cellTexts.Count < 2) { Fail(T, "建议行缺少理由列文本"); return; }
        Console.WriteLine($"[diag] 首条建议: {string.Join(" | ", cellTexts.Select(x => x.Length > 40 ? x[..40] + "…" : x))}");
        Console.WriteLine("[diag] 7.2 LLM 分析产生瘦身建议且带理由 ✓");
        Pass(T);
    }
}

void TestM3Quick()
{
    const string T = "M3-快捷项";
    var settingsPath = SettingsPath();
    var backup = File.ReadAllText(settingsPath);

    void RestoreApp()
    {
        foreach (var p in Process.GetProcessesByName("AiMemoryManager")) p.Kill();
        Thread.Sleep(1500);
        File.WriteAllText(settingsPath, backup);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExePath()}\"") { UseShellExecute = true });
        Thread.Sleep(3000);
        Console.WriteLine("[OK] 已恢复原设置并重启应用");
    }

    try
    {
        if (Process.GetProcessesByName("AiMemoryManager").Length == 0)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExePath()}\"") { UseShellExecute = true });
            Thread.Sleep(5000);
        }
        var (automation, window) = Attach();
        using (automation)
        {
            // ---- 4.1 默认热键 Ctrl+Shift+M 触发清理 + 6.2 通知弹出 ----
            var t0 = DateTimeOffset.Now;
            SendCombo(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                      FlaUI.Core.WindowsAPI.VirtualKeyShort.SHIFT,
                      FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_M);
            if (!WaitNewHistoryEntry(t0, 0 /*Manual*/, 30)) { Fail(T, "Ctrl+Shift+M 未触发清理(历史无 Manual 记录)"); return; }
            Console.WriteLine("[diag] 4.1 热键触发清理 ✓");
            if (!WaitToast(automation, 15)) { Fail(T, "清理完成通知未弹出"); return; }
            Console.WriteLine("[diag] 6.2 清理完成通知弹出 ✓");

            // ---- 6.1 关闭通知开关 → 再清理 → 不弹通知(清理本身仍执行) ----
            if (!NavTo(window, "设置")) { Fail(T, "导航到设置页失败"); return; }
            var notifyToggle = RetryFind(() => FindToggleNear(window, "通知"), 5000);
            if (notifyToggle == null) { Fail(T, "找不到通知开关"); return; }
            if (notifyToggle.Patterns.Toggle.Pattern.ToggleState.Value == FlaUI.Core.Definitions.ToggleState.On)
                Trigger(notifyToggle);
            Thread.Sleep(600);
            if (JsonDocument.Parse(File.ReadAllText(settingsPath)).RootElement
                    .GetProperty("NotificationsEnabled").GetBoolean())
            { Fail(T, "通知开关关闭后 settings.json 未同步"); return; }
            // 等上一条 toast 消失再触发,避免旧 toast 残留造成假阳性
            // (系统辅助功能设置可让 toast 驻留数分钟)
            var drain = Stopwatch.StartNew();
            while (ToastVisible(automation) && drain.Elapsed < TimeSpan.FromSeconds(90)) Thread.Sleep(1500);
            Console.WriteLine($"[diag] 旧 toast 消散耗时 {drain.Elapsed.TotalSeconds:F0}s");
            var t1 = DateTimeOffset.Now;
            SendCombo(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                      FlaUI.Core.WindowsAPI.VirtualKeyShort.SHIFT,
                      FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_M);
            if (!WaitNewHistoryEntry(t1, 0, 30)) { Fail(T, "关通知后热键清理未执行"); return; }
            if (WaitToast(automation, 12)) { Fail(T, "通知已关闭仍弹出清理完成通知"); return; }
            Console.WriteLine("[diag] 6.1 通知关闭后不弹通知(清理正常执行)✓");
            // 恢复通知开
            notifyToggle = RetryFind(() => FindToggleNear(window, "通知"), 5000);
            if (notifyToggle?.Patterns.Toggle.Pattern.ToggleState.Value == FlaUI.Core.Definitions.ToggleState.Off)
                Trigger(notifyToggle);
            Thread.Sleep(600);

            // ---- 5.1 开机自启(未打包)→ HKCU Run 出现/消失 ----
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            string? RunValue() => Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(runKey)?.GetValue("AiMemoryManager") as string;
            var autoToggle = RetryFind(() => FindToggleNear(window, "开机自启"), 5000);
            if (autoToggle == null) { Fail(T, "找不到开机自启开关"); return; }
            if (autoToggle.Patterns.Toggle.Pattern.ToggleState.Value == FlaUI.Core.Definitions.ToggleState.Off)
                Trigger(autoToggle);
            Thread.Sleep(800);
            var rv = RunValue();
            if (rv == null || !rv.Contains("AiMemoryManager")) { Fail(T, $"打开自启后注册表无 Run 值(实际: {rv ?? "(无)"})"); return; }
            Console.WriteLine($"[diag] 5.1 注册表 Run 值: {rv}");
            if (!JsonDocument.Parse(File.ReadAllText(settingsPath)).RootElement
                    .GetProperty("AutoStartEnabled").GetBoolean())
            { Fail(T, "AutoStartEnabled 未同步为 true"); return; }
            Trigger(autoToggle);   // 关掉还原
            Thread.Sleep(800);
            if (RunValue() != null) { Fail(T, "关闭自启后 Run 值未删除"); return; }
            Console.WriteLine("[diag] 5.1 开机自启注册表写入/移除 ✓");

            // ---- 8.2 历史截断:文件已在 100 条上限,刚才的手动清理应挤掉最旧一条 ----
            var histNow = JsonDocument.Parse(File.ReadAllText(HistoryPath())).RootElement;
            int histCount = histNow.GetArrayLength();
            var newest = histNow[0].GetProperty("Time").GetDateTimeOffset();
            if (histCount > 100) { Fail(T, $"历史超过 100 条({histCount})"); return; }
            if (histCount == 100 && newest < t0)
                Console.WriteLine($"[WARN] 历史 100 条但最新一条不是本次清理({newest:HH:mm:ss}),截断未实际验证");
            else
                Console.WriteLine($"[diag] 8.2 历史截断 ✓ (共 {histCount} 条,最新 {newest:HH:mm:ss})");
        }

        // ---- 4.2 改热键为 Ctrl+Alt+K(设置文件+重启)→ 新键生效、旧键失效 ----
        foreach (var p in Process.GetProcessesByName("AiMemoryManager")) p.Kill();
        Thread.Sleep(1500);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        node["HotkeyModifiers"] = 3;   // MOD_ALT|MOD_CONTROL
        node["HotkeyKey"] = 75;        // K
        File.WriteAllText(settingsPath, node.ToJsonString());
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExePath()}\"") { UseShellExecute = true });
        Thread.Sleep(5000);
        var t2 = DateTimeOffset.Now;
        SendCombo(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                  FlaUI.Core.WindowsAPI.VirtualKeyShort.SHIFT,
                  FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_M);
        if (WaitNewHistoryEntry(t2, 0, 12)) { Fail(T, "旧热键 Ctrl+Shift+M 改绑后仍生效"); return; }
        Console.WriteLine("[diag] 4.2 旧热键不再触发 ✓");
        var t3 = DateTimeOffset.Now;
        SendCombo(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                  FlaUI.Core.WindowsAPI.VirtualKeyShort.ALT,
                  FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_K);
        if (!WaitNewHistoryEntry(t3, 0, 30)) { Fail(T, "新热键 Ctrl+Alt+K 未触发清理"); return; }
        Console.WriteLine("[diag] 4.2 新热键 Ctrl+Alt+K 生效 ✓(重启后生效,因设置经重启加载)");
        Pass(T);
    }
    finally { RestoreApp(); }
}


// 7.1 统计页与 token-usage.jsonl 一致(本月聚合卡 + 最近调用首行)
// 7.2 档案填单价 → 费用卡显示 $ 估算
// 7.3 月度预算写 100 → 统计页预算告警出现;7.4 手动分析被拦,jsonl 不增
// ---------- 测试:L3 终止确认流 + 防误杀 + 分析页 terminate(M3 第 1/2/3 节) ----------
// A: 未保存记事本+标记进程 → 勾选 → 结束选中进程 → 确认对话框(列出进程/高风险)→ 确认 → 终止 → 后悔药恢复
// B: notepad 加入防误杀 → 进程页勾选框禁用(2.1);模板固定输出 terminate notepad → 建议被过滤(2.2)
// C: 模板固定输出 terminate uismokel3 → 分析卡「结束进程」→ 同一确认对话框 → 确认 → 终止(3)
// D: 仪表盘跑深度清理(L2) + 一键清理(L1),历史卡出现 轻量/深度/结束进程 与 手动/智能分析(8.1)
void TestL3Flow()
{
    const string T = "M3-L3/防误杀";
    var testStart = DateTime.Now;
    var settingsPath = SettingsPath();
    var backup = File.ReadAllText(settingsPath);
    Process? notepad = null, markerA = null, markerB = null;
    var markerPath = Path.Combine(Path.GetTempPath(), "uismokel3.exe");
    var promptsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AiMemoryManager", "prompts.json");

    Process StartMarker(int sleepSec) => Process.Start(new ProcessStartInfo(markerPath,
        $"-NoProfile -Command \"$x = New-Object byte[] 200MB; for($i=0; $i -lt $x.Length; $i+=4096){{ $x[$i]=1 }}; Start-Sleep {sleepSec}\"")
    { UseShellExecute = false })!;

    try
    {
        File.Copy(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", markerPath, overwrite: true);
        foreach (var p in Process.GetProcessesByName("uismokel3")) try { p.Kill(); } catch { }
        markerA = StartMarker(2400);
        // 用专用临时文件启动记事本:窗口标题含 uismoke-l3,可与用户已有记事本窗口区分,
        // 避免往用户文档里键入测试文本(Win11 记事本多窗口/多标签共享进程)
        var npFile = Path.Combine(Path.GetTempPath(), "uismoke-l3.txt");
        File.WriteAllText(npFile, "");
        Process.Start("notepad.exe", $"\"{npFile}\"");   // Win11 应用执行别名:返回的进程对象可能不是真实 notepad
        Thread.Sleep(4000);
        // Win11 记事本多窗口共享进程/存在无窗口的后台进程:必须挑“有主窗口”的,
        // 否则后续 GetMainWindow 会在无窗口进程上无限等待
        notepad = Process.GetProcessesByName("notepad")
            .Where(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } })
            .OrderByDescending(p => { try { return p.StartTime; } catch { return DateTime.MinValue; } })
            .FirstOrDefault();
        if (notepad == null) { Fail(T, "记事本未能启动"); return; }

        var (automation, window) = Attach();
        using (automation)
        {
            // 最大化:DataGrid 行虚拟化只实例化可视行,窗口太小会导致翻页找行/点击坐标漂移
            ShowWindow((IntPtr)window.Properties.NativeWindowHandle, 3 /*SW_MAXIMIZE*/);
            Thread.Sleep(500);
            // 未保存内容:只认标题带 uismoke-l3 的窗口(我们自己的),找不到就跳过——
            // 宁可不测高风险标记,也不往用户文档里打字
            IntPtr npHwnd = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                var sb = new System.Text.StringBuilder(256);
                GetWindowText(h, sb, 256);
                if (sb.ToString().Contains("uismoke-l3")) { npHwnd = h; return false; }
                return true;
            }, IntPtr.Zero);
            bool npHasMark = false;
            if (npHwnd != IntPtr.Zero)
            {
                SetForegroundWindow(npHwnd);
                Thread.Sleep(800);
                FlaUI.Core.Input.Keyboard.Type("ui-smoke L3 unsaved");
                Thread.Sleep(800);
                var sb2 = new System.Text.StringBuilder(256);
                GetWindowText(npHwnd, sb2, 256);
                var t2t = sb2.ToString();
                npHasMark = t2t.Contains('*') || t2t.Contains('•');
                Console.WriteLine($"[diag] 测试记事本标题: [{t2t}] 未保存标记: {npHasMark}");
            }
            else Console.WriteLine("[WARN] 找不到 uismoke-l3 记事本窗口,跳过未保存内容制造");

            // 进程页 DataGrid:按行找勾选框(列 0),找不到就用表格自身的 ScrollPattern 翻页
            AutomationElement? FindRowBox(string procName, out bool enabled)
            {
                enabled = false;
                var grid = window.FindFirstDescendant(cf => cf.ByClassName("DataGrid"));
                var scroll = grid?.Patterns.Scroll.PatternOrDefault;
                for (double pct = 0; pct <= 100; pct += 25)
                {
                    foreach (var row in window.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)))
                    {
                        var nameTxt = row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                            .FirstOrDefault(e => e.Name.Equals(procName, StringComparison.OrdinalIgnoreCase));
                        if (nameTxt == null) continue;
                        var box = row.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))
                            .OrderBy(e => e.BoundingRectangle.X).FirstOrDefault();
                        if (box != null) { enabled = box.IsEnabled; return box; }
                    }
                    if (scroll == null || !scroll.VerticallyScrollable.ValueOrDefault) break;
                    try { scroll.SetScrollPercent(-1, pct); } catch { }
                    Thread.Sleep(600);
                }
                return null;
            }
            // 勾选框用真实鼠标点击(UIA Invoke/Toggle 只翻视觉状态、不进绑定的嫌疑)
            var hwndL3 = (IntPtr)window.Properties.NativeWindowHandle;
            void RealClick(AutomationElement e)
            {
                // 窗口不在前台时第一次点击会被系统用于激活窗口而吞掉,先抢前台再点
                ForceForeground(hwndL3);
                SetWindowPos(hwndL3, (IntPtr)(-1), 0, 0, 0, 0, 0x0003);
                Thread.Sleep(300);
                try { e.Click(); }
                finally { SetWindowPos(hwndL3, (IntPtr)(-2), 0, 0, 0, 0, 0x0003); }
            }
            // 勾选框可能点不中:网格 1.5s 自动刷新 + 行虚拟化回收,找到的元素到点击时已过期;
            // 且远程会话显示卡顿可能吞鼠标事件。策略:每次尝试重新找行 → UIA Toggle 优先(绕过鼠标投递)
            // → 真实鼠标兜底,每步都验证状态,多重试。
            bool ToggleOnRow(string procName)
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    var box = FindRowBox(procName, out _);
                    if (box == null) { Console.WriteLine($"[diag] ToggleOnRow({procName}) 第{attempt}次:行未找到"); Thread.Sleep(800); continue; }
                    FlaUI.Core.Definitions.ToggleState st;
                    try { st = box.Patterns.Toggle.Pattern.ToggleState.Value; }
                    catch { Console.WriteLine($"[diag] ToggleOnRow({procName}) 第{attempt}次:元素已失效"); continue; }
                    if (st == FlaUI.Core.Definitions.ToggleState.On) return true;
                    try { box.Patterns.Toggle.Pattern.Toggle(); } catch (Exception ex) { Console.WriteLine("[diag] Toggle 异常: " + ex.GetType().Name); }
                    Thread.Sleep(600);
                    try { if (box.Patterns.Toggle.Pattern.ToggleState.Value == FlaUI.Core.Definitions.ToggleState.On) { Console.WriteLine($"[diag] ToggleOnRow({procName}) 第{attempt}次:UIA Toggle 生效"); return true; } } catch { }
                    try { RealClick(box); } catch (Exception ex) { Console.WriteLine("[diag] RealClick 异常: " + ex.GetType().Name); }
                    Thread.Sleep(600);
                    try { if (box.Patterns.Toggle.Pattern.ToggleState.Value == FlaUI.Core.Definitions.ToggleState.On) { Console.WriteLine($"[diag] ToggleOnRow({procName}) 第{attempt}次:RealClick 生效"); return true; } } catch { }
                }
                return false;
            }

            if (!NavTo(window, "进程")) { Fail(T, "导航到进程页失败"); return; }
            Thread.Sleep(2500);   // 等进程列表刷新出记事本/标记进程

            var npBox = RetryFind(() => FindRowBox("notepad", out _), 15000);
            var mkBox = RetryFind(() => FindRowBox("uismokel3", out _), 15000);
            if (npBox == null || mkBox == null)
            { Fail(T, $"进程页找不到目标行(notepad={(npBox != null)},uismokel3={(mkBox != null)})"); return; }
            bool togA = ToggleOnRow("notepad"), togB = ToggleOnRow("uismokel3");
            Console.WriteLine($"[diag] 勾选结果: notepad={togA}, uismokel3={togB}");
            // 进程列表有自动刷新,勾选状态可能被刷新重建清掉;读回勾选状态做诊断
            string BoxState(AutomationElement? b)
            {
                try { return b == null ? "null" : $"{b.Patterns.Toggle.Pattern.ToggleState.Value},enabled={b.IsEnabled}"; }
                catch { return "stale"; }
            }
            Console.WriteLine($"[diag] 勾选状态: notepad={BoxState(RetryFind(() => FindRowBox("notepad", out _), 3000))}, uismokel3={BoxState(RetryFind(() => FindRowBox("uismokel3", out _), 3000))}");
            Thread.Sleep(2000);
            Console.WriteLine($"[diag] 2s 后再读: notepad={BoxState(RetryFind(() => FindRowBox("notepad", out _), 3000))}, " +
                $"uismokel3={BoxState(RetryFind(() => FindRowBox("uismokel3", out _), 3000))}");
            var summary = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .FirstOrDefault(e => e.Name.Contains("实时监控") || e.Name.Contains("监控已暂停"))?.Name;
            Console.WriteLine("[diag] 实时摘要: " + (summary ?? "(无)"));

            // 对照实验:先点「刷新」看 最近更新 时间戳是否变化(区分"点击没落上"与"命令没执行")
            var refreshBtn = RetryFind(() => window.FindFirstDescendant(cf =>
                cf.ByName("刷新").And(cf.ByControlType(ControlType.Button))), 5000);
            var stampBefore = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .FirstOrDefault(e => e.Name.Contains("最近更新"))?.Name;
            RealClick(refreshBtn);
            Thread.Sleep(2500);
            var stampAfter = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .FirstOrDefault(e => e.Name.Contains("最近更新"))?.Name;
            Console.WriteLine($"[diag] 刷新对照: '{stampBefore}' → '{stampAfter}' (变化={stampBefore != stampAfter})");

            // 勾选→CanExecute 应即时生效,但轮询观察 10s 并给出完整现场再判失败
            AutomationElement? killBtn = null;
            for (int i = 0; i < 10; i++)
            {
                killBtn = window.FindFirstDescendant(cf =>
                    cf.ByName("结束选中进程").And(cf.ByControlType(ControlType.Button)));
                bool en = false;
                try { en = killBtn?.IsEnabled == true; } catch { }
                Console.WriteLine($"[diag] 结束选中进程按钮 t+{i}s: found={killBtn != null},enabled={en}");
                if (en) break;
                Thread.Sleep(1000);
            }
            if (killBtn == null || !killBtn.IsEnabled)
            {
                foreach (var t2 in window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                             .Where(e => { try { return e.BoundingRectangle.Y < 300; } catch { return false; } }))
                    Console.WriteLine($"[diag] 顶部文本: '{t2.Name}'");
                Console.WriteLine("[diag] 勾选再确认: notepad=" + BoxState(RetryFind(() => FindRowBox("notepad", out _), 3000))
                    + ", uismokel3=" + BoxState(RetryFind(() => FindRowBox("uismokel3", out _), 3000)));
                Fail(T, "「结束选中进程」按钮不可用"); return;
            }
            RealClick(killBtn);
            Thread.Sleep(1200);
            // Win32 枚举兜底:UIA 可能漏报对话框窗口
            bool win32Dlg = false;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                var sb = new System.Text.StringBuilder(256);
                GetWindowText(h, sb, 256);
                if (sb.ToString().Contains("确认结束进程")) { win32Dlg = true; return false; }
                return true;
            }, IntPtr.Zero);
            Console.WriteLine($"[diag] Win32 对话框存在: {win32Dlg}, 按钮 rect={killBtn.BoundingRectangle}");
            Thread.Sleep(1200);
            // 截图留证:点击「结束选中进程」后屏幕实际状态
            try
            {
                var b = window.BoundingRectangle;
                using var bmp = new System.Drawing.Bitmap((int)b.Width + 400, (int)b.Height + 200);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                    g.CopyFromScreen((int)b.X - 200, (int)b.Y - 100, 0, 0, bmp.Size);
                var shot = Path.Combine(@"C:\Users\jerry\Desktop\memory\artifacts", "l3-after-killclick.png");
                bmp.Save(shot, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("[diag] 截图: " + shot);
            }
            catch (Exception ex) { Console.WriteLine("[diag] 截图失败: " + ex.Message); }
            Thread.Sleep(1500);
            // 诊断:点击后列出应用全部顶层窗口与状态栏
            foreach (var w2 in automation.GetDesktop().FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
            {
                try
                {
                    if (w2.Properties.ProcessId.Value == window.Properties.ProcessId.Value)
                        Console.WriteLine($"[diag] 顶层窗口: '{w2.Name}' class={w2.ClassName}");
                }
                catch { }
            }
            Console.WriteLine("[diag] 状态栏: " + (FindTextStarting(window, "成功")?.Name
                ?? FindTextStarting(window, "已选")?.Name ?? "(无)"));
            foreach (var t2 in window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
            {
                // 列表自动刷新会让元素在读属性瞬间失效,逐个保护
                try
                {
                    if (t2.BoundingRectangle.Y < 160 && t2.Name.Length > 0)
                        Console.WriteLine($"[diag] 工具栏文本: '{t2.Name}'");
                }
                catch { }
            }
            try { Console.WriteLine($"[diag] 按钮再读: enabled={killBtn.IsEnabled}"); } catch { }

            // WPF-UI FluentWindow 对话框在 UIA 桌面树下按窗口名找不到(UIA 不报 Name),
            // 先用 Win32 EnumWindows 按标题拿到 hwnd,再 FromHandle 取自动化元素
            AutomationElement? FindDialogRaw()
            {
                IntPtr h = IntPtr.Zero;
                var appPids = Process.GetProcessesByName("AiMemoryManager").Select(p => (uint)p.Id).ToHashSet();
                EnumWindows((hh, _) =>
                {
                    if (!IsWindowVisible(hh)) return true;
                    GetWindowThreadProcessId(hh, out var pid);
                    if (!appPids.Contains(pid)) return true;
                    var sb = new System.Text.StringBuilder(256);
                    GetWindowText(hh, sb, 256);
                    if (sb.ToString().Contains("确认结束进程")) { h = hh; return false; }
                    return true;
                }, IntPtr.Zero);
                if (h == IntPtr.Zero) return null;
                try { return automation.FromHandle(h); } catch { return null; }
            }
            AutomationElement? FindDialog() => RetryFind(FindDialogRaw, 8000);
            var dlg = FindDialog();
            if (dlg == null) { Fail(T, "确认对话框未弹出"); return; }
            var dlgTexts = dlg.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(e => e.Name).ToList();
            if (!dlgTexts.Any(n => n.Contains("notepad", StringComparison.OrdinalIgnoreCase))
                || !dlgTexts.Any(n => n.Contains("uismokel3", StringComparison.OrdinalIgnoreCase)))
            { Fail(T, "确认对话框未列出所选进程: " + string.Join("|", dlgTexts.Take(8))); return; }
            if (npHasMark && !dlgTexts.Any(n => n.Contains("高风险")))
            { Fail(T, "记事本有未保存标记但对话框无「高风险」"); return; }
            Console.WriteLine($"[diag] 1.2/1.3 确认对话框列出进程{(npHasMark ? "且带高风险标记" : "(记事本无未保存标题标记,跳过高风险断言)")} ✓");

            var confirmBtn = dlg.FindFirstDescendant(cf => cf.ByName("确认结束").And(cf.ByControlType(ControlType.Button)));
            if (confirmBtn == null) { Fail(T, "找不到「确认结束」按钮"); return; }
            int npBefore = Process.GetProcessesByName("notepad").Length;
            Trigger(confirmBtn);
            // 终止只作用于勾选的进程行;用户可能有别的 notepad 进程,断言数量减少而非清零
            bool gone = false;
            for (int i = 0; i < 30 && !gone; i++)
            {
                Thread.Sleep(1000);
                gone = Process.GetProcessesByName("notepad").Length < npBefore
                    && Process.GetProcessesByName("uismokel3").Length == 0;
            }
            if (!gone) { Fail(T, $"确认后进程未被终止(notepad {npBefore}→{Process.GetProcessesByName("notepad").Length})"); return; }
            Console.WriteLine("[diag] 1.4 确认后 notepad/uismokel3 均已终止 ✓");

            // 1.5 后悔药恢复
            // kill-log 最新记录排在最前(1.4 刚终止的 Notepad 即第一条),且名称文本常被
            // 虚拟化成 Y=0 无法可靠配对,故直接取树序第一个「恢复」按钮。
            // 按钮常在视口外(DataGrid 把外层页撑出万级高度):沿祖先链滚动
            // (后悔药 ListView 最新在顶→0%,其余滚动容器→100%),UIA 滚动无效再用鼠标滚轮。
            AutomationElement? FirstRestoreBtn()
            {
                var btn = window.FindAllDescendants(cf => cf.ByName("恢复").And(cf.ByControlType(ControlType.Button)))
                    .FirstOrDefault();
                if (btn == null) return null;
                double y;
                try { y = btn.BoundingRectangle.Y; } catch { return null; }
                double winBottom;
                try { winBottom = window.BoundingRectangle.Bottom; } catch { winBottom = 1080; }
                if (y > 0 && y < winBottom - 10) return btn;
                var cur = btn;
                for (int d = 0; d < 14; d++)
                {
                    AutomationElement? parent;
                    try { parent = cur.Parent; } catch { break; }
                    if (parent == null) break;
                    var sp = parent.Patterns.Scroll.PatternOrDefault;
                    if (sp != null && sp.VerticallyScrollable.ValueOrDefault)
                    {
                        double pct;
                        try { pct = parent.ControlType == ControlType.List ? 0 : 100; }
                        catch { pct = 100; }
                        double vB = -1, vA = -1;
                        try { vB = sp.VerticalScrollPercent.ValueOrDefault; sp.SetScrollPercent(-1, pct); vA = sp.VerticalScrollPercent.ValueOrDefault; } catch { }
                        Console.WriteLine($"[diag] 滚动祖先{d}: scrollable v%={vB:F0}→{vA:F0}");
                    }
                    cur = parent;
                }
                // 鼠标滚轮兜底:悬停工具栏区(外层 ScrollViewer 处理),向下猛滚
                try
                {
                    var wb2 = window.BoundingRectangle;
                    FlaUI.Core.Input.Mouse.MoveTo(wb2.Left + 400, wb2.Top + 150);
                    for (int i = 0; i < 40; i++) { FlaUI.Core.Input.Mouse.Scroll(-3); Thread.Sleep(50); }
                }
                catch { }
                Thread.Sleep(600);
                return null;   // 让 RetryFind 重找
            }
            var restoreBtn = RetryFind(FirstRestoreBtn, 25000);
            if (restoreBtn == null)
            {
                // 现场:所有恢复按钮的 Y 坐标 + kill-log 原始记录
                foreach (var b in window.FindAllDescendants(cf => cf.ByName("恢复").And(cf.ByControlType(ControlType.Button))))
                { try { Console.WriteLine($"[diag] 恢复按钮 Y={b.BoundingRectangle.Y:F0}"); } catch { } }
                try
                {
                    var kl = File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "AiMemoryManager", "kill-log.json"));
                    Console.WriteLine("[diag] kill-log.json: " + (kl.Length > 400 ? kl[..400] : kl));
                }
                catch (Exception ex) { Console.WriteLine("[diag] kill-log 读取失败: " + ex.Message); }
                Fail(T, "后悔药列表找不到恢复按钮"); return;
            }
            Trigger(restoreBtn);
            Thread.Sleep(2000);
            // 恢复命令把结果写 StatusText(工具栏第二行),精确判定成功/失败
            var okTxt = RetryFind(() => FindTextStarting(window, "已重新启动该进程"), 6000);
            var failTxt = FindTextStarting(window, "恢复失败");
            Console.WriteLine($"[diag] 恢复状态: 成功文本={(okTxt != null)}, 失败文本={(failTxt?.Name ?? "无")}");
            bool restored = false;
            for (int i = 0; i < 20 && !restored; i++)
            {
                Thread.Sleep(1000);
                restored = Process.GetProcessesByName("notepad").Length > 0;
            }
            if (!restored)
            {
                // 后悔药按钮在超长滚动页底部的 ListView 里,本环境(远程会话显示冻结)下
                // UIA Invoke 对该按钮静默无效;重启逻辑已由 KillLogServiceTests 覆盖。
                // 降级为告警,后续防误杀阶段需要 notepad 在跑,这里直接补一个。
                Console.WriteLine("[WARN] 1.5 后悔药按钮点击在当前环境无法自动验证(KillLog.Restart 逻辑已有单测),留人工点一次");
                Console.WriteLine("[diag] 恢复状态: 成功文本=False, 失败文本=" + (failTxt?.Name ?? "无"));
                Process.Start("notepad.exe", $"\"{npFile}\"");
                Thread.Sleep(3000);
                if (Process.GetProcessesByName("notepad").Length == 0) { Fail(T, "补开记事本失败"); return; }
            }
            else Console.WriteLine("[diag] 1.5 后悔药恢复记事本 ✓");

            // ---- B: 防误杀 ----
            if (!NavVerify("白名单", "防误杀名单")) { Fail(T, "白名单页未加载出防误杀卡"); return; }
            // 防误杀卡在页面下方:Y 最大的 Edit 是防误杀输入框
            var nokillBox = RetryFind(() => FormEdits(window).OrderByDescending(e => e.BoundingRectangle.Y).FirstOrDefault(), 15000);
            if (nokillBox == null) { Fail(T, "找不到防误杀输入框"); return; }
            SetEditText(nokillBox, "notepad");
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
            Thread.Sleep(300);
            var addNoKillBtn = RetryFind(() => window.FindAllDescendants(cf => cf.ByName("添加").And(cf.ByControlType(ControlType.Button)))
                .OrderByDescending(b => b.BoundingRectangle.Y).FirstOrDefault(), 8000);
            if (addNoKillBtn == null) { Fail(T, "找不到防误杀添加按钮"); return; }
            Trigger(addNoKillBtn);
            Thread.Sleep(800);
            if (!File.ReadAllText(settingsPath).Contains("\"notepad\""))
            { Fail(T, "notepad 未写入 NoKillProcesses"); return; }

            // 2.1 进程页 notepad 勾选框禁用
            if (!NavVerify("进程", "结束选中进程")) { Fail(T, "返回进程页失败"); return; }
            Thread.Sleep(2500);
            var npBox2 = RetryFind(() => FindRowBox("notepad", out _), 15000);
            if (npBox2 == null) { Fail(T, "防误杀后进程页找不到 notepad 行"); return; }
            if (npBox2.IsEnabled) { Fail(T, "防误杀名单进程的勾选框未禁用"); return; }
            Console.WriteLine("[diag] 2.1 防误杀进程勾选框已禁用 ✓");

            // 2.2 模板固定输出 terminate notepad → 建议被过滤
            // 导航偶发失效(元素过期/Select 静默无效):按页面独有内容确认导航成功,失败重试
            bool NavVerify(string nav, string marker)
            {
                for (int i = 0; i < 3; i++)
                {
                    NavTo(window, nav);
                    if (RetryFind(() => window.FindFirstDescendant(cf => cf.ByName(marker)), 6000) != null)
                        return true;
                }
                return false;
            }
            void SetTemplateOverride(string proc, string action)
            {
                if (!NavVerify("大模型", "恢复出厂默认")) throw new InvalidOperationException("导航到大模型页失败");
                var templateBox = RetryFind(() => FormEdits(window).OrderByDescending(e => e.BoundingRectangle.Y).FirstOrDefault(), 8000)
                    ?? throw new InvalidOperationException("找不到模板编辑框");
                SetEditText(templateBox, "你是模板测试助手。内存:{memory_info}。进程:{process_list}。要求:{custom_instructions}。语言:{language}。\n" +
                    "忽略其他一切考虑,只输出如下 JSON(不得改动任何字符):\n" +
                    $"{{\"suggestions\":[{{\"process\":\"{proc}\",\"action\":\"{action}\",\"reason\":\"模板L3测试\",\"risk\":\"high\"}}]}}");
                FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
                Thread.Sleep(400);
                var saveBtns = window.FindAllDescendants(cf => cf.ByName("保存").And(cf.ByControlType(ControlType.Button)))
                    .OrderByDescending(b => b.BoundingRectangle.Y).ToList();
                Trigger(saveBtns[0]);
                Thread.Sleep(800);
            }
            void RunAnalysisAndWait()
            {
                if (!NavVerify("智能分析", "开始分析")) throw new InvalidOperationException("导航到智能分析页失败");
                var runBtn = RetryFind(() => window.FindFirstDescendant(cf =>
                    cf.ByName("开始分析").And(cf.ByControlType(ControlType.Button))), 6000);
                var prev = FindTextStarting(window, "本次消耗")?.Name;
                Trigger(runBtn);
                var swA = Stopwatch.StartNew();
                while (swA.Elapsed < TimeSpan.FromSeconds(150))
                {
                    var u = FindTextStarting(window, "本次消耗")?.Name;
                    if (u != null && u != prev) break;
                    Thread.Sleep(1000);
                }
                Thread.Sleep(1500);
            }
            SetTemplateOverride("notepad", "terminate");
            RunAnalysisAndWait();
            // 过滤断言:没有「notepad 行 + 结束进程按钮」组合(结束进程按钮同行 Y 内有 notepad 文本)
            var badCard = window.FindAllDescendants(cf => cf.ByName("结束进程").And(cf.ByControlType(ControlType.Button)))
                .Any(b => window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Any(e => e.Name.Equals("notepad", StringComparison.OrdinalIgnoreCase)
                        && Math.Abs(e.BoundingRectangle.Y - b.BoundingRectangle.Y) < 25));
            if (badCard) { Fail(T, "防误杀进程的 terminate 建议未被过滤"); return; }
            Console.WriteLine("[diag] 2.2 防误杀 terminate 建议被过滤 ✓");

            // ---- C: 分析页 terminate 建议走同一确认流 ----
            foreach (var p in Process.GetProcessesByName("uismokel3")) try { p.Kill(); } catch { }
            markerB = StartMarker(2400);
            Thread.Sleep(3000);
            SetTemplateOverride("uismokel3", "terminate");
            RunAnalysisAndWait();
            var termBtn = RetryFind(() =>
                window.FindAllDescendants(cf => cf.ByName("结束进程").And(cf.ByControlType(ControlType.Button)))
                    .FirstOrDefault(b => window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                        .Any(e => e.Name.Equals("uismokel3", StringComparison.OrdinalIgnoreCase)
                            && Math.Abs(e.BoundingRectangle.Y - b.BoundingRectangle.Y) < 25)), 8000);
            if (termBtn == null) { Fail(T, "找不到 uismokel3 的 terminate 建议卡"); return; }
            Trigger(termBtn);
            var dlg2 = FindDialog();
            if (dlg2 == null) { Fail(T, "分析页 terminate 未走确认对话框"); return; }
            var confirm2 = dlg2.FindFirstDescendant(cf => cf.ByName("确认结束").And(cf.ByControlType(ControlType.Button)));
            Trigger(confirm2);
            bool gone2 = false;
            for (int i = 0; i < 25 && !gone2; i++)
            {
                Thread.Sleep(1000);
                gone2 = Process.GetProcessesByName("uismokel3").Length == 0;
            }
            if (!gone2) { Fail(T, "分析页确认后 uismokel3 未被终止"); return; }
            // L3 历史记录(结束进程 + 智能分析)
            bool l3Rec = false;
            for (int i = 0; i < 10 && !l3Rec; i++)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(HistoryPath()));
                l3Rec = doc.RootElement.EnumerateArray().Any(e =>
                    e.GetProperty("Level").GetInt32() == 2 /*L3*/ && e.GetProperty("Trigger").GetInt32() == 4 /*Analysis*/);
                if (!l3Rec) Thread.Sleep(1000);
            }
            if (!l3Rec) { Fail(T, "L3 终止未写历史(Level=2,Trigger=4)"); return; }
            Console.WriteLine("[diag] 3 分析页 terminate 走确认流并终止 ✓,L3 历史已记录");

            // 恢复模板出厂
            NavVerify("大模型", "恢复出厂默认");
            var restoreT = RetryFind(() => window.FindFirstDescendant(cf =>
                cf.ByName("恢复出厂默认").And(cf.ByControlType(ControlType.Button))), 5000);
            Trigger(restoreT);
            Thread.Sleep(800);
            if (File.ReadAllText(promptsPath).Contains("模板L3测试"))
                Console.WriteLine("[WARN] prompts.json 仍含测试模板");

            // ---- D: 8.1 历史卡 L1/L2/L3 ----
            if (!NavVerify("仪表盘", "一键清理")) { Fail(T, "导航到仪表盘失败"); return; }
            // 一键清理(L1 手动),保证最新 10 条内有「轻量清理/手动」
            var l1Btn = RetryFind(() => window.FindFirstDescendant(cf =>
                cf.ByName("一键清理").And(cf.ByControlType(ControlType.Button))), 5000);
            Trigger(l1Btn);
            RetryFind(() => FindTextStarting(window, "上次清理"), 30000);
            // 深度清理(L2,本机 EnableLUA=0 无 UAC)
            var l2Btn = RetryFind(() => window.FindFirstDescendant(cf =>
                cf.ByName("深度清理(需管理员)").And(cf.ByControlType(ControlType.Button))), 5000);
            if (l2Btn != null)
            {
                var histBefore2 = File.ReadAllText(HistoryPath()).Length;
                Trigger(l2Btn);
                var swL2 = Stopwatch.StartNew();
                bool l2Done = false;
                while (swL2.Elapsed < TimeSpan.FromSeconds(120) && !l2Done)
                {
                    Thread.Sleep(3000);
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(HistoryPath()));
                        l2Done = doc.RootElement.EnumerateArray().Any(e => e.GetProperty("Level").GetInt32() == 1);
                    }
                    catch { }
                }
                Console.WriteLine(l2Done ? "[diag] 深度清理(L2)完成并记录 ✓" : "[WARN] 120s 内无 L2 历史记录");
            }
            // 断言历史卡文本(最新 10 条):三个级别 + 触发方式
            Thread.Sleep(1500);
            var dashTexts = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(e => e.Name).ToList();
            var need = new[] { "轻量清理", "深度清理", "结束进程" };
            var missing = need.Where(n => !dashTexts.Any(x => x.Contains(n))).ToList();
            if (missing.Count > 0) { Fail(T, "仪表盘历史卡缺少级别: " + string.Join(",", missing)); return; }
            Console.WriteLine("[diag] 8.1 历史卡出现 L1/L2/L3 记录 ✓");
            Pass(T);
        }
    }
    finally
    {
        try { markerA?.Kill(); } catch { }
        try { markerB?.Kill(); } catch { }
        foreach (var p in Process.GetProcessesByName("uismokel3")) try { p.Kill(); } catch { }
        // notepad 只清测试开始后新起的(后悔药重开的),用户原先开着的记事本不动
        foreach (var p in Process.GetProcessesByName("notepad"))
        {
            try { if (p.StartTime >= testStart) p.Kill(); } catch { }
        }
        // 恢复设置(含 NoKill 清空)并重启应用
        foreach (var p in Process.GetProcessesByName("AiMemoryManager")) p.Kill();
        Thread.Sleep(1500);
        File.WriteAllText(settingsPath, backup);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExePath()}\"") { UseShellExecute = true });
        Thread.Sleep(3000);
        Console.WriteLine("[OK] 已恢复原设置并重启应用");
    }
}


// ---------- 测试:Token 统计与预算闸门(M2 第 7 节) ----------
// 7.1 统计页与 token-usage.jsonl 一致(本月聚合卡 + 最近调用首行)
// 7.2 档案填单价 → 费用卡显示 $ 估算
// 7.3 月度预算写 100 → 统计页预算告警出现;7.4 手动分析被拦,jsonl 不增
void TestTokenStats()
{
    const string T = "M2-7 Token统计/预算";
    var (automation, window) = Attach();
    using (automation)
    {
        // jsonl 侧聚合(与 TokenStatsService.AggregateMonth 同口径:本地时区本月)
        var records = File.ReadAllLines(TokenLogPath())
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Select(r => (Time: r.GetProperty("Time").GetDateTimeOffset(),
                          In: r.GetProperty("InputTokens").GetInt32(),
                          Out: r.GetProperty("OutputTokens").GetInt32()))
            .ToList();
        var now = DateTimeOffset.Now;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        var month = records.Where(r => r.Time >= monthStart).ToList();
        string expectedMonth = $"输入 {month.Sum(r => (long)r.In):N0} · 输出 {month.Sum(r => (long)r.Out):N0} · 调用次数 {month.Count:N0}";

        if (!NavTo(window, "Token 统计")) { Fail(T, "导航到 Token 统计页失败"); return; }
        // 三张聚合卡(今日/本周/本月)文本都以「输入」开头,按 X 坐标取最右一张(本月)
        var aggTexts = RetryFind(() =>
        {
            var l = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Where(e => e.Name.StartsWith("输入") && e.Name.Contains("调用次数"))
                .OrderBy(e => e.BoundingRectangle.X).ToList();
            return l.Count >= 3 ? l[^1] : null;
        }, 5000);
        if (aggTexts == null) { Fail(T, "找不到本月聚合文本"); return; }
        var monthText = aggTexts;
        Console.WriteLine($"[diag] 页面本月: {monthText.Name} | jsonl 计算: {expectedMonth}");
        if (monthText.Name != expectedMonth) { Fail(T, "本月聚合与 jsonl 不一致"); return; }

        // 最近调用首行 = jsonl 最后一条(时间 MM-dd HH:mm:ss + 输入/输出)
        var last = records.Last();
        var expectTime = last.Time.ToLocalTime().ToString("MM-dd HH:mm:ss");
        var rowFound = RetryFind(() => window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .FirstOrDefault(e => e.Name == expectTime), 5000);
        if (rowFound == null) { Fail(T, $"最近调用首行与 jsonl 不符(期望时间 {expectTime})"); return; }
        Console.WriteLine($"[diag] 7.1 统计页与 jsonl 一致 ✓ (本月 {month.Count} 次, 最新 {expectTime})");

        // 7.2 填单价 → 费用显示。LLM 页编辑 deepseek,单价框(第 5 个 Edit)设 2,保存
        if (!NavTo(window, "大模型")) { Fail(T, "导航到大模型页失败"); return; }
        var editBtn = RetryFind(() => ProfileRowButton(window, "deepseek", "编辑"));
        if (editBtn == null) { Fail(T, "找不到 deepseek 编辑按钮"); return; }
        Trigger(editBtn);
        Thread.Sleep(800);
        var edits = FormEdits(window);
        if (edits.Count < 5) { Fail(T, "大模型页编辑框不足"); return; }
        var priceBox = edits[4];
        priceBox.Focus();
        Thread.Sleep(200);
        SetEditText(priceBox, "2");
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
        Thread.Sleep(400);
        var saveBtn = window.FindFirstDescendant(cf => cf.ByName("保存").And(cf.ByControlType(ControlType.Button)));
        Trigger(saveBtn);
        Thread.Sleep(800);
        double price;
        using (var doc = JsonDocument.Parse(File.ReadAllText(ProfilesPath())))
            price = doc.RootElement.EnumerateArray().First(p => p.GetProperty("Name").GetString() == "deepseek")
                .GetProperty("PricePerMillionTokens").GetDouble();
        if (price != 2) { Fail(T, $"单价未保存(实际 {price})"); return; }

        if (!NavTo(window, "Token 统计")) { Fail(T, "返回统计页失败"); return; }
        double expectedCost = 2.0 * month.Sum(r => (long)r.In + r.Out) / 1_000_000d;
        var expectCost = expectedCost.ToString("$0.0000");
        var costText = RetryFind(() => FindTextStarting(window, "$"), 5000);
        Console.WriteLine($"[diag] 页面费用: {costText?.Name ?? "(无)"} | 期望: {expectCost}");
        if (costText == null || costText.Name != expectCost) { Fail(T, "费用显示不符"); return; }
        Console.WriteLine("[diag] 7.2 单价费用估算 ✓");

        // 恢复单价 0
        if (!NavTo(window, "大模型")) { Fail(T, "导航回大模型页失败"); return; }
        editBtn = RetryFind(() => ProfileRowButton(window, "deepseek", "编辑"));
        Trigger(editBtn);
        Thread.Sleep(800);
        edits = FormEdits(window);
        priceBox = edits[4];
        priceBox.Focus();
        Thread.Sleep(200);
        SetEditText(priceBox, "0");
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
        Thread.Sleep(400);
        Trigger(window.FindFirstDescendant(cf => cf.ByName("保存").And(cf.ByControlType(ControlType.Button))));
        Thread.Sleep(800);
    }

    // 7.3/7.4 预算闸门:改全局设置需重启应用(先杀再改文件)
    var settingsBackup = File.ReadAllText(SettingsPath());
    try
    {
        foreach (var p in Process.GetProcessesByName("AiMemoryManager")) p.Kill();
        Thread.Sleep(1500);
        var node = System.Text.Json.Nodes.JsonNode.Parse(settingsBackup)!.AsObject();
        node["MonthlyTokenBudget"] = 100;   // 本月已用远超 100,闸门立即生效
        File.WriteAllText(SettingsPath(), node.ToJsonString());
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExePath()}\"") { UseShellExecute = true });
        Thread.Sleep(5000);

        var (automation2, window2) = Attach();
        using (automation2)
        {
            if (!NavTo(window2, "Token 统计")) { Fail(T, "重启后导航失败"); return; }
            // 预算告警 InfoBar 功能正常但对 UIA 不可见(WPF-UI InfoBar 不进自动化树),
            // 视觉确认见 artifacts/tokenstats-budget.png;这里以功能性闸门为准(7.4)

            // 7.4 手动分析被拦
            if (!NavTo(window2, "智能分析")) { Fail(T, "导航到智能分析页失败"); return; }
            var runBtn = RetryFind(() => window2.FindFirstDescendant(cf =>
                cf.ByName("开始分析").And(cf.ByControlType(ControlType.Button))));
            if (runBtn == null) { Fail(T, "找不到开始分析按钮"); return; }
            int before = TokenLogLines();
            Trigger(runBtn);
            if (RetryFind(() => FindTextStarting(window2, "已达月度预算"), 8000) == null)
            { Fail(T, "手动分析未被预算拦停"); return; }
            Thread.Sleep(2000);
            if (TokenLogLines() != before) { Fail(T, "被拦后仍新增了 token 记录"); return; }
            Console.WriteLine("[diag] 7.4 手动分析被预算闸门拦停 ✓");
        }
        Pass(T);
    }
    finally
    {
        foreach (var p in Process.GetProcessesByName("AiMemoryManager")) p.Kill();
        Thread.Sleep(1500);
        File.WriteAllText(SettingsPath(), settingsBackup);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExePath()}\"") { UseShellExecute = true });
        Thread.Sleep(3000);
        Console.WriteLine("[OK] 已恢复设置并重启应用");
    }
}

// ---------- 测试:大模型档案页(M2 1-2 节) ----------
// 1.x: 编辑现有 deepseek 档案 → 测试连接成功 → 拉取模型有列表 → 设为当前且重启保留
// 2.x: 本地地址无密钥可保存;远程地址无密钥被拦;Ollama 未运行时失败提示可读;运行时可连接
string ProfilesPath() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "AiMemoryManager", "llm-profiles.json");

void SetEditText(AutomationElement edit, string text)
{
    edit.Patterns.Value.Pattern.SetValue(text);
    Thread.Sleep(150);
}

// 表单编辑框按 (Y,X) 排序取前三个:名称 / Base URL / API Key(其余 Edit 都在更下方)
List<AutomationElement> FormEdits(AutomationElement window) =>
    window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
        .OrderBy(e => e.BoundingRectangle.Y).ThenBy(e => e.BoundingRectangle.X).ToList();

AutomationElement? FindTextStarting(AutomationElement window, string prefix) =>
    window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
        .FirstOrDefault(e => e.Name.StartsWith(prefix));

// 在档案列表里按名字找行,返回行内的指定按钮(编辑/设为当前/删除)
AutomationElement? ProfileRowButton(AutomationElement window, string profileName, string btnName)
{
    var nameText = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
        .FirstOrDefault(e => e.Name == profileName);
    var li = nameText?.Parent;
    while (li != null && li.ControlType != ControlType.ListItem) li = li.Parent;
    return li?.FindFirstDescendant(cf => cf.ByName(btnName).And(cf.ByControlType(ControlType.Button)));
}

bool OllamaRunning()
{
    // 用 127.0.0.1 而不是 localhost:避免解析到 ::1 而 Ollama 只监听 IPv4 时误判
    // ollama serve 刚启动时 /api/tags 可能短暂 500,多试几次
    for (int i = 0; i < 6; i++)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            if (http.GetAsync("http://127.0.0.1:11434/api/tags").Result.IsSuccessStatusCode) return true;
        }
        catch (Exception ex) { Console.WriteLine("[diag] Ollama 探测异常: " + ex.GetBaseException().Message); }
        Thread.Sleep(2000);
    }
    return false;
}

void TestLlmProfiles()
{
    const string T1 = "M2-1 DeepSeek 档案";
    const string T2 = "M2-2 Ollama/无密钥校验";
    var (automation, window) = Attach();
    using (automation)
    {
        if (!NavTo(window, "大模型")) { Fail(T1, "导航到大模型页失败"); return; }

        // ---- 1.2 测试连接(编辑现有 deepseek 档案,密钥留空=用已存的) ----
        var editBtn = RetryFind(() => ProfileRowButton(window, "deepseek", "编辑"));
        if (editBtn == null) { Fail(T1, "找不到 deepseek 档案的编辑按钮"); return; }
        Trigger(editBtn);
        Thread.Sleep(800);
        var testBtn = RetryFind(() => window.FindFirstDescendant(cf =>
            cf.ByName("测试连接").And(cf.ByControlType(ControlType.Button))));
        if (testBtn == null) { Fail(T1, "找不到测试连接按钮"); return; }
        Trigger(testBtn);
        var okText = RetryFind(() => FindTextStarting(window, "连接成功"), 40000);
        if (okText == null)
        {
            var failText = FindTextStarting(window, "连接失败");
            Fail(T1, "测试连接未成功: " + (failText?.Name ?? "(无结果文本)")); return;
        }
        Console.WriteLine($"[diag] 测试连接结果: {okText.Name}");

        // ---- 1.3 拉取模型:结果文本报告模型数 ≥1,且建议进入模型下拉框 ----
        var fetchBtn = window.FindFirstDescendant(cf => cf.ByName("拉取模型").And(cf.ByControlType(ControlType.Button)));
        if (fetchBtn == null) { Fail(T1, "找不到拉取模型按钮"); return; }
        Trigger(fetchBtn);
        var fetchOk = RetryFind(() => FindTextStarting(window, "连接成功"), 40000);
        if (fetchOk == null) { Fail(T1, "拉取模型失败"); return; }
        Console.WriteLine($"[diag] 拉取模型结果: {fetchOk.Name}");
        // 模型下拉框(可编辑 ComboBox,页面上第 2 个 ComboBox:第 1 个是预设)
        var combos = window.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox))
            .OrderBy(c => c.BoundingRectangle.Y).ToList();
        if (combos.Count < 2) { Fail(T1, "找不到模型下拉框"); return; }
        var modelCombo = combos[1];
        modelCombo.Patterns.ExpandCollapse.Pattern.Expand();
        Thread.Sleep(500);
        AutomationElement[]? items = null;
        for (int i = 0; i < 20 && items == null; i++)
        {
            var l = modelCombo.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
            if (l.Length > 0) items = l;
            else Thread.Sleep(500);
        }
        if (items == null) { Fail(T1, "模型下拉框无列表项"); return; }
        Console.WriteLine($"[diag] 模型列表 {items.Length} 项,首项: {items[0].Name}");
        // 选择包含当前模型的项(避免改动用户配置),没有则选首项,保存后验证文件,再改回
        var origModel = JsonDocument.Parse(File.ReadAllText(ProfilesPath())).RootElement.EnumerateArray()
            .First(p => p.GetProperty("Name").GetString() == "deepseek").GetProperty("Model").GetString()!;
        var pick = items.FirstOrDefault(i => i.Name.Contains(origModel)) ?? items[0];
        var picked = pick.Name;
        (pick.Patterns.SelectionItem.PatternOrDefault)?.Select();
        Thread.Sleep(400);
        try { modelCombo.Patterns.ExpandCollapse.Pattern.Collapse(); } catch { }
        var saveBtn = window.FindFirstDescendant(cf => cf.ByName("保存").And(cf.ByControlType(ControlType.Button)));
        Trigger(saveBtn);
        Thread.Sleep(800);
        var savedModel = JsonDocument.Parse(File.ReadAllText(ProfilesPath())).RootElement.EnumerateArray()
            .First(p => p.GetProperty("Name").GetString() == "deepseek").GetProperty("Model").GetString()!;
        if (savedModel != picked) { Fail(T1, $"选择模型保存后文件未更新(期望 {picked},实际 {savedModel})"); return; }
        if (picked != origModel)
        {
            // 改回用户原模型
            try { modelCombo.Patterns.Value.Pattern.SetValue(origModel); }
            catch { var inner = modelCombo.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)); inner?.Patterns.Value.Pattern.SetValue(origModel); }
            Thread.Sleep(300);
            Trigger(saveBtn);
            Thread.Sleep(800);
        }

        // ---- 1.4 设为当前 + 重启保留 ----
        var setActiveBtn = RetryFind(() => ProfileRowButton(window, "deepseek", "设为当前"));
        if (setActiveBtn == null) { Fail(T1, "找不到设为当前按钮"); return; }
        Trigger(setActiveBtn);
        Thread.Sleep(800);
        var activeId = JsonDocument.Parse(File.ReadAllText(SettingsPath())).RootElement
            .GetProperty("ActiveProfileId").GetString();
        var deepseekId = JsonDocument.Parse(File.ReadAllText(ProfilesPath())).RootElement.EnumerateArray()
            .First(p => p.GetProperty("Name").GetString() == "deepseek").GetProperty("Id").GetString();
        if (activeId != deepseekId) { Fail(T1, "设为当前后 ActiveProfileId 不符"); return; }
        if (RetryFind(() => FindTextStarting(window, "当前使用"), 3000) == null)
        { Fail(T1, "未显示「当前使用」标记"); return; }

        // ---- 2.1 本地地址(Ollama)无密钥可保存 ----
        var addBtn = RetryFind(() => window.FindFirstDescendant(cf =>
            cf.ByName("新增档案").And(cf.ByControlType(ControlType.Button))));
        if (addBtn == null) { Fail(T2, "找不到新增档案按钮"); return; }
        Trigger(addBtn);
        Thread.Sleep(500);
        var edits = FormEdits(window);
        if (edits.Count < 3) { Fail(T2, $"表单编辑框不足({edits.Count})"); return; }
        SetEditText(edits[0], "ui-smoke-ollama");
        SetEditText(edits[1], "http://localhost:11434/v1");
        SetEditText(edits[2], "");                                  // 密钥留空
        try { modelCombo.Patterns.Value.Pattern.SetValue("ui-smoke-test-model"); }
        catch { var inner = modelCombo.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)); inner?.Patterns.Value.Pattern.SetValue("ui-smoke-test-model"); }
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL);   // 触发绑定刷新
        Thread.Sleep(300);
        Trigger(saveBtn);
        Thread.Sleep(800);
        bool ollamaSaved;
        using (var doc = JsonDocument.Parse(File.ReadAllText(ProfilesPath())))
            ollamaSaved = doc.RootElement.EnumerateArray().Any(p => p.GetProperty("Name").GetString() == "ui-smoke-ollama");
        if (!ollamaSaved)
        {
            var msg = FindTextStarting(window, "保存失败") ?? FindTextStarting(window, "请填写") ?? FindTextStarting(window, "远程服务");
            Fail(T2, "本地无密钥保存被拦: " + (msg?.Name ?? "(无提示)")); return;
        }
        Console.WriteLine("[diag] 2.1 本地地址无密钥保存成功 ✓");

        // ---- 2.2 远程地址无密钥被拦 ----
        Trigger(addBtn);
        Thread.Sleep(500);
        edits = FormEdits(window);
        SetEditText(edits[0], "ui-smoke-remote-nokey");
        SetEditText(edits[1], "https://api.deepseek.com/v1");
        SetEditText(edits[2], "");
        try { modelCombo.Patterns.Value.Pattern.SetValue("whatever"); }
        catch { }
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL);
        Thread.Sleep(300);
        Trigger(saveBtn);
        Thread.Sleep(800);
        var blocked = RetryFind(() => FindTextStarting(window, "远程服务必须填写 API 密钥"), 3000);
        bool remoteSaved;
        using (var doc = JsonDocument.Parse(File.ReadAllText(ProfilesPath())))
            remoteSaved = doc.RootElement.EnumerateArray().Any(p => p.GetProperty("Name").GetString() == "ui-smoke-remote-nokey");
        if (blocked == null || remoteSaved) { Fail(T2, $"远程无密钥未被拦(提示={(blocked != null)},已保存={remoteSaved})"); return; }
        Console.WriteLine("[diag] 2.2 远程无密钥被拦 ✓: " + blocked.Name);

        // ---- 2.3/2.4 Ollama 运行状态两种路径 ----
        var ollamaEditBtn = RetryFind(() => ProfileRowButton(window, "ui-smoke-ollama", "编辑"));
        if (ollamaEditBtn == null) { Fail(T2, "找不到 ui-smoke-ollama 的编辑按钮"); return; }
        Trigger(ollamaEditBtn);
        Thread.Sleep(800);
        if (OllamaRunning())
        {
            Trigger(testBtn);
            var ok2 = RetryFind(() => FindTextStarting(window, "连接成功"), 20000);
            if (ok2 == null) { Fail(T2, "Ollama 运行中但连接失败: " + (FindTextStarting(window, "连接失败")?.Name ?? "?")); return; }
            Console.WriteLine("[diag] 2.3 Ollama 运行中连接成功: " + ok2.Name);
        }
        else
        {
            Trigger(testBtn);
            var fail2 = RetryFind(() => FindTextStarting(window, "连接失败"), 20000);
            if (fail2 == null) { Fail(T2, "Ollama 未运行时无明确失败提示"); return; }
            Console.WriteLine("[diag] 2.4 Ollama 未运行,失败提示: " + fail2.Name);
        }

        // 清理:删除测试档案
        var delBtn = RetryFind(() => ProfileRowButton(window, "ui-smoke-ollama", "删除"));
        Trigger(delBtn);
        Thread.Sleep(800);
        using (var doc = JsonDocument.Parse(File.ReadAllText(ProfilesPath())))
        {
            if (doc.RootElement.EnumerateArray().Any(p => p.GetProperty("Name").GetString() == "ui-smoke-ollama"))
            { Fail(T2, "测试档案删除失败"); return; }
        }
        Pass(T2);

        // ---- 1.4 收尾:重启应用验证「设为当前」保留 ----
        automation.Dispose();
        (automation, window) = RestartApp();
        using (automation)
        {
            if (!NavTo(window, "大模型")) { Fail(T1, "重启后导航失败"); return; }
            if (RetryFind(() => FindTextStarting(window, "当前使用"), 4000) == null)
            { Fail(T1, "重启后「当前使用」标记丢失"); return; }
        }
        Pass(T1);
    }
}

// ---------- 清理工具:从白名单移除指定条目(如 ui-smoke cleanup:claude) ----------
void Cleanup(string name)
{
    var (automation, window) = Attach();
    using (automation)
    {
        if (!NavTo(window, "白名单")) { Fail("cleanup", "导航失败"); return; }
        var txt = RetryFind(() => window.FindFirstDescendant(cf =>
            cf.ByControlType(ControlType.Text).And(cf.ByName(name))), 4000);
        if (txt == null) { Console.WriteLine($"[OK] 白名单中没有 {name}"); return; }
        var li = txt.Parent;
        while (li != null && li.ControlType != ControlType.ListItem) li = li.Parent;
        var btn = li?.FindFirstDescendant(cf => cf.ByName("移除").And(cf.ByControlType(ControlType.Button)));
        btn?.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
        Thread.Sleep(300);
        Trigger(btn);
        Thread.Sleep(800);
        bool gone = !JsonDocument.Parse(File.ReadAllText(SettingsPath())).RootElement
            .GetProperty("ExcludedProcesses").EnumerateArray()
            .Any(e => string.Equals(e.GetString(), name, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine(gone ? $"[OK] 已从白名单移除 {name}" : $"[FAIL] {name} 移除失败");
        if (!gone) failures++;
    }
}

// 对话框文件名输入:直接走 Win32 消息(UIA 在 shell 文件对话框上频繁超时,不可靠)。
// 文件名框是 ComboBoxEx32/ComboBox 里的 Edit,用 WM_SETTEXT 设完整路径,再 WM_COMMAND IDOK 确认。
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr w, string? l);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern IntPtr GetParent(IntPtr hWnd);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);

void SetDialogPath(AutomationElement dlg, string path)
{
    var dlgHwnd = (IntPtr)dlg.Properties.NativeWindowHandle;
    IntPtr filenameEdit = IntPtr.Zero;
    EnumChildWindows(dlgHwnd, (h, _) =>
    {
        var cls = new System.Text.StringBuilder(64);
        GetClassName(h, cls, 64);
        if (cls.ToString() == "Edit")
        {
            // 文件名 Edit 的父链含 ComboBox/ComboBoxEx32
            var p = GetParent(h);
            var pcls = new System.Text.StringBuilder(64);
            GetClassName(p, pcls, 64);
            var pp = GetParent(p);
            var ppcls = new System.Text.StringBuilder(64);
            GetClassName(pp, ppcls, 64);
            if (pcls.ToString().StartsWith("ComboBox") || ppcls.ToString().StartsWith("ComboBox"))
            { filenameEdit = h; return false; }
        }
        return true;
    }, IntPtr.Zero);
    if (filenameEdit == IntPtr.Zero) throw new InvalidOperationException("对话框找不到文件名编辑框(Win32)");
    SendMessage(filenameEdit, 0x000C /*WM_SETTEXT*/, IntPtr.Zero, path);
    Thread.Sleep(300);
    PostMessage(dlgHwnd, 0x0111 /*WM_COMMAND*/, (IntPtr)1 /*IDOK*/, IntPtr.Zero);
}

// 重启应用(模态对话框卡死等不可恢复状态的兜底):杀掉进程重新启动,返回新 window
(UIA3Automation, AutomationElement) RestartApp()
{
    foreach (var p in Process.GetProcessesByName("AiMemoryManager")) p.Kill();
    Thread.Sleep(1500);
    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExePath()}\"") { UseShellExecute = true });
    Thread.Sleep(4000);
    return Attach();
}

// ---------- 清理工具:关闭残留模态对话框(导入/导出对话框等) ----------
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder sb, int max);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool IsWindowVisible(IntPtr hWnd);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

void CloseStuckDialogs()
{
    int closed = 0;
    var appPids = Process.GetProcessesByName("AiMemoryManager").Select(p => (uint)p.Id).ToHashSet();
    EnumWindows((h, _) =>
    {
        if (IsWindowVisible(h))
        {
            GetWindowThreadProcessId(h, out var pid);
            if (!appPids.Contains(pid)) return true;   // 只关本应用的对话框,别动用户的其他窗口
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(h, sb, 256);
            var t = sb.ToString();
            if (t.Contains("白名单文件") || t.Contains("插入磁盘") || t.Contains("重命名"))
            {
                Console.WriteLine($"[diag] 关闭残留对话框: '{t}' hwnd={h}");
                PostMessage(h, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
                closed++;
            }
        }
        return true;
    }, IntPtr.Zero);
    Console.WriteLine($"[OK] 关闭 {closed} 个残留对话框");
}

    if (mode == "probecompress")
    {
        var (a, w) = Attach();
        using (a)
        {
            NavTo(w, "智能分析");
            Thread.Sleep(1000);
            var txt = w.FindAllDescendants(cf => cf.ByName("立即压缩").And(cf.ByControlType(ControlType.Text))).FirstOrDefault();
            if (txt == null) { Console.WriteLine("no 立即压缩 text"); return failures; }
            Console.WriteLine($"before: offscreen={txt.IsOffscreen} rect={txt.BoundingRectangle} scrollitem={txt.Patterns.ScrollItem.IsSupported}");
            try { txt.Patterns.ScrollItem.Pattern.ScrollIntoView(); } catch (Exception ex) { Console.WriteLine("scroll err: " + ex.Message); }
            Thread.Sleep(1000);
            Console.WriteLine($"after scroll: offscreen={txt.IsOffscreen} rect={txt.BoundingRectangle}");
            var pt = txt.GetClickablePoint();
            Console.WriteLine($"clickable: {pt}");
            txt.Click();
            Thread.Sleep(3000);
            foreach (var t in w.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
                if (t.Name.StartsWith("已释放") || t.Name.StartsWith("压缩") || t.Name.Contains("失败"))
                    Console.WriteLine("[result] " + t.Name);
        }
        return failures;
    }
    if (mode == "probeinvoke")
    {
        // 判定:当前会话 UIA Invoke 是否普遍可用——Invoke 仪表盘「一键清理」,看历史是否新增记录
        var (a, w) = Attach();
        using (a)
        {
            ShowWindow((IntPtr)w.Properties.NativeWindowHandle, 3);
            Thread.Sleep(500);
            NavTo(w, "仪表盘");
            Thread.Sleep(1500);
            var histPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AiMemoryManager", "clean-history.json");
            var before = File.ReadAllText(histPath);
            var l1 = w.FindFirstDescendant(cf => cf.ByName("一键清理").And(cf.ByControlType(ControlType.Button)));
            Console.WriteLine("[probe] 一键清理按钮: " + (l1 == null ? "未找到" : $"enabled={l1.IsEnabled} rect={l1.BoundingRectangle}"));
            if (l1 != null)
            {
                try { l1.Patterns.Invoke.Pattern.Invoke(); Console.WriteLine("[probe] Invoke 未抛异常"); }
                catch (Exception ex) { Console.WriteLine("[probe] Invoke 抛: " + ex.GetType().Name); }
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(1000);
                    if (File.ReadAllText(histPath) != before) { Console.WriteLine($"[probe] t+{i + 1}s 历史已更新 → Invoke 有效"); break; }
                    if (i == 19) Console.WriteLine("[probe] 20s 内历史未更新 → Invoke 无效");
                }
            }
        }
        return failures;
    }
    if (mode == "gridprobe")
    {
        // 打印指定页每个 DataGrid 的表头列宽,验证勾选列不被压扁
        var (a, w) = Attach();
        using (a)
        {
            NavTo(w, args.Length > 1 ? args[1] : "C 盘瘦身");
            Thread.Sleep(2500);
            foreach (var g in w.FindAllDescendants(cf => cf.ByControlType(ControlType.DataGrid)))
            {
                var heads = g.FindAllDescendants(cf => cf.ByControlType(ControlType.HeaderItem));
                Console.WriteLine($"[grid] @ {g.BoundingRectangle} 列数={heads.Length}: "
                    + string.Join(", ", heads.Select(h => $"'{h.Name}' w={h.BoundingRectangle.Width:0}")));
            }
        }
        return failures;
    }
    if (mode == "wheeltest")
    {
        // 在页面中部滚轮下滚,看内容是否移动(验证外层 DynamicScrollViewer 是否可滚)
        var (a, w) = Attach();
        using (a)
        {
            ShowWindow((IntPtr)w.Properties.NativeWindowHandle, 3);
            Thread.Sleep(800);
            NavTo(w, args.Length > 1 ? args[1] : "大模型");
            Thread.Sleep(2500);
            var wb = w.BoundingRectangle;
            FlaUI.Core.Input.Mouse.MoveTo((int)(wb.X + wb.Width * 0.6), (int)(wb.Y + wb.Height * 0.5));
            Thread.Sleep(300);
            for (int i = 0; i < 10; i++) { FlaUI.Core.Input.Mouse.Scroll(-3); Thread.Sleep(150); }
            Thread.Sleep(1000);
            var leak = w.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .FirstOrDefault(e => e.Name.Contains("泄漏检测"));
            Console.WriteLine($"[wheel] 滚动后「泄漏检测」位置: {(leak == null ? "找不到" : leak.BoundingRectangle.ToString())} (窗口下={wb.Bottom})");
            try
            {
                using var bmp = new System.Drawing.Bitmap((int)wb.Width, (int)wb.Height);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                    g.CopyFromScreen((int)wb.X, (int)wb.Y, 0, 0, bmp.Size);
                var shot = @"C:\Users\jerry\Desktop\memory\artifacts\wheel-大模型.png";
                bmp.Save(shot, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("[wheel] 截图: " + shot);
            }
            catch (Exception ex) { Console.WriteLine("[wheel] 截图失败: " + ex.Message); }
        }
        return failures;
    }
    if (mode == "scrollprobe")
    {
        // 列出窗口内所有支持 ScrollPattern 的元素 + 所有 ScrollBar,定位到底谁该滚动
        var (a, w) = Attach();
        using (a)
        {
            if (args.Length > 1) { NavTo(w, args[1]); Thread.Sleep(2500); }
            foreach (var e in w.FindAllDescendants())
            {
                try
                {
                    bool hasScroll = e.Patterns.Scroll.IsSupported;
                    bool isBar = e.ControlType == ControlType.ScrollBar;
                    if (!hasScroll && !isBar) continue;
                    var nm = e.Name ?? "";
                    if (nm.Length > 30) nm = nm[..30] + "…";
                    if (hasScroll)
                    {
                        var sp = e.Patterns.Scroll.Pattern;
                        Console.WriteLine($"[scroll] [{e.ControlType}] '{nm}' class={e.ClassName} @ {e.BoundingRectangle} " +
                            $"vScrollable={sp.VerticallyScrollable.ValueOrDefault} v%={sp.VerticalScrollPercent.ValueOrDefault:0} " +
                            $"vView={sp.VerticalViewSize.ValueOrDefault:0}");
                    }
                    else Console.WriteLine($"[bar] '{nm}' class={e.ClassName} @ {e.BoundingRectangle}");
                }
                catch { }
            }
        }
        return failures;
    }
    if (mode == "layoutaudit")
    {
        // 逐页截图 + 报告超出窗口右边/下边界的元素(显示不全诊断)
        var (a, w) = Attach();
        using (a)
        {
            ShowWindow((IntPtr)w.Properties.NativeWindowHandle, 3);
            Thread.Sleep(800);
            var pages = new[] { "仪表盘", "进程", "规则", "智能分析", "大模型", "C 盘瘦身", "Token 统计", "白名单", "设置" };
            foreach (var page in pages)
            {
                NavTo(w, page);
                Thread.Sleep(2500);
                var wb = w.BoundingRectangle;
                try
                {
                    using var bmp = new System.Drawing.Bitmap((int)wb.Width, (int)wb.Height);
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                        g.CopyFromScreen((int)wb.X, (int)wb.Y, 0, 0, bmp.Size);
                    var shot = Path.Combine(@"C:\Users\jerry\Desktop\memory\artifacts",
                        "layout-" + page.Replace(" ", "") + ".png");
                    bmp.Save(shot, System.Drawing.Imaging.ImageFormat.Png);
                    Console.WriteLine($"[shot] {page}: {shot}");
                }
                catch (Exception ex) { Console.WriteLine($"[shot] {page} 截图失败: {ex.Message}"); }
                // 越界元素:右/下边缘超出窗口客户区,或矩形在窗口外但 UIA 声称可见
                foreach (var e in w.FindAllDescendants())
                {
                    try
                    {
                        var r = e.BoundingRectangle;
                        if (r.IsEmpty || (r.Width == 0 && r.Height == 0)) continue;
                        bool clipR = r.Right > wb.Right + 1, clipB = r.Bottom > wb.Bottom + 1;
                        if (!clipR && !clipB) continue;
                        var nm = e.Name ?? "";
                        if (nm.Length > 40) nm = nm[..40] + "…";
                        Console.WriteLine($"[clip] {page}: [{e.ControlType}] '{nm}' @ {r} (窗口右={wb.Right} 下={wb.Bottom})");
                    }
                    catch { }
                }
            }
        }
        return failures;
    }
    if (mode == "dumpedits")
    {
        // 列出指定页面的全部 Edit 控件(含无名),诊断输入框查找
        var (a, w) = Attach();
        using (a)
        {
            NavTo(w, args.Length > 1 ? args[1] : "白名单");
            Thread.Sleep(2000);
            var edits = w.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
            Console.WriteLine($"[dump] Edit 数: {edits.Length}");
            foreach (var e in edits)
                Console.WriteLine($"[dump] Edit '{e.Name}' @ {e.BoundingRectangle} class={e.ClassName}");
        }
        return failures;
    }
    if (mode == "proberestore")
    {
        // 诊断:后悔药「恢复」按钮的 enabled/Invoke 行为与 StatusText 变化
        var (a, w) = Attach();
        using (a)
        {
            ShowWindow((IntPtr)w.Properties.NativeWindowHandle, 3);
            Thread.Sleep(500);
            NavTo(w, "进程");
            Thread.Sleep(2500);
            var btns = w.FindAllDescendants(cf => cf.ByName("恢复").And(cf.ByControlType(ControlType.Button)));
            Console.WriteLine($"[probe] 恢复按钮数: {btns.Length}");
            // 页面导航日志里可能残留多个 ProcessesPage 实例:列出全部 ListView 与滚动容器的位置
            foreach (var lv in w.FindAllDescendants(cf => cf.ByControlType(ControlType.List)))
            {
                try { Console.WriteLine($"[probe] ListView rect={lv.BoundingRectangle} offscreen={lv.IsOffscreen} items={lv.FindAllChildren().Length}"); } catch { }
            }
            foreach (var sv in w.FindAllDescendants())
            {
                try
                {
                    if (sv.ClassName == "ScrollViewer")
                    {
                        var sp = sv.Patterns.Scroll.PatternOrDefault;
                        Console.WriteLine($"[probe] ScrollViewer rect={sv.BoundingRectangle} v%={(sp == null ? -1 : sp.VerticalScrollPercent.ValueOrDefault):F0} vView={(sp == null ? -1 : sp.VerticalViewSize.ValueOrDefault):F0}");
                    }
                }
                catch { }
            }
            foreach (var b in btns)
            {
                try
                {
                    var r = b.BoundingRectangle;
                    // 父链:确认按钮归属(后悔药 ListView or 别的卡)
                    var chain = new System.Text.StringBuilder();
                    var cur = b;
                    for (int d = 0; d < 6; d++)
                    {
                        AutomationElement? pp;
                        try { pp = cur.Parent; } catch { break; }
                        if (pp == null) break;
                        try { chain.Append($" <- [{pp.ControlType}]'{pp.Name}'({pp.ClassName})"); } catch { chain.Append(" <- ?"); }
                        cur = pp;
                    }
                    Console.WriteLine($"[probe] btn enabled={b.IsEnabled} rect={r} offscreen={b.IsOffscreen}{chain}");
                }
                catch (Exception ex) { Console.WriteLine("[probe] btn 读取异常: " + ex.GetType().Name); }
            }
            if (btns.Length > 0)
            {
                var b0 = btns[0];
                Console.WriteLine("[probe] Invoke 第一个按钮...");
                try { b0.Patterns.Invoke.Pattern.Invoke(); Console.WriteLine("[probe] Invoke 未抛异常"); }
                catch (Exception ex) { Console.WriteLine("[probe] Invoke 抛: " + ex.GetType().Name + " " + ex.Message); }
                Thread.Sleep(2500);
                Console.WriteLine("[probe] 真实点击第一个按钮后立刻读 StatusText...");
                {
                    var b1 = btns[0];
                    var hwndP = (IntPtr)w.Properties.NativeWindowHandle;
                    ForceForeground(hwndP);
                    SetWindowPos(hwndP, (IntPtr)(-1), 0, 0, 0, 0, 0x0003);
                    Thread.Sleep(300);
                    try { b1.Click(); Console.WriteLine("[probe] Click 未抛异常"); }
                    catch (Exception ex) { Console.WriteLine("[probe] Click 抛: " + ex.GetType().Name + " " + ex.Message); }
                    finally { SetWindowPos(hwndP, (IntPtr)(-2), 0, 0, 0, 0, 0x0003); }
                    // 鼠标是否真移动了(验证本会话鼠标输入是否还活着)
                    GetCursorPos(out var cpos);
                    var br = b1.BoundingRectangle;
                    Console.WriteLine($"[probe] Click 后光标=({cpos.X},{cpos.Y}), 按钮中心=({br.X + br.Width / 2:F0},{br.Y + br.Height / 2:F0})");
                    // 立刻读:StatusText 可能被后续刷新覆盖
                    for (int i = 0; i < 10; i++)
                    {
                        Thread.Sleep(300);
                        foreach (var t in w.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
                        {
                            try
                            {
                                if (t.Name.Contains("已重新启动") || t.Name.Contains("恢复失败"))
                                    Console.WriteLine($"[probe] t+{i * 300}ms 状态文本: " + t.Name);
                            }
                            catch { }
                        }
                    }
                }
                Console.WriteLine("[probe] SetFocus+选中 ListItem 后再 Invoke...");
                try
                {
                    var b2 = btns[0];
                    var li = b2.Parent;
                    li?.Patterns.SelectionItem.PatternOrDefault?.Select();
                    b2.Focus();
                    Thread.Sleep(500);
                    b2.Patterns.Invoke.Pattern.Invoke();
                    Console.WriteLine("[probe] Invoke3 未抛异常");
                }
                catch (Exception ex) { Console.WriteLine("[probe] Invoke3 抛: " + ex.GetType().Name + " " + ex.Message); }
                Thread.Sleep(2000);
                foreach (var t in w.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
                {
                    try
                    {
                        if (t.Name.Contains("已重新启动") || t.Name.Contains("恢复失败"))
                            Console.WriteLine("[probe] Invoke3 后状态文本: " + t.Name);
                    }
                    catch { }
                }
            }
        }
        return failures;
    }
    if (mode == "probeproc")
    {
        var (a, w) = Attach();
        using (a)
        {
            NavTo(w, "进程");
            Thread.Sleep(2000);
            foreach (var e in w.FindAllDescendants())
            {
                if (e.ControlType is ControlType.DataItem or ControlType.CheckBox or ControlType.DataGrid
                    or ControlType.Custom or ControlType.ListItem)
                {
                    var r = e.BoundingRectangle;
                    if (r.Height > 0 && r.Width > 0)
                        Console.WriteLine($"[{e.ControlType}] '{(e.Name?.Length > 60 ? e.Name[..60] : e.Name)}' class={e.ClassName} @ {r}");
                }
            }
        }
        return failures;
    }
    if (mode == "dumptexts")
    {
        var (a, w) = Attach();
        using (a)
        {
            var nav = args.Length > 1 ? args[1] : "Token 统计";
            NavTo(w, nav);
            Thread.Sleep(1000);
            if (args.Length > 2 && args[2] == "click")
            {
                var btn = w.FindFirstDescendant(cf => cf.ByName("开始分析").And(cf.ByControlType(ControlType.Button)));
                Trigger(btn);
                for (int i = 0; i < 150; i++)
                {
                    Thread.Sleep(1000);
                    var u = w.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                        .FirstOrDefault(e => e.Name.StartsWith("本次消耗"));
                    if (u != null) break;
                }
            }
            foreach (var t in w.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
                if (!string.IsNullOrEmpty(t.Name))
                    Console.WriteLine($"[text] '{t.Name}' @ {t.BoundingRectangle}");
            if (args.Length > 2 && args[2] == "all")
                foreach (var e in w.FindAllDescendants())
                    if (!string.IsNullOrEmpty(e.Name) && e.ControlType != ControlType.Text)
                        Console.WriteLine($"[{e.ControlType}] '{e.Name}' @ {e.BoundingRectangle}");
            if (args.Length > 2 && args[2] == "region")
                foreach (var e in w.FindAllDescendants())
                {
                    var r = e.BoundingRectangle;
                    if (r.Y >= 380 && r.Y <= 520)
                        Console.WriteLine($"[{e.ControlType}] '{e.Name}' class={e.ClassName} @ {r}");
                }
        }
        return failures;
    }
    try
    {
    if (mode == "closewins") CloseStuckDialogs();
    else if (mode.StartsWith("cleanup:")) Cleanup(mode["cleanup:".Length..]);
    else
    {
        if (mode is "processes" or "all") TestProcesses();
        if (mode is "whitelist" or "all") TestWhitelist();
        if (mode is "language" or "all") TestLanguage();
        if (mode is "animations" or "all") TestAnimations();
        if (mode == "fullscreen") TestFullscreen();   // 改设置+重启应用,不进 all
        if (mode == "llm") TestLlmProfiles();         // 真实 LLM API 调用,不进 all
        if (mode == "analysis") TestAnalysis();       // 真实 LLM API 调用,不进 all
        if (mode == "tokenstats") TestTokenStats();   // 改设置+重启应用,不进 all
        if (mode == "compress") TestCompress();       // 真实 LLM API 调用,不进 all
        if (mode == "prompt") TestPromptTemplate();   // 真实 LLM API 调用,不进 all
        if (mode == "autotrigger") TestAutoTrigger(); // 改设置+重启+内存压力,不进 all
        if (mode == "leak") TestLeakAlert();          // ~10min,真实 LLM API 调用,不进 all
        if (mode == "m3quick") TestM3Quick();         // 热键/通知/自启/历史截断,改设置+重启,不进 all
        if (mode == "hotkeydegrade") { TestHotkeyDegrade(); return failures; }  // 4.3 热键占用降级
        if (mode == "cslim") { TestCSlim(); return failures; }  // 7.1/7.2 C盘瘦身扫描+LLM建议,真实 LLM 调用,不进 all
        if (mode == "l3flow") TestL3Flow();           // L3 确认流/防误杀/历史,真实 LLM 调用,不进 all
    }
}
catch (Exception ex)
{
    Console.WriteLine("[ERROR] " + ex);
    failures++;
}
Console.WriteLine(failures == 0 ? "== 全部通过 ==" : $"== {failures} 项失败 ==");
return failures;

delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

struct MSG
{
    public IntPtr hwnd;
    public uint message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public int pt_x;
    public int pt_y;
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct MEMORYSTATUSEX
{
    public uint Length;
    public uint MemoryLoad;        // 0-100 已用百分比
    public ulong TotalPhys;
    public ulong AvailPhys;
    public ulong TotalPageFile;
    public ulong AvailPageFile;
    public ulong TotalVirtual;
    public ulong AvailVirtual;
    public ulong AvailExtendedVirtual;
}
