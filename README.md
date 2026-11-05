# CS2 Launcher

Windows / Linux CS2 服主管理端的 Avalonia 应用骨架。

## 开发运行

安装 .NET 10 SDK（以及平台所需的 Avalonia 桌面运行依赖），然后执行：

```sh
dotnet restore CS2Launcher.slnx
dotnet build CS2Launcher.slnx
dotnet run --project src/CS2Launcher.Host
```

使用 Avalonia 12.1.4 与 TKCode.Arc 0.4.0-preview.1，版本依据 Arc 官方兼容说明锁定。
Arc 为预览依赖，升级前需要验证 API 与主题兼容性。

## 当前范围

- HMCL 风格的分组侧栏、自绘标题栏与简洁首页；Arc 胶囊按钮和内置图标。
- 随主题变化的渐变占位背景，支持本地 PNG/JPEG 自定义背景与恢复默认；不附带游戏图片。
- 首页、实例管理、实例列表、下载中心、联机组网和设置导航；各页面有独立功能占位和明确的未接入状态。
- 浅色、深色与系统主题切换，Arc 控件与页面同步；主题和背景设置仅在当前会话生效。
- 浅色采用暖米灰，所有 Arc 按钮统一胶囊；主题预设、控件样式集中管理，见 [界面样式约定](docs/ui/design-system.md)。
- 设置支持 0–100% 背景不透明度；次级按钮与小按钮采用紧凑高度。
- MVVM 结构与可替换的主题服务，页面业务暂未接入。

尚未实现实例持久化、Steam 登录、服务器下载/启动、插件、EasyTier、玩家客户端或 TUI。
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

当前环境已通过 .NET 10 SDK 的 `dotnet build CS2Launcher.slnx --no-restore`，零警告、零错误。
尝试运行时因无法连接 X 显示服务（`XOpenDisplay failed`）退出，尚未完成桌面渲染验证。
手动检查步骤见 [界面验收](docs/ui/shell-review.md)。
