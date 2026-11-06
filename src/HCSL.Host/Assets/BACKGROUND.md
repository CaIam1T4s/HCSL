# 默认背景

`Views/Controls/DefaultBackground.axaml` 使用纯色渐变作为占位背景，不包含图片或建筑图形。
颜色通过 `App.axaml` 中的 `BackdropStartColor` 和 `BackdropEndColor` 动态资源读取：
浅色主题为蓝灰至暖米色，深色主题为深蓝灰至暖棕色，跟随系统模式沿用系统主题。

自定义 PNG/JPEG 置于默认背景上方，恢复默认时释放 Bitmap 并显示当前主题渐变。
