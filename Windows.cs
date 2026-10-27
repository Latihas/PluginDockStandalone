using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PluginDockStandalone;

internal sealed class DockOverlayWindow : Window {
	private readonly PluginDockController controller;

	public DockOverlayWindow(PluginDockController controller)
		: base("插件收纳栏###PluginDockStandalone.Dock") {
		this.controller = controller;
		RespectCloseHotkey = false;
		IsOpen = false;
		Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
	}

	public override void Draw() {
		Flags = controller.BuildOverlayFlags();
		controller.DrawDockLayout();
	}

	public override void OnClose() {
		controller.OnOverlayClosed();
	}
}

internal sealed class PluginDockConfigWindow(PluginDockController controller) : Window("插件收纳栏设置###PluginDockStandalone.Config") {
	public override void Draw() => controller.DrawConfigUi();
}