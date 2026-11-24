# Hello Counter-Strike! Launcher

> [!IMPORTANT]
> **温馨提示**：本项目主要采用 **氛围编程（Vibe Coding）** 方式开发，开发过程中使用了 AI 辅助。
>
> 如果您介意这种开发方式，可以选择不使用本项目。

用 SteamCMD 部署 CS2 专用服务器，把不同玩法的配置保存为预设，再在控制台里查看日志、换图和管理玩家。不想开服？可以单独使用客户端连接和保存常用服务器，并通过 Steam 加入。

## 选一个入口


| 你想做什么               | 入口                          |
| -------------------------- | ------------------------------- |
| 在桌面上部署和管理服务器 | [服务端 GUI](src/HCSL.Host)   |
| 在终端里管理服务器       | [服务端 TUI](src/HCSL.Tui)    |
| 保存服务器地址并连接     | [连接客户端](src/HCSL.Client) |

GUI 和 TUI 都由[本机守护进程](src/HCSL.Daemon)负责管理 SteamCMD 和 CS2，但两者的功能并不完全相同。连接客户端不负责部署服务器。

## 从源码启动

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。Linux 下运行桌面入口需要图形会话和相关系统库（如 `libx11`、`libfontconfig`）；终端入口需要支持 UTF-8 的终端。使用连接客户端时，本机需要 Steam。

```sh
git clone https://github.com/CaIam1T4s/HCSL.git

cd HCSL

# 按需选择一条
# 服务端 GUI
dotnet run --project src/HCSL.Host

# 服务端 TUI
dotnet run --project src/HCSL.Tui

# 连接客户端
dotnet run --project src/HCSL.Client
```

## 开服时会用到什么？

- **实例和预设**：用实例隔离运行环境，预设保存插件与 CFG 配置。同一实例的多个预设可以引用同一个图池。
- **部署和控制台**：通过 SteamCMD 安装、更新或校验 CS2；部署后可启动、停止服务，在独立控制台查看日志、发送命令、换图和管理在线玩家。
- **地图与插件**：可登记工坊单图、导入合集、导入本地插件包、安装框架和管理管理员名单。登记工坊地图不会立即下载；插件能否热重载取决于框架。

内容库默认位于用户主目录下的 `HCSL-Library`，与实例数据分开存放。

## 预览版边界

- **平台**：项目面向 Windows 和 Linux；Linux 是服务端的主要验证环境。Windows 端到端运行、安装器和代码签名尚未完成验证。
- **连接**：客户端通过 Steam 发起直连，地址格式通过校验不代表服务器可达。公网开服流程、GSLT 和 VAC 修改入口尚未接入。
- **地图轮换**：内置图池可走地图组轮换，工坊合集可走服务端原生合集轮换；混合内置地图和工坊单图的图池不支持自动轮换。
- **数据兼容**：实例导入和分享尚未接入。配置与目录布局可能变化，不建议用于生产环境。

## 鸣谢与许可证

界面设计参考 [HMCL](https://github.com/HMCL-dev/HMCL)。项目使用 [Avalonia](https://avaloniaui.net/)、[TKCode.Arc](https://github.com/AIDotNet/TKCode.Arc)、[Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) 和 [Inter](https://rsms.me/inter/) 字体。

项目采用 [MIT 许可证](LICENSE)，与 Valve 无关联，也未获 Valve 背书。Counter-Strike 及相关商标归 Valve 所有。
