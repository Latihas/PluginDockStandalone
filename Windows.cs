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
		controller.DrawOverlayUi();
	}

	public override void OnClose() {
		controller.OnOverlayClosed();
	}
}

internal sealed class PluginDockConfigWindow : Window {
	private readonly PluginDockController controller;

	public PluginDockConfigWindow(PluginDockController controller)
		: base("插件收纳栏设置###PluginDockStandalone.Config") {
		this.controller = controller;
		IsOpen = false;
	}

	public override void Draw() {
		controller.DrawConfigUi();
	}
}