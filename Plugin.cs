using System.Diagnostics.CodeAnalysis;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PluginDockStandalone;

[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
public sealed class Plugin : IDalamudPlugin {
	private readonly PluginDockController controller = new();
	[PluginService] public static IDalamudPluginInterface PI { get; private set; } = null!;
	[PluginService] public static ICommandManager Command { get; private set; } = null!;
	[PluginService] public static IChatGui Chat { get; private set; } = null!;
	[PluginService] public static IFramework Framework { get; private set; } = null!;
	[PluginService] public static IPluginLog Log { get; private set; } = null!;
	[PluginService] public static ITextureProvider Texture { get; private set; } = null!;

	public void Dispose() => controller.Dispose();
}