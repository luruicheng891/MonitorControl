// ============================================================================
// MonitorControlSetup —— 最小安装器（自解压式）
//
//   · 双击运行  -> 安装窗口：解压主程序到 %LOCALAPPDATA%\MonitorControl，
//                 创建开始菜单快捷方式，注册“应用和功能”卸载入口，
//                 可选开机自启动与安装后立即运行。
//   · /uninstall -> 卸载：结束进程、清理文件、快捷方式与注册表项。
//
//   全程只写 HKCU 与用户目录，无需管理员权限。
// ============================================================================

using Microsoft.Win32;
using System.Diagnostics;
using System.Reflection;

namespace MonitorControlSetup;

internal static class Program
{
    internal const string DisplayName = "显示器控制 (MonitorControl)";
    internal const string AppValueName = "MonitorControl"; // 与主程序 AutoStart 共用同一 Run 值

    internal static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorControl");
    internal static readonly string AppExePath = Path.Combine(InstallDir, "MonitorControl.exe");
    internal static readonly string StartMenuDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
    internal static readonly string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MonitorControl";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            Uninstall();
            return 0;
        }

        Application.Run(new SetupForm());
        return 0;
    }

    // ---------------- 安装 ----------------

    internal static void Install(bool autoStart, bool launchAfter)
    {
        // 1. 结束正在运行的实例（文件被占用会复制失败）
        StopApp();

        // 2. 解压主程序
        Directory.CreateDirectory(InstallDir);
        using (var stream = typeof(Program).Assembly.GetManifestResourceStream(
                   n => n.EndsWith("MonitorControl.exe", StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException("安装包中找不到主程序资源。"))
        using (var file = File.Create(AppExePath))
        {
            stream.CopyTo(file);
        }

        // 3. 开始菜单快捷方式
        CreateShortcut(Path.Combine(StartMenuDir, "显示器控制.lnk"), AppExePath);
        string? setupExe = Environment.ProcessPath;
        if (setupExe != null)
            CreateShortcut(Path.Combine(StartMenuDir, "卸载 显示器控制.lnk"), setupExe, "/uninstall");

        // 4. “应用和功能”卸载入口（HKCU，无需管理员）
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath))
        {
            key.SetValue("DisplayName", DisplayName);
            key.SetValue("DisplayVersion", typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
            key.SetValue("Publisher", "MonitorControl");
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("DisplayIcon", AppExePath);
            key.SetValue("EstimatedSize", (uint)(new FileInfo(AppExePath).Length / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            if (Environment.ProcessPath != null)
                key.SetValue("UninstallString", $"\"{Environment.ProcessPath}\" /uninstall");
        }

        // 5. 开机自启动（与主程序托盘菜单里的开关是同一个注册表值）
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            if (autoStart)
                key.SetValue(AppValueName, $"\"{AppExePath}\"");
            else
                key.DeleteValue(AppValueName, false);
        }

        // 6. 立即运行
        if (launchAfter)
            Process.Start(new ProcessStartInfo(AppExePath) { UseShellExecute = true });
    }

    // ---------------- 卸载 ----------------

    internal static void Uninstall()
    {
        var choice = MessageBox.Show(
            $"确定卸载 {DisplayName} 吗？", "卸载",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (choice != DialogResult.Yes)
            return;

        StopApp();

        // 开机自启动
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
        {
            key?.DeleteValue(AppValueName, false);
        }

        // “应用和功能”入口
        Registry.CurrentUser.DeleteSubKey(UninstallKeyPath, false);

        // 快捷方式
        TryDeleteFile(Path.Combine(StartMenuDir, "显示器控制.lnk"));
        TryDeleteFile(Path.Combine(StartMenuDir, "卸载 显示器控制.lnk"));

        // 程序文件
        TryDeleteFile(AppExePath);
        TryDeleteDir(InstallDir);

        MessageBox.Show("卸载完成。", "卸载", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---------------- 工具 ----------------

    private static void StopApp()
    {
        foreach (var process in Process.GetProcessesByName("MonitorControl"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch { /* 进程已退出等情况 */ }
        }
    }

    private static void CreateShortcut(string linkPath, string targetPath, string? arguments = null)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(linkPath);
        link.TargetPath = targetPath;
        link.WorkingDirectory = Path.GetDirectoryName(targetPath)!;
        link.IconLocation = $"{targetPath},0";
        if (arguments != null)
            link.Arguments = arguments;
        link.Save();
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}

internal static class ResourceExtensions
{
    public static Stream? GetManifestResourceStream(this Assembly assembly, Func<string, bool> predicate)
        => assembly.GetManifestResourceNames().FirstOrDefault(predicate) is { } name
            ? assembly.GetManifestResourceStream(name)
            : null;
}

// ============================================================================
// SetupForm —— 安装窗口
// ============================================================================

internal sealed class SetupForm : Form
{
    private readonly CheckBox _autoStart;
    private readonly CheckBox _launchAfter;
    private readonly Button _installButton;
    private readonly Button _uninstallButton;
    private readonly Label _statusLabel;

    public SetupForm()
    {
        Text = Program.DisplayName + " 安装程序";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(400, 190);

        bool installed = File.Exists(Program.AppExePath);

        Controls.Add(new Label
        {
            Text = Program.DisplayName,
            Location = new Point(16, 14),
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 12f, FontStyle.Bold),
        });
        Controls.Add(new Label
        {
            Text = $"安装位置：{Program.InstallDir}\n安装后将添加系统托盘图标，在托盘即可调节亮度 / 切换 HDR。",
            Location = new Point(16, 44),
            Size = new Size(368, 44),
        });

        _autoStart = new CheckBox
        {
            Text = "开机自启动（登录 Windows 后自动运行）",
            Location = new Point(16, 96),
            AutoSize = true,
            Checked = true,
        };
        _launchAfter = new CheckBox
        {
            Text = "安装完成后立即运行",
            Location = new Point(16, 120),
            AutoSize = true,
            Checked = true,
        };
        Controls.Add(_autoStart);
        Controls.Add(_launchAfter);

        _installButton = new Button
        {
            Text = installed ? "重新安装" : "安装",
            Location = new Point(16, 150),
            Size = new Size(120, 30),
        };
        _installButton.Click += (_, _) => DoInstall();

        _uninstallButton = new Button
        {
            Text = "卸载",
            Location = new Point(148, 150),
            Size = new Size(120, 30),
            Visible = installed,
        };
        _uninstallButton.Click += (_, _) => { Program.Uninstall(); Close(); };

        _statusLabel = new Label
        {
            Text = "",
            Location = new Point(280, 158),
            AutoSize = true,
        };

        Controls.Add(_installButton);
        Controls.Add(_uninstallButton);
        Controls.Add(_statusLabel);
    }

    private void DoInstall()
    {
        try
        {
            _installButton.Enabled = false;
            _statusLabel.Text = "正在安装…";
            Program.Install(_autoStart.Checked, _launchAfter.Checked);
            _statusLabel.Text = "安装完成。";
            MessageBox.Show("安装完成！可在系统托盘中找到显示器控制图标。", "安装", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "";
            _installButton.Enabled = true;
            MessageBox.Show(ex.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
