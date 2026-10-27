using Dalamud.Interface.Windowing;

namespace PluginDockStandalone;

internal sealed class DockOverlayWindow : Window {
	public DockOverlayWindow() : base("插件收纳栏###PluginDockStandalone.Dock") {
		RespectCloseHotkey = false;
		IsOpen = true;
	}

	public override void Draw() => Plugin.controller.DrawDockLayout();
	public override void OnClose() => Plugin.controller.OnOverlayClosed();
}

internal sealed class PluginDockConfigWindow() : Window("插件收纳栏设置###PluginDockStandalone.Config") {
	public override void Draw() => Plugin.controller.DrawConfigUi();
}