using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;
using Dalamud.Game.Text;

namespace PluginDockStandalone;

internal sealed partial class PluginDockController {
	private sealed class IconLibraryApplyTarget {
		public DockItemKind Kind = DockItemKind.Plugin;
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

	private sealed class Config : IPluginConfiguration {
		public int Version { get; set; } = 1;
		public bool DockEnabled = true;
		public bool DockOpen;
		public bool NoDecoration = true;
		public bool Collapsed;
		public bool LockPosition;
		public bool TransparentButtons = true;
		public float IconSize = 32f;
		public float IconSpacing = 4f;
		public string? DockHeaderIconPath;
		public int MaxIconsPerRow;
		public DockExpandDirection ExpandDirection = DockExpandDirection.Right;
		public DockWrapDirection WrapDirection = DockWrapDirection.Down;
		public float AutoHideSeconds;
		public bool DockAnchorInitialized;
		public Vector2 DockAnchor;
		public List<DockItem> Items = [];

		public bool WindowHiderEnabled = true;
		public List<string> HiddenImGuiWindows = [];
		public Dictionary<string, ImGuiWindowRestoreState> HiddenImGuiWindowRestoreStates = new(StringComparer.OrdinalIgnoreCase);
	}

	private sealed class ImGuiWindowRestoreState {
		public bool HasPosition;
		public Vector2 Position;

		public bool HasSize;
		public Vector2 Size;

		public bool HasCollapsed;
		public bool Collapsed;
	}

	private sealed class PendingImGuiWindowRestore {
		public ImGuiWindowRestoreState RestoreState = new();
		public int RemainingFrames;
	}

	private sealed class DockItem {
		public DockItemKind Kind = DockItemKind.Plugin;
		public string InternalName = string.Empty;
		public string? CustomDisplayName;
		public bool Hidden;
		public string? CustomIconPath;
		public uint CustomGameIconId;
		public string? CustomSeIconChar;
		public string? LinkedPluginInternalName;
		public bool PreferConfigUiOnClick;
		public string? ClickCommand;
		public List<DockItemMacro> ContextMenuMacros = [];
		public bool ToggleOnClick;
		public string? ToggleTargetWindowName;
	}

	private sealed class DockItemMacro {
		public string Name = string.Empty;
		public string Command = string.Empty;
	}

	private enum DockItemKind {
		Plugin = 0,
		ImGuiWindow = 1,
		Command = 2
	}

	private sealed class WindowHiderPreset {
		public string Name = string.Empty;
		public List<string> HiddenWindows = [];
	}

	private enum DockExpandDirection {
		Right = 0,
		Left = 1,
		Down = 2,
		Up = 3
	}

	private enum DockWrapDirection {
		Down = 0,
		Up = 1,
		Right = 2,
		Left = 3
	}
}