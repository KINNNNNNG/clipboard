# 原生 DWM 面板边框设计

## 目标

让剪贴板面板使用 Windows 11 原生的灰色窗口边框和圆角，避免应用自行绘制边框后在主题、DPI、激活状态变化时出现白边或黑边。

## 方案

- 使用 `OverlappedPresenter.SetBorderAndTitleBar(true, false)`：保留系统边框，隐藏标题栏。
- 使用 `DWMWA_BORDER_COLOR` 的 `DWMWA_COLOR_DEFAULT`，让 DWM 根据系统主题和激活状态选择边框颜色。
- 保留 `DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND`，由 DWM 处理圆角。
- 删除 `SetWindowRgn`、`CreateRoundRectRgn`、`WS_EX_LAYERED`、`SetLayeredWindowAttributes` 和 `WM_STYLECHANGING` 样式过滤；这些是之前手工模拟无边框窗口的机制，会阻止系统接管圆角和边框。
- 顶层 XAML 使用不透明的主题背景，不再用透明宿主和顶层手工圆角；列表卡片等内部控件样式保持不变。

## 行为边界

Win+V 接管、窗口定位、点击外部隐藏、搜索、图片加载和设置窗口行为不变。面板仍不可调整大小、不可最大化、不可最小化，也不显示在 Alt+Tab 切换器中。

## 验证

- 单元测试确认 presenter 使用 `hasBorder=true`、`hasTitleBar=false`，且不再调用手工边框刷新。
- 单元测试确认顶层 XAML 使用主题背景，不包含透明宿主或顶层手工圆角。
- x64 构建必须 0 警告、0 错误。
- 运行时检查窗口无自定义 region，样式由系统 presenter 管理；屏幕截图确认边框为系统灰色、四角圆润，且没有额外白边或黑边。
