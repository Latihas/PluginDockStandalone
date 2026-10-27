using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PluginDockStandalone;

internal class DService {
	public static void Initialize(IDalamudPluginInterface pluginInterface)
		=> pluginInterface.Create<DService>();

	[PluginService] public static IDalamudPluginInterface PI { get; private set; } = null!;
	[PluginService] public static ICommandManager Command { get; private set; } = null!;
	[PluginService] public static IChatGui Chat { get; private set; } = null!;
	[PluginService] public static IFramework Framework { get; private set; } = null!;
	[PluginService] public static IPluginLog Log { get; private set; } = null!;
	[PluginService] public static ITextureProvider Texture { get; private set; } = null!;
	[PluginService] public static INotificationManager NotificationManager { get; private set; } = null!;
}