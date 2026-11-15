// ============================================================================
// MonitorControl —— 程序入口
//
//   无参数启动  -> 托盘后台应用（TrayApp）：
//     · 悬停托盘图标滚动滚轮 -> 调节亮度
//     · 左键点击托盘图标     -> 亮度滑块
//     · 右键托盘图标         -> 开机自启动 / HDR / 退出
//   带参数启动  -> 命令行模式：
//     MonitorControl list                          列出所有显示器及亮度/HDR 状态
//     MonitorControl brightness <索引> <0-100>      设置指定显示器亮度
//     MonitorControl hdr <索引> <on|off>            开/关指定显示器 HDR
//     MonitorControl autostart <on|off|status>      设置/查看开机自启动
//
// 目标框架：.NET 8 (net8.0-windows)；NuGet：System.Management 8.0.0
// ============================================================================

namespace MonitorControl;

internal static class Program
{
    [STAThread] // WinForms 要求 STA 线程
    private static int Main(string[] args)
    {
        // 单实例：避免出现多个托盘图标
        using var mutex = new Mutex(true, @"Local\MonitorControl.SingleInstance", out bool firstInstance);
        if (!firstInstance)
            return 0;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash((Exception)e.ExceptionObject);

        if (args.Length == 0)
        {
            Application.Run(new TrayApp());
            return 0;
        }

        return RunCli(args);
    }

    // ---------------- 命令行模式 ----------------

    private static int RunCli(string[] args)
    {
        try
        {
            if (args[0].Equals("autostart", StringComparison.OrdinalIgnoreCase))
            {
                RequireArg(args, 1, "用法：MonitorControl autostart <on|off|status>");
                switch (args[1].ToLowerInvariant())
                {
                    case "on":
                        AutoStart.Set(true);
                        Console.WriteLine("已开启开机自启动。");
                        return 0;
                    case "off":
                        AutoStart.Set(false);
                        Console.WriteLine("已关闭开机自启动。");
                        return 0;
                    default:
                        Console.WriteLine($"开机自启动：{(AutoStart.IsEnabled() ? "已开启" : "已关闭")}");
                        return 0;
                }
            }

            var monitors = MonitorApi.EnumerateMonitors();
            if (monitors.Count == 0)
            {
                Console.WriteLine("未找到任何显示器。");
                return 1;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "list":
                    PrintMonitors(monitors);
                    return 0;

                case "brightness":
                    RequireArg(args, 2, "用法：MonitorControl brightness <索引> <0-100>");
                    {
                        var monitor = GetMonitor(monitors, args[1]);
                        MonitorApi.SetBrightness(monitor, int.Parse(args[2]));
                        int? p = MonitorApi.ReadBrightness(monitor);
                        Console.WriteLine($"已设置显示器 {monitor.Index}（{monitor.FriendlyName}）亮度 -> {(p is int v ? $"{v}%" : "未知（显示器未回报）")}");
                    }
                    return 0;

                case "hdr":
                    RequireArg(args, 2, "用法：MonitorControl hdr <索引> <on|off>");
                    {
                        var monitor = GetMonitor(monitors, args[1]);
                        bool enable = args[2] switch
                        {
                            "on" or "1" or "true" => true,
                            "off" or "0" or "false" => false,
                            _ => throw new ArgumentException("参数必须是 on 或 off"),
                        };
                        MonitorApi.SetHdr(monitor, enable);
                        Console.WriteLine($"已设置显示器 {monitor.Index}（{monitor.FriendlyName}）HDR -> {(enable ? "开" : "关")}");
                    }
                    return 0;

                default:
                    Console.WriteLine("未知命令。可用命令：list / brightness / hdr / autostart");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误：{ex.Message}");
            return 1;
        }
    }

    /// <summary>崩溃日志写到 %TEMP%，便于排查后台运行时的异常。</summary>
    private static void LogCrash(Exception ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "MonitorControl-error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { /* 日志失败时静默 */ }
    }

    private static void PrintMonitors(List<MonitorApi.MonitorInfo> monitors)
    {
        Console.WriteLine($"共 {monitors.Count} 台显示器：");
        foreach (var m in monitors)
        {
            var (supported, enabled) = MonitorApi.GetHdrState(m) ?? (false, false);
            string hdr = !supported ? "不支持" : enabled ? "开" : "关";

            string brightness = MonitorApi.ReadBrightness(m) is { } p
                ? $"{p}%"
                : "不支持（请检查显示器 OSD 中 DDC/CI 是否开启）";

            Console.WriteLine(
                $"  [{m.Index}] {m.FriendlyName}  设备={m.DeviceName}  " +
                $"分辨率={m.Bounds.right - m.Bounds.left}x{m.Bounds.bottom - m.Bounds.top}  " +
                $"亮度={brightness}  HDR={hdr}");
        }
    }

    private static void RequireArg(string[] args, int count, string usage)
    {
        if (args.Length - 1 < count) throw new ArgumentException(usage);
    }

    private static MonitorApi.MonitorInfo GetMonitor(List<MonitorApi.MonitorInfo> monitors, string indexText)
    {
        int index = int.Parse(indexText);
        return monitors.FirstOrDefault(m => m.Index == index)
            ?? throw new ArgumentException($"显示器索引 {index} 不存在，可用 0..{monitors.Count - 1}，先运行 list 查看。");
    }
}
