// ============================================================================
// MonitorApi —— 核心显示器能力（源自 Seelen UI 的最小提取，无 UI / CLI 依赖）
//
//   1. 枚举显示器        Win32: EnumDisplayMonitors + QueryDisplayConfig
//   2. 调节亮度          外部显示器: DDC/CI (dxva2.dll Get/SetMonitorBrightness)
//                        笔记本内屏: WMI ROOT\WMI (WmiMonitorBrightnessMethods)
//   3. 切换 HDR          Win32 DisplayConfig 高级颜色 API
//
// 运行前提：Windows 10 1703+；无需管理员权限；外接显示器调亮度需开启 DDC/CI。
// ============================================================================

using System.Management;
using System.Runtime.InteropServices;

namespace MonitorControl;

internal static class MonitorApi
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 11;
    private const uint DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE = 12;
    private const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;

    // ---------------- 显示器数据模型 ----------------

    internal sealed class MonitorInfo
    {
        public int Index;
        public IntPtr HMonitor;
        public string DeviceName = "";
        public string FriendlyName = "";
        public RECT Bounds;
        public List<string> PnpIds = new();
        public List<DISPLAYCONFIG_PATH_INFO> Paths = new();

        public override string ToString() => $"[{Index}] {FriendlyName} ({DeviceName})";
    }

    // ---------------- 枚举显示器 ----------------

    internal static List<MonitorInfo> EnumerateMonitors()
    {
        var list = new List<MonitorInfo>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFOEXW();
            info.cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>();
            if (GetMonitorInfoW(hMonitor, ref info))
            {
                list.Add(new MonitorInfo
                {
                    Index = list.Count,
                    HMonitor = hMonitor,
                    DeviceName = info.szDevice,
                    Bounds = info.rcMonitor,
                });
            }
            return true;
        }, IntPtr.Zero);

        // 与 Seelen UI 相同的匹配策略：QueryDisplayConfig 里源模式位置 == 显示器
        // 在虚拟桌面上的左上角，即认为该 path 扫描输出到这台显示器。
        foreach (var m in list)
        {
            m.Paths = GetMatchingPaths(m.HMonitor, m.Bounds);
            foreach (var path in m.Paths)
            {
                var name = GetTargetDeviceName(path);
                if (string.IsNullOrEmpty(m.FriendlyName))
                    m.FriendlyName = name.monitorFriendlyDeviceName ?? m.DeviceName;

                string pnp = PnpIdFromDevicePath(name.monitorDevicePath ?? "");
                if (pnp.Length > 0 && !m.PnpIds.Contains(pnp))
                    m.PnpIds.Add(pnp);
            }
        }
        return list;
    }

    /// <summary>返回源模式位置与该 HMONITOR 左上角重合的所有活动 path。</summary>
    private static List<DISPLAYCONFIG_PATH_INFO> GetMatchingPaths(IntPtr hMonitor, RECT bounds)
    {
        var info = new MONITORINFOEXW();
        info.cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>();
        GetMonitorInfoW(hMonitor, ref info);
        bounds = info.rcMonitor;

        var result = new List<DISPLAYCONFIG_PATH_INFO>();
        var (paths, modes) = QueryActivePaths();

        foreach (var path in paths)
        {
            uint idx = path.sourceInfo.modeInfoIdx;
            if (idx == 0xFFFF_FFFF || idx >= (uint)modes.Length)
                continue;

            var mode = modes[idx];
            if (mode.infoType != DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                continue;

            if (mode.sourceModePositionX == bounds.left && mode.sourceModePositionY == bounds.top)
                result.Add(path);
        }
        return result;
    }

    private static (DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes) QueryActivePaths()
    {
        int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
        if (err != 0) throw new InvalidOperationException($"GetDisplayConfigBufferSizes 失败 (0x{err:X8})");

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

        err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (err != 0) throw new InvalidOperationException($"QueryDisplayConfig 失败 (0x{err:X8})");

        return (paths[..(int)pathCount], modes[..(int)modeCount]);
    }

    private static DISPLAYCONFIG_TARGET_DEVICE_NAME GetTargetDeviceName(DISPLAYCONFIG_PATH_INFO path)
    {
        var name = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
        name.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        name.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        name.header.adapterIdLow = path.targetInfo.adapterIdLow;
        name.header.adapterIdHigh = path.targetInfo.adapterIdHigh;
        name.header.id = path.targetInfo.id;

        int err = DisplayConfigGetDeviceInfo(ref name);
        return err == 0 ? name : default;
    }

    // ---------------- HDR（高级颜色） ----------------

    /// <summary>返回 (是否支持 HDR, 当前是否开启)；显示器不支持时返回 null。</summary>
    internal static (bool supported, bool enabled)? GetHdrState(MonitorInfo m)
    {
        foreach (var path in m.Paths)
        {
            var info = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
            info.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
            info.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
            info.header.adapterIdLow = path.targetInfo.adapterIdLow;
            info.header.adapterIdHigh = path.targetInfo.adapterIdHigh;
            info.header.id = path.targetInfo.id;

            if (DisplayConfigGetDeviceInfo(ref info) == 0)
            {
                // bit0 = advancedColorSupported, bit1 = advancedColorEnabled
                return ((info.value & 0x1) != 0, (info.value & 0x2) != 0);
            }
        }
        return null;
    }

    internal static void SetHdr(MonitorInfo m, bool enable)
    {
        foreach (var path in m.Paths)
        {
            var state = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE();
            state.header.type = DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE;
            state.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>();
            state.header.adapterIdLow = path.targetInfo.adapterIdLow;
            state.header.adapterIdHigh = path.targetInfo.adapterIdHigh;
            state.header.id = path.targetInfo.id;
            state.value = enable ? 1u : 0u;

            if (DisplayConfigSetDeviceInfo(ref state) == 0)
                return;
        }
        throw new InvalidOperationException("设置 HDR 失败：找不到匹配的显示路径（显示器可能不支持 HDR）。");
    }

    // ---------------- 亮度 ----------------

    /// <summary>读取亮度百分比；DDC/CI 与 WMI 均不可用时返回 null。</summary>
    internal static int? ReadBrightness(MonitorInfo m)
    {
        // 优先 DDC/CI（外部显示器）
        foreach (var pm in GetPhysicalMonitors(m.HMonitor))
        {
            try
            {
                if (GetMonitorBrightness(pm.hPhysicalMonitor, out uint min, out uint current, out uint max)
                    && max > min && current >= min && current <= max)
                {
                    uint span = max - min;
                    return (int)(((current - min) * 100 + span / 2) / span);
                }
            }
            finally
            {
                DestroyPhysicalMonitor(pm.hPhysicalMonitor);
            }
        }

        // 回退 WMI（笔记本内屏）
        var wmi = FindWmiBrightnessInstance(m.PnpIds);
        if (wmi != null)
            return Convert.ToInt32(wmi.GetPropertyValue("CurrentBrightness"));

        return null;
    }

    internal static void SetBrightness(MonitorInfo m, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);

        // 优先 DDC/CI（外部显示器）
        foreach (var pm in GetPhysicalMonitors(m.HMonitor))
        {
            try
            {
                if (GetMonitorBrightness(pm.hPhysicalMonitor, out uint min, out _, out uint max) && max > min)
                {
                    uint raw = min + (uint)Math.Round(percent / 100.0 * (max - min));
                    if (SetMonitorBrightness(pm.hPhysicalMonitor, raw))
                        return;
                }
            }
            finally
            {
                DestroyPhysicalMonitor(pm.hPhysicalMonitor);
            }
        }

        // 回退 WMI（笔记本内屏）
        if (!TrySetWmiBrightness(m.PnpIds, (byte)percent))
            throw new InvalidOperationException(
                "该显示器不支持亮度调节：DDC/CI 与 WMI 均不可用（请在显示器 OSD 中开启 DDC/CI）。");
    }

    /// <summary>获取该逻辑显示器背后的物理显示器句柄（DDC/CI 入口），用完必须 Destroy。</summary>
    private static List<PHYSICAL_MONITOR> GetPhysicalMonitors(IntPtr hMonitor)
    {
        var result = new List<PHYSICAL_MONITOR>();
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
            return result;

        var monitors = new PHYSICAL_MONITOR[count];
        if (GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
            result.AddRange(monitors);
        return result;
    }

    // ---------------- 常驻 DDC/CI 会话（托盘滚轮快速调节用） ----------------

    /// <summary>
    /// 常驻打开的物理显示器句柄 + 缓存的亮度范围。滚轮调节时只写不读，
    /// 配合调用方的节流合并，避免密集 I2C 事务导致背光闪烁。
    /// 调用方负责用 DdcLock 串行化所有事务（包括 Dispose）。
    /// </summary>
    internal sealed class DdcSession : IDisposable
    {
        private sealed class Panel
        {
            public IntPtr Handle;
            public uint Min;
            public uint Max;
        }

        private readonly List<Panel> _panels = new();

        /// <summary>没有 DDC 面板的显示器（笔记本内屏）走 WMI 兜底。</summary>
        private readonly List<string> _wmiPnpIds = new();

        public DdcSession(List<MonitorInfo> monitors)
        {
            foreach (var m in monitors)
            {
                bool anyDdc = false;
                foreach (var pm in GetPhysicalMonitors(m.HMonitor))
                {
                    if (GetMonitorBrightness(pm.hPhysicalMonitor, out uint min, out _, out uint max) && max > min)
                    {
                        _panels.Add(new Panel { Handle = pm.hPhysicalMonitor, Min = min, Max = max });
                        anyDdc = true;
                    }
                    else
                    {
                        DestroyPhysicalMonitor(pm.hPhysicalMonitor);
                    }
                }
                if (!anyDdc)
                    _wmiPnpIds.AddRange(m.PnpIds);
            }
        }

        /// <summary>只写不读。调用方必须持有 DdcLock。</summary>
        public void SetPercent(int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            foreach (var p in _panels)
            {
                uint raw = p.Min + (uint)Math.Round(percent / 100.0 * (p.Max - p.Min));
                SetMonitorBrightness(p.Handle, raw);
            }
            if (_wmiPnpIds.Count > 0)
                TrySetWmiBrightness(_wmiPnpIds, (byte)percent);
        }

        public void Dispose()
        {
            foreach (var p in _panels)
                DestroyPhysicalMonitor(p.Handle);
            _panels.Clear();
        }
    }


    // ---------------- WMI（笔记本内屏亮度） ----------------

    private static ManagementObject? FindWmiBrightnessInstance(List<string> pnpIds)
    {
        if (pnpIds.Count == 0) return null;
        using var searcher = new ManagementObjectSearcher(
            @"root\wmi", "SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness");
        foreach (var obj in searcher.Get())
        {
            string? instance = obj.GetPropertyValue("InstanceName") as string;
            if (instance != null && pnpIds.Contains(PnpIdFromWmiInstance(instance)))
                return (ManagementObject)obj;
        }
        return null;
    }

    private static bool TrySetWmiBrightness(List<string> pnpIds, byte percent)
    {
        if (pnpIds.Count == 0) return false;
        using var searcher = new ManagementObjectSearcher(
            @"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
        foreach (var obj in searcher.Get())
        {
            string? instance = obj.GetPropertyValue("InstanceName") as string;
            if (instance == null || !pnpIds.Contains(PnpIdFromWmiInstance(instance)))
                continue;

            var inParams = ((ManagementObject)obj).GetMethodParameters("WmiSetBrightness");
            inParams["Timeout"] = 0u;
            inParams["Brightness"] = percent;
            ((ManagementObject)obj).InvokeMethod("WmiSetBrightness", inParams, null);
            return true;
        }
        return false;
    }

    // ---------------- PnP ID 归一化（与 Seelen UI 相同的匹配逻辑） ----------------

    /// <summary>`\\?\DISPLAY#BOE0900#4&10fd3ab1&0&UID265988#{guid}` -> `DISPLAY\BOE0900\4&10FD3AB1&0&UID265988`</summary>
    private static string PnpIdFromDevicePath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];

        int hash = path.IndexOf("#{", StringComparison.Ordinal);
        if (hash >= 0)
            path = path[..hash];

        return path.Replace('#', '\\').ToUpperInvariant();
    }

    /// <summary>`DISPLAY\BOE0900\4&10fd3ab1&0&UID265988_0` -> `DISPLAY\BOE0900\4&10FD3AB1&0&UID265988`</summary>
    private static string PnpIdFromWmiInstance(string instanceName)
    {
        string id = instanceName;
        int last = id.LastIndexOf('_');
        if (last >= 0 && last < id.Length - 1 && id[(last + 1)..].All(char.IsAsciiDigit))
            id = id[..last];
        return id.ToUpperInvariant();
    }

    // ---------------- Win32 P/Invoke ----------------

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clipRect, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW info);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags, ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE requestPacket);

    [DllImport("dxva2.dll")]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint numberOfPhysicalMonitors);

    [DllImport("dxva2.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] physicalMonitorArray);

    [DllImport("dxva2.dll")]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint minimumBrightness, out uint currentBrightness, out uint maximumBrightness);

    [DllImport("dxva2.dll")]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint newBrightness);

    [DllImport("dxva2.dll")]
    private static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

    // ---------------- 互操作结构体 ----------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public uint adapterIdLow;
        public int adapterIdHigh;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public uint adapterIdLow;
        public int adapterIdHigh;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public uint adapterIdLow;
        public int adapterIdHigh;
        public uint id;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint additionalSignalInfo0;   // union，此处不使用
        public uint additionalSignalInfo1;
        public uint additionalSignalInfo2;
        public uint flags;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public uint adapterIdLow;
        [FieldOffset(12)] public int adapterIdHigh;

        // union（源模式部分，我们只关心源模式位置）
        [FieldOffset(16)] public uint sourceModeWidth;
        [FieldOffset(20)] public uint sourceModeHeight;
        [FieldOffset(24)] public uint sourceModePixelFormat;
        [FieldOffset(28)] public int sourceModePositionX;
        [FieldOffset(32)] public int sourceModePositionY;

        // union 占位（目标模式更大，保证结构体总大小为 64 字节）
        [FieldOffset(16)] public ulong reserved0;
        [FieldOffset(24)] public ulong reserved1;
        [FieldOffset(32)] public ulong reserved2;
        [FieldOffset(40)] public ulong reserved3;
        [FieldOffset(48)] public ulong reserved4;
        [FieldOffset(56)] public ulong reserved5;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;                 // bit0: supported, bit1: enabled
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;                 // bit0: EnableAdvancedColor
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }
}
