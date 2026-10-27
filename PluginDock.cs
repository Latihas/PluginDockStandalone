using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using static System.String;
using static PluginDockStandalone.Config;

namespace PluginDockStandalone;

internal sealed partial class PluginDockController : IDisposable {
	private const string IconLibraryWindowKey = "###PluginDockStandalone.IconLibrary";
	private const string IconLibraryWindowName = "图标素材库" + IconLibraryWindowKey;
	private static readonly Vector4 DockItemKindColorPlugin = new(0.45f, 0.85f, 0.55f, 1f);
	private static readonly Vector4 DockItemKindColorCommand = new(1.0f, 0.75f, 0.25f, 1f);
	private static readonly HttpClient IconHttp = new() {
		Timeout = TimeSpan.FromSeconds(15)
	};
	private readonly Config ModuleConfig;
	private readonly WindowSystem windowSystem = new("PluginDock");
	private readonly FileDialogManager fileDialogManager = new();
	private readonly DockOverlayWindow Overlay;
	private readonly PluginDockConfigWindow ConfigWindow;
	private bool fileDialogActive;
	private string search = Empty;
	private bool showImGuiMetricsWindow;
	private int pinnedEditorSelectedIndex = -1;
	private bool dockAnchorDirty;
	private bool dockDragging;
	private bool dockDragMoved;
	private Vector2 dockDragStartMouse;
	private Vector2 dockDragStartAnchor;
	private readonly List<(DockItem Item, IExposedPlugin? Plugin)> visibleDockItemsScratch = [];
	private bool visibleDockItemsCacheValid;
	private bool visibleDockItemsCacheCollapsed;
	private int visibleDockItemsCacheItemsVersion;
	private int visibleDockItemsCachePluginsVersion;
	private long loadedPluginsCacheNextRefreshAtTick;
	private int loadedPluginsCacheVersion;
	private List<IExposedPlugin> loadedPluginsCache = [];
	private Dictionary<string, IExposedPlugin> loadedPluginByInternalNameCache = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> localIconPathCache = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, Task> iconDownloadTasks = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, DateTime> iconDownloadCooldownUntilUtc = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, ISharedImmediateTexture> textureCache = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string?> resolvedIconPathCache = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<uint, ISharedImmediateTexture> gameIconCache = [];
	private ISharedImmediateTexture? fallbackPluginIcon;
	private bool showIconLibraryWindow;
	private IconLibraryApplyTarget? iconLibraryApplyTarget;
	private IconLibraryMode iconLibraryMode = IconLibraryMode.GameIcon;
	private float iconLibraryPreviewIconSize = 32f;
	private int iconLibraryGameIconStartId = 1;
	private int iconLibraryGameIconCount = 240;
	private int iconLibraryGameIconJumpId;
	private bool iconLibraryGameIconOnlyValid = true;
	private uint iconLibraryLastSelectedGameIconId;
	private string iconLibraryLastActionMessage = Empty;
	private string iconLibrarySeIconFilter = Empty;
	private string iconLibrarySeIconFilterLast = Empty;
	private readonly List<int> iconLibrarySeIconFilteredIndices = [];
	private SeIconCharOutputFormat iconLibrarySeIconOutputFormat = SeIconCharOutputFormat.Decimal;
	private List<SeIconCharEntry>? seIconCharEntries;
	private readonly Dictionary<uint, ISharedImmediateTexture> gameIconPreviewCache = [];
	private readonly Queue<uint> gameIconPreviewCacheOrder = new();

	public PluginDockController() {
		ModuleConfig = Plugin.PI.GetPluginConfig() as Config ?? new();
		if (ModuleConfig.Items.RemoveAll(x =>
			    x.Kind == DockItemKind.Plugin &&
			    x.InternalName.Equals("PluginDock", StringComparison.OrdinalIgnoreCase)) > 0) ModuleConfig.Save();
		windowSystem.AddWindow(Overlay = new DockOverlayWindow(this));
		windowSystem.AddWindow(ConfigWindow = new PluginDockConfigWindow(this));
		Overlay.Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
		                ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
		                ImGuiWindowFlags.AlwaysAutoResize;
		if (ModuleConfig.LockPosition) Overlay.Flags |= ImGuiWindowFlags.NoMove;
		Overlay.IsOpen = true;
		Plugin.PI.UiBuilder.Draw += Draw;
		Plugin.PI.UiBuilder.OpenConfigUi += OpenConfigUi;
		Plugin.PI.UiBuilder.OpenMainUi += OpenConfigUi;
	}

	public void Dispose() {
		Plugin.PI.UiBuilder.Draw -= Draw;
		Plugin.PI.UiBuilder.OpenConfigUi -= OpenConfigUi;
		Plugin.PI.UiBuilder.OpenMainUi -= OpenConfigUi;
		windowSystem.RemoveAllWindows();
	}

	internal void DrawConfigUi() {
		var lockPosition = ModuleConfig.LockPosition;
		if (ImGui.Checkbox("锁定位置", ref lockPosition)) {
			ModuleConfig.LockPosition = lockPosition;
			Overlay.Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
			                ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
			                ImGuiWindowFlags.AlwaysAutoResize;
			if (ModuleConfig.LockPosition) Overlay.Flags |= ImGuiWindowFlags.NoMove;
			
			ModuleConfig.Save();
		}
		var dockHeaderIconPath = ModuleConfig.DockHeaderIconPath ?? Empty;
		if (DrawDockTextInputWithPicker("悬浮窗图标路径", "##dock_header_icon_path", ref dockHeaderIconPath, 260, "浏览", "##dock_header_icon_pick", OpenDockHeaderIconPicker)) {
			var trimmed = dockHeaderIconPath.Trim();
			ModuleConfig.DockHeaderIconPath = IsNullOrWhiteSpace(trimmed) ? null : trimmed;
			ModuleConfig.Save();
		}
		var iconSize = ModuleConfig.IconSize;
		if (ImGui.SliderFloat("图标大小", ref iconSize, 16f, 64f, "%.0f")) {
			ModuleConfig.IconSize = iconSize;
			ModuleConfig.Save();
		}
		var iconSpacing = ModuleConfig.IconSpacing;
		if (ImGui.SliderFloat("图标间距", ref iconSpacing, 0f, 16f, "%.0f")) {
			ModuleConfig.IconSpacing = iconSpacing;
			ModuleConfig.Save();
		}
		var maxIconsPerRow = ModuleConfig.MaxIconsPerRow;
		if (ImGui.SliderInt("每行最多##dock_max_row", ref maxIconsPerRow, 0, 50)) {
			ModuleConfig.MaxIconsPerRow = maxIconsPerRow;
			ModuleConfig.Save();
		}
		var loadedPlugins = GetLoadedPluginsCached();
		var uiPlugins = loadedPlugins.Where(p => p.HasMainUi || p.HasConfigUi).ToList();
		if (uiPlugins.Count == 0)
			ImGui.TextDisabled("未找到可打开主界面/设置的已加载插件（仍可编辑已收纳的“窗口”条目）。");
		var lineHeight = ImGui.GetTextLineHeightWithSpacing();
		var listHeight = MathF.Min(360f, lineHeight * 16f);
		if (ImGui.BeginTable("dock_manage", 2, ImGuiTableFlags.SizingStretchProp |
		                                       ImGuiTableFlags.BordersInnerV |
		                                       ImGuiTableFlags.NoSavedSettings)) {
			ImGui.TableSetupColumn($"已收纳 ({ModuleConfig.Items.Count})", ImGuiTableColumnFlags.WidthStretch, 0.55f);
			ImGui.TableSetupColumn("可收纳", ImGuiTableColumnFlags.WidthStretch, 0.45f);
			ImGui.TableHeadersRow();
			ImGui.TableNextRow();
			ImGui.TableNextColumn();
			if (ImGui.SmallButton("新增命令项##dock_add_cmd"))
				AddDockItemForCommand();
			ImGui.BeginChild("dock_pinned", new Vector2(0f, listHeight), true);
			DrawPinnedEditor(loadedPlugins);
			ImGui.EndChild();
			ImGui.TableNextColumn();
			ImGui.SetNextItemWidth(-1f);
			InputTextUtf8("搜索##dock_search", ref search, 128);
			DrawPluginPicker(uiPlugins);
			ImGui.EndTable();
		} else {
			ImGui.SetNextItemWidth(-1f);
			InputTextUtf8("搜索##dock_search", ref search, 128);
			DrawPluginPicker(uiPlugins);
			if (ImGui.CollapsingHeader($"已收纳 ({ModuleConfig.Items.Count})", ImGuiTreeNodeFlags.DefaultOpen)) {
				if (ImGui.SmallButton("新增命令项##dock_add_cmd_fallback"))
					AddDockItemForCommand();
				DrawPinnedEditor(loadedPlugins);
			}
		}
	}

	private void Draw() {
		windowSystem.Draw();
		DrawImGuiMetricsWindow();
		DrawIconLibraryWindow();
		if (fileDialogActive) fileDialogManager.Draw();
	}

	private void OpenConfigUi() => ToggleOverlayConfig();

	// private void OpenMainUi() {
	// 	// ApplyOverlayState();
	// 	
	// 	ModuleConfig.Save();
	// }

	private void ToggleOverlayConfig() {
		ConfigWindow.IsOpen = true;
	}

	private static string ConfigDirectoryPath {
		get {
			var path = Plugin.PI.GetPluginConfigDirectory();
			if (!Directory.Exists(path))
				Directory.CreateDirectory(path);
			return path;
		}
	}

	private void AddDockItemForCommand() {
		var displayNumber = ModuleConfig.Items.Count(x => x.Kind == DockItemKind.Command) + 1;
		var displayName = $"命令{displayNumber}";
		ModuleConfig.Items.Add(new DockItem {
			Kind = DockItemKind.Command,
			InternalName = $"Command-{Guid.NewGuid():N}",
			CustomDisplayName = displayName,
			Hidden = false,
			CustomIconPath = Empty,
			CustomGameIconId = 0,
			CustomSeIconChar = Empty,
			ClickCommand = Empty
		});
		pinnedEditorSelectedIndex = ModuleConfig.Items.Count - 1;
		ModuleConfig.Save();
	}

	internal void OnOverlayClosed() {
		ModuleConfig.Save();
	}

	private void EnsureLoadedPluginsCache() {
		var now = Environment.TickCount64;
		if (now < loadedPluginsCacheNextRefreshAtTick)
			return;
		loadedPluginsCacheNextRefreshAtTick = now + 1000;
		try {
			var plugins = GetLoadedPlugins()
				.Where(p => !p.InternalName.Equals("PluginDock", StringComparison.OrdinalIgnoreCase))
				.ToList();
			loadedPluginsCache = plugins;
			loadedPluginByInternalNameCache = plugins.ToDictionary(p => p.InternalName, StringComparer.OrdinalIgnoreCase);
			loadedPluginsCacheVersion++;
			visibleDockItemsCacheValid = false;
		} catch {
			loadedPluginsCache = [];
			loadedPluginByInternalNameCache = new Dictionary<string, IExposedPlugin>(StringComparer.OrdinalIgnoreCase);
			loadedPluginsCacheVersion++;
			visibleDockItemsCacheValid = false;
		}
	}

	private IReadOnlyList<IExposedPlugin> GetLoadedPluginsCached() {
		EnsureLoadedPluginsCache();
		return loadedPluginsCache;
	}

	private Dictionary<string, IExposedPlugin> GetLoadedPluginByInternalNameCached() {
		EnsureLoadedPluginsCache();
		return loadedPluginByInternalNameCache;
	}

	private static List<IExposedPlugin> GetLoadedPlugins() =>
		Plugin.PI.InstalledPlugins
			.Where(p => p.IsLoaded)
			.OrderBy(p => p.Name)
			.ToList();

	private static void DrawDockLabelWithHelp(string label) {
		//TODO
		ImGui.AlignTextToFramePadding();
		ImGui.TextUnformatted(label);
		ImGui.SameLine();
	}

	private static float CalcSmallButtonWidth(string label) =>
		ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;

	private static int GetValidUtf8Length(byte[] bytes, int maxLength) {
		var length = Math.Min(bytes.Length, maxLength); // 确保不会截断在 UTF-8 多字节字符的中间
		if (length > 0 && length < bytes.Length) {
			// 从 length-1 向前扫描，跳过所有续字节 (10xxxxxx)
			var pos = length - 1;
			while (pos > 0 && (bytes[pos] & 0xC0) == 0x80)
				pos--; // 现在 pos 指向一个非续字节（可能是 ASCII 或多字节起始）
			var b = bytes[pos];
			int charLen;
			if ((b & 0x80) == 0) charLen = 1; // ASCII: 0xxxxxxx
			else if ((b & 0xE0) == 0xC0) charLen = 2; // 2字节: 110xxxxx
			else if ((b & 0xF0) == 0xE0) charLen = 3; // 3字节: 1110xxxx
			else if ((b & 0xF8) == 0xF0) charLen = 4; // 4字节: 11110xxx
			else charLen = 1; // 无效字节，当作单字节			// 检查从 pos 开始的字符是否完整包含在 length 内
			if (pos + charLen > length)
				length = pos;
		}
		return length;
	}

	private static string DecodeUtf8Buffer(byte[] buffer) {
		var length = Array.IndexOf(buffer, (byte)0);
		if (length < 0)
			length = buffer.Length;
		return length <= 0 ? Empty : Encoding.UTF8.GetString(buffer, 0, length);
	}

	private static bool InputTextUtf8(string id, ref string value, int maxLength, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None) {
		if (maxLength <= 0)
			return false;
		var buffer = new byte[maxLength];
		var bytes = Encoding.UTF8.GetBytes(value);
		var length = GetValidUtf8Length(bytes, maxLength - 1);
		Array.Copy(bytes, buffer, length);
		var changed = ImGui.InputText(id, buffer, flags);
		if (!changed)
			return false;
		var newValue = DecodeUtf8Buffer(buffer);
		if (newValue == value)
			return false;
		value = newValue;
		return true;
	}

	private static bool DrawDockTextInputRow(string label, string id, ref string value, int maxLength) {
		DrawDockLabelWithHelp(label);
		ImGui.SetNextItemWidth(-1f);
		return InputTextUtf8(id, ref value, maxLength);
	}

	private static bool DrawDockTextInputWithPicker(
		string label,
		string inputId,
		ref string value,
		int maxLength,
		string pickLabel,
		string pickButtonId,
		Action onPick) {
		DrawDockLabelWithHelp(label);
		var buttonWidth = CalcSmallButtonWidth(pickLabel);
		var spacing = ImGui.GetStyle().ItemSpacing.X;
		ImGui.SetNextItemWidth(MathF.Max(80f, ImGui.GetContentRegionAvail().X - buttonWidth - spacing));
		var changed = InputTextUtf8(inputId, ref value, maxLength);
		ImGui.SameLine();
		if (ImGui.SmallButton($"{pickLabel}{pickButtonId}"))
			onPick();
		return changed;
	}

	private static bool DrawDockIntInputWithPicker(
		string label,
		string inputId,
		ref int value,
		string pickLabel,
		string pickButtonId,
		Action onPick) {
		DrawDockLabelWithHelp(label);
		var buttonWidth = CalcSmallButtonWidth(pickLabel);
		var spacing = ImGui.GetStyle().ItemSpacing.X;
		ImGui.SetNextItemWidth(MathF.Max(80f, ImGui.GetContentRegionAvail().X - buttonWidth - spacing));
		var changed = ImGui.InputInt(inputId, ref value);
		ImGui.SameLine();
		if (ImGui.SmallButton($"{pickLabel}{pickButtonId}"))
			onPick();
		return changed;
	}

	private void OpenDockHeaderIconPicker() {
		var startPath = ResolveDockHeaderIconPickerStartPath();
		fileDialogActive = true;
		fileDialogManager.OpenFileDialog(
			"选择悬浮窗图标",
			"图片{.png,.jpg,.jpeg}",
			(success, paths) => {
				fileDialogActive = false;
				if (!success || paths.Count == 0)
					return;
				var picked = paths[0].Trim();
				if (picked.Length == 0)
					return;
				ModuleConfig.DockHeaderIconPath = picked;
				ModuleConfig.Save();
			},
			1,
			startPath);
	}

	private string? ResolveDockHeaderIconPickerStartPath() {
		var current = ModuleConfig.DockHeaderIconPath?.Trim() ?? Empty;
		if (current.Length == 0)
			return null;
		if (Directory.Exists(current))
			return current;
		if (!Path.IsPathRooted(current))
			return null;
		var dir = Path.GetDirectoryName(current);
		return !IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : null;
	}

	private void DrawPinnedEditor(IReadOnlyList<IExposedPlugin> plugins) {
		if (ModuleConfig.Items.Count == 0) {
			pinnedEditorSelectedIndex = -1;
			ImGui.TextDisabled("（空）");
			return;
		}
		var pluginByInternalName = plugins.ToDictionary(p => p.InternalName, StringComparer.OrdinalIgnoreCase);
		for (var i = 0; i < ModuleConfig.Items.Count; i++) {
			var item = ModuleConfig.Items[i];
			var originalDisplayName = item.Kind == DockItemKind.Plugin && pluginByInternalName.TryGetValue(item.InternalName, out var p)
				? p.Name
				: item.InternalName;
			var displayName = item.Kind == DockItemKind.Command
				? IsNullOrWhiteSpace(item.CustomDisplayName)
					? "命令"
					: item.CustomDisplayName!.Trim()
				: IsNullOrWhiteSpace(item.CustomDisplayName)
					? originalDisplayName
					: item.CustomDisplayName!.Trim();
			ImGui.PushID(i);
			var canMoveUp = i > 0;
			ImGui.BeginDisabled(!canMoveUp);
			var moveUpClicked = ImGui.SmallButton("↑");
			ImGui.EndDisabled();
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("上移");
			ImGui.SameLine();
			var canMoveDown = i < ModuleConfig.Items.Count - 1;
			ImGui.BeginDisabled(!canMoveDown);
			var moveDownClicked = ImGui.SmallButton("↓");
			ImGui.EndDisabled();
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("下移");
			if (moveUpClicked && canMoveUp || moveDownClicked && canMoveDown) {
				var targetIndex = moveUpClicked ? i - 1 : i + 1;
				var expandedItem = pinnedEditorSelectedIndex >= 0 && pinnedEditorSelectedIndex < ModuleConfig.Items.Count
					? ModuleConfig.Items[pinnedEditorSelectedIndex]
					: null;
				(ModuleConfig.Items[i], ModuleConfig.Items[targetIndex]) = (ModuleConfig.Items[targetIndex], ModuleConfig.Items[i]);
				pinnedEditorSelectedIndex = expandedItem != null ? ModuleConfig.Items.IndexOf(expandedItem) : -1;
				ModuleConfig.Save();
				ImGui.PopID();
				return;
			}
			ImGui.SameLine();
			var hidden = item.Hidden;
			if (ImGui.Checkbox("隐藏", ref hidden)) {
				item.Hidden = hidden;
				ModuleConfig.Save();
			}
			ImGui.SameLine();
			var kindText = item.Kind switch {
				DockItemKind.Plugin => "[插件]",
				DockItemKind.Command => "[命令]",
				_ => $"[{item.Kind}]"
			};
			var kindColor = item.Kind switch {
				DockItemKind.Command => DockItemKindColorCommand,
				_ => DockItemKindColorPlugin
			};
			ImGui.TextColored(kindColor, kindText);
			ImGui.SameLine();
			ImGui.TextUnformatted(displayName);
			if (ImGui.IsItemHovered()) {
				if (item.Kind == DockItemKind.Command) {
					var cmd = (item.ClickCommand ?? Empty).Trim();
					ImGui.BeginTooltip();
					ImGui.TextUnformatted(cmd.Length == 0 ? "未设置命令" : cmd);
					ImGui.EndTooltip();
				} else if (!IsNullOrWhiteSpace(item.CustomDisplayName) &&
				           !displayName.Equals(originalDisplayName, StringComparison.Ordinal)) {
					ImGui.BeginTooltip();
					ImGui.TextUnformatted(originalDisplayName);
					ImGui.EndTooltip();
				}
			}
			ImGui.SameLine();
			var removeClicked = ImGui.SmallButton("删除");
			ImGui.SameLine();
			var expanded = pinnedEditorSelectedIndex == i;
			var editClicked = ImGui.SmallButton(expanded ? "收起" : "编辑");
			if (removeClicked) {
				RemoveDockItemAtIndex(i);
				i--;
				ImGui.PopID();
				continue;
			}
			if (editClicked)
				pinnedEditorSelectedIndex = expanded ? -1 : i;
			if (pinnedEditorSelectedIndex == i) {
				var customDisplayName = item.CustomDisplayName ?? Empty;
				if (DrawDockTextInputRow("显示名称", "##dock_custom_name", ref customDisplayName, 128)) {
					item.CustomDisplayName = customDisplayName.Trim();
					ModuleConfig.Save();
				}
				var clickCommand = item.ClickCommand ?? Empty;
				if (DrawDockTextInputRow("点击命令", "##dock_click_cmd", ref clickCommand, 260)) {
					item.ClickCommand = clickCommand.Trim();
					ModuleConfig.Save();
				}
				DrawDockItemContextMacrosEditor(item);
				var gameIconId = (int)item.CustomGameIconId;
				if (DrawDockIntInputWithPicker("游戏图标ID", "##dock_game_icon_id", ref gameIconId, "素材库", "##pick_game_icon",
					    () => OpenIconLibraryForDockItem(item, IconLibraryApplyField.GameIconId, IconLibraryMode.GameIcon))) {
					item.CustomGameIconId = (uint)Math.Clamp(gameIconId, 0, int.MaxValue);
					ModuleConfig.Save();
				}
				var customIconPath = item.CustomIconPath ?? Empty;
				if (DrawDockTextInputRow("自定义图标路径", "##dock_custom_icon_path", ref customIconPath, 260)) {
					var trimmed = customIconPath.Trim();
					item.CustomIconPath = trimmed.Length == 0 ? null : trimmed;
					if (!IsNullOrWhiteSpace(item.InternalName))
						localIconPathCache.Remove(item.InternalName);
					ModuleConfig.Save();
				}
				var seIconText = item.CustomSeIconChar ?? Empty;
				if (DrawDockTextInputWithPicker("游戏图标字符", "##dock_se_icon_char", ref seIconText, 260, "素材库", "##pick_se_icon",
					    () => OpenIconLibraryForDockItem(item, IconLibraryApplyField.SeIconChar, IconLibraryMode.SeIconChar))) {
					item.CustomSeIconChar = seIconText.Trim();
					ModuleConfig.Save();
				}
			}
			ImGui.PopID();
			ImGui.Separator();
		}
	}

