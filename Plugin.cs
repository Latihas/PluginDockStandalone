using System.Diagnostics.CodeAnalysis;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PluginDockStandalone;

[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
public sealed class Plugin : IDalamudPlugin {
	internal static PluginDockController controller = null!;
	[PluginService] internal static IDalamudPluginInterface PI { get; private set; } = null!;
	[PluginService] internal static IFramework Framework { get; private set; } = null!;
	[PluginService] internal static IPluginLog Log { get; private set; } = null!;
	[PluginService] internal static ITextureProvider Texture { get; private set; } = null!;

	public Plugin() {
		controller = new();
	}

	public void Dispose() => controller.Dispose();
}