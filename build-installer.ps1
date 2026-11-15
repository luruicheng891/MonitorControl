# ============================================================================
# MonitorControl 一键打包脚本：生成 dist\MonitorControlSetup.exe 安装包
#   [1/3] 发布主程序（自包含单文件，无需目标机安装 .NET）
#   [2/3] 发布安装器（把主程序嵌入单文件安装包）
#   [3/3] 输出 dist\MonitorControlSetup.exe
# ============================================================================
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host '[1/3] 发布主程序（自包含单文件）...'
dotnet publish "$root\MonitorControl.csproj" -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o "$root\Setup\embed"
if ($LASTEXITCODE -ne 0) { throw '主程序发布失败' }

Write-Host '[2/3] 发布安装器（框架依赖单文件）...'
dotnet publish "$root\Setup\Setup.csproj" -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -o "$root\dist"
if ($LASTEXITCODE -ne 0) { throw '安装器发布失败' }

$setup = Join-Path $root 'dist\MonitorControlSetup.exe'
Write-Host "[3/3] 完成：$setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
