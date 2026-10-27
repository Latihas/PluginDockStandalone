using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Textures;

namespace PluginDockStandalone;

internal sealed partial class PluginDockController {
	private void DrawImGuiMetricsWindow() {
		if (!showImGuiMetricsWindow)
			return;
		try {
			ImGui.ShowMetricsWindow(ref showImGuiMetricsWindow);
		} catch {
			showImGuiMetricsWindow = false;
		}
	}

	private void OpenIconLibrary(IconLibraryApplyTarget? target, IconLibraryMode mode) {
		iconLibraryApplyTarget = target;
		iconLibraryMode = mode;
		showIconLibraryWindow = true;
	}

	private void OpenIconLibraryForDockItem(Config.DockItem item, IconLibraryApplyField field, IconLibraryMode mode) {
		if (string.IsNullOrWhiteSpace(item.InternalName)) {
			OpenIconLibrary(null, mode);
			return;
		}
		OpenIconLibrary(new IconLibraryApplyTarget {
			Kind = item.Kind,
			InternalName = item.InternalName.Trim(),
			Field = field
		}, mode);
	}

	private void DrawIconLibraryWindow() {
		if (!showIconLibraryWindow) return;
		var open = showIconLibraryWindow;
		if (ImGui.Begin(IconLibraryWindowName, ref open)) {
			showIconLibraryWindow = open;
			ImGui.TextDisabled("点击条目：复制到剪贴板；若设置了目标且“应用到”匹配，会同时写入该收纳项。");
			if (!string.IsNullOrWhiteSpace(iconLibraryLastActionMessage)) {
				ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), iconLibraryLastActionMessage);
			}
			if (iconLibraryApplyTarget != null) {
				var kindText = iconLibraryApplyTarget.Kind switch {
					Config.DockItemKind.Plugin => "插件",
					Config.DockItemKind.Command => "命令",
					_ => iconLibraryApplyTarget.Kind.ToString()
				};
				ImGui.TextUnformatted($"目标：{kindText} - {iconLibraryApplyTarget.InternalName}");
				ImGui.SameLine();
				ImGui.TextDisabled("应用到：");
				ImGui.SameLine();
				if (ImGui.RadioButton("游戏图标ID", iconLibraryApplyTarget.Field == IconLibraryApplyField.GameIconId)) {
					iconLibraryApplyTarget.Field = IconLibraryApplyField.GameIconId;
					iconLibraryMode = IconLibraryMode.GameIcon;
				}

				ImGui.SameLine();
				if (ImGui.RadioButton("SeIconChar", iconLibraryApplyTarget.Field == IconLibraryApplyField.SeIconChar)) {
					iconLibraryApplyTarget.Field = IconLibraryApplyField.SeIconChar;
					iconLibraryMode = IconLibraryMode.SeIconChar;
				}
				ImGui.SameLine();
			} else {
				ImGui.TextDisabled("目标：无（仅复制到剪贴板）");
			}
			ImGui.Separator();
			// var modeValue = (int)iconLibraryMode;
			// var modeLabels = new[] { "游戏图标ID", "SeIconChar 字符" };
			// if (ImGui.Combo("类型", ref modeValue, modeLabels, modeLabels.Length))
			// 	iconLibraryMode = (IconLibraryMode)Math.Clamp(modeValue, 0, modeLabels.Length - 1);
			// ImGui.Separator();
			switch (iconLibraryMode) {
				case IconLibraryMode.GameIcon:
					DrawIconLibraryGameIcons();
					break;
				case IconLibraryMode.SeIconChar:
					DrawIconLibrarySeIconChars();
					break;
			}
			ImGui.End();
		} else {
			showIconLibraryWindow = open;
		}
	}

	private void SetIconLibraryLastAction(string message) {
		iconLibraryLastActionMessage = message;
	}

	private bool TryGetDockItemForIconLibraryTarget(out Config.DockItem item) {
		item = null!;
		if (iconLibraryApplyTarget == null)
			return false;
		var name = iconLibraryApplyTarget.InternalName.Trim();
		if (name.Length == 0)
			return false;
		foreach (var x in ModuleConfig.Items) {
			if (x.Kind != iconLibraryApplyTarget.Kind)
				continue;
			if (x.InternalName.Equals(name, StringComparison.OrdinalIgnoreCase)) {
				item = x;
				return true;
			}
		}
		return false;
	}

	private void ApplyIconLibraryGameIconSelection(uint iconId) {
		iconLibraryLastSelectedGameIconId = iconId;
		ImGui.SetClipboardText(iconId.ToString(CultureInfo.InvariantCulture));
		var applied = false;
		if (iconLibraryApplyTarget is { Field: IconLibraryApplyField.GameIconId }) {
			if (TryGetDockItemForIconLibraryTarget(out var item)) {
				item.CustomGameIconId = iconId;
				ModuleConfig.Save();
				applied = true;
			} else {
				iconLibraryApplyTarget = null;
			}
		}
		SetIconLibraryLastAction(applied ? $"已应用游戏图标ID：{iconId}" : $"已复制游戏图标ID：{iconId}");
	}

	private void ApplyIconLibrarySeIconCharSelection(SeIconCharEntry entry) {
		var output = BuildSeIconCharOutput(entry);
		if (output.Length == 0)
			return;
		ImGui.SetClipboardText(output);
		var applied = false;
		if (iconLibraryApplyTarget is { Field: IconLibraryApplyField.SeIconChar }) {
			if (TryGetDockItemForIconLibraryTarget(out var item)) {
				item.CustomSeIconChar = output;
				ModuleConfig.Save();
				applied = true;
			} else {
				iconLibraryApplyTarget = null;
			}
		}
		SetIconLibraryLastAction(applied ? $"已应用 SeIconChar：{output}" : $"已复制 SeIconChar：{output}");
	}

	private string BuildSeIconCharOutput(SeIconCharEntry entry) {
		return iconLibrarySeIconOutputFormat switch {
			SeIconCharOutputFormat.Name => entry.Name,
			SeIconCharOutputFormat.Decimal => entry.Value.ToString(CultureInfo.InvariantCulture),
			SeIconCharOutputFormat.Hex => $"0x{entry.Value:X}",
			_ => entry.Name
		};
	}

	private ISharedImmediateTexture? GetGameIconPreviewTexture(uint iconId) {
		if (iconId == 0)
			return null;
		if (gameIconPreviewCache.TryGetValue(iconId, out var cached))
			return cached;
		try {
			var texture = Plugin.Texture.GetFromGameIcon(new GameIconLookup(iconId));
			gameIconPreviewCache[iconId] = texture;
			gameIconPreviewCacheOrder.Enqueue(iconId);
			const int limit = 512;
			while (gameIconPreviewCacheOrder.Count > limit) {
				var remove = gameIconPreviewCacheOrder.Dequeue();
				gameIconPreviewCache.Remove(remove);
			}
			return texture;
		} catch {
			return null;
		}
	}

	private void DrawIconLibraryGameIcons() {
		iconLibraryGameIconStartId = Math.Max(0, iconLibraryGameIconStartId);
		iconLibraryGameIconCount = Math.Clamp(iconLibraryGameIconCount, 1, 2000);
		var previewSize = iconLibraryPreviewIconSize;
		if (ImGui.SliderFloat("预览大小", ref previewSize, 16f, 64f, "%.0f"))
			iconLibraryPreviewIconSize = Math.Clamp(previewSize, 16f, 64f);
		var onlyValid = iconLibraryGameIconOnlyValid;
		if (ImGui.Checkbox("仅显示有效图标", ref onlyValid))
			iconLibraryGameIconOnlyValid = onlyValid;
		ImGui.SameLine();
		if (ImGui.SmallButton("清空预览缓存")) {
			gameIconPreviewCache.Clear();
			gameIconPreviewCacheOrder.Clear();
		}
		ImGui.SetNextItemWidth(140f);
		if (ImGui.InputInt("起始ID", ref iconLibraryGameIconStartId))
			iconLibraryGameIconStartId = Math.Max(0, iconLibraryGameIconStartId);
		ImGui.SameLine();
		ImGui.SetNextItemWidth(120f);
		if (ImGui.InputInt("数量", ref iconLibraryGameIconCount))
			iconLibraryGameIconCount = Math.Clamp(iconLibraryGameIconCount, 1, 2000);
		ImGui.SameLine();
		if (ImGui.SmallButton("上一页"))
			iconLibraryGameIconStartId = Math.Max(0, iconLibraryGameIconStartId - iconLibraryGameIconCount);
		ImGui.SameLine();
		if (ImGui.SmallButton("下一页"))
			iconLibraryGameIconStartId = Math.Max(0, iconLibraryGameIconStartId + iconLibraryGameIconCount);
		ImGui.SetNextItemWidth(140f);
		ImGui.InputInt("跳转到", ref iconLibraryGameIconJumpId);
		ImGui.SameLine();
		if (ImGui.SmallButton("跳转"))
			iconLibraryGameIconStartId = Math.Max(0, iconLibraryGameIconJumpId);
		ImGui.SameLine();
		ImGui.TextDisabled(iconLibraryLastSelectedGameIconId != 0
			? $"最近选择：{iconLibraryLastSelectedGameIconId}"
			: "最近选择：-");
		ImGui.BeginChild("iconlib_game_grid_child", new Vector2(0f, 0f), true);
		var buttonSize = new Vector2(iconLibraryPreviewIconSize, iconLibraryPreviewIconSize);
		var spacing = ImGui.GetStyle().ItemSpacing.X;
		var available = ImGui.GetContentRegionAvail().X;
		var columns = Math.Max(1, (int)MathF.Floor((available + spacing) / (buttonSize.X + spacing)));
		columns = Math.Clamp(columns, 1, 32);
		const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings;
		if (ImGui.BeginTable("iconlib_game_grid", columns, tableFlags)) {
			for (var i = 0; i < iconLibraryGameIconCount; i++) {
				var raw = iconLibraryGameIconStartId + i;
				if (raw < 0)
					continue;
				var iconId = (uint)raw;
				var texture = GetGameIconPreviewTexture(iconId);
				var hasWrap = texture != null && texture.TryGetWrap(out _, out _);
				if (iconLibraryGameIconOnlyValid && !hasWrap)
					continue;
				ImGui.TableNextColumn();
				ImGui.PushID(raw);
				bool clicked;
				if (texture != null && texture.TryGetWrap(out var wrap, out _))
					clicked = ImGui.ImageButton(wrap.Handle, buttonSize);
				else
					clicked = ImGui.Button(" ", buttonSize);
				if (ImGui.IsItemHovered()) {
					ImGui.BeginTooltip();
					ImGui.TextUnformatted($"ID: {iconId}");
					if (texture != null && texture.TryGetWrap(out var tooltipWrap, out _))
						ImGui.Image(tooltipWrap.Handle, new Vector2(buttonSize.X * 2f, buttonSize.Y * 2f));
					ImGui.EndTooltip();
				}
				if (clicked)
					ApplyIconLibraryGameIconSelection(iconId);
				ImGui.PopID();
			}
			ImGui.EndTable();
		}
		ImGui.EndChild();
	}

	private void EnsureSeIconCharEntries() {
		if (seIconCharEntries != null)
			return;
		seIconCharEntries = [];
		foreach (var icon in Enum.GetValues<SeIconChar>()) {
			var value = Convert.ToInt32(icon);
			if (value == 0)
				continue;
			seIconCharEntries.Add(new SeIconCharEntry {
				Name = icon.ToString(),
				Value = value,
				IconText = icon.ToIconString()
			});
		}
		seIconCharEntries.Sort(static (a, b) => a.Value.CompareTo(b.Value));
	}

	private void RefreshSeIconCharFilterCache() {
		EnsureSeIconCharEntries();
		var filter = iconLibrarySeIconFilter.Trim();
		if (filter.Equals(iconLibrarySeIconFilterLast, StringComparison.Ordinal))
			return;
		iconLibrarySeIconFilterLast = filter;
		iconLibrarySeIconFilteredIndices.Clear();
		if (filter.Length == 0)
			return;
		var isNumeric = int.TryParse(filter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var filterNumber);
		foreach (var (entry, index) in seIconCharEntries!.Select((e, i) => (e, i))) {
			if (entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) {
				iconLibrarySeIconFilteredIndices.Add(index);
				continue;
			}
			if (isNumeric && entry.Value == filterNumber) {
				iconLibrarySeIconFilteredIndices.Add(index);
				continue;
			}
			if ($"0x{entry.Value:X}".Contains(filter, StringComparison.OrdinalIgnoreCase))
				iconLibrarySeIconFilteredIndices.Add(index);
		}
	}

	private void DrawIconLibrarySeIconChars() {
		EnsureSeIconCharEntries();
		RefreshSeIconCharFilterCache();
		ImGui.SetNextItemWidth(260f);
		InputTextUtf8("筛选（名字/数值/0x）", ref iconLibrarySeIconFilter, 128);
		ImGui.SameLine();
		var fmt = (int)iconLibrarySeIconOutputFormat;
		var fmtLabels = new[] { "枚举名", "十进制", "十六进制(0x)" };
		if (ImGui.Combo("复制/填入格式", ref fmt, fmtLabels, fmtLabels.Length))
			iconLibrarySeIconOutputFormat = (SeIconCharOutputFormat)Math.Clamp(fmt, 0, fmtLabels.Length - 1);
		var filter = iconLibrarySeIconFilter.Trim();
		var hasFilter = filter.Length > 0;
		var rowCount = hasFilter ? iconLibrarySeIconFilteredIndices.Count : seIconCharEntries!.Count;
		ImGui.BeginChild("iconlib_seicon_child", new Vector2(0f, 0f), true);
		const ImGuiTableFlags flags = ImGuiTableFlags.RowBg |
		                              ImGuiTableFlags.BordersOuter |
		                              ImGuiTableFlags.BordersInnerV |
		                              ImGuiTableFlags.ScrollY |
		                              ImGuiTableFlags.SizingFixedFit |
		                              ImGuiTableFlags.NoSavedSettings;
		if (!ImGui.BeginTable("iconlib_seicon_table", 5, flags)) {
			ImGui.EndChild();
			return;
		}
		ImGui.TableSetupScrollFreeze(0, 1);
		ImGui.TableSetupColumn("预览", ImGuiTableColumnFlags.WidthFixed, Math.Max(46f, iconLibraryPreviewIconSize + 14f));
		ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.WidthStretch);
		ImGui.TableSetupColumn("数值", ImGuiTableColumnFlags.WidthFixed, 80f);
		ImGui.TableSetupColumn("Hex", ImGuiTableColumnFlags.WidthFixed, 90f);
		ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 60f);
		ImGui.TableHeadersRow();
		for (var row = 0; row < rowCount; row++) {
			var entryIndex = hasFilter ? iconLibrarySeIconFilteredIndices[row] : row;
			if (entryIndex < 0 || entryIndex >= seIconCharEntries!.Count)
				continue;
			var entry = seIconCharEntries[entryIndex];
			ImGui.PushID(entry.Value);
			ImGui.TableNextRow();
			ImGui.TableNextColumn();
			var previewClicked = ImGui.Button(entry.IconText, new Vector2(iconLibraryPreviewIconSize, iconLibraryPreviewIconSize));
			if (ImGui.IsItemHovered()) {
				ImGui.BeginTooltip();
				ImGui.TextUnformatted(entry.Name);
				ImGui.TextDisabled($"数值：{entry.Value}  Hex：0x{entry.Value:X}");
				ImGui.EndTooltip();
			}
			ImGui.TableNextColumn();
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted(entry.Name);
			ImGui.TableNextColumn();
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted(entry.Value.ToString(CultureInfo.InvariantCulture));
			ImGui.TableNextColumn();
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted($"0x{entry.Value:X}");
			ImGui.TableNextColumn();
			var copyClicked = ImGui.SmallButton("复制");
			if (previewClicked || copyClicked)
				ApplyIconLibrarySeIconCharSelection(entry);
			ImGui.PopID();
		}
		ImGui.EndTable();
		ImGui.EndChild();
	}
}