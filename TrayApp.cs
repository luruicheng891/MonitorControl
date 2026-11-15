// ============================================================================
// TrayApp —— 托盘后台应用（原生 Shell_NotifyIcon 实现）
//
//   · 鼠标悬停在托盘图标上滚动滚轮 -> 实时调节亮度（±5%，全部可用显示器）
//     （WinForms 的 NotifyIcon 不提供 MouseWheel 事件，因此这里用低级鼠标
//       钩子 + Shell_NotifyIconGetRect 判断光标是否在图标上）
//   · 左键点击托盘图标           -> 弹出亮度滑块
//   · 右键点击托盘图标           -> 菜单：开机自启动 / HDR 开关 / 退出
//
//   DDC/CI 是慢速 I2C 总线，所有亮度写入都在后台线程经信号量串行执行。
// ============================================================================

using System.Drawing;
using System.Runtime.InteropServices;

namespace MonitorControl;

internal sealed class TrayApp : ApplicationContext
{
    /// <summary>串行化 DDC/CI 事务：并发 I2C 读写会互相破坏。</summary>
    private static readonly SemaphoreSlim DdcLock = new(1, 1);

    private const uint WM_TRAY = 0x8000 + 1;        // WM_APP + 1
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEWHEEL = 0x020A;

    private readonly Icon _icon;
    private readonly TrayWindow _window;
    private readonly ContextMenuStrip _menu;
    private readonly IntPtr _hook;

    /// <summary>钩子回调必须保存在字段里，否则会被 GC 回收导致钩子失效。</summary>
    private readonly LowLevelMouseProcDelegate _hookProc;

    /// <summary>节流发送定时器：把密集的滚轮/滑块事件合并成约每 120ms 一次 I2C 写入。</summary>
    private readonly System.Windows.Forms.Timer _sendTimer;

    /// <summary>常驻 DDC/CI 会话（句柄只开一次，只写不读）。</summary>
    private MonitorApi.DdcSession? _session;
    private List<MonitorApi.MonitorInfo> _sessionMonitors = new();

    private BrightnessPopup? _popup;
    private Toast? _toast;

    private List<MonitorApi.MonitorInfo> _monitors = new();

    /// <summary>最近一次设置的亮度百分比（滚轮调节的基础值），-1 表示未知。</summary>
    private int _lastPercent = -1;

    /// <summary>用户期望的目标亮度；-1 表示无待发送请求。</summary>
    private int _targetPercent = -1;

    /// <summary>最后一次实际写入显示器的亮度；-1 表示新会话尚未写入。</summary>
    private int _sentPercent = -1;

    public TrayApp()
    {
        _icon = LoadIcon();
        _window = new TrayWindow();
        _window.TrayMessage += OnTrayMessage;

        _sendTimer = new System.Windows.Forms.Timer { Interval = 120 };
        _sendTimer.Tick += (_, _) => DrainTarget();
        _sendTimer.Start();

        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _window.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = _icon.Handle,
            szTip = "显示器控制\n左键：亮度滑块 | 悬停滚轮：调亮度 | 右键：菜单",
        };
        if (Shell_NotifyIcon(NIM_ADD, ref nid) == 0)
            throw new InvalidOperationException("创建托盘图标失败。");

        _menu = CreateMenu();

        // 低级鼠标钩子：捕获托盘图标上的滚轮
        _hookProc = LowLevelMouseProc;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("安装鼠标钩子失败，滚轮调节不可用。");

        RefreshMonitors();
    }

    private static Icon LoadIcon()
    {
        try
        {
            using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("MonitorControl.assets.app.ico");
            return stream != null ? new Icon(stream) : SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    // ---------------- 托盘消息 ----------------

    private void OnTrayMessage(Message m)
    {
        if (m.Msg != WM_TRAY)
            return;

        int mouseMessage = m.LParam.ToInt32() & 0xFFFF;
        switch (mouseMessage)
        {
            case WM_LBUTTONUP:
                ShowPopup();
                break;

            case WM_RBUTTONUP:
                // 先把前台焦点切到我们的隐藏窗口，菜单才能在点击外部时自动收起
                SetForegroundWindow(_window.Handle);
                RefreshMonitors();
                RebuildMenu(_menu);
                _menu.Show(Cursor.Position);
                break;
        }
    }

    /// <summary>低级鼠标钩子：光标在托盘图标上时拦截滚轮并调节亮度。</summary>
    private IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam.ToInt64() == WM_MOUSEWHEEL)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (IsCursorOnTrayIcon(info.pt))
            {
                int step = Math.Sign((short)((info.mouseData >> 16) & 0xFFFF)) * 2;
                int target = Math.Clamp(GetCurrentPercent() + step, 0, 100);
                SetAllBrightness(target);
                ShowToast(target);
                return new IntPtr(1); // 已处理，不再传给系统
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private bool IsCursorOnTrayIcon(POINT pt)
    {
        var id = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _window.Handle,
            uID = 1,
        };
        if (Shell_NotifyIconGetRect(ref id, out RECT rect) != 0)
            return false; // 图标可能在溢出区，拿不到矩形就不处理

        return pt.x >= rect.left && pt.x < rect.right && pt.y >= rect.top && pt.y < rect.bottom;
    }

    // ---------------- 显示器列表 ----------------

    private void RefreshMonitors()
    {
        try
        {
            _monitors = MonitorApi.EnumerateMonitors();
        }
        catch
        {
            _monitors = new List<MonitorApi.MonitorInfo>();
        }

        // 显示器列表变化时重建常驻会话（后台执行，避免阻塞 UI 线程的 I2C 读）
        bool unchanged = _session != null
            && _sessionMonitors.Count == _monitors.Count
            && _sessionMonitors.Zip(_monitors, (a, b) => a.HMonitor == b.HMonitor).All(equal => equal);
        if (!unchanged)
        {
            var monitors = _monitors.ToList();
            Task.Run(() => RebuildSession(monitors));
        }
    }

    /// <summary>重建常驻 DDC/CI 会话。在后台线程执行。</summary>
    private void RebuildSession(List<MonitorApi.MonitorInfo> monitors)
    {
        DdcLock.Wait();
        try
        {
            _session?.Dispose();
            _session = new MonitorApi.DdcSession(monitors);
            _sessionMonitors = monitors;
            _sentPercent = -1; // 新会话尚未写入
        }
        catch
        {
            _session = null;
        }
        finally
        {
            DdcLock.Release();
        }
    }

    /// <summary>取滚轮调节的基准亮度：优先缓存，否则读第一台支持的显示器。</summary>
    private int GetCurrentPercent()
    {
        if (_lastPercent >= 0)
            return _lastPercent;

        int percent = 50;
        var capable = _monitors.FirstOrDefault();
        if (capable != null)
        {
            DdcLock.Wait();
            try
            {
                percent = MonitorApi.ReadBrightness(capable) ?? 50;
            }
            catch { }
            finally
            {
                DdcLock.Release();
            }
        }
        _lastPercent = percent;
        return percent;
    }

    /// <summary>
    /// 记录目标亮度，由节流定时器统一发送。密集的滚轮/滑块事件只会产生
    /// 约 8 次/秒的 I2C 写入（只写不读），显示器背光不会再闪烁。
    /// </summary>
    private void SetAllBrightness(int percent)
    {
        _lastPercent = percent;
        _targetPercent = percent;
    }

    /// <summary>定时器回调：有待发送的目标且与会话中已写入值不同时，写入最新值。</summary>
    private void DrainTarget()
    {
        if (_targetPercent < 0 || _targetPercent == _sentPercent)
            return;

        int percent = _targetPercent;
        var session = _session;
        if (session == null)
            return; // 会话尚未就绪，下个节拍重试

        _sentPercent = percent;
        Task.Run(() =>
        {
            DdcLock.Wait();
            try
            {
                session.SetPercent(percent);
            }
            catch
            {
                // 写入失败（显示器休眠/断开等），保留值等待下次变化
            }
            finally
            {
                DdcLock.Release();
            }
        });
    }

    // ---------------- 滚轮反馈小气泡 ----------------

    private void ShowToast(int percent)
    {
        _toast?.Close();
        _toast?.Dispose();
        _toast = new Toast(percent) { Location = GetAnchorPosition(new Size(96, 48)) };
        _toast.Show();
    }

    // ---------------- 亮度弹出滑块 ----------------

    private void ShowPopup()
    {
        RefreshMonitors();

        _popup?.Close();
        _popup?.Dispose();
        _popup = new BrightnessPopup(GetCurrentPercent());
        _popup.PercentChanged += p => SetAllBrightness(p);
        _popup.Location = GetAnchorPosition(_popup.Size);
        _popup.Show();
        _popup.Activate();

        // 后台读取真实亮度，回填滑块（用户可能在显示器 OSD 上手动改过）
        var monitor = _monitors.FirstOrDefault();
        if (monitor == null)
            return;
        Task.Run(() =>
        {
            DdcLock.Wait();
            int? actual;
            try
            {
                actual = MonitorApi.ReadBrightness(monitor);
            }
            finally
            {
                DdcLock.Release();
            }
            if (actual is int v)
            {
                var popup = _popup;
                if (popup != null && !popup.IsDisposed && popup.IsHandleCreated)
                    popup.BeginInvoke(() => popup.SetPercent(v, updateBar: true));
            }
        });
    }

    /// <summary>以鼠标位置为锚点计算弹出位置（悬在指针上方），并夹在屏幕工作区内。</summary>
    private static Point GetAnchorPosition(Size size)
    {
        var p = Cursor.Position;
        p.Offset(-size.Width / 2, -size.Height - 16);
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        p.X = Math.Clamp(p.X, area.Left, area.Right - size.Width);
        p.Y = Math.Clamp(p.Y, area.Top, area.Bottom - size.Height);
        return p;
    }

    // ---------------- 右键菜单 ----------------

    private ContextMenuStrip CreateMenu() => new();

    private void RebuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();

        // 开机自启动
        var autostart = new ToolStripMenuItem("开机自启动")
        {
            Checked = AutoStart.IsEnabled(),
        };
        autostart.Click += (_, _) =>
        {
            try
            {
                AutoStart.Set(!AutoStart.IsEnabled());
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "显示器控制", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            autostart.Checked = AutoStart.IsEnabled();
        };
        menu.Items.Add(autostart);

        // HDR 子菜单（每台支持 HDR 的显示器一项）
        var hdrMonitors = _monitors.Where(m => MonitorApi.GetHdrState(m)?.supported ?? false).ToList();
        if (hdrMonitors.Count > 0)
        {
            var hdrMenu = new ToolStripMenuItem("HDR");
            foreach (var m in hdrMonitors)
            {
                bool enabled = MonitorApi.GetHdrState(m)?.enabled ?? false;
                var item = new ToolStripMenuItem(m.FriendlyName)
                {
                    Checked = enabled,
                };
                item.Click += (_, _) => ToggleHdr(m, item);
                hdrMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(hdrMenu);
        }

        menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitThread();
        menu.Items.Add(exit);
    }

    private void ToggleHdr(MonitorApi.MonitorInfo monitor, ToolStripMenuItem item)
    {
        bool enable = !item.Checked;
        var ui = SynchronizationContext.Current;
        Task.Run(() =>
        {
            try
            {
                MonitorApi.SetHdr(monitor, enable);
            }
            catch (Exception ex)
            {
                ui?.Post(_ => MessageBox.Show(
                    ex.Message, "显示器控制", MessageBoxButtons.OK, MessageBoxIcon.Warning), null);
            }
        });
    }

    protected override void ExitThreadCore()
    {
        Cleanup();
        base.ExitThreadCore();
    }

    private void Cleanup()
    {
        if (_hook != IntPtr.Zero)
            UnhookWindowsHookEx(_hook);

        _sendTimer.Stop();
        _sendTimer.Dispose();

        DdcLock.Wait();
        try
        {
            _session?.Dispose();
        }
        finally
        {
            DdcLock.Release();
        }

        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _window.Handle,
            uID = 1,
        };
        Shell_NotifyIcon(NIM_DELETE, ref nid);

        _window.Dispose();
        _icon.Dispose();
        _popup?.Dispose();
        _toast?.Dispose();
    }

    // ---------------- 隐藏消息窗口 ----------------

    /// <summary>隐藏消息窗口，承载托盘图标回调。用 Form 而不是 NativeWindow 自定义类，避免窗口类注册问题。</summary>
    private sealed class TrayWindow : Form
    {
        public event Action<Message>? TrayMessage;

        public TrayWindow()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            _ = Handle; // 强制创建句柄；窗体本身永不显示
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW：不出现在任务栏/Alt-Tab
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_TRAY)
                TrayMessage?.Invoke(m);
            base.WndProc(ref m);
        }
    }

    // ---------------- 托盘 / 钩子互操作 ----------------

    private const uint NIM_ADD = 0;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x, y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr extraInfo;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int Shell_NotifyIcon(uint message, ref NOTIFYICONDATAW data);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProcDelegate lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    private delegate IntPtr LowLevelMouseProcDelegate(int code, IntPtr wParam, IntPtr lParam);
}

