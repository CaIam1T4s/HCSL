# Hello_Counter_Strike_Launcher (HCSL)

Windows / Linux CS2 服主管理端的 Avalonia 应用骨架。

## 开发运行

安装 .NET 10 SDK（以及平台所需的 Avalonia 桌面运行依赖），然后执行：

```sh
dotnet restore Hello_Counter_Strike_Launcher.slnx
dotnet build Hello_Counter_Strike_Launcher.slnx
dotnet run --project src/HCSL.Host
```

使用 Avalonia 12.1.4 与 TKCode.Arc 0.4.0-preview.1，版本依据 Arc 官方兼容说明锁定。
Arc 为预览依赖，升级前需要验证 API 与主题兼容性。

## 当前范围

- HMCL 风格的分组侧栏、自绘标题栏与简洁首页；Arc 胶囊按钮和内置图标。
- 随主题变化的渐变占位背景，支持本地 PNG/JPEG 自定义背景与恢复默认；不附带游戏图片。
- 首页、实例管理、实例列表、下载中心和设置导航；各页面有独立功能占位和明确的未接入状态。
- 浅色、深色与系统主题切换，Arc 控件与页面同步；主题和背景设置仅在当前会话生效。
- 浅色采用暖米灰，所有 Arc 按钮统一胶囊；主题预设、控件样式集中管理，见 [界面样式约定](docs/ui/design-system.md)。
- 设置支持背景显示强度、侧栏着色不透明度和应用内背景模糊；窗口保持不透明。次级按钮与小按钮采用紧凑高度。
- MVVM 结构与可替换的主题服务；下载中心已接入本机宿主的 SteamCMD 工具探测，其他服务器业务待接入。

实例持久化、Linux 单实例生命周期、GUI/TUI 服务器控制、本地插件包与 CFG/地图池管理已实现；玩家 Steam 身份认证尚待对齐。组网已取消；独立玩家客户端已实现服务器列表、IP/域名格式校验、系统密码存储、Steam 连接请求与手动命令后备（真实 Steam/CS2 联动待验收），见 [玩家客户端](docs/player-client.md)。运行：`dotnet run --project src/HCSL.Client`。
没有真实或模拟的成功启动行为。实例将只管理配置和插件，CS2 安装及版本由全局服务管理。

## 结构

`Views` 负责布局，`ViewModels` 负责导航及展示状态，`Services` 封装平台/UI 服务。
后续通过服务接口接入宿主进程，不在页面中直接运行 SteamCMD 或 CS2。

完整目录与依赖约束见仓库技能 [cs2-project-architecture](skills/cs2-project-architecture/SKILL.md)。
`AGENTS.md` 指向此技能，供后续开发读取。规划目录已使用 `.gitkeep` 保留，
尚未实现的模块没有创建空工程，也没有加入 solution。

## 验证

在安装 SDK 的桌面环境中构建后，检查六个导航入口、三种主题、最小窗口尺寸以及键盘导航。
首页启动按钮应保持禁用，所有未接入页面明确显示待实现状态。

当前环境已通过 .NET 10 SDK 的 `dotnet build Hello_Counter_Strike_Launcher.slnx --no-restore`，零警告、零错误。
尝试运行时因无法连接 X 显示服务（`XOpenDisplay failed`）退出，尚未完成桌面渲染验证。
手动检查步骤见 [界面验收](docs/ui/shell-review.md)。

TUI 使用与 GUI 对应的五项一级导航、左侧二级菜单、共享服务器状态区和单行快捷键栏。一级与二级用 Tab/Shift+Tab 同级切换并即时显示，Enter 进入下一级，Esc 返回；实例列表采用中央列表与底部操作按钮。在设置页可以即时切换中文/英文，语言仅替换文字；固定深色配色与 Unicode 字符，语言为会话设置。F7 打开配置预设，F8 打开管理。F5 启动及管理页停服/恢复、日志已接入；VAC 修改、玩家 Steam 配对仍未接入；SteamCMD 工具任务已通过本机 IPC 接入下载中心。

服主 GUI/TUI 的配置组合统一称为“配置预设”：实例内可有多套，一次运行一套，切换前需停服。两端已提供新建、复制、重命名、删除、切换的禁用入口，存储与执行业务尚未接入。

## 宿主基础

已实现独立的目录初始化、稳定身份及跨进程管理租约，GUI/TUI 下载中心已通过本机 IPC 使用相关能力。使用方式、目录约束和验证命令见 [目录与管理租约](docs/workspace-foundation.md)，后续任务见 [宿主与 SteamCMD 计划](docs/plans/host-foundation-steamcmd.md)。

已接入独立 SteamCMD 工具与登录探测入口，支持受管安装、自更新状态、匿名/账号交互、取消及遗留会话恢复，见 [SteamCMD 会话说明](docs/steamcmd-session.md)。现已支持全局 CS2 安装/更新与校验，使用方式见 [CS2 安装说明](docs/cs2-installation.md)；修复输出解析后，真实匿名登录探测已成功；网络连接仍可能波动，账号交互仅通过模拟验证。

## 启动本机宿主与界面

直接启动 TUI 或 GUI，应用会自动连接或启动随附的本机 Daemon；失败后可在下载中心重试连接。两端共用 SteamCMD 任务，关闭界面不会取消已提交任务。下载中心还提供全局 CS2 安装/更新及校验，准备/登录按钮仍只探测登录。完整操作与验证边界见 [本机宿主与两端接入](docs/local-daemon-ui.md)。

设置、私有数据、内容库及实例目录的统一约定见 [目录布局](docs/directory-layout.md)。新内容库默认在用户主目录的 `HCSL-Library`，已有工作区继续使用保存的位置，不自动迁移。

实例与配置预设持久化及 TUI 管理见 [使用与验证说明](docs/instance-storage.md)。Linux 离线实例部署已有独立 CLI；GUI/TUI 开服仍待接入，Daemon 的 CLI 验证入口见下文。

Linux 实例离线部署的预检、部署与中断恢复见 [部署说明](docs/deployment/offline-deployment.md)。不启动 CS2；TUI F5 开服仍待接入。

Daemon 单实例启动、日志、停服与会话恢复已有 [Linux 命令行验证入口](docs/server-lifecycle.md)。 日常调试与插件/部署诊断优先使用 [hcsl 开发命令行](docs/dev-cli.md)。 自动化无法覆盖的界面/外部程序/平台组合与人工验证步骤见 [人工验证清单](docs/manual-verification.md)。目前限定回环 LAN 验证模式，GUI/TUI 服务器控制仍待接入。真实 CS2 的端到端验收步骤与检查项见 [单实例验收清单](docs/acceptance/real-cs2-single-instance.md)；本轮只完成脚本与清单，尚未对真实 CS2 执行。
