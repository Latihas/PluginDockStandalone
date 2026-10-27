using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Dalamud.Configuration;

namespace PluginDockStandalone;

[SuppressMessage("ReSharper", "FieldCanBeMadeReadOnly.Global")]
public class Config : IPluginConfiguration {
	public int Version { get; set; }
	public bool Collapsed;
	public bool LockPosition;
	public float IconSize = 32f;
	public float IconSpacing = 4f;
	public string? DockHeaderIconPath;
	public int MaxIconsPerRow;
	public bool DockAnchorInitialized;
	public Vector2 DockAnchor;
	public List<DockItem> Items = [];
	internal void Save() => Plugin.PI.SavePluginConfig(this);

	public sealed class DockItem {
		public DockItemKind Kind = DockItemKind.Plugin;
		public string InternalName = string.Empty;
		public string? CustomDisplayName;
		public bool Hidden;
		public string? CustomIconPath;
		public uint CustomGameIconId;
		public string? CustomSeIconChar;
		public string ClickCommand = "";
		public List<DockItemMacro> ContextMenuMacros = [];
	}

	public enum DockItemKind {
		Plugin = 0,
		Command = 1
	}

	public sealed class DockItemMacro {
		public string Name = string.Empty;
		public string Command = string.Empty;
	}
}