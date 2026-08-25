# 方向键选择视觉状态修复设计

> **状态说明：** 重复事件绑定已移除，并已有 XAML/Windows 自动测试；原生焦点框、虚拟化和 DPI 下的可见运行时证明仍在阶段五。当前证据见 [Clipboard 开发状态](../../STATUS.md)。

## 问题

剪贴板历史列表同时存在外层 `Grid` 的方向键处理和 `ListView` 的 `PreviewKeyDown` 处理。按一次上下方向键时，两套导航逻辑可能同时更新选择状态，造成多张卡片保留蓝色选中边框。

## 目标

- 每次按上/下方向键只移动一个列表项。
- 任意时刻只保留一个列表项的选中视觉状态。
- 保留鼠标点击、回车粘贴、Escape 关闭和自动滚动行为。

## 方案

移除 `HistoryList` 上的 `PreviewKeyDown="Root_KeyDown"`，保留根 `Grid` 的 `KeyDown="Root_KeyDown"` 作为唯一方向键入口。列表项中的按键事件继续沿路由冒泡到根元素；`ClipboardPanelViewModel.SelectedIndex` 仍是选择状态的唯一来源，`HistoryList_SelectionChanged` 继续处理鼠标选择同步。

## 测试

在 `XamlResourceConfigurationTests` 中增加回归断言，验证根元素保留 `KeyDown="Root_KeyDown"`，而历史列表不注册 `PreviewKeyDown`。先运行该测试确认当前实现失败，再移除重复事件绑定并运行 Windows 测试集及构建验证。

## 范围与风险

本次只修改 XAML 事件绑定和对应配置测试，不改变 ViewModel 选择算法。若列表焦点事件无法冒泡到根元素，则测试或人工验证会暴露该问题，再单独调整事件入口。
