# ⚡ RepoOPS — Script Task Runner

> 基于 **ASP.NET + SignalR + WebView2** 构建的图形化 PowerShell 脚本任务执行器，专为 Windows 环境设计。

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows-0078D6.svg)]()
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

提供直观的 Web UI 管理和执行 PowerShell 脚本，支持 **Desktop（WinForms + WebView2 壳）** 和 **Web（纯浏览器）** 两种运行模式。

---

## ✨ 功能概览

| 模块 | 说明 |
| --- | --- |
| 📋 分类任务列表 | 左侧面板以树形结构展示任务，支持 1-2 级分组，JSON 配置灵活定义 |
| 📺 实时终端输出 | 右侧 xterm.js 终端实时渲染脚本输出，完整支持 ANSI 颜色高亮 |
| 🔄 多任务并行 | 同时运行多个任务，每个任务在独立标签页中显示，互不影响 |
| ⏹️ 任务取消 | 可随时停止正在执行的任务，自动终止整个进程树 |
| 🖥️ Desktop 模式 | WinForms + WebView2 原生窗口，双击 EXE 即可运行，随机端口无冲突 |
| 🌐 Web 模式 | 纯浏览器访问，支持发布为自包含单文件 EXE（~130 MB） |
| ⚙️ 可视化配置 | 内置配置编辑器，支持在界面上直接编辑 `tasks.json`，无需手动改文件 |
| 🌍 中英双语 | 界面支持中文 / English 一键切换 |

---

## 🏎️ 为什么选择 RepoOPS？

| 维度 | RepoOPS | 手动终端执行 |
| --- | --- | --- |
| **可视化** | 分组任务树 + xterm.js 实时终端 | 手动记住脚本路径与参数 |
| **并行执行** | 多标签页并行运行，输出互不干扰 | 多窗口手动管理 |
| **实时反馈** | SignalR WebSocket 流式推送 | 需盯着终端窗口 |
| **进程管理** | 一键停止，自动清理子进程树 | 需手动 Kill |
| **配置即代码** | `tasks.json` 声明式定义，版本可控 | 运维文档易过期 |

> 简而言之：**把零散的脚本变成可管理、可观察、可复用的任务面板。**

---

## 🧱 技术架构

```text
┌──────────────────────────────────────────────────┐
│           RepoOPS.Desktop (WinForms)             │
│        WebView2 窗口 ─ 自动选择可用端口           │
└──────────────┬───────────────────────────────────┘
               │  或直接浏览器访问 (Web 模式)
┌──────────────▼───────────────────────────────────┐
│           RepoOPS.Host (ASP.NET Kestrel)         │
│  ┌─────────────────────────────────────────────┐ │
│  │  TaskHub (SignalR)                          │ │
│  │  StartTask() / StopTask() / 流式输出推送    │ │
│  └──────────────┬──────────────────────────────┘ │
│  ┌──────────────▼──────────────────────────────┐ │
│  │  RepoOPS.Lib — 核心库                       │ │
│  │  ScriptTaskService  进程管理 / 并发控制      │ │
│  │  ConfigService      tasks.json 加载与持久化  │ │
│  │  Models             TaskConfig / TaskGroup   │ │
│  └──────────────┬──────────────────────────────┘ │
└─────────────────┼────────────────────────────────┘
                  │
┌─────────────────▼────────────────────────────────┐
│           pwsh 进程 (PowerShell 7+)              │
│  stdout / stderr → SignalR → xterm.js 渲染       │
└──────────────────────────────────────────────────┘
```

```text
repoOPS/
├── repoOPS.sln
├── scripts/examples/           # 示例 PowerShell 脚本
├── docs/                       # 文档（配置指南 / 开发指南 / 编译说明）
├── src/
│   ├── RepoOPS/                # Web 版本入口（监听 localhost:5088）
│   │   ├── Program.cs
│   │   ├── tasks.json          # 任务配置文件
│   │   └── wwwroot/            # 前端静态文件（HTML / JS / CSS）
│   ├── RepoOPS.Desktop/        # Desktop 版本（WinForms + WebView2 壳）
│   │   ├── Program.cs          # 启动内嵌 Web 服务器 + WinForms 窗口
│   │   └── MainForm.cs         # WebView2 主窗体
│   ├── RepoOPS.Host/           # 共享的 Web 应用构建逻辑
│   │   └── RepoOpsWebApp.cs    # 路由、SignalR、静态文件配置
│   └── RepoOPS.Lib/            # 共享核心库
│       ├── Hubs/TaskHub.cs     # SignalR Hub — 实时通信
│       ├── Services/           # ConfigService + ScriptTaskService
│       └── Models/             # TaskConfig / TaskGroup / TaskItem / RunningTask
└── wwwroot/                    # 前端静态资源（含 Brotli 压缩版本）
```

**技术栈一览：**

- **框架**：.NET 10 + ASP.NET (Kestrel)
- **实时通信**：SignalR (WebSocket)
- **终端渲染**：xterm.js + ANSI color
- **Desktop 壳**：WinForms + Microsoft.Web.WebView2
- **脚本执行**：System.Diagnostics.Process → pwsh
- **前端**：HTML / CSS / JavaScript（无框架依赖）

---

## 🚀 快速开始

### 使用发行包

前往 [Releases](../../releases) 下载 Desktop 版本，解压后双击 `RepoOPS.Desktop.exe` 即可运行。

**运行条件**：
- Windows 10/11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Win10/11 通常已预装）
- [PowerShell 7+](https://learn.microsoft.com/powershell/scripting/install/installing-powershell)

### 从源码构建

```bash
git clone https://github.com/kukisama/repoOPS.git
cd repoOPS

# 开发模式（Web 版本，浏览器访问 http://localhost:5088）
dotnet run --project src/RepoOPS

# 发布 Desktop 版本（推荐）
dotnet publish src/RepoOPS.Desktop/RepoOPS.Desktop.csproj -c Release -r win-x64 -o publish/Desktop

# 发布 Web 版本（自包含单文件 EXE）
dotnet publish src/RepoOPS/RepoOPS.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/WebSingle
```

---

## ⚙️ 配置任务

编辑 `tasks.json` 定义任务列表（也可在界面中通过 ⚙️ 按钮打开可视化编辑器）。详细字段说明见 [配置指南](docs/configuration.md)。

```jsonc
{
  "scriptsBasePath": "scripts",
  "groups": [
    {
      "name": "我的项目",
      "icon": "📱",
      "tasks": [
        {
          "id": "build-android",
          "name": "打包 Android",
          "description": "构建 Android APK",
          "script": "examples/build-android.ps1",
          "arguments": "-BuildType Release",
          "icon": "🔨"
        }
      ]
    }
  ]
}
```

> 修改 `tasks.json` 后，点击界面左上角的刷新按钮 🔄 即可热加载新配置，无需重启。

---

## 📚 相关文档

- [配置指南](docs/configuration.md) — `tasks.json` 完整字段说明与多项目配置示例
- [开发指南](docs/development.md) — 架构详解、核心流程与开发环境搭建
- [编译说明](docs/编译说明.md) — 环境准备、编译、发布与运行的完整步骤

---

## 📝 许可证

本项目基于 [MIT 许可证](LICENSE) 开源。
