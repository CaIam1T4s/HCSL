<h1 align="center">Hello Counter-Strike! Launcher</h1>

Hello Counter-Strike! Launcher（**HCSL**）是面向 CS2 服主与玩家的 Windows / Linux 桌面工具集合，使用 .NET 10 与 Avalonia 构建

## 文档目录

- [项目简介](#项目简介)
- [当前进展与下一步](#当前进展与下一步)
- [快速开始](#快速开始)
- [项目功能](#项目功能)
- [当前功能的缺陷](#当前功能的缺陷)
- [鸣谢和说明](#鸣谢和说明)

## 项目简介

本项目当前使用 Avalonia 12.1.5 与 TKCode.Arc 1.1.0构建

项目包含三个前端，一个服务端宿主和一个CLI工具：

- **服务端GUI**（`HCSL.Host`）：提供服务端下载，管理和运行，使用实例来隔离进行插件和配置隔离
- **服务端TUI**（`HCSL.Tui`）：无桌面环境下的终端管理界面，提供GUI支持的全部功能，支持ssh远程终端使用
- **玩家客户端**（`HCSL.Client`）：查看保存的服务器列表，提供相比CS2原本命令行更直观的进服操作，对所有已知ip和密码的CS2服务器都适用
- **本机 Daemon**（`HCSL.Daemon`）：在服务端启动时自动启动，持有服务器进程与 SteamCMD 任务，通过本机 IPC 为 GUI/TUI 提供能力，已包含在服务端GUI/TUI的release中
- **命令行调试工具**（`build/HCSL.dev`）：为方便开发者调试使用的CLI工具，不包含在Release中

> 本项目与 Valve 无关联，未获 Valve 背书。Counter-Strike 及相关商标归 Valve 所有

## 快速开始

### 环境要求

- .NET 10 SDK（开发与框架依赖版运行都需要）
- Windows：无额外系统依赖
- Linux：Avalonia GUI 需要 X11 相关系统库（如 `libx11`、`libfontconfig`），TUI 需要支持 UTF-8 的终端

### 从源码构建并运行

```sh
git clone https://github.com/CaIam1T4s/Hello_Counter_Strike_Launcher.git
cd Hello_Counter_Strike_Launcher

dotnet restore Hello_Counter_Strike_Launcher.slnx
dotnet build Hello_Counter_Strike_Launcher.slnx

# 服主 GUI
dotnet run --project src/HCSL.Host

# 服主 TUI
dotnet run --project src/HCSL.Tui

# 玩家客户端
dotnet run --project src/HCSL.Client
```

测试：

```sh
bash build/test-fast.sh full
```

### 数据位置

设置、私有数据、实例与内容库分别管理。内容库默认位于用户主目录的 `HCSL-Library`文件夹下

## 项目功能

- **实例**：实例管理一个服务端可执行文件的链接和
- **配置预设**：一个实例可持有多个配置预设，一次运行使用一个预设，切换前需停服
- **插件与框架**：本地插件包导入、框架安装（Metamod:Source、CounterStrikeSharp、Source2Mod、SwiftlyS2、ModSharp）、版本与依赖校验、部署清单与回滚。
- **CFG 与地图**：只读 CFG 树浏览与 `exec`、地图池管理与顺序保存、普通地图与工坊地图/合集启动。
- **服务器控制**：启动、停服、恢复、部署（不启动）、日志分页与跟随、本机控制台、低内存二次确认与退出策略。
- **SteamCMD 与 CS2**：受管 SteamCMD 安装与自更新、匿名/账号登录探测、全局 CS2 安装/更新/校验。
- **服务器管理**：在线玩家名单、踢人、管理员名单读写与按框架热重载。
- **玩家客户端**：服务器列表、IP/域名与端口格式校验、系统密码存储、Steam 连接请求与手动命令后备。
- **架构**：Core / Application / Infrastructure / Frontend / Contracts 分层，UI 不直接运行 SteamCMD 或 CS2；本机 IPC 由 Daemon 串行执行写操作。

## 当前功能的缺陷

- **网络能力受限**：仅支持直接 IP/局域网，公网传输策略未实现；VAC 修改入口未接入，GSLT 与公网开服未实现。
- **玩家身份链路未闭环**：玩家 Steam 身份认证、GUI 端的玩家 Steam 配对、真实 Steam/CS2 联动仍待验收；玩家客户端以格式校验为主，不预检可达性。
- **Windows 覆盖不足**：TUI 仅在 Linux 终端实测，Windows 下未验证；Windows 打包安装器与代码签名尚未建立。
- **界面验证不完整**：当前环境无 X 显示服务，GUI 只完成 Headless 测试与构建验证，真实渲染、输入设备与文件管理器交互需人工检查。
- **设置不持久**：主题、背景、语言等偏好仅在当前会话生效；实例导入/分享与他人实例包尚未实现。
- **部分入口为占位**：VAC 修改、部分配置预设执行业务与远端管理仍为禁用或未接入状态；开发者 CLI `hcsl` 不随发行包提供。
- **文档缺失**：`docs/` 使用文档尚未创建；发行包（自包含/框架依赖、安装器、校验和）尚未产出。

## 鸣谢和说明

### 鸣谢

- [HMCL](https://github.com/HMCL-dev/HMCL)：本项目的界面设计与交互参考。
- [Avalonia](https://avaloniaui.net/)：跨平台桌面 UI 框架。
- [TKCode.Arc](https://github.com/AIDotNet/TKCode.Arc)：Arc 控件与主题。
- [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui)：TUI 框架。
- [.NET](https://dotnet.microsoft.com/) 与 [Inter](https://rsms.me/inter/) 字体：运行环境与界面字体。

### 说明

- 本项目以 MIT 许可证发布，见 [LICENSE](LICENSE)。
- 项目处于早期预览阶段，接口、配置格式与目录布局仍可能变化；请勿用于生产环境。
- 本项目不注入游戏进程、不读写游戏内存，也不提供作弊功能。
- 本项目与 Valve 无关联；Counter-Strike、Steam 及相关商标归 Valve 所有。
