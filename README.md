# FreeCom 免费串口调试助手（v0.2）

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

全功能免费、无账户、无授权、**MIT 开源**的串口调试上位机。v0.2：串口通信 + 5 种协议多窗口绘图 + 本地 Control API + MCP AI 自动化（P0/P1 工具集）+ **虚拟串口（com0com）与设备模拟器**（原"虚拟回环"已移除）。

> 许可：本项目代码以 MIT 协议开源（见 [LICENSE](LICENSE)）。可选依赖 com0com 驱动为第三方 GPL 项目，不随本仓库分发，使用时按需自行下载（见"虚拟串口"章节）。

产品需求（PRD）、技术方案与测试计划见仓库上级目录文档。

2026-09-22 内存优化已更新独立版：接收视图按字符限额保留最新内容，隐藏时停止构建文档；曲线使用严格上限环形缓存；二进制解析移除逐帧整缓冲复制。原始日志32MiB、显示日志8MiB、每条曲线50万点的默认缓存预算保持不变。测试结果和复现步骤见 [内存压力测试与优化报告](../内存压力测试与优化报告-20260922.md)。

## 功能

| 模块 | 能力 |
| --- | --- |
| 通信 | 串口全参数（波特率/数据位/校验/停止位/流控/DTR/RTS）；**虚拟串口对（com0com）与物理串口统一使用**；快速重连的打开重试与确定性关闭 |
| 收发 | 文本/HEX 双模式、UTF-8/GBK/ASCII、回车风格、时间戳、RX/TX 着色、自动滚动、发送历史、循环发送、**发送文件（原始字节流分块下发）**、计数与速率 |
| 协议绘图 | TEXT / CSV / STAMP / EasyHex / ModbusRTU（CRC16 校验、按地址码分窗、坏帧重同步） |
| 绘图窗口 | 多窗口多曲线（每窗 16 条）、默认窗口继承、ScottPlot 渲染、曲线重命名、抽稀与点数上限、**示波器式坐标轴窗口（X 滚动 N 点窗 / X 固定范围，Y 固定窗口超出裁剪）** |
| **虚拟串口管理器**（新） | 基于 com0com 创建/删除真实 COM 端口对（UAC 提权）；驱动检测与下载引导（SHA256 校验） |
| **设备模拟器**（新） | 以"下位机"身份占用端口对另一端：五协议流量按间隔发送 + 手动文本/HEX 发送 |
| MCP/AI | 本地 Control HTTP API（127.0.0.1 + Bearer Token，28 端点）+ `freecom-mcp.exe`（26 工具：收发/发送文件/等待/期望回显、模拟器、曲线统计、协议帮助、发送历史、原始与显示导出、虚拟串口对管理） |
| 数据 | 原始 DAT / 显示 TXT / 曲线 CSV 导出；JSON 配置（schema v2，自动迁移 v1 虚拟回环配置） |

## 虚拟串口（首次使用）

1. 菜单 **工具 → 虚拟串口管理器** → 若驱动未安装，点"驱动下载与安装说明"（com0com 官方签名包，校验 SHA256 后安装，需管理员）；
2. 管理器中 **创建一对**（自动分配如 COM26↔COM27，需 UAC）；
3. 主界面选择其中一个端口打开（对端可接：设备模拟器、或 SSCOM 等第三方工具做联调演示）。

> 本机（开发机）已预置测试端口对：COM20↔COM21、COM22↔COM23、COM24↔COM25。

## 构建 / 测试 / 运行

一键验证（CMD/PowerShell/双击）：`串口上位机\run_verify.cmd`（构建 + 232 项测试 + E2E 13 项断言）。

**测试基座（v0.1.1 起）**：全部自动化测试运行在 **com0com 虚拟串口对的真实串口路径**上（SerialPort API → Windows 串口栈 → 驱动），不依赖物理串口硬件；原进程内"虚拟回环"已删除。运行测试的前置条件 = 端口对存在（可用环境变量 `FREECOM_TEST_PAIRS="A:B,C:D"` 覆盖默认对）。

Git Bash 手动执行：

```bash
cd /c/Users/admin/Desktop/串口上位机/freecom
export DOTNET_ROOT="C:\Users\admin\dotnet8"
/c/Users/admin/dotnet8/dotnet.exe test tests/FreeCom.Tests/FreeCom.Tests.csproj   # 232 项
bash tests/tools/e2e_mvp.sh                                                      # E2E 13 项（真实串口路径）
```

桌面程序（独立版，双击即用）：`串口上位机\启动FreeCom.cmd` 或 `freecom\publish\FreeCom\FreeCom.App.exe`。

无头宿主（真实串口双端扮演，供脚本/CI 验证）：

```bash
/c/Users/admin/dotnet8/dotnet.exe src/FreeCom.Host/bin/Release/net8.0/FreeCom.Host.dll \
  --port 17341 --token demo --protocol TEXT --app-port COM22 --dev-port COM23 --interval 20 --duration 20
```

## MCP 客户端接入（Cursor 等）

1. FreeCom → 工具 → 勾选 **启用 MCP 服务**，查看 Token；
2. 客户端配置 `freecom-mcp.exe`（同目录），环境变量 `FREECOM_URL=http://127.0.0.1:17340`、`FREECOM_TOKEN=<Token>`；
3. 工具（26 个）：`diag_connectivity` `serial_list` `serial_open/close/status` `device_send` `send_file` `receive_read` `receive_wait` `send_expect` `send_history` `protocol_get/set` `protocol_help` `plot_windows` `plot_data` `curve_stats` `curve_export` `export_raw` `export_display` `simulator_start/stop` `vcom_list/create/remove` `app_info`。仅监听 127.0.0.1，无遥测。

## 目录结构

```
freecom/
├─ src/FreeCom.Core/    # 核心库（串口传输/VirtualComManager/管线/协议/绘图/Control API）
├─ src/FreeCom.App/     # WPF 主界面 + 虚拟串口管理器 + 设备模拟器
├─ src/FreeCom.Mcp/     # MCP stdio 桥
├─ src/FreeCom.Host/    # 无头测试宿主（真实串口双端）
├─ tests/               # 232 项测试（真实虚拟串口路径）+ e2e_mvp.sh
└─ tools/com0com/       # 驱动安装包与建对脚本（开发机预置）
```
