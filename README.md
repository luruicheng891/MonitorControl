# MonitorControl（显示器控制）

一款 Windows 托盘常驻小工具：**鼠标悬停滚轮即可实时调节显示器亮度，右键即可开关 HDR、设置开机自启动**。轻量、免安装感、随开随用。

> 本项目是 [Seelen UI](https://github.com/eythaan/Seelen-UI) 显示器控制核心的**二次开发作品**。
> 亮度调节（DDC/CI + WMI）、HDR 切换（DisplayConfig）、显示器枚举等核心逻辑均提取自 Seelen UI 的
> Rust 实现（`src/background/system_modules/brightness`、`src/background/platform/monitor` 等），并由
> Seelen UI 的开发者与贡献者以 AGPL-3.0 协议开源，在此致谢。本项目同样以 **AGPL-3.0** 协议发布。

## 功能

- **托盘滚轮调亮度**：鼠标悬停在托盘图标上滚动滚轮，±2% 实时调节；内置 120ms 节流与 I2C 只写优化，背光不闪烁
- **左键弹出滑块**：点击托盘图标弹出亮度滑块，打开时自动回读显示器真实亮度
- **右键菜单**：
  - 开机自启动开关（HKCU 注册表，无需管理员权限）
  - HDR 子菜单（每台支持 HDR 的显示器独立开关）
  - 退出
- **多显示器支持**：多屏环境下统一调节所有支持亮度的显示器
- **命令行接口**：保留无 UI 的 CLI 模式，方便脚本调用

## 安装

从 [Releases](../../releases) 下载 `MonitorControlSetup.exe` 双击安装（自带 .NET 8 运行时，无需额外环境）：

- 安装位置：`%LOCALAPPDATA%\MonitorControl`
- 全程仅写入用户目录与 HKCU 注册表，**不需要管理员权限**
- 可选开机自启动（安装界面勾选，之后可在托盘菜单随时切换）
- 卸载：开始菜单"卸载 显示器控制"或 Windows 设置 → 应用和功能

## 使用

| 操作 | 效果 |
| --- | --- |
| 悬停托盘图标 + 滚轮 | 调节亮度（±2%） |
| 左键点击托盘图标 | 亮度滑块 |
| 右键点击托盘图标 | 自启动 / HDR / 退出 |

命令行模式：

```text
MonitorControl list                       列出所有显示器及亮度/HDR 状态
MonitorControl brightness <索引> <0-100>   设置指定显示器亮度
MonitorControl hdr <索引> <on|off>         开/关指定显示器 HDR
MonitorControl autostart <on|off|status>   设置/查看开机自启动
```

## 适配范围

与 Seelen UI 完全一致（核心代码同源），不依赖显卡品牌 API（NVAPI/ADL），N 卡 / A 卡 / Intel 核显通用：

- **外接显示器亮度**：显示器需支持 DDC/CI（绝大多数 2010 年后的桌面显示器都支持；个别型号需在显示器 OSD 菜单中开启 DDC/CI）
- **笔记本内屏亮度**：走 WMI（`ROOT\WMI`），基本全支持
- **HDR**：显示器需硬件支持 HDR（Windows 高级颜色）

> 已知限制：托盘图标收进任务栏"溢出区"（^ 展开按钮）时，Windows 拿不到图标矩形，滚轮调节会失效——把图标拖到任务栏常驻区即可。

## 技术原理

从 Seelen UI 提取的三条 Windows 原生通路：

| 能力 | 技术 |
| --- | --- |
| 枚举显示器 | Win32：`EnumDisplayMonitors` + `QueryDisplayConfig` |
| 亮度（外接屏） | DDC/CI：`dxva2.dll` 的 `Get/SetMonitorBrightness`（VCP 0x10） |
| 亮度（笔记本内屏） | WMI：`WmiMonitorBrightness` / `WmiSetBrightness` |
| HDR 开关 | DisplayConfig 高级颜色 API：`DISPLAYCONFIG_GET/SET_ADVANCED_COLOR_INFO` |

托盘滚轮采用低级鼠标钩子（`WH_MOUSE_LL`）+ `Shell_NotifyIconGetRect` 判断光标是否位于图标上，仅命中时拦截滚轮事件。

## 构建

需要 .NET 8 SDK（Windows）：

```powershell
git clone <本仓库>
cd MonitorControl
powershell -ExecutionPolicy Bypass -File build-installer.ps1
# 产物：dist\MonitorControlSetup.exe
```

或仅编译调试版：`dotnet build MonitorControl.csproj`

## 许可证

[AGPL-3.0](../LICENSE) —— 与上游 Seelen UI 保持一致。核心显示器控制逻辑源自 Seelen UI 项目。