// ============================================================================
// BrightnessPopup —— 左键点击弹出的亮度滑块（失焦自动关闭）
// ============================================================================

internal sealed class BrightnessPopup : Form
{
    public event Action<int>? PercentChanged;

    private readonly TrackBar _bar;
    private readonly Label _label;

    public BrightnessPopup(int initialPercent)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(300, 92);
        BackColor = SystemColors.Window;

        _label = new Label
        {
            Text = $"亮度 {initialPercent}%",
            Location = new Point(14, 10),
            AutoSize = true,
        };
        _bar = new TrackBar
        {
            Location = new Point(10, 34),
            Size = new Size(280, 45),
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 10,
            Value = Math.Clamp(initialPercent, 0, 100),
        };
        _bar.Scroll += (_, _) =>
        {
            _label.Text = $"亮度 {_bar.Value}%";
            PercentChanged?.Invoke(_bar.Value);
        };

        Controls.Add(_label);
        Controls.Add(_bar);
        Deactivate += (_, _) => Close();
    }

    public void SetPercent(int percent, bool updateBar)
    {
        _label.Text = $"亮度 {percent}%";
        if (updateBar)
            _bar.Value = Math.Clamp(percent, 0, 100);
    }
}

// ============================================================================
// Toast —— 滚轮调节时的小气泡提示（不抢焦点，1 秒后自动消失）
// ============================================================================

internal sealed class Toast : Form
{
    private const int WS_EX_TOPMOST = 0x8;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x80;

    public Toast(int percent)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(96, 48);
        BackColor = SystemColors.Info;
        ForeColor = SystemColors.InfoText;

        Controls.Add(new Label
        {
            Text = $"{percent}%",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 13f, FontStyle.Bold),
        });

        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            Close();
        };
        timer.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }
}