	private void DrawDockItemContextMacrosEditor(DockItem item) {
		DrawDockLabelWithHelp("右键宏命令");
		ImGui.NewLine();
		ImGui.Indent();
		for (var i = 0; i < item.ContextMenuMacros.Count; i++) {
			var macro = item.ContextMenuMacros[i];
			ImGui.PushID(i);
			ImGui.TextDisabled($"宏 {i + 1}");
			var name = macro.Name;
			if (DrawDockTextInputRow("名称", "##dock_macro_name", ref name, 128)) {
				macro.Name = name.Trim();
				ModuleConfig.Save();
			}
			var command = macro.Command;
			if (DrawDockTextInputRow("命令", "##dock_macro_cmd", ref command, 260)) {
				macro.Command = command.Trim();
				ModuleConfig.Save();
			}
			if (ImGui.SmallButton("删除宏##dock_macro_remove")) {
				item.ContextMenuMacros.RemoveAt(i);
				ModuleConfig.Save();
				ImGui.PopID();
				i--;
				continue;
			}
			ImGui.PopID();
			ImGui.Separator();
		}
		if (ImGui.SmallButton("新增宏命令##dock_macro_add")) {
			item.ContextMenuMacros.Add(new DockItemMacro());
			ModuleConfig.Save();
		}
		ImGui.Unindent();
	}

	private void RemoveDockItemAtIndex(int index) {
		if (index < 0 || index >= ModuleConfig.Items.Count)
			return;
		ModuleConfig.Items.RemoveAt(index);
		if (ModuleConfig.Items.Count == 0) {
			pinnedEditorSelectedIndex = -1;
		} else {
			if (pinnedEditorSelectedIndex == index)
				pinnedEditorSelectedIndex = Math.Clamp(index, 0, ModuleConfig.Items.Count - 1);
			else if (pinnedEditorSelectedIndex > index)
				pinnedEditorSelectedIndex--;
		}
		ModuleConfig.Save();
	}

	private void DrawPluginPicker(IReadOnlyList<IExposedPlugin> plugins) {
		var filter = search.Trim();
		var pinnedSet = new HashSet<string>(
			ModuleConfig.Items.Where(x => x.Kind == DockItemKind.Plugin).Select(x => x.InternalName),
			StringComparer.OrdinalIgnoreCase);
		var lineHeight = ImGui.GetTextLineHeightWithSpacing();
		var height = MathF.Min(360f, lineHeight * 16f);
		const ImGuiTableFlags flags = ImGuiTableFlags.RowBg |
		                              ImGuiTableFlags.BordersOuter |
		                              ImGuiTableFlags.BordersInnerV |
		                              ImGuiTableFlags.BordersInnerH |
		                              ImGuiTableFlags.SizingFixedFit |
		                              ImGuiTableFlags.ScrollY |
		                              ImGuiTableFlags.NoSavedSettings;
		if (!ImGui.BeginTable("plugindock_picker", 4, flags, new Vector2(0f, height)))
			return;
		ImGui.TableSetupScrollFreeze(0, 1);
		ImGui.TableSetupColumn("收纳", ImGuiTableColumnFlags.WidthFixed, 48f);
		ImGui.TableSetupColumn("插件", ImGuiTableColumnFlags.WidthStretch);
		ImGui.TableSetupColumn("主界面", ImGuiTableColumnFlags.WidthFixed, 60f);
		ImGui.TableSetupColumn("设置", ImGuiTableColumnFlags.WidthFixed, 60f);
		ImGui.TableHeadersRow();
		foreach (var plugin in plugins) {
			if (plugin.InternalName.Equals("PluginDock", StringComparison.OrdinalIgnoreCase))
				continue;
			if (!IsNullOrWhiteSpace(filter)) {
				if (!plugin.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
				    !plugin.InternalName.Contains(filter, StringComparison.OrdinalIgnoreCase))
					continue;
			}
			ImGui.PushID(plugin.InternalName);
			ImGui.TableNextRow(ImGuiTableRowFlags.None, ImGui.GetFrameHeight());
			ImGui.TableNextColumn();
			var startX = ImGui.GetCursorPosX();
			var availX = ImGui.GetContentRegionAvail().X;
			var checkboxSize = ImGui.GetFrameHeight();
			if (availX > checkboxSize)
				ImGui.SetCursorPosX(startX + (availX - checkboxSize) * 0.5f);
			var pinned = pinnedSet.Contains(plugin.InternalName);
			if (ImGui.Checkbox("##pin", ref pinned)) {
				if (pinned) {
					ModuleConfig.Items.Add(new DockItem {
						Kind = DockItemKind.Plugin,
						InternalName = plugin.InternalName,
						CustomDisplayName = Empty,
						Hidden = false,
						CustomIconPath = Empty,
						CustomGameIconId = 0,
						CustomSeIconChar = Empty,
						ClickCommand = Empty
					});
					pinnedSet.Add(plugin.InternalName);
				} else {
					ModuleConfig.Items.RemoveAll(x =>
						x.Kind == DockItemKind.Plugin &&
						x.InternalName.Equals(plugin.InternalName, StringComparison.OrdinalIgnoreCase));
					pinnedSet.Remove(plugin.InternalName);
				}
				ModuleConfig.Save();
			}
			ImGui.TableNextColumn();
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted(plugin.Name);
			if (ImGui.IsItemHovered()) {
				ImGui.BeginTooltip();
				ImGui.TextUnformatted(plugin.InternalName);
				ImGui.EndTooltip();
			}
			ImGui.TableNextColumn();
			if (plugin.HasMainUi) {
				if (ImGui.Button("打开", new Vector2(-1f, 0f)))
					TryOpenPlugin(plugin, false);
			} else {
				ImGui.AlignTextToFramePadding();
				ImGui.TextDisabled("-");
			}
			ImGui.TableNextColumn();
			if (plugin.HasConfigUi) {
				if (ImGui.Button("设置", new Vector2(-1f, 0f)))
					TryOpenPlugin(plugin, true);
			} else {
				ImGui.AlignTextToFramePadding();
				ImGui.TextDisabled("-");
			}
			ImGui.PopID();
		}
		ImGui.EndTable();
	}

	internal void DrawDockLayout() {
		ImGui.PushStyleColor(ImGuiCol.WindowBg, 0);
		ImGui.PushStyleColor(ImGuiCol.Button, 0);
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.08f));
		ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 1f, 1f, 0.14f));
		DrawDockLayoutCore();
		ImGui.PopStyleColor(4);
	}

	private void DrawDockLayoutCore() {
		var origin = ImGui.GetCursorPos();
		var iconSize = ModuleConfig.IconSize;
		var spacing = ModuleConfig.IconSpacing;
		if (!ModuleConfig.Collapsed)
			EnsureVisibleDockItemsCache();
		ApplyDockAnchor(origin);
		ImGui.SetCursorPos(origin);
		DrawDockHeader();
		if (!ModuleConfig.Collapsed) {
			for (var i = 0; i < visibleDockItemsScratch.Count; i++) {
				var pos = GetDockItemPosition(i, iconSize, spacing);
				ImGui.SetCursorPos(origin + pos);
				var (item, plugin) = visibleDockItemsScratch[i];
				if (item.Kind == DockItemKind.Command)
					DrawOneCommandDockItem(item);
				else if (plugin != null)
					DrawOneDockItem(plugin, item);
			}
		}
	}

	[SuppressMessage("ReSharper", "PossibleLossOfFraction")]
	private Vector2 GetDockItemPosition(int index, float iconSize, float spacing) {
		var maxPerLine = ModuleConfig.MaxIconsPerRow <= 0 ? int.MaxValue : ModuleConfig.MaxIconsPerRow;
		var step = iconSize + spacing;
		return new Vector2(step * (index % maxPerLine + 1), step * (index / maxPerLine));
	}

	private void EnsureVisibleDockItemsCache() {
		var itemVersion = ComputeDockItemsFingerprint();
		if (visibleDockItemsCacheValid && visibleDockItemsCacheCollapsed == ModuleConfig.Collapsed &&
		    visibleDockItemsCacheItemsVersion == itemVersion &&
		    visibleDockItemsCachePluginsVersion == loadedPluginsCacheVersion)
			return;
		visibleDockItemsScratch.Clear();
		var pluginByInternalName = GetLoadedPluginByInternalNameCached();
		foreach (var item in ModuleConfig.Items) {
			if (item.Hidden) continue;
			if (item.Kind == DockItemKind.Command) {
				if (!IsNullOrWhiteSpace(item.InternalName)) visibleDockItemsScratch.Add((item, null));
				continue;
			}
			if (pluginByInternalName.TryGetValue(item.InternalName, out var plugin))
				visibleDockItemsScratch.Add((item, plugin));
		}
		visibleDockItemsCacheValid = true;
		visibleDockItemsCacheCollapsed = ModuleConfig.Collapsed;
		visibleDockItemsCacheItemsVersion = itemVersion;
		visibleDockItemsCachePluginsVersion = loadedPluginsCacheVersion;
	}

	private int ComputeDockItemsFingerprint() {
		var hash = new HashCode();
		hash.Add(ModuleConfig.Items.Count);
		foreach (var item in ModuleConfig.Items) {
			hash.Add(item.Kind);
			hash.Add(item.InternalName, StringComparer.OrdinalIgnoreCase);
			hash.Add(item.Hidden);
		}
		return hash.ToHashCode();
	}

	private void ApplyDockAnchor(Vector2 headerLocalPos) {
		var windowPos = ImGui.GetWindowPos();
		if (!ModuleConfig.DockAnchorInitialized) {
			ModuleConfig.DockAnchorInitialized = true;
			ModuleConfig.DockAnchor = windowPos + headerLocalPos;
			dockAnchorDirty = true;
		}
		if (!UpdateDockDragState() && dockAnchorDirty) {
			dockAnchorDirty = false;
			ModuleConfig.Save();
		}
	}

	private bool UpdateDockDragState() {
		if (ModuleConfig.LockPosition) {
			dockDragging = false;
			dockDragMoved = false;
			return false;
		}
		var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
		if (!dockDragging) {
			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
				dockDragging = true;
				dockDragMoved = false;
				dockDragStartMouse = ImGui.GetMousePos();
				dockDragStartAnchor = ModuleConfig.DockAnchor;
			}
			return false;
		}
		if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
			dockDragging = false;
			return false;
		}
		var delta = ImGui.GetMousePos() - dockDragStartMouse;
		var threshold = MathF.Max(ImGui.GetIO().MouseDragThreshold, 1f);
		if (!dockDragMoved && delta.LengthSquared() >= threshold * threshold)
			dockDragMoved = true;
		if (!dockDragMoved)
			return false;
		ModuleConfig.DockAnchor = dockDragStartAnchor + delta;
		dockAnchorDirty = true;
		return true;
	}

	private void DrawDockHeader() {
		ImGui.PushID("dock_header");
		var size = new Vector2(ModuleConfig.IconSize, ModuleConfig.IconSize);
		var headerIcon = GetDockHeaderIcon();
		var clicked = headerIcon.TryGetWrap(out var wrap, out _) ? ImGui.ImageButton(wrap.Handle, size) : DrawDockHeaderFallbackIcon(size);
		if (ImGui.IsItemHovered()) {
			ImGui.BeginTooltip();
			ImGui.TextUnformatted("插件收纳栏");
			ImGui.TextDisabled(ModuleConfig.Collapsed ? "左键展开 / 右键更多" : "左键收起 / 右键更多");
			ImGui.EndTooltip();
		}
		if (clicked) {
			ModuleConfig.Collapsed = !ModuleConfig.Collapsed;
			
			ModuleConfig.Save();
		}
		if (ImGui.BeginPopupContextItem("ctx")) {
			if (ImGui.MenuItem(ModuleConfig.Collapsed ? "展开" : "收起")) {
				ModuleConfig.Collapsed = !ModuleConfig.Collapsed;
				
				ModuleConfig.Save();
			}
			if (ImGui.MenuItem("锁定位置", ModuleConfig.LockPosition)) {
				ModuleConfig.LockPosition = !ModuleConfig.LockPosition;
				Overlay.Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
				                ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
				                ImGuiWindowFlags.AlwaysAutoResize;
				if (ModuleConfig.LockPosition) Overlay.Flags |= ImGuiWindowFlags.NoMove;
				
				ModuleConfig.Save();
			}
			if (ImGui.MenuItem("打开配置"))
				ToggleOverlayConfig();
			ImGui.EndPopup();
		}
		ImGui.PopID();
	}

	private static bool DrawDockHeaderFallbackIcon(Vector2 size) {
		var clicked = ImGui.InvisibleButton("dock_header_fallback", size);
		var drawList = ImGui.GetWindowDrawList();
		var min = ImGui.GetItemRectMin();
		var max = ImGui.GetItemRectMax();
		var rounding = MathF.Max(2f, size.X * 0.16f);
		var bg = ImGui.GetColorU32(new Vector4(0.10f, 0.22f, 0.26f, 0.95f));
		var border = ImGui.GetColorU32(new Vector4(0.55f, 0.85f, 0.88f, 0.9f));
		drawList.AddRectFilled(min, max, bg, rounding);
		drawList.AddRect(min, max, border, rounding);
		var pad = size.X * 0.18f;
		var square = size.X * 0.18f;
		var gap = size.X * 0.08f;
		var topY = min.Y + pad;
		var squareColor = ImGui.GetColorU32(new Vector4(0.92f, 0.70f, 0.25f, 1f));
		for (var i = 0; i < 3; i++) {
			var x = min.X + pad + i * (square + gap);
			drawList.AddRectFilled(new Vector2(x, topY), new Vector2(x + square, topY + square), squareColor, 2f);
		}
		var barTop = min.Y + size.Y * 0.58f;
		var barColor = ImGui.GetColorU32(new Vector4(0.08f, 0.53f, 0.63f, 1f));
		drawList.AddRectFilled(
			new Vector2(min.X + pad, barTop),
			new Vector2(max.X - pad, max.Y - pad * 0.7f),
			barColor,
			rounding * 0.6f);
		if (ImGui.IsItemHovered()) {
			var hover = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.12f));
			drawList.AddRectFilled(min, max, hover, rounding);
		}
		if (ImGui.IsItemActive()) {
			var active = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.2f));
			drawList.AddRectFilled(min, max, active, rounding);
		}
		return clicked;
	}

	private static void DrawDockItemContextMacrosMenu(DockItem item, bool includeLeadingSeparator) {
		if (item.ContextMenuMacros.Count == 0)
			return;
		var hasEntries = false;
		for (var i = 0; i < item.ContextMenuMacros.Count; i++) {
			var macro = item.ContextMenuMacros[i];
			var command = macro.Command.Trim();
			if (command.Length == 0)
				continue;
			if (!hasEntries) {
				if (includeLeadingSeparator)
					ImGui.Separator();
				ImGui.TextDisabled("宏命令");
				hasEntries = true;
			}
			var name = macro.Name.Trim();
			var displayName = SanitizeMacroDisplay(name, 40);
			var label = IsNullOrWhiteSpace(displayName) ? $"宏 {i + 1}" : displayName;
			if (ImGui.MenuItem($"{label}##dock_macro_{i}"))
				TryExecuteClickCommand(command);
		}
		if (hasEntries)
			ImGui.Separator();
	}

	private static string SanitizeMacroDisplay(string input, int maxLength) {
		if (IsNullOrWhiteSpace(input) || maxLength <= 0)
			return Empty;
		var builder = new StringBuilder(input.Length);
		foreach (var ch in input.Where(ch => ch is not (< ' ' or '\u007f'))) {
			builder.Append(ch);
			if (builder.Length >= maxLength)
				break;
		}
		var result = builder.ToString().Trim();
		if (builder.Length >= maxLength && maxLength > 3 && result.Length > maxLength - 3)
			result = result[..Math.Max(0, maxLength - 3)].TrimEnd() + "...";
		return result;
	}

	private void DrawOneCommandDockItem(DockItem item) {
		var commandItemKey = item.InternalName.Trim();
		if (commandItemKey.Length == 0)
			return;
		ImGui.PushID($"cmd_{commandItemKey}");
		var customName = item.CustomDisplayName?.Trim() ?? Empty;
		var displayName = IsNullOrWhiteSpace(customName) ? "命令" : customName;
		var size = new Vector2(ModuleConfig.IconSize, ModuleConfig.IconSize);
		var tex = GetIconForCommand(item);
		var hasSeIcon = TryGetDockItemSeIcon(item, out var seIcon);
		var clicked = DrawDockItemButton(size, tex, hasSeIcon, seIcon, GetPlaceholderLetter(displayName));
		var cmd = (item.ClickCommand ?? Empty).Trim();
		if (ImGui.IsItemHovered()) {
			ImGui.BeginTooltip();
			ImGui.TextUnformatted(displayName);
			ImGui.TextDisabled(cmd.Length == 0 ? "未设置命令" : cmd);
			ImGui.TextDisabled("左键：执行命令");
			ImGui.EndTooltip();
		}
		if (clicked)
			TryExecuteClickCommand(item.ClickCommand);
		if (ImGui.BeginPopupContextItem("ctx")) {
			ImGui.BeginDisabled(cmd.Length == 0);
			if (ImGui.MenuItem("执行命令"))
				TryExecuteClickCommand(item.ClickCommand);
			ImGui.EndDisabled();
			DrawDockItemContextMacrosMenu(item, true);
			ImGui.EndPopup();
		}
		ImGui.PopID();
	}

	private void DrawOneDockItem(IExposedPlugin plugin, DockItem item) {
		ImGui.PushID(plugin.InternalName);
		var customName = item.CustomDisplayName?.Trim() ?? Empty;
		var displayName = IsNullOrWhiteSpace(customName) ? plugin.Name : customName;
		var size = new Vector2(ModuleConfig.IconSize, ModuleConfig.IconSize);
		var tex = GetIconFor(plugin, item);
		var hasSeIcon = TryGetDockItemSeIcon(item, out var seIcon);
		var clicked = DrawDockItemButton(size, tex, hasSeIcon, seIcon, GetPlaceholderLetter(plugin.Name));
		if (ImGui.IsItemHovered()) {
			ImGui.BeginTooltip();
			ImGui.TextUnformatted(displayName);
			if (!IsNullOrWhiteSpace(customName) &&
			    !customName.Equals(plugin.Name, StringComparison.Ordinal)) {
				ImGui.TextDisabled(plugin.Name);
			}
			ImGui.TextDisabled(plugin.InternalName);
			ImGui.EndTooltip();
		}
		if (clicked) {
			TryExecuteClickCommand(item.ClickCommand);
			TryOpenPlugin(plugin, false);
		}
		if (ImGui.BeginPopupContextItem("ctx")) {
			if (ImGui.MenuItem("打开主界面", false, plugin.HasMainUi))
				TryOpenPlugin(plugin, false);
			if (ImGui.MenuItem("打开设置", false, plugin.HasConfigUi))
				TryOpenPlugin(plugin, true);
			ImGui.Separator();
			DrawDockItemContextMacrosMenu(item, false);
			ImGui.EndPopup();
		}
		ImGui.PopID();
	}

	private static bool TryGetDockItemSeIcon(DockItem item, out SeIconChar seIcon) {
		if (item.CustomGameIconId != 0 || !IsNullOrWhiteSpace(item.CustomIconPath)) {
			seIcon = default;
			return false;
		}
		return TryGetSeIconChar(item.CustomSeIconChar, out seIcon);
	}

	private bool DrawDockItemButton(Vector2 size, ISharedImmediateTexture? texture, bool hasSeIcon, SeIconChar seIcon, string placeholderLetter) {
		ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
		try {
			if (hasSeIcon)
				return ImGui.Button(seIcon.ToIconString(), size);
			if (texture != null && texture.TryGetWrap(out var wrap, out _))
				return ImGui.ImageButton(wrap.Handle, size);
			return DrawDockPlaceholderButton(size, placeholderLetter);
		} finally {
			ImGui.PopStyleVar();
		}
	}

	private static bool DrawDockPlaceholderButton(Vector2 size, string letter) {
		var clicked = ImGui.InvisibleButton("##dock_placeholder", size);
		var itemMin = ImGui.GetItemRectMin();
		var itemMax = ImGui.GetItemRectMax();
		var inset = MathF.Max(1f, ImGui.GetStyle().FrameBorderSize);
		var min = itemMin + new Vector2(inset);
		var max = itemMax - new Vector2(inset);
		var contentSize = max - min;
		var drawList = ImGui.GetWindowDrawList();
		var rounding = MathF.Max(2f, contentSize.X * 0.14f);
		var background = ImGui.GetColorU32(new Vector4(0.12f, 0.25f, 0.32f, 0.95f));
		var border = ImGui.GetColorU32(new Vector4(0.45f, 0.75f, 0.82f, 0.9f));
		var textColor = ImGui.GetColorU32(new Vector4(0.92f, 0.98f, 1f, 1f));
		if (ImGui.IsItemHovered())
			background = ImGui.GetColorU32(new Vector4(0.20f, 0.40f, 0.48f, 0.98f));
		if (ImGui.IsItemActive())
			background = ImGui.GetColorU32(new Vector4(0.28f, 0.50f, 0.58f, 1f));
		drawList.AddRectFilled(min, max, background, rounding);
		drawList.AddRect(min, max, border, rounding);
		var text = IsNullOrWhiteSpace(letter) ? "?" : letter;
		var textSize = ImGui.CalcTextSize(text);
		drawList.AddText(min + (contentSize - textSize) * 0.5f, textColor, text);
		return clicked;
	}

	private static string GetPlaceholderLetter(string? name) {
		foreach (var ch in name ?? Empty) {
			if (ch is >= 'A' and <= 'Z')
				return ch.ToString();
			if (ch is >= 'a' and <= 'z')
				return char.ToUpperInvariant(ch).ToString();
		}
		return "?";
	}

	private static bool TryGetSeIconChar(string? raw, out SeIconChar icon) {
		icon = default;
		var text = (raw ?? Empty).Trim();
		if (text.Length == 0)
			return false;
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
		    int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexValue) &&
		    hexValue != 0) {
			icon = (SeIconChar)hexValue;
			return true;
		}
		if (!Enum.TryParse(text, true, out icon))
			return false;
		return Convert.ToInt32(icon) != 0;
	}

	private ISharedImmediateTexture GetDockHeaderIcon() {
		var customPath = ModuleConfig.DockHeaderIconPath?.Trim() ?? Empty;
		if (customPath.Length > 0) {
			if (Path.IsPathRooted(customPath)) {
				if (textureCache.TryGetValue(customPath, out var cached))
					return cached;
				if (File.Exists(customPath)) {
					var texture = Plugin.Texture.GetFromFileAbsolute(customPath);
					textureCache[customPath] = texture;
					return texture;
				}
			}
		}
		return GetFallbackPluginIcon();
	}

	private ISharedImmediateTexture? GetIconForCommand(DockItem item) {
		var gameIcon = GetGameIconTexture(item.CustomGameIconId);
		if (gameIcon != null)
			return gameIcon;
		var resolvedPath = ResolveCustomIconPath(item.CustomIconPath);
		if (IsNullOrWhiteSpace(resolvedPath) || !Path.IsPathRooted(resolvedPath))
			return null;
		if (textureCache.TryGetValue(resolvedPath, out var cached))
			return cached;
		if (!File.Exists(resolvedPath))
			return null;
		var texture = Plugin.Texture.GetFromFileAbsolute(resolvedPath);
		textureCache[resolvedPath] = texture;
		return texture;
	}

	private ISharedImmediateTexture? GetGameIconTexture(uint iconId) {
		if (iconId == 0)
			return null;
		if (gameIconCache.TryGetValue(iconId, out var cached))
			return cached;
		try {
			var texture = Plugin.Texture.GetFromGameIcon(new GameIconLookup(iconId));
			gameIconCache[iconId] = texture;
			return texture;
		} catch {
			return null;
		}
	}

	private ISharedImmediateTexture? GetIconFor(IExposedPlugin plugin, DockItem item) {
		var gameIcon = GetGameIconTexture(item.CustomGameIconId);
		if (gameIcon != null)
			return gameIcon;
		var resolvedPath = ResolveIconPath(plugin, item);
		if (IsNullOrWhiteSpace(resolvedPath) || !Path.IsPathRooted(resolvedPath))
			return null;
		if (textureCache.TryGetValue(resolvedPath, out var cached))
			return cached;
		if (!File.Exists(resolvedPath))
			return null;
		var texture = Plugin.Texture.GetFromFileAbsolute(resolvedPath);
		textureCache[resolvedPath] = texture;
		return texture;
	}

	private ISharedImmediateTexture GetFallbackPluginIcon() {
		if (fallbackPluginIcon != null)
			return fallbackPluginIcon;
		try {
			var bundledIcon = TryResolveBundledIconPath();
			if (!IsNullOrWhiteSpace(bundledIcon) && File.Exists(bundledIcon))
				return fallbackPluginIcon = Plugin.Texture.GetFromFileAbsolute(bundledIcon);
		} catch {
			//
		}
		try {
			var assetsDir = Plugin.PI.DalamudAssetDirectory.FullName;
			var defaultIcon = Path.Combine(assetsDir, "3", "UIRes", "defaultIcon.png");
			if (!File.Exists(defaultIcon)) {
				defaultIcon = Directory.EnumerateFiles(assetsDir, "defaultIcon.png", SearchOption.AllDirectories)
					.FirstOrDefault() ?? defaultIcon;
			}
			if (File.Exists(defaultIcon))
				return fallbackPluginIcon = Plugin.Texture.GetFromFileAbsolute(defaultIcon);
		} catch {
			//
		}
		try {
			return fallbackPluginIcon = Plugin.Texture.GetFromGameIcon(new GameIconLookup(60001));
		} catch {
			return fallbackPluginIcon = Plugin.Texture.GetFromGameIcon(new GameIconLookup(1));
		}
	}

	private static string? TryResolveBundledIconPath() {
		try {
			var assemblyLocation = Plugin.PI.AssemblyLocation;
			var baseDir = assemblyLocation.DirectoryName ?? Empty;
			if (baseDir.Length == 0)
				return null;
			var candidate = Path.Combine(baseDir, "images", "icon.png");
			if (File.Exists(candidate))
				return candidate;
			candidate = Path.Combine(baseDir, "icon.png");
			return File.Exists(candidate) ? candidate : null;
		} catch {
			return null;
		}
	}

	private string? ResolveIconPath(IExposedPlugin plugin, DockItem item) {
		var cacheKey = plugin.InternalName + "|" + (item.CustomIconPath ?? Empty) + "|" + plugin.Version;
		if (resolvedIconPathCache.TryGetValue(cacheKey, out var cachedPath))
			return cachedPath;
		var custom = ResolveCustomIconPath(item.CustomIconPath);
		if (!IsNullOrWhiteSpace(custom))
			return resolvedIconPathCache[cacheKey] = custom;
		var localPath = GetLocalIconPath(plugin);
		if (!IsNullOrWhiteSpace(localPath))
			return resolvedIconPathCache[cacheKey] = localPath;
		var cachedUrlPath = GetCachedIconPathFromIconUrl(plugin);
		if (!IsNullOrWhiteSpace(cachedUrlPath))
			return resolvedIconPathCache[cacheKey] = cachedUrlPath;
		resolvedIconPathCache[cacheKey] = null;
		return null;
	}

	private static string? ResolveCustomIconPath(string? rawPath) {
		var raw = (rawPath ?? Empty).Trim();
		if (raw.Length == 0)
			return null;
		if (raw.StartsWith("dalamud:", StringComparison.OrdinalIgnoreCase) ||
		    raw.StartsWith("sys:", StringComparison.OrdinalIgnoreCase)) {
			var idx = raw.IndexOf(':');
			var relative = idx >= 0 ? raw[(idx + 1)..].Trim() : Empty;
			if (relative.Length == 0)
				return FindDalamudDefaultIconPath();
			relative = relative.Replace('/', Path.DirectorySeparatorChar)
				.Replace('\\', Path.DirectorySeparatorChar)
				.TrimStart(Path.DirectorySeparatorChar);
			try {
				var assetsDir = Plugin.PI.DalamudAssetDirectory.FullName;
				return Path.Combine(assetsDir, relative);
			} catch {
				return null;
			}
		}
		if (Path.IsPathRooted(raw))
			return raw;
		return Path.Combine(ConfigDirectoryPath, raw);
	}

	private static string? FindDalamudDefaultIconPath() {
		try {
			var assetsDir = Plugin.PI.DalamudAssetDirectory.FullName;
			var defaultIcon = Path.Combine(assetsDir, "3", "UIRes", "defaultIcon.png");
			if (File.Exists(defaultIcon))
				return defaultIcon;
			return Directory.EnumerateFiles(assetsDir, "defaultIcon.png", SearchOption.AllDirectories)
				.FirstOrDefault();
		} catch {
			return null;
		}
	}

	private string? GetLocalIconPath(IExposedPlugin plugin) {
		if (localIconPathCache.TryGetValue(plugin.InternalName, out var cached))
			return IsNullOrWhiteSpace(cached) ? null : cached;
		var iconPath = FindPluginIcon(plugin);
		localIconPathCache[plugin.InternalName] = iconPath ?? Empty;
		return iconPath;
	}

	private string? GetCachedIconPathFromIconUrl(IExposedPlugin plugin) {
		var iconUrl = plugin.Manifest.IconUrl;
		if (IsNullOrWhiteSpace(iconUrl))
			return null;
		if (!Uri.TryCreate(iconUrl, UriKind.Absolute, out var uri))
			return null;
		if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
		    !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
			return null;
		var iconDir = Path.Combine(ConfigDirectoryPath, "PluginDockIcons");
		var fileBase = SanitizeFileName(plugin.InternalName);
		foreach (var ext in new[] { ".png", ".jpg", ".jpeg" }) {
			var existing = Path.Combine(iconDir, fileBase + ext);
			if (File.Exists(existing))
				return existing;
		}
		var extFromUrl = Path.GetExtension(uri.AbsolutePath);
		if (!IsSupportedImageExtension(extFromUrl))
			extFromUrl = ".png";
		var targetPath = Path.Combine(iconDir, fileBase + extFromUrl);
		EnsureIconDownloaded(plugin.InternalName, iconUrl, targetPath);
		return targetPath;
	}

	private static bool IsSupportedImageExtension(string? ext) {
		if (IsNullOrWhiteSpace(ext))
			return false;
		return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
		       ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
		       ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
	}

	private static string SanitizeFileName(string name) {
		if (IsNullOrEmpty(name))
			return "plugin";
		var invalid = Path.GetInvalidFileNameChars();
		Span<char> buffer = stackalloc char[name.Length];
		for (var i = 0; i < name.Length; i++) {
			var ch = name[i];
			buffer[i] = invalid.Contains(ch) ? '_' : ch;
		}
		return new string(buffer);
	}

	private void EnsureIconDownloaded(string internalName, string url, string targetPath) {
		if (File.Exists(targetPath))
			return;
		if (iconDownloadCooldownUntilUtc.TryGetValue(internalName, out var untilUtc) && untilUtc > DateTime.UtcNow)
			return;
		if (!iconDownloadTasks.TryAdd(internalName, Task.CompletedTask))
			return;
		iconDownloadTasks[internalName] = Task.Run(async () => {
			try {
				Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
				using var response = await IconHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
				response.EnsureSuccessStatusCode();
				var tempPath = targetPath + ".tmp";
				await using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
				await using (var dest = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
					await source.CopyToAsync(dest).ConfigureAwait(false);
				}
				File.Move(tempPath, targetPath, true);
			} catch (Exception ex) {
				Plugin.Log.Warning(ex, $"PluginDock 图标下载失败: {internalName}");
				iconDownloadCooldownUntilUtc[internalName] = DateTime.UtcNow.AddMinutes(10);
			} finally {
				iconDownloadTasks.TryRemove(internalName, out _);
			}
		});
	}

	private static string? FindPluginIcon(IExposedPlugin plugin) {
		try {
			var baseDir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
				"XIVLauncherCN",
				"installedPlugins",
				plugin.InternalName);
			if (!Directory.Exists(baseDir))
				return null;

			string? PickByVersion() {
				var dirs = Directory.EnumerateDirectories(baseDir)
					.Select(d => {
						var name = Path.GetFileName(d);
						return Version.TryParse(name, out var v) ? (Path: d, Version: v) : (Path: d, Version: null);
					})
					.Where(x => x.Version != null)
					.OrderByDescending(x => x.Version)
					.ToList();
				var exact = dirs.FirstOrDefault(x => x.Version != null && x.Version.Equals(plugin.Version));
				return (exact.Path ?? dirs.FirstOrDefault().Path) ?? null;
			}

			var versionDir = PickByVersion();
			if (IsNullOrWhiteSpace(versionDir) || !Directory.Exists(versionDir))
				return null;
			var preferred = new[] { "icon.png", "icon.jpg", "icon.jpeg" };
			foreach (var file in preferred) {
				var direct = Path.Combine(versionDir, file);
				if (File.Exists(direct))
					return direct;
				var assets = Path.Combine(versionDir, "Assets", file);
				if (File.Exists(assets))
					return assets;
			}

			static bool IsSupported(string p) {
				var ext = Path.GetExtension(p);
				return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
				       ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
				       ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
			}

			return Directory.EnumerateFiles(versionDir, "*icon*.*", SearchOption.AllDirectories)
				.Where(IsSupported)
				.FirstOrDefault();
		} catch {
			return null;
		}
	}

	private static void TryExecuteClickCommand(string? command) {
		var trimmed = (command ?? Empty).Trim();
		if (trimmed.Length == 0)
			return;
		if (!TryNormalizeClickCommand(trimmed, out var normalized, out var normalizeError)) {
			Plugin.Log.Error(normalizeError);
			return;
		}
		try {
			if (Plugin.Framework.IsInFrameworkUpdateThread) {
				ExecuteCommandInternal(normalized);
				return;
			}
			_ = Plugin.Framework.RunOnFrameworkThread(() => ExecuteCommandInternal(normalized));
		} catch (Exception ex) {
			Plugin.Log.Error(ex, $"PluginDock 执行命令失败: {normalized}");
		}
	}

	private static void ExecuteCommandInternal(string normalized) {
		try {
			var handled = Plugin.Command.ProcessCommand(normalized);
			if (!handled)
				TryFallbackEchoCommand(normalized);
		} catch (Exception ex) {
			Plugin.Log.Error(ex, $"PluginDock 执行命令失败: {normalized}");
		}
	}

	private static void TryFallbackEchoCommand(string? command) {
		var text = (command ?? Empty).Trim();
		if (text.Length == 0)
			return;
		if (text[0] == '\\')
			text = "/" + text[1..];
		if (!text.StartsWith("/", StringComparison.Ordinal))
			return;
		var spaceIndex = text.IndexOf(' ');
		var commandName = spaceIndex > 1 ? text[1..spaceIndex] : text[1..];
		var args = spaceIndex > 0 && spaceIndex + 1 < text.Length ? text[(spaceIndex + 1)..] : Empty;
		if (!commandName.Equals("e", StringComparison.OrdinalIgnoreCase) &&
		    !commandName.Equals("echo", StringComparison.OrdinalIgnoreCase))
			return;
		if (args.Length == 0)
			return;
		var entry = new XivChatEntry {
			Type = XivChatType.Echo,
			Message = new SeStringBuilder().AddText(args).Build()
		};
		Plugin.Chat.Print(entry);
	}

	private static bool TryNormalizeClickCommand(string? input, out string normalized, out string error) {
		normalized = Empty;
		error = Empty;
		var text = (input ?? Empty).Trim();
		if (text.Length == 0) {
			error = "未设置命令";
			return false;
		}
		text = text.TrimEnd(';').Trim();
		var processIndex = text.IndexOf("ProcessCommand", StringComparison.OrdinalIgnoreCase);
		if (processIndex >= 0) {
			var openParen = text.IndexOf('(', processIndex);
			var closeParen = text.LastIndexOf(')');
			if (openParen >= 0 && closeParen > openParen)
				text = text.Substring(openParen + 1, closeParen - openParen - 1).Trim();
		}

		static string StripWrapping(string value) {
			var v = value.Trim().TrimEnd(';').Trim();
			if (v.Length >= 3 &&
			    (v.StartsWith("@\"", StringComparison.Ordinal) || v.StartsWith("@'", StringComparison.Ordinal))) {
				var quote = v[1];
				if (v[^1] == quote)
					v = v.Substring(2, v.Length - 3);
				return v.Trim();
			}
			if (v.Length >= 2 &&
			    (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\'')) {
				v = v.Substring(1, v.Length - 2);
			}
			return v.Trim();
		}

		text = StripWrapping(text);
		if (text.Length == 0) {
			error = "命令解析为空（请填写 /xxx 或 Plugin.Command.ProcessCommand(\"/xxx\")）";
			return false;
		}
		if (text[0] == '／')
			text = "/" + text[1..];
		else if (text[0] == '＼')
			text = "\\" + text[1..];
		if (!text.StartsWith("/", StringComparison.Ordinal) &&
		    !text.StartsWith("\\", StringComparison.Ordinal))
			text = "/" + text;
		normalized = text;
		return true;
	}

	private static void TryOpenPlugin(IExposedPlugin plugin, bool preferConfig) {
		if (preferConfig) {
			if (plugin.HasConfigUi) plugin.OpenConfigUi();
			else if (plugin.HasMainUi) plugin.OpenMainUi();
			return;
		}
		if (plugin.HasMainUi) plugin.OpenMainUi();
		else if (plugin.HasConfigUi) plugin.OpenConfigUi();
	}
}