using Dalamud.Plugin;

namespace PluginDockStandalone;

public sealed class PluginDockPlugin : IDalamudPlugin {
	public string Name => "插件收纳栏";

	private readonly PluginDockController controller;

	public PluginDockPlugin(IDalamudPluginInterface pluginInterface) {
		DService.Initialize(pluginInterface);
		controller = new PluginDockController();
	}

	public void Dispose() {
		controller.Dispose();
	}
}