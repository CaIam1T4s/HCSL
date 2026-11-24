# Hello Counter-Strike! Launcher

**HCSL · 0.2.3 预览版**

HCSL 是面向 Windows 和 Linux 的 CS2 服务端管理工具和连接客户端，使用 .NET 10、Avalonia 12.1.5 和 TKCode.Arc 1.1.0 构建。

## 当前可用功能

### 服务端 GUI

- **实例与预设**：实例隔离运行环境，预设保存插件和 CFG 配置。图池由实例管理，由预设引用。
- **地图与图池**：管理内置地图和工坊地图，支持从工坊链接或 ID 登记单图、导入合集。登记不等于下载；内置图池可使用地图组轮换，工坊合集走服务端原生合集轮换，混合内置地图与单张工坊地图的图池不支持自动轮换。
- **部署与运行**：通过 SteamCMD 安装、更新和校验 CS2；支持独立部署、启动、停服和部署清理。
- **独立控制台**：查看和搜索日志、发送命令、即时换图、查看在线名单和踢人。
- **插件与管理员**：导入本地插件包、安装框架、管理管理员名单；是否支持热重载取决于所用框架。

### 服务端 TUI

提供终端下的服务端管理入口，包括实例与预设、日志、CFG 浏览和图池编辑。GUI 和 TUI 的功能及操作方式并不完全相同。

### 客户端

保存服务器地址、端口与密码，并通过 Steam 发起连接。地址格式校验不代表服务器可达。

## 快速开始

以下是从源码运行的方式，不依赖已发布的安装包。开发和构建需要 **.NET 10 SDK**；框架依赖版的最终用户需要适用的 .NET 运行时，而不是 SDK。

```sh
git clone https://github.com/CaIam1T4s/Hello_Counter_Strike_Launcher.git
cd Hello_Counter_Strike_Launcher

dotnet restore Hello_Counter_Strike_Launcher.slnx
dotnet build Hello_Counter_Strike_Launcher.slnx
```

按需运行一个入口：

```sh
# 服务端 GUI
dotnet run --project src/HCSL.Host

# 服务端 TUI
dotnet run --project src/HCSL.Tui

# 客户端
dotnet run --project src/HCSL.Client
```

Linux 下，桌面入口需要可用的图形会话及相关系统库（例如 `libx11`、`libfontconfig`）；TUI 需要支持 UTF-8 的终端。服务端 GUI 和 TUI 会连接或启动本机守护进程，由后者管理 CS2 与 SteamCMD。

## 数据位置

设置、私有数据、实例和内容库分别管理。内容库默认位于用户主目录下的 `HCSL-Library`。服务端运行数据不写入源码目录作为项目资产。

## 当前限制与验证范围

- **网络**：以直接连接为主。公网开服流程、GSLT 和 VAC 修改入口尚未接入；能发起连接不代表目标公网可达。
- **平台**：Linux 是服务端和 TUI 的主要验证环境。Windows 的端到端运行、安装器和代码签名尚未完成验证。
- **验证**：解决方案构建和全量自动化测试已有通过记录，但不能替代真实桌面、Steam 工坊和 CS2 验证。例如，在运行中的合集内手动换图后是否保留原轮换，仍需实测。
- **预览版**：实例导入／分享尚未接入。接口、配置格式和目录布局仍可能变化，不建议用于生产环境。

## 项目结构

| 路径 | 用途 |
|---|---|
| `src/HCSL.Host` | 服务端 GUI |
| `src/HCSL.Tui` | 服务端终端界面 |
| `src/HCSL.Client` | 客户端 |
| `src/HCSL.Daemon` | 本机守护进程，通过 IPC 为服务端界面提供能力 |
| `build/HCSL.Dev` | 开发与诊断 CLI，不随发行包提供 |
| `tests/` | 自动化测试 |

## 鸣谢与许可证

界面设计参考 [HMCL](https://github.com/HMCL-dev/HMCL)。项目使用 [.NET](https://dotnet.microsoft.com/)、[Avalonia](https://avaloniaui.net/)、[TKCode.Arc](https://github.com/AIDotNet/TKCode.Arc)、[Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) 和 [Inter](https://rsms.me/inter/) 字体。

本项目以 [MIT 许可证](LICENSE)发布，与 Valve 无关联，也未获 Valve 背书。Counter-Strike 及相关商标归 Valve 所有。
