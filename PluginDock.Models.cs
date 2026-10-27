using Dalamud.Game.Text;

namespace PluginDockStandalone;

internal sealed partial class PluginDockController {
	private sealed class IconLibraryApplyTarget {
		public Config.DockItemKind Kind = Config.DockItemKind.Plugin;
		public string InternalName = string.Empty;
		public IconLibraryApplyField Field = IconLibraryApplyField.GameIconId;
	}

	private enum IconLibraryMode {
		GameIcon = 0,
		SeIconChar = 1
	}

	private enum IconLibraryApplyField {
		GameIconId = 0,
		SeIconChar = 1
	}

	private enum SeIconCharOutputFormat {
		Name = 0,
		Decimal = 1,
		Hex = 2
	}

	private sealed class SeIconCharEntry {
		public SeIconChar Icon;
		public string Name = string.Empty;
		public string IconText = string.Empty;
		public int Value;
	}
}