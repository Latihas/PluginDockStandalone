using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;

namespace PluginDockStandalone;

internal sealed partial class PluginDockController : IDisposable {
	private const string SmallIconSuffix = "-SmallIcon";
	private const string IconLibraryWindowKey = "###PluginDockStandalone.IconLibrary";
	private const string IconLibraryWindowName = "图标素材库" + IconLibraryWindowKey;
	private static readonly string SelfImGuiWindowKeyPrefix =
		IconLibraryWindowKey[..(IconLibraryWindowKey.LastIndexOf('.') + 1)];
	private static readonly Vector4 DockItemKindColorPlugin = new(0.45f, 0.85f, 0.55f, 1f);
	private static readonly Vector4 DockItemKindColorWindow = new(0.35f, 0.75f, 1.0f, 1f);
	private static readonly Vector4 DockItemKindColorCommand = new(1.0f, 0.75f, 0.25f, 1f);

	private static readonly HttpClient IconHttp = new() {
		Timeout = TimeSpan.FromSeconds(15)
	};


	private const string CommandName = "/pdock";
	private Config ModuleConfig = null!;
	private readonly WindowSystem windowSystem = new("PluginDockStandalone");
	private readonly FileDialogManager fileDialogManager = new();
	private DockOverlayWindow? Overlay;
	private PluginDockConfigWindow? ConfigWindow;
	private bool initialized;
	private bool fileDialogActive;
	private readonly string selfPluginInternalName;

	private string search = string.Empty;
	private bool showImGuiMetricsWindow;
	private string imGuiWindowNameFilter = string.Empty;
	private int pinnedEditorSelectedIndex = -1;
	private readonly List<string> imGuiWindowNameCache = [];
	private string imGuiWindowNameCacheError = string.Empty;
	private string imGuiWindowRestoreIniError = string.Empty;
	private string imGuiWindowRestoreIniErrorWindow = string.Empty;
	private readonly Dictionary<string, PendingImGuiWindowRestore> pendingImGuiWindowRestores = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, int> pendingImGuiWindowFocusRequests = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> transientHiddenImGuiWindows = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, ImGuiWindowRestoreState> transientHiddenImGuiWindowRestoreStates = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> forcedVisibleImGuiWindowKeys = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> toggleOnClickOpenedTargets = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> hardHiddenImGuiWindowKeysScratch = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> imGuiWindowKeyCacheByName = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<nint, string> imGuiWindowKeyCacheByNamePtr = new();
	private int selectedWindowHiderPresetIndex;
	private string windowHiderPresetNameInput = string.Empty;
	private string shareCodeExport = string.Empty;
	private string shareCodeImport = string.Empty;
	private string shareCodeImportError = string.Empty;
	private bool applyPresetAfterImport = true;
	private bool requestOpenOverlayConfig;
	private int requestOpenOverlayConfigDelayFrames;
	private double dockAutoCollapseAtTime;
	private bool dockAnchorDirty;
	private bool dockDragging;
	private bool dockDragMoved;
	private Vector2 dockDragStartMouse;
	private Vector2 dockDragStartAnchor;

	private readonly List<(DockItem Item, IExposedPlugin? Plugin)> visibleDockItemsScratch = [];

	private long loadedPluginsCacheNextRefreshAtTick;
	private List<IExposedPlugin> loadedPluginsCache = [];
	private Dictionary<string, IExposedPlugin> loadedPluginByInternalNameCache = new(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, string> localIconPathCache = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, Task> iconDownloadTasks = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, DateTime> iconDownloadCooldownUntilUtc = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, ISharedImmediateTexture> textureCache = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<uint, ISharedImmediateTexture> gameIconCache = [];
	private ISharedImmediateTexture? fallbackPluginIcon;
	private DateTime lastUiDrawExceptionAtUtc;

	private bool showIconLibraryWindow;
	private IconLibraryApplyTarget? iconLibraryApplyTarget;
	private IconLibraryMode iconLibraryMode = IconLibraryMode.GameIcon;
	private float iconLibraryPreviewIconSize = 32f;
	private int iconLibraryGameIconStartId = 1;
	private int iconLibraryGameIconCount = 240;
	private int iconLibraryGameIconJumpId;
	private bool iconLibraryGameIconOnlyValid = true;
	private uint iconLibraryLastSelectedGameIconId;
	private string iconLibraryLastActionMessage = string.Empty;
	private double iconLibraryLastActionAtTime;
	private string iconLibrarySeIconFilter = string.Empty;
	private string iconLibrarySeIconFilterLast = string.Empty;
	private readonly List<int> iconLibrarySeIconFilteredIndices = [];
	private SeIconCharOutputFormat iconLibrarySeIconOutputFormat = SeIconCharOutputFormat.Decimal;
	private List<SeIconCharEntry>? seIconCharEntries;
	private readonly Dictionary<uint, ISharedImmediateTexture> gameIconPreviewCache = [];
	private readonly Queue<uint> gameIconPreviewCacheOrder = new();

	public PluginDockController() {
		ModuleConfig = LoadConfig() ?? new Config();
		ModuleConfig.HiddenImGuiWindowRestoreStates ??= new Dictionary<string, ImGuiWindowRestoreState>(StringComparer.OrdinalIgnoreCase);
		ModuleConfig.WindowHiderPresets ??= [];
		selfPluginInternalName = ResolveSelfPluginInternalName();
		SanitizeConfigForSelfProtection();
		EnsureOverlay();
		EnsureConfigWindow();
		ApplyOverlayState();

		DService.Command.AddHandler(CommandName, new CommandInfo(OnCommand) {
			HelpMessage = $"{CommandName} 打开/关闭收纳栏\n{CommandName} open|close\n{CommandName} hidewin|showwin <窗口名>\n{CommandName} config"
		});

		DService.PI.UiBuilder.Draw += Draw;
		DService.PI.UiBuilder.OpenConfigUi += OpenConfigUi;
		DService.PI.UiBuilder.OpenMainUi += OpenMainUi;
		initialized = true;
	}

	private static string ResolveSelfPluginInternalName() {
		try {
			var pi = DService.PI;
			var prop = pi.GetType().GetProperty("InternalName", BindingFlags.Public | BindingFlags.Instance);
			var value = prop?.GetValue(pi) as string;
			if (!string.IsNullOrWhiteSpace(value))
				return value.Trim();
		} catch {
		}

		return typeof(PluginDockController).Assembly.GetName().Name ?? "PluginDockStandalone";
	}

	private static bool IsSelfRelatedImGuiWindow(string? windowNameOrId) {
		if (string.IsNullOrWhiteSpace(windowNameOrId))
			return false;

		var key = NormalizeImGuiWindowNameKey(windowNameOrId);
		return key.StartsWith(SelfImGuiWindowKeyPrefix, StringComparison.OrdinalIgnoreCase);
	}

	private void SanitizeConfigForSelfProtection() {
		var changed = false;

		if (!string.IsNullOrWhiteSpace(selfPluginInternalName)) {
			var removed = ModuleConfig.Items.RemoveAll(x =>
				x.Kind == DockItemKind.Plugin &&
				x.InternalName.Equals(selfPluginInternalName, StringComparison.OrdinalIgnoreCase));
			if (removed > 0)
				changed = true;
		}

		var removedSelfWindows = ModuleConfig.Items.RemoveAll(x =>
			x.Kind == DockItemKind.ImGuiWindow &&
			IsSelfRelatedImGuiWindow(x.InternalName));
		if (removedSelfWindows > 0)
			changed = true;

		if (ModuleConfig.HiddenImGuiWindows.Count > 0) {
			var removedHidden = ModuleConfig.HiddenImGuiWindows.RemoveAll(x => IsSelfRelatedImGuiWindow(x));
			if (removedHidden > 0)
				changed = true;
		}

		if (ModuleConfig.HiddenImGuiWindowRestoreStates.Count > 0) {
			foreach (var name in ModuleConfig.HiddenImGuiWindowRestoreStates.Keys.ToArray()) {
				if (!IsSelfRelatedImGuiWindow(name))
					continue;
				ModuleConfig.HiddenImGuiWindowRestoreStates.Remove(name);
				changed = true;
			}
		}

		if (changed)
			SaveConfig(ModuleConfig);
	}

	public void Dispose() {
		DService.Command.RemoveHandler(CommandName);
		DService.PI.UiBuilder.Draw -= Draw;
		DService.PI.UiBuilder.OpenConfigUi -= OpenConfigUi;
		DService.PI.UiBuilder.OpenMainUi -= OpenMainUi;
		windowSystem.RemoveAllWindows();
	}

	internal void DrawConfigUi() {
		if (ImGui.BeginTabBar("PluginDockTabs")) {
			if (ImGui.BeginTabItem("悬浮窗隐藏")) {
				DrawWindowHiderTab();
				ImGui.EndTabItem();
			}

			if (ImGui.BeginTabItem("收纳栏")) {
				DrawDockTab();
				ImGui.EndTabItem();
			}

			if (ImGui.BeginTabItem("调试")) {
				DrawDebugTab();
				ImGui.EndTabItem();
			}

			ImGui.EndTabBar();
		}
	}

	private void DrawWindowHiderTab() {
		var hiderEnabled = ModuleConfig.WindowHiderEnabled;
		if (ImGui.Checkbox("隐藏悬浮窗", ref hiderEnabled)) {
			ModuleConfig.WindowHiderEnabled = hiderEnabled;
			SaveConfig(ModuleConfig);
		}

		ImGui.SameLine();
		ImGuiOm.HelpMarker("按 ImGui 窗口名隐藏悬浮窗；取消隐藏会尝试恢复隐藏前的位置。");

		var hiddenCount = ModuleConfig.HiddenImGuiWindows.Count;
		var hiddenHeaderFlags = hiddenCount > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
		if (ImGui.CollapsingHeader($"已隐藏 ({hiddenCount})", hiddenHeaderFlags))
			DrawHiddenWindowList();

		if (ImGui.CollapsingHeader("窗口列表", ImGuiTreeNodeFlags.DefaultOpen))
			DrawWindowNameTools();

		if (ImGui.CollapsingHeader("预设/分享码"))
			DrawWindowHiderPresetsAndShareCodes();
	}

	private void DrawDockTab() {
		var open = ModuleConfig.DockEnabled && ModuleConfig.DockOpen;
		if (ImGui.Checkbox("显示收纳栏", ref open)) {
			if (open) {
				ModuleConfig.DockEnabled = true;
				ModuleConfig.DockOpen = true;
			} else {
				ModuleConfig.DockOpen = false;
			}

			ApplyOverlayState();
			SaveConfig(ModuleConfig);
		}

		ImGui.SameLine();
		ImGuiOm.HelpMarker("左键：插件=打开主界面（无主界面则打开设置）；窗口=显示/隐藏；右键：更多操作。");

		if (ImGui.CollapsingHeader("外观")) {
			var noDecoration = ModuleConfig.NoDecoration;
			if (ImGui.Checkbox("无边框/自动大小", ref noDecoration)) {
				ModuleConfig.NoDecoration = noDecoration;
				ApplyOverlayState();
				SaveConfig(ModuleConfig);
			}

			var collapsed = ModuleConfig.Collapsed;
			if (ImGui.Checkbox("折叠为单图标", ref collapsed)) {
				ModuleConfig.Collapsed = collapsed;
				ApplyOverlayState();
				SaveConfig(ModuleConfig);
			}

			var lockPosition = ModuleConfig.LockPosition;
			if (ImGui.Checkbox("锁定位置（不可拖动）", ref lockPosition)) {
				ModuleConfig.LockPosition = lockPosition;
				ApplyOverlayState();
				SaveConfig(ModuleConfig);
			}

			var transparentButtons = ModuleConfig.TransparentButtons;
			if (ImGui.Checkbox("图标无按钮底色", ref transparentButtons)) {
				ModuleConfig.TransparentButtons = transparentButtons;
				SaveConfig(ModuleConfig);
			}

			var dockHeaderIconPath = ModuleConfig.DockHeaderIconPath ?? string.Empty;
			if (DrawDockTextInputWithPicker("悬浮窗图标路径", "仅支持绝对路径。留空=使用默认图标。", "##dock_header_icon_path", ref dockHeaderIconPath, 260, "浏览", "##dock_header_icon_pick", OpenDockHeaderIconPicker)) {
				var trimmed = dockHeaderIconPath.Trim();
				ModuleConfig.DockHeaderIconPath = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
				SaveConfig(ModuleConfig);
			}

			var iconSize = ModuleConfig.IconSize;
			if (ImGui.SliderFloat("图标大小", ref iconSize, 16f, 64f, "%.0f")) {
				ModuleConfig.IconSize = iconSize;
				SaveConfig(ModuleConfig);
			}

			var iconSpacing = ModuleConfig.IconSpacing;
			if (ImGui.SliderFloat("图标间距", ref iconSpacing, 0f, 16f, "%.0f")) {
				ModuleConfig.IconSpacing = iconSpacing;
				SaveConfig(ModuleConfig);
			}

			var maxIconsPerRow = ModuleConfig.MaxIconsPerRow;
			if (ImGui.SliderInt("每行最多##dock_max_row", ref maxIconsPerRow, 0, 50)) {
				ModuleConfig.MaxIconsPerRow = maxIconsPerRow;
				SaveConfig(ModuleConfig);
			}

			ImGui.SameLine();
			ImGuiOm.HelpMarker("0 = 不限制");

			var expandDirection = (int)ModuleConfig.ExpandDirection;
			var expandDirectionLabels = new[] { "向右", "向左", "向下", "向上" };
			if (ImGui.Combo("展开方向##dock_expand_dir", ref expandDirection, expandDirectionLabels, expandDirectionLabels.Length)) {
				ModuleConfig.ExpandDirection = (DockExpandDirection)Math.Clamp(expandDirection, 0, expandDirectionLabels.Length - 1);
				if (ModuleConfig.ExpandDirection is DockExpandDirection.Right or DockExpandDirection.Left) {
					if (ModuleConfig.WrapDirection is DockWrapDirection.Left or DockWrapDirection.Right)
						ModuleConfig.WrapDirection = DockWrapDirection.Down;
				} else {
					if (ModuleConfig.WrapDirection is DockWrapDirection.Down or DockWrapDirection.Up)
						ModuleConfig.WrapDirection = DockWrapDirection.Right;
				}

				SaveConfig(ModuleConfig);
			}

			if (ModuleConfig.ExpandDirection is DockExpandDirection.Right or DockExpandDirection.Left) {
				var wrap = ModuleConfig.WrapDirection == DockWrapDirection.Up ? 1 : 0;
				var wrapLabels = new[] { "向下", "向上" };
				if (ImGui.Combo("换行方向##dock_wrap_dir_h", ref wrap, wrapLabels, wrapLabels.Length)) {
					ModuleConfig.WrapDirection = wrap == 1 ? DockWrapDirection.Up : DockWrapDirection.Down;
					SaveConfig(ModuleConfig);
				}
			} else {
				var wrap = ModuleConfig.WrapDirection == DockWrapDirection.Left ? 1 : 0;
				var wrapLabels = new[] { "向右", "向左" };
				if (ImGui.Combo("换列方向##dock_wrap_dir_v", ref wrap, wrapLabels, wrapLabels.Length)) {
					ModuleConfig.WrapDirection = wrap == 1 ? DockWrapDirection.Left : DockWrapDirection.Right;
					SaveConfig(ModuleConfig);
				}
			}

			var autoHideSeconds = ModuleConfig.AutoHideSeconds;
			if (ImGui.SliderFloat("展开后自动隐藏(秒)##dock_autohide", ref autoHideSeconds, 0f, 30f, "%.0f")) {
				ModuleConfig.AutoHideSeconds = autoHideSeconds;
				if (autoHideSeconds <= 0f)
					dockAutoCollapseAtTime = 0;
				SaveConfig(ModuleConfig);
			}

			ImGui.SameLine();
			ImGuiOm.HelpMarker("0 = 关闭");
		}

		if (ImGui.CollapsingHeader("收纳内容", ImGuiTreeNodeFlags.DefaultOpen)) {
			var loadedPlugins = GetLoadedPluginsCached();
			var uiPlugins = loadedPlugins.Where(p => p.HasMainUi || p.HasConfigUi).ToList();
			if (uiPlugins.Count == 0)
				ImGui.TextDisabled("未找到可打开主界面/设置的已加载插件（仍可编辑已收纳的“窗口”条目）。");

			var lineHeight = ImGui.GetTextLineHeightWithSpacing();
			var listHeight = MathF.Min(360f, lineHeight * 16f);

			const ImGuiTableFlags manageFlags = ImGuiTableFlags.SizingStretchProp |
			                                    ImGuiTableFlags.BordersInnerV |
			                                    ImGuiTableFlags.NoSavedSettings;

			if (ImGui.BeginTable("dock_manage", 2, manageFlags)) {
				ImGui.TableSetupColumn($"已收纳 ({ModuleConfig.Items.Count})", ImGuiTableColumnFlags.WidthStretch, 0.55f);
				ImGui.TableSetupColumn("可收纳", ImGuiTableColumnFlags.WidthStretch, 0.45f);
				ImGui.TableHeadersRow();

				ImGui.TableNextRow();

				ImGui.TableNextColumn();
				if (ImGui.SmallButton("新增命令项##dock_add_cmd"))
					AddDockItemForCommand();

				ImGui.SameLine();
				ImGuiOm.HelpMarker("添加一个空白收纳项，仅用于“点击命令”（发送宏指令 /xxx），不绑定插件/窗口。");

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

					ImGui.SameLine();
					ImGuiOm.HelpMarker("添加一个空白收纳项，仅用于“点击命令”（发送宏指令 /xxx），不绑定插件/窗口。");
					DrawPinnedEditor(loadedPlugins);
				}
			}
		}
	}

	private void DrawDebugTab() {
		if (ImGui.Button(showImGuiMetricsWindow ? "关闭 ImGui 指标(Metrics)" : "打开 ImGui 指标(Metrics)")) {
			showImGuiMetricsWindow = !showImGuiMetricsWindow;
			if (showImGuiMetricsWindow && imGuiWindowNameCache.Count == 0)
				RefreshImGuiWindowNameCache();
		}

		ImGui.SameLine();
		ImGuiOm.HelpMarker("用于查看窗口名：在 Metrics -> Windows 里找到悬浮窗的名字。");

		if (ImGui.Button("打开图标素材库"))
			OpenIconLibrary(null, IconLibraryMode.GameIcon);

		ImGui.SameLine();
		ImGuiOm.HelpMarker("预览游戏图标ID/SeIconChar，点击条目可复制；设置了“目标”时也可一键应用到收纳条目。");

		if (!string.IsNullOrWhiteSpace(imGuiWindowRestoreIniError))
			ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"恢复窗口 ini 失败: {imGuiWindowRestoreIniErrorWindow} ({imGuiWindowRestoreIniError})");

		if (ImGui.CollapsingHeader("窗口列表缓存")) {
			ImGui.TextDisabled($"缓存：{imGuiWindowNameCache.Count}  隐藏：{ModuleConfig.HiddenImGuiWindows.Count}  待恢复：{pendingImGuiWindowRestores.Count}");

			if (ImGui.SmallButton("刷新"))
				RefreshImGuiWindowNameCache();

			ImGui.SameLine();
			if (ImGui.SmallButton("清空")) {
				imGuiWindowNameCache.Clear();
				imGuiWindowNameCacheError = string.Empty;
			}

			if (!string.IsNullOrWhiteSpace(imGuiWindowNameCacheError))
				ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), imGuiWindowNameCacheError);
		}
	}

	private void DrawHiddenWindowList() {
		if (ModuleConfig.HiddenImGuiWindows.Count == 0) {
			ImGui.TextDisabled("无");
			return;
		}

		if (ImGui.SmallButton("全部显示")) {
			var names = ModuleConfig.HiddenImGuiWindows.ToArray();
			for (var i = 0; i < names.Length; i++)
				SetImGuiWindowHidden(names[i], false, false);

			SaveConfig(ModuleConfig);
		}

		ImGui.SameLine();
		if (ImGui.SmallButton("复制")) {
			ImGui.SetClipboardText(string.Join('\n', ModuleConfig.HiddenImGuiWindows));
			HelpersOm.NotificationInfo($"已复制 {ModuleConfig.HiddenImGuiWindows.Count} 个窗口名");
		}

		var lineHeight = ImGui.GetTextLineHeightWithSpacing();
		var height = MathF.Min(220f, lineHeight * 10f);
		ImGui.BeginChild("hidden_win_list", new Vector2(0f, height), true);

		var hiddenNames = ModuleConfig.HiddenImGuiWindows.ToArray();
		for (var i = 0; i < hiddenNames.Length; i++) {
			var name = hiddenNames[i];
			if (string.IsNullOrWhiteSpace(name)) continue;

			ImGui.PushID($"hidewin_{i}");
			ImGui.TextUnformatted(name);
			ImGui.SameLine();
			if (ImGui.SmallButton("找回")) {
				RecoverHiddenImGuiWindow(name, false);
				ImGui.PopID();
				continue;
			}

			ImGui.SameLine();
			if (ImGui.SmallButton("重置")) {
				RecoverHiddenImGuiWindow(name, true);
				ImGui.PopID();
				continue;
			}

			ImGui.SameLine();
			if (ImGui.SmallButton("收纳##dock"))
				AddDockItemForImGuiWindow(name);

			ImGui.PopID();
		}

		ImGui.EndChild();
	}

	private void RecoverHiddenImGuiWindow(string windowNameOrId, bool resetPosition) {
		if (string.IsNullOrWhiteSpace(windowNameOrId))
			return;

		var trimmed = windowNameOrId.Trim();
		if (trimmed.Length == 0)
			return;

		var focusKey = NormalizeImGuiWindowNameKey(trimmed);
		var focusTarget = focusKey.Length > 0 ? focusKey : trimmed;

		try {
			if (TryGetWindowManagerWindow(trimmed, out var window) ||
			    focusKey.Length > 0 && TryGetWindowManagerWindow(focusKey, out window)) {
				window.IsOpen = true;
			}
		} catch {
		}

		if (resetPosition) {
			ClearTransientHiddenByKey(focusTarget);
			SetImGuiWindowForceVisible(focusTarget, true);

			var restoreState = BuildFallbackVisibleRestoreState();
			QueueImGuiWindowRestore(focusTarget, restoreState);
			QueueImGuiWindowFocus(focusTarget, 45);

			if (!TryRestoreImGuiWindowIni(focusTarget, restoreState, out var restoreError) &&
			    !string.IsNullOrWhiteSpace(restoreError)) {
				imGuiWindowRestoreIniError = restoreError;
				imGuiWindowRestoreIniErrorWindow = trimmed;
			} else {
				imGuiWindowRestoreIniError = string.Empty;
				imGuiWindowRestoreIniErrorWindow = string.Empty;
			}
		} else {
			ShowAndFocusImGuiWindowTemporary(focusTarget);
		}

		SetImGuiWindowHiddenByKey(focusTarget, false);
	}

	private void DrawWindowNameTools() {
		if (ImGui.SmallButton("刷新"))
			RefreshImGuiWindowNameCache();

		ImGui.SameLine();
		ImGui.SetNextItemWidth(260f);
		InputTextUtf8("过滤##win_filter", ref imGuiWindowNameFilter, 128);

		ImGui.SameLine();
		ImGuiOm.HelpMarker("列表来自 ImGui Ini（非实时）。列表里找不到时：打开“调试”-> ImGui Metrics 查看窗口名。");

		if (!string.IsNullOrWhiteSpace(imGuiWindowNameCacheError))
			ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), imGuiWindowNameCacheError);

		if (imGuiWindowNameCache.Count == 0) {
			ImGui.TextDisabled("无");
			return;
		}

		var filter = imGuiWindowNameFilter?.Trim() ?? string.Empty;
		var lineHeight = ImGui.GetTextLineHeightWithSpacing();
		var height = MathF.Min(240f, lineHeight * 12f);

		if (!ImGui.BeginChild("win_name_cache", new Vector2(0f, height), true))
			return;

		for (var i = 0; i < imGuiWindowNameCache.Count; i++) {
			var name = imGuiWindowNameCache[i];
			if (!string.IsNullOrWhiteSpace(filter) &&
			    !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
				continue;

			ImGui.PushID(i);

			var isHidden = IsImGuiWindowHidden(name);
			var isSelf = IsSelfRelatedImGuiWindow(name);
			if (isHidden)
				ImGui.TextDisabled(name);
			else
				ImGui.TextUnformatted(name);

			ImGui.SameLine();
			if (ImGui.SmallButton("复制")) {
				ImGui.SetClipboardText(name);
			}

			ImGui.SameLine();
			ImGui.BeginDisabled(isSelf);
			if (ImGui.SmallButton(isHidden ? "显示" : "隐藏"))
				SetImGuiWindowHidden(name, !isHidden);
			ImGui.EndDisabled();
			if (isSelf && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
				ImGui.SetTooltip("不能隐藏本插件自身或相关窗口");

			ImGui.SameLine();
			ImGui.BeginDisabled(isSelf);
			if (ImGui.SmallButton("收纳##dock"))
				AddDockItemForImGuiWindow(name);
			ImGui.EndDisabled();
			if (isSelf && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
				ImGui.SetTooltip("不能收纳本插件自身或相关窗口");

			ImGui.PopID();
		}

		ImGui.EndChild();
	}

	private void DrawWindowHiderPresetsAndShareCodes() {
		if (ModuleConfig.WindowHiderPresets.Count > 0) {
			selectedWindowHiderPresetIndex = Math.Clamp(selectedWindowHiderPresetIndex, 0, ModuleConfig.WindowHiderPresets.Count - 1);
			var currentPresetName = ModuleConfig.WindowHiderPresets[selectedWindowHiderPresetIndex].Name;

			if (ImGui.BeginCombo("当前预设", string.IsNullOrWhiteSpace(currentPresetName) ? "（未命名）" : currentPresetName)) {
				for (var i = 0; i < ModuleConfig.WindowHiderPresets.Count; i++) {
					var preset = ModuleConfig.WindowHiderPresets[i];
					var name = string.IsNullOrWhiteSpace(preset.Name) ? "（未命名）" : preset.Name;
					var selected = i == selectedWindowHiderPresetIndex;
					if (ImGui.Selectable(name, selected))
						selectedWindowHiderPresetIndex = i;
					if (selected)
						ImGui.SetItemDefaultFocus();
				}

				ImGui.EndCombo();
			}

			ImGui.SameLine();
			if (ImGui.SmallButton("应用"))
				ApplyWindowHiderPreset(ModuleConfig.WindowHiderPresets[selectedWindowHiderPresetIndex]);

			ImGui.SameLine();
			if (ImGui.SmallButton("删除")) {
				ModuleConfig.WindowHiderPresets.RemoveAt(selectedWindowHiderPresetIndex);
				if (ModuleConfig.WindowHiderPresets.Count == 0)
					selectedWindowHiderPresetIndex = 0;
				else
					selectedWindowHiderPresetIndex = Math.Clamp(selectedWindowHiderPresetIndex, 0, ModuleConfig.WindowHiderPresets.Count - 1);
				SaveConfig(ModuleConfig);
			}
		} else {
			ImGui.TextDisabled("暂无预设");
		}

		ImGui.SetNextItemWidth(260f);
		InputTextUtf8("预设名", ref windowHiderPresetNameInput, 64);
		ImGui.SameLine();
		if (ImGui.SmallButton("保存当前"))
			SaveCurrentHiddenWindowsAsPreset(windowHiderPresetNameInput);

		if (ImGui.SmallButton("复制当前分享码")) {
			shareCodeExport = CreateWindowHiderShareCode("当前隐藏列表", ModuleConfig.HiddenImGuiWindows);
			ImGui.SetClipboardText(shareCodeExport);
			HelpersOm.NotificationInfo("已复制分享码到剪贴板");
		}

		if (ModuleConfig.WindowHiderPresets.Count > 0) {
			ImGui.SameLine();
			if (ImGui.SmallButton("复制预设分享码")) {
				var preset = ModuleConfig.WindowHiderPresets[selectedWindowHiderPresetIndex];
				shareCodeExport = CreateWindowHiderShareCode(preset.Name, preset.HiddenWindows);
				ImGui.SetClipboardText(shareCodeExport);
				HelpersOm.NotificationInfo("已复制分享码到剪贴板");
			}
		}

		ImGui.SetNextItemWidth(-1f);
		InputTextMultilineUtf8("导入分享码##share_import", ref shareCodeImport, 8192, new Vector2(-1f, 80f));

		ImGui.Checkbox("导入后应用", ref applyPresetAfterImport);
		ImGui.SameLine();
		if (ImGui.SmallButton("导入")) {
			if (TryDecodeWindowHiderShareCode(shareCodeImport, out var preset, out var error)) {
				shareCodeImportError = string.Empty;
				ImportWindowHiderPreset(preset, applyPresetAfterImport);
			} else {
				shareCodeImportError = error;
			}
		}

		if (!string.IsNullOrWhiteSpace(shareCodeImportError))
			ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), shareCodeImportError);
	}

	private void SaveCurrentHiddenWindowsAsPreset(string nameInput) {
		var name = (nameInput ?? string.Empty).Trim();
		if (string.IsNullOrWhiteSpace(name))
			name = $"预设{ModuleConfig.WindowHiderPresets.Count + 1}";

		var hiddenWindows = NormalizeWindowNameList(ModuleConfig.HiddenImGuiWindows);
		if (hiddenWindows.Count == 0) {
			HelpersOm.NotificationError("当前隐藏列表为空，无法保存预设");
			return;
		}

		var existingIndex = ModuleConfig.WindowHiderPresets.FindIndex(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
		if (existingIndex >= 0) {
			ModuleConfig.WindowHiderPresets[existingIndex].HiddenWindows = hiddenWindows;
			selectedWindowHiderPresetIndex = existingIndex;
		} else {
			var preset = new WindowHiderPreset {
				Name = MakeUniqueWindowHiderPresetName(name),
				HiddenWindows = hiddenWindows
			};

			ModuleConfig.WindowHiderPresets.Add(preset);
			selectedWindowHiderPresetIndex = ModuleConfig.WindowHiderPresets.Count - 1;
		}

		SaveConfig(ModuleConfig);
		HelpersOm.NotificationInfo($"已保存预设：{ModuleConfig.WindowHiderPresets[selectedWindowHiderPresetIndex].Name}（{hiddenWindows.Count} 个窗口）");
	}

	private void ApplyWindowHiderPreset(WindowHiderPreset preset) {
		var desired = NormalizeWindowNameList(preset.HiddenWindows);
		if (desired.Count == 0) {
			HelpersOm.NotificationError("预设为空，未做任何操作");
			return;
		}

		var current = ModuleConfig.HiddenImGuiWindows.ToArray();
		var desiredSet = new HashSet<string>(desired, StringComparer.OrdinalIgnoreCase);
		var currentSet = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);

		for (var i = 0; i < current.Length; i++) {
			var name = current[i];
			if (!desiredSet.Contains(name))
				SetImGuiWindowHidden(name, false, false);
		}

		for (var i = 0; i < desired.Count; i++) {
			var name = desired[i];
			if (!currentSet.Contains(name))
				SetImGuiWindowHidden(name, true, false);
		}

		ModuleConfig.HiddenImGuiWindows = desired;
		ModuleConfig.WindowHiderEnabled = true;
		SaveConfig(ModuleConfig);
		HelpersOm.NotificationInfo($"已应用预设：{(string.IsNullOrWhiteSpace(preset.Name) ? "（未命名）" : preset.Name)}（{desired.Count} 个窗口）");
	}

	private void ImportWindowHiderPreset(WindowHiderPreset preset, bool apply) {
		var name = (preset.Name ?? string.Empty).Trim();
		if (string.IsNullOrWhiteSpace(name))
			name = $"导入预设{ModuleConfig.WindowHiderPresets.Count + 1}";

		var hiddenWindows = NormalizeWindowNameList(preset.HiddenWindows);
		if (hiddenWindows.Count == 0) {
			HelpersOm.NotificationError("分享码解析成功，但没有包含任何窗口名");
			return;
		}

		var imported = new WindowHiderPreset {
			Name = MakeUniqueWindowHiderPresetName(name),
			HiddenWindows = hiddenWindows
		};

		ModuleConfig.WindowHiderPresets.Add(imported);
		selectedWindowHiderPresetIndex = ModuleConfig.WindowHiderPresets.Count - 1;
		SaveConfig(ModuleConfig);
		HelpersOm.NotificationInfo($"已导入预设：{imported.Name}（{hiddenWindows.Count} 个窗口）");

		if (apply)
			ApplyWindowHiderPreset(imported);
	}

	private string MakeUniqueWindowHiderPresetName(string desiredName) {
		var baseName = string.IsNullOrWhiteSpace(desiredName) ? "预设" : desiredName.Trim();
		var candidate = baseName;
		var suffix = 1;

		while (ModuleConfig.WindowHiderPresets.Any(x => x.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))) {
			candidate = $"{baseName} ({suffix})";
			suffix++;
		}

		return candidate;
	}

	private static List<string> NormalizeWindowNameList(IEnumerable<string>? names) {
		var result = new List<string>();
		if (names == null)
			return result;

		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var raw in names) {
			var trimmed = raw?.Trim();
			if (string.IsNullOrWhiteSpace(trimmed))
				continue;

			if (IsSelfRelatedImGuiWindow(trimmed))
				continue;

			if (seen.Add(trimmed))
				result.Add(trimmed);

			if (result.Count >= 200)
				break;
		}

		return result;
	}

	private const string WindowHiderShareCodePrefix = "PDRPDH1:";

	private static string CreateWindowHiderShareCode(string? name, IEnumerable<string> hiddenWindows) {
		var payload = new WindowHiderSharePayloadV1 {
			v = 1,
			n = (name ?? string.Empty).Trim(),
			w = NormalizeWindowNameList(hiddenWindows)
		};

		var json = JsonSerializer.Serialize(payload);
		var raw = Encoding.UTF8.GetBytes(json);

		using var output = new MemoryStream();
		using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, true)) {
			gzip.Write(raw, 0, raw.Length);
		}

		var encoded = ToBase64Url(output.ToArray());
		return WindowHiderShareCodePrefix + encoded;
	}

	private static bool TryDecodeWindowHiderShareCode(string? shareCode, out WindowHiderPreset preset, out string error) {
		preset = new WindowHiderPreset();
		error = string.Empty;

		var input = (shareCode ?? string.Empty).Trim();
		if (input.Length == 0) {
			error = "分享码为空";
			return false;
		}

		if (input.StartsWith(WindowHiderShareCodePrefix, StringComparison.OrdinalIgnoreCase))
			input = input.Substring(WindowHiderShareCodePrefix.Length);

		byte[] compressed;
		try {
			compressed = FromBase64Url(input);
		} catch (Exception ex) {
			error = $"分享码解析失败：{ex.Message}";
			return false;
		}

		string json;
		try {
			using var inputStream = new MemoryStream(compressed);
			using var gzip = new GZipStream(inputStream, CompressionMode.Decompress);
			using var reader = new StreamReader(gzip, Encoding.UTF8);
			json = reader.ReadToEnd();
		} catch (Exception ex) {
			error = $"分享码解压失败：{ex.Message}";
			return false;
		}

		WindowHiderSharePayloadV1? payload;
		try {
			payload = JsonSerializer.Deserialize<WindowHiderSharePayloadV1>(json);
		} catch (Exception ex) {
			error = $"分享码内容无效：{ex.Message}";
			return false;
		}

		if (payload == null || payload.v != 1) {
			error = "分享码版本不支持";
			return false;
		}

		var hiddenWindows = NormalizeWindowNameList(payload.w);
		if (hiddenWindows.Count == 0) {
			error = "分享码里没有窗口名";
			return false;
		}

		preset.Name = payload.n ?? string.Empty;
		preset.HiddenWindows = hiddenWindows;
		return true;
	}

	private static string ToBase64Url(byte[] data) {
		var base64 = Convert.ToBase64String(data);
		return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
	}

	private static byte[] FromBase64Url(string input) {
		var base64 = input.Replace('-', '+').Replace('_', '/');
		var remainder = base64.Length % 4;
		if (remainder == 2) base64 += "==";
		else if (remainder == 3) base64 += "=";
		else if (remainder != 0) throw new FormatException("Base64 长度不正确");
		return Convert.FromBase64String(base64);
	}

	private void OnCommand(string command, string args) {
		var parts = (args ?? string.Empty).Trim()
			.Split(' ', StringSplitOptions.RemoveEmptyEntries);

		var verb = parts.FirstOrDefault() ?? string.Empty;
		var rest = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : string.Empty;

		if (verb.Equals("config", StringComparison.OrdinalIgnoreCase) ||
		    verb.Equals("cfg", StringComparison.OrdinalIgnoreCase)) {
			RequestOpenOverlayConfig();
			return;
		}

		if (verb.Equals("hidewin", StringComparison.OrdinalIgnoreCase) ||
		    verb.Equals("hidewindow", StringComparison.OrdinalIgnoreCase)) {
			if (string.IsNullOrWhiteSpace(rest)) {
				HelpersOm.NotificationError($"用法: {CommandName} hidewin <窗口名>");
				return;
			}

			if (SetImGuiWindowHidden(rest.Trim(), true))
				HelpersOm.NotificationInfo($"已添加隐藏窗口: {rest.Trim()}");
			return;
		}

		if (verb.Equals("showwin", StringComparison.OrdinalIgnoreCase) ||
		    verb.Equals("showwindow", StringComparison.OrdinalIgnoreCase)) {
			if (string.IsNullOrWhiteSpace(rest)) {
				HelpersOm.NotificationError($"用法: {CommandName} showwin <窗口名>");
				return;
			}

			if (SetImGuiWindowHidden(rest.Trim(), false))
				HelpersOm.NotificationInfo($"已移除隐藏窗口: {rest.Trim()}");
			return;
		}

		EnsureOverlay();
		ModuleConfig.DockEnabled = true;

		if (string.IsNullOrWhiteSpace(verb)) {
			ModuleConfig.DockOpen = Overlay != null && !Overlay.IsOpen;
			ApplyOverlayState();
			SaveConfig(ModuleConfig);
			return;
		}

		if (verb.Equals("open", StringComparison.OrdinalIgnoreCase) ||
		    verb.Equals("on", StringComparison.OrdinalIgnoreCase)) {
			ModuleConfig.DockOpen = true;
			ApplyOverlayState();
			SaveConfig(ModuleConfig);
			return;
		}

		if (verb.Equals("close", StringComparison.OrdinalIgnoreCase) ||
		    verb.Equals("off", StringComparison.OrdinalIgnoreCase)) {
			ModuleConfig.DockOpen = false;
			ApplyOverlayState();
			SaveConfig(ModuleConfig);
			return;
		}

		HelpersOm.NotificationError($"用法: {CommandName} [open|close|hidewin|showwin|config]");
	}

	private void EnsureOverlay() {
		if (Overlay != null) return;

		Overlay = new DockOverlayWindow(this);
		windowSystem.AddWindow(Overlay);
	}

	private void EnsureConfigWindow() {
		if (ConfigWindow != null) return;

		ConfigWindow = new PluginDockConfigWindow(this);
		windowSystem.AddWindow(ConfigWindow);
	}

	private void ApplyOverlayState() {
		if (Overlay == null) return;

		Overlay.Flags = BuildOverlayFlags();
		Overlay.IsOpen = ModuleConfig.DockEnabled && ModuleConfig.DockOpen;
	}

	internal ImGuiWindowFlags BuildOverlayFlags() {
		var flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
		if (ModuleConfig.Collapsed || ModuleConfig.NoDecoration)
			flags |= ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize;
		if (ModuleConfig.LockPosition)
			flags |= ImGuiWindowFlags.NoMove;
		return flags;
	}

	private void Draw() {
		if (!initialized)
			return;

		windowSystem.Draw();
		OnUiBuilderDraw();
	}

	private void OpenConfigUi() => ToggleOverlayConfig(true);

	private void OpenMainUi() {
		EnsureOverlay();
		ModuleConfig.DockEnabled = true;
		ModuleConfig.DockOpen = true;
		ApplyOverlayState();
		SaveConfig(ModuleConfig);
	}

	private void ToggleOverlayConfig(bool open) {
		EnsureConfigWindow();
		if (ConfigWindow == null)
			return;

		ConfigWindow.IsOpen = open;
	}

	private static Config? LoadConfig() => DService.PI.GetPluginConfig() as Config;

	private static void SaveConfig(Config config) {
		DService.PI.SavePluginConfig(config);
	}

	private static string ConfigDirectoryPath {
		get {
			var path = DService.PI.GetPluginConfigDirectory();
			if (!Directory.Exists(path))
				Directory.CreateDirectory(path);
			return path;
		}
	}

	private void OnUiBuilderDraw() {
		if (!initialized)
			return;

		try {
			ProcessOverlayConfigRequest();
			ApplyHiddenImGuiWindows();
			ApplyPendingImGuiWindowRestores();
			ApplyPendingImGuiWindowFocusRequests();
			DrawImGuiMetricsWindow();
			DrawIconLibraryWindow();
			if (fileDialogActive)
				fileDialogManager.Draw();
		} catch (Exception ex) {
			var now = DateTime.UtcNow;
			if (now - lastUiDrawExceptionAtUtc > TimeSpan.FromSeconds(3)) {
				lastUiDrawExceptionAtUtc = now;
				DService.Log.Error(ex, "PluginDock OnUiBuilderDraw crashed.");
			}
		}
	}

	private void RequestOpenOverlayConfig() {
		requestOpenOverlayConfig = true;
		requestOpenOverlayConfigDelayFrames = Math.Max(requestOpenOverlayConfigDelayFrames, 1);
	}

	private void ProcessOverlayConfigRequest() {
		if (!requestOpenOverlayConfig)
			return;

		if (requestOpenOverlayConfigDelayFrames > 0) {
			requestOpenOverlayConfigDelayFrames--;
			return;
		}

		requestOpenOverlayConfig = false;
		requestOpenOverlayConfigDelayFrames = 0;

		try {
			ToggleOverlayConfig(true);
		} catch (Exception ex) {
			DService.Log.Error(ex, "Failed to open PluginDock config overlay.");
		}
	}

	private void ApplyHiddenImGuiWindows() {
		var hasHardHidden = ModuleConfig.WindowHiderEnabled && ModuleConfig.HiddenImGuiWindows.Count > 0;
		var hasTransientHidden = transientHiddenImGuiWindows.Count > 0;

		if (!hasHardHidden && !hasTransientHidden)
			return;

		UpdateForcedVisibleImGuiWindowKeys();

		hardHiddenImGuiWindowKeysScratch.Clear();

		if (hasHardHidden) {
			foreach (var name in ModuleConfig.HiddenImGuiWindows) {
				if (IsImGuiWindowForceVisible(name))
					continue;
				var key = GetImGuiWindowNameKeyCached(name);
				if (key.Length > 0)
					hardHiddenImGuiWindowKeysScratch.Add(key);
			}
		}

		if (hasTransientHidden) {
			foreach (var name in transientHiddenImGuiWindows) {
				if (IsImGuiWindowForceVisible(name))
					continue;
				var key = GetImGuiWindowNameKeyCached(name);
				if (key.Length > 0)
					hardHiddenImGuiWindowKeysScratch.Add(key);
			}
		}

		ApplyHardHiddenImGuiWindowsByKeys(hardHiddenImGuiWindowKeysScratch);
	}

	private void UpdateForcedVisibleImGuiWindowKeys() {
		if (forcedVisibleImGuiWindowKeys.Count == 0)
			return;

		var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		try {
			CollectOpenWindowKeys(windowSystem, activeKeys);
			var dailySystem = TryGetDailyRoutinesWindowSystem();
			if (dailySystem != null)
				CollectOpenWindowKeys(dailySystem, activeKeys);
		} catch {
		}

		foreach (var name in toggleOnClickOpenedTargets) {
			var key = NormalizeImGuiWindowNameKey(name);
			if (key.Length > 0)
				activeKeys.Add(key);
		}

		foreach (var key in forcedVisibleImGuiWindowKeys.ToArray()) {
			if (!activeKeys.Contains(key))
				forcedVisibleImGuiWindowKeys.Remove(key);
		}
	}

	private bool IsImGuiWindowForceVisible(string name) {
		var key = NormalizeImGuiWindowNameKey(name);
		if (key.Length == 0)
			return false;
		return forcedVisibleImGuiWindowKeys.Contains(key);
	}

	private void SetImGuiWindowForceVisible(string name, bool visible) {
		var key = NormalizeImGuiWindowNameKey(name);
		if (key.Length == 0)
			return;

		if (visible) {
			forcedVisibleImGuiWindowKeys.Add(key);
			TrySetImGuiWindowHardHiddenByKey(name, false);
		} else
			forcedVisibleImGuiWindowKeys.Remove(key);
	}

	private bool IsImGuiWindowHidden(string name) =>
		ModuleConfig.HiddenImGuiWindows.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));

	private bool IsImGuiWindowHiddenByKey(string windowNameOrId) {
		if (!ModuleConfig.WindowHiderEnabled)
			return false;

		var key = NormalizeImGuiWindowNameKey(windowNameOrId);
		if (key.Length == 0)
			return false;

		return ModuleConfig.HiddenImGuiWindows.Any(x =>
			NormalizeImGuiWindowNameKey(x).Equals(key, StringComparison.OrdinalIgnoreCase));
	}

	private bool IsImGuiWindowInHiddenListByKey(string windowNameOrId) {
		var key = NormalizeImGuiWindowNameKey(windowNameOrId);
		if (key.Length == 0)
			return false;

		return ModuleConfig.HiddenImGuiWindows.Any(x =>
			NormalizeImGuiWindowNameKey(x).Equals(key, StringComparison.OrdinalIgnoreCase));
	}

	private bool IsImGuiWindowTransientHidden(string name) {
		if (string.IsNullOrWhiteSpace(name))
			return false;
		return transientHiddenImGuiWindows.Contains(name.Trim());
	}

	private bool IsImGuiWindowHiddenAny(string name) =>
		!IsImGuiWindowForceVisible(name) && (IsImGuiWindowHiddenByKey(name) || IsImGuiWindowTransientHidden(name));

	private void SetImGuiWindowTransientHidden(string name, bool hidden) {
		if (string.IsNullOrWhiteSpace(name))
			return;

		var trimmed = name.Trim();
		if (trimmed.Length == 0)
			return;

		if (hidden && IsSelfRelatedImGuiWindow(trimmed)) {
			HelpersOm.NotificationError("不能隐藏本插件自身或相关窗口");
			return;
		}

		if (hidden) {
			if (transientHiddenImGuiWindows.Add(trimmed)) {
				if (TryCaptureImGuiWindowRestoreState(trimmed, out var restoreState))
					transientHiddenImGuiWindowRestoreStates[trimmed] = restoreState;
			}

			return;
		}

		if (!transientHiddenImGuiWindows.Remove(trimmed))
			return;

		var hasRestoreState = transientHiddenImGuiWindowRestoreStates.TryGetValue(trimmed, out var restoreStateToUse);
		transientHiddenImGuiWindowRestoreStates.Remove(trimmed);

		TrySetImGuiWindowHardHiddenByKey(trimmed, false);

		if (!hasRestoreState || restoreStateToUse == null)
			QueueImGuiWindowRestore(trimmed, BuildFallbackVisibleRestoreState());
		else if (IsLikelyHiddenRestoreState(restoreStateToUse))
			QueueImGuiWindowRestore(trimmed, BuildVisibleRestoreStateFrom(restoreStateToUse));
		else
			QueueImGuiWindowRestore(trimmed, restoreStateToUse);

		QueueImGuiWindowFocus(trimmed, 45);
	}

	private bool SetImGuiWindowHidden(string name, bool hidden, bool save = true) {
		if (string.IsNullOrWhiteSpace(name))
			return false;

		var trimmed = name.Trim();
		if (hidden && IsSelfRelatedImGuiWindow(trimmed)) {
			HelpersOm.NotificationError("不能隐藏本插件自身或相关窗口");
			return false;
		}

		var existingIndex = ModuleConfig.HiddenImGuiWindows.FindIndex(x => x.Equals(trimmed, StringComparison.OrdinalIgnoreCase));

		if (hidden) {
			ModuleConfig.WindowHiderEnabled = true;
			if (existingIndex == -1) {
				if (TryCaptureImGuiWindowRestoreState(trimmed, out var restoreState))
					ModuleConfig.HiddenImGuiWindowRestoreStates[trimmed] = restoreState;

				ModuleConfig.HiddenImGuiWindows.Add(trimmed);
			}

			TrySetImGuiWindowHardHiddenByKey(trimmed, true);
		} else {
			if (existingIndex != -1)
				ModuleConfig.HiddenImGuiWindows.RemoveAt(existingIndex);

			TrySetImGuiWindowHardHiddenByKey(trimmed, false);

			if (ModuleConfig.HiddenImGuiWindowRestoreStates.TryGetValue(trimmed, out var restoreState)) {
				if (!TryRestoreImGuiWindowIni(trimmed, restoreState, out var restoreError) &&
				    !string.IsNullOrWhiteSpace(restoreError)) {
					imGuiWindowRestoreIniError = restoreError;
					imGuiWindowRestoreIniErrorWindow = trimmed;
				} else {
					imGuiWindowRestoreIniError = string.Empty;
					imGuiWindowRestoreIniErrorWindow = string.Empty;
				}

				QueueImGuiWindowRestore(trimmed, restoreState);
				ModuleConfig.HiddenImGuiWindowRestoreStates.Remove(trimmed);
			}
		}

		SetImGuiWindowForceVisible(trimmed, false);

		if (save)
			SaveConfig(ModuleConfig);

		return true;
	}

	private void SetImGuiWindowHiddenByKey(string windowNameOrId, bool hidden, bool save = true) {
		if (string.IsNullOrWhiteSpace(windowNameOrId))
			return;

		var trimmed = windowNameOrId.Trim();
		if (hidden && IsSelfRelatedImGuiWindow(trimmed)) {
			HelpersOm.NotificationError("不能隐藏本插件自身或相关窗口");
			return;
		}

		var key = NormalizeImGuiWindowNameKey(trimmed);
		if (key.Length == 0)
			return;

		if (hidden) {
			if (IsImGuiWindowInHiddenListByKey(trimmed)) {
				ModuleConfig.WindowHiderEnabled = true;
				SetImGuiWindowForceVisible(trimmed, false);
				RemoveTransientHiddenByKey(trimmed);
				TrySetImGuiWindowHardHiddenByKey(trimmed, true);
				if (save)
					SaveConfig(ModuleConfig);
				return;
			}

			SetImGuiWindowHidden(trimmed, true, save);
			RemoveTransientHiddenByKey(trimmed);
			return;
		}

		var hiddenNames = ModuleConfig.HiddenImGuiWindows
			.Where(x => NormalizeImGuiWindowNameKey(x).Equals(key, StringComparison.OrdinalIgnoreCase))
			.ToArray();

		if (hiddenNames.Length == 0) {
			SetImGuiWindowForceVisible(trimmed, false);
			RemoveTransientHiddenByKey(trimmed);
			return;
		}

		SetImGuiWindowHidden(hiddenNames[0], false, false);

		if (hiddenNames.Length > 1) {
			ModuleConfig.HiddenImGuiWindows.RemoveAll(x =>
				NormalizeImGuiWindowNameKey(x).Equals(key, StringComparison.OrdinalIgnoreCase));

			foreach (var name in ModuleConfig.HiddenImGuiWindowRestoreStates.Keys.ToArray()) {
				if (NormalizeImGuiWindowNameKey(name).Equals(key, StringComparison.OrdinalIgnoreCase))
					ModuleConfig.HiddenImGuiWindowRestoreStates.Remove(name);
			}
		}

		SetImGuiWindowForceVisible(trimmed, false);
		RemoveTransientHiddenByKey(trimmed);
		TrySetImGuiWindowHardHiddenByKey(trimmed, false);

		if (save)
			SaveConfig(ModuleConfig);
	}

	private void AddDockItemForImGuiWindow(string windowName) {
		if (string.IsNullOrWhiteSpace(windowName))
			return;

		var trimmed = windowName.Trim();
		if (IsSelfRelatedImGuiWindow(trimmed)) {
			HelpersOm.NotificationError("不能收纳本插件自身或相关窗口");
			return;
		}

		var exists = ModuleConfig.Items.Any(x =>
			x.Kind == DockItemKind.ImGuiWindow &&
			x.InternalName.Equals(trimmed, StringComparison.OrdinalIgnoreCase));

		if (exists) {
			HelpersOm.NotificationInfo($"已在收纳列表中: {trimmed}");
			return;
		}

		ModuleConfig.Items.Add(new DockItem {
			Kind = DockItemKind.ImGuiWindow,
			InternalName = trimmed,
			CustomDisplayName = string.Empty,
			Hidden = false,
			CustomIconPath = string.Empty,
			CustomGameIconId = 0,
			CustomSeIconChar = string.Empty,
			LinkedPluginInternalName = GuessLinkedPluginInternalNameForWindow(trimmed),
			PreferConfigUiOnClick = false,
			ClickCommand = string.Empty,
			ToggleOnClick = trimmed.EndsWith(SmallIconSuffix, StringComparison.OrdinalIgnoreCase),
			ToggleTargetWindowName = string.Empty
		});

		SaveConfig(ModuleConfig);
		HelpersOm.NotificationInfo($"已收纳窗口: {trimmed}");
	}

	private void AddDockItemForCommand() {
		var displayNumber = ModuleConfig.Items.Count(x => x.Kind == DockItemKind.Command) + 1;
		var displayName = $"命令{displayNumber}";

		ModuleConfig.Items.Add(new DockItem {
			Kind = DockItemKind.Command,
			InternalName = $"Command-{Guid.NewGuid():N}",
			CustomDisplayName = displayName,
			Hidden = false,
			CustomIconPath = string.Empty,
			CustomGameIconId = 0,
			CustomSeIconChar = string.Empty,
			LinkedPluginInternalName = string.Empty,
			PreferConfigUiOnClick = false,
			ClickCommand = string.Empty,
			ToggleOnClick = false,
			ToggleTargetWindowName = string.Empty
		});

		pinnedEditorSelectedIndex = ModuleConfig.Items.Count - 1;
		SaveConfig(ModuleConfig);
		HelpersOm.NotificationInfo($"已添加命令项: {displayName}");
	}

	private string? GuessLinkedPluginInternalNameForWindow(string windowName) {
		if (string.IsNullOrWhiteSpace(windowName))
			return null;

		var plugins = GetLoadedPluginsCached();
		if (plugins.Count == 0)
			return null;

		static int ScoreMatch(string fullName, IExposedPlugin plugin) {
			var internalName = plugin.InternalName ?? string.Empty;
			var pluginName = plugin.Name ?? string.Empty;

			if (internalName.Length > 0 &&
			    fullName.Contains($"###{internalName}", StringComparison.OrdinalIgnoreCase))
				return 1000 + internalName.Length;

			if (internalName.Length > 0 &&
			    fullName.EndsWith(internalName, StringComparison.OrdinalIgnoreCase))
				return 900 + internalName.Length;

			if (internalName.Length > 0 &&
			    fullName.Contains(internalName, StringComparison.OrdinalIgnoreCase))
				return 800 + internalName.Length;

			if (pluginName.Length > 0 &&
			    fullName.Contains(pluginName, StringComparison.OrdinalIgnoreCase))
				return 700 + pluginName.Length;

			return 0;
		}

		var bestScore = 0;
		IExposedPlugin? best = null;
		var tie = false;

		foreach (var plugin in plugins) {
			var score = ScoreMatch(windowName, plugin);
			if (score <= 0)
				continue;

			if (score > bestScore) {
				bestScore = score;
				best = plugin;
				tie = false;
				continue;
			}

			if (score == bestScore)
				tie = true;
		}

		if (best == null || tie)
			return null;

		return best.InternalName;
	}

	private static bool TryRestoreImGuiWindowIni(string windowName, ImGuiWindowRestoreState restoreState, out string error) {
		error = string.Empty;

		if (!TryGetImGuiIniText(out var iniText, out error))
			return false;

		var sectionName = windowName;
		if (TryResolveImGuiWindowIniSectionNameByKey(windowName, iniText, out var resolvedName))
			sectionName = resolvedName;

		if (!TryUpsertImGuiWindowIniSection(sectionName, iniText, restoreState, out var updatedIniText))
			return false;

		return TryLoadImGuiIniText(updatedIniText, out error);
	}

	private static bool TryResolveImGuiWindowIniSectionNameByKey(string windowNameOrId, string iniText, out string sectionName) {
		sectionName = string.Empty;

		if (string.IsNullOrWhiteSpace(windowNameOrId) || string.IsNullOrWhiteSpace(iniText))
			return false;

		var key = NormalizeImGuiWindowNameKey(windowNameOrId);
		if (key.Length == 0)
			return false;

		using var reader = new StringReader(iniText);
		while (reader.ReadLine() is { } line) {
			if (!line.StartsWith("[Window][", StringComparison.Ordinal))
				continue;

			var start = "[Window][".Length;
			var end = line.IndexOf(']', start);
			if (end <= start)
				continue;

			var name = line.Substring(start, end - start).Trim();
			if (name.Length == 0)
				continue;

			if (NormalizeImGuiWindowNameKey(name).Equals(key, StringComparison.OrdinalIgnoreCase)) {
				sectionName = name;
				return true;
			}
		}

		return false;
	}

	private static bool TryLoadImGuiIniText(string iniText, out string error) {
		error = string.Empty;

		try {
			var imguiType = typeof(ImGui);

			var loadFromMemory = imguiType.GetMethod(
				"LoadIniSettingsFromMemory",
				BindingFlags.Public | BindingFlags.Static,
				null,
				new[] { typeof(string) },
				null);

			if (loadFromMemory != null) {
				loadFromMemory.Invoke(null, new object?[] { iniText });
				return true;
			}

			try {
				var u8Text = new ImU8String(iniText);
				try {
					ImGui.LoadIniSettingsFromMemory(u8Text);
					return true;
				} finally {
					try {
						u8Text.Recycle();
					} catch {
					}
				}
			} catch (MissingMethodException) {
			}

			var loadFromDisk = imguiType.GetMethod(
				"LoadIniSettingsFromDisk",
				BindingFlags.Public | BindingFlags.Static,
				null,
				new[] { typeof(string) },
				null);

			if (loadFromDisk != null) {
				var tempPath = Path.Combine(Path.GetTempPath(), $"plugindock_imgui_{Guid.NewGuid():N}.ini");
				try {
					File.WriteAllText(tempPath, iniText);
					loadFromDisk.Invoke(null, new object?[] { tempPath });
					return true;
				} finally {
					try {
						if (File.Exists(tempPath))
							File.Delete(tempPath);
					} catch {
					}
				}
			}

			try {
				var tempPath = Path.Combine(Path.GetTempPath(), $"plugindock_imgui_{Guid.NewGuid():N}.ini");
				try {
					File.WriteAllText(tempPath, iniText);
					var u8Path = new ImU8String(tempPath);
					try {
						ImGui.LoadIniSettingsFromDisk(u8Path);
						return true;
					} finally {
						try {
							u8Path.Recycle();
						} catch {
						}
					}
				} finally {
					try {
						if (File.Exists(tempPath))
							File.Delete(tempPath);
					} catch {
					}
				}
			} catch (MissingMethodException) {
			}

			error = "当前 Dalamud ImGui 绑定不支持 LoadIniSettingsFromMemory/LoadIniSettingsFromDisk";
			return false;
		} catch (Exception ex) {
			error = ex.InnerException?.Message ?? ex.Message;
			return false;
		}
	}

	private static bool TryUpsertImGuiWindowIniSection(
		string windowName,
		string iniText,
		ImGuiWindowRestoreState restoreState,
		out string updatedIniText) {
		updatedIniText = iniText;

		if (string.IsNullOrWhiteSpace(windowName) || string.IsNullOrWhiteSpace(iniText))
			return false;

		var normalizedName = windowName.Trim();
		if (string.IsNullOrWhiteSpace(normalizedName))
			return false;

		var writer = new StringBuilder(iniText.Length + 256);
		using var reader = new StringReader(iniText);

		var inTarget = false;
		var targetFound = false;
		var wrotePos = false;
		var wroteSize = false;
		var wroteCollapsed = false;

		void WriteMissingFieldsIfNeeded() {
			if (restoreState.HasPosition && !wrotePos)
				writer.AppendLine($"Pos={FormatIniVector2(restoreState.Position)}");

			if (restoreState.HasSize && !wroteSize)
				writer.AppendLine($"Size={FormatIniVector2(restoreState.Size)}");

			if (restoreState.HasCollapsed && !wroteCollapsed)
				writer.AppendLine($"Collapsed={(restoreState.Collapsed ? 1 : 0)}");
		}

		while (reader.ReadLine() is { } line) {
			if (line.StartsWith("[", StringComparison.Ordinal)) {
				if (inTarget) {
					WriteMissingFieldsIfNeeded();
					inTarget = false;
				}

				if (line.StartsWith("[Window][", StringComparison.Ordinal)) {
					var start = "[Window][".Length;
					var end = line.IndexOf(']', start);
					if (end > start) {
						var name = line.Substring(start, end - start);
						if (name.Equals(normalizedName, StringComparison.Ordinal)) {
							inTarget = true;
							targetFound = true;
							wrotePos = wroteSize = wroteCollapsed = false;
						}
					}
				}

				writer.AppendLine(line);
				continue;
			}

			if (inTarget) {
				if (restoreState.HasPosition && line.StartsWith("Pos=", StringComparison.Ordinal)) {
					writer.AppendLine($"Pos={FormatIniVector2(restoreState.Position)}");
					wrotePos = true;
					continue;
				}

				if (restoreState.HasSize && line.StartsWith("Size=", StringComparison.Ordinal)) {
					writer.AppendLine($"Size={FormatIniVector2(restoreState.Size)}");
					wroteSize = true;
					continue;
				}

				if (restoreState.HasCollapsed && line.StartsWith("Collapsed=", StringComparison.Ordinal)) {
					writer.AppendLine($"Collapsed={(restoreState.Collapsed ? 1 : 0)}");
					wroteCollapsed = true;
					continue;
				}
			}

			writer.AppendLine(line);
		}

		if (inTarget)
			WriteMissingFieldsIfNeeded();

		if (!targetFound) {
			writer.AppendLine($"[Window][{normalizedName}]");
			if (restoreState.HasPosition)
				writer.AppendLine($"Pos={FormatIniVector2(restoreState.Position)}");
			if (restoreState.HasSize)
				writer.AppendLine($"Size={FormatIniVector2(restoreState.Size)}");
			if (restoreState.HasCollapsed)
				writer.AppendLine($"Collapsed={(restoreState.Collapsed ? 1 : 0)}");
			writer.AppendLine();
		}

		updatedIniText = writer.ToString();
		return true;
	}

	private static string FormatIniVector2(Vector2 value) =>
		$"{value.X.ToString(CultureInfo.InvariantCulture)},{value.Y.ToString(CultureInfo.InvariantCulture)}";

	private void ApplyPendingImGuiWindowRestores() {
		if (pendingImGuiWindowRestores.Count == 0)
			return;

		List<string>? toRemove = null;

		foreach (var (name, pending) in pendingImGuiWindowRestores) {
			if (IsImGuiWindowHiddenAny(name))
				continue;

			if (pending.RemainingFrames <= 0) {
				(toRemove ??= []).Add(name);
				continue;
			}

			pending.RemainingFrames--;

			try {
				if (pending.RestoreState.HasPosition)
					ImGui.SetWindowPos(name, pending.RestoreState.Position, ImGuiCond.Always);

				if (pending.RestoreState.HasSize)
					ImGui.SetWindowSize(name, pending.RestoreState.Size, ImGuiCond.Always);

				if (pending.RestoreState.HasCollapsed)
					ImGui.SetWindowCollapsed(name, pending.RestoreState.Collapsed, ImGuiCond.Always);
			} catch {
			}
		}

		if (toRemove == null)
			return;

		foreach (var name in toRemove)
			pendingImGuiWindowRestores.Remove(name);
	}

	private void QueueImGuiWindowRestore(string name, ImGuiWindowRestoreState restoreState) {
		pendingImGuiWindowRestores[name] = new PendingImGuiWindowRestore {
			RestoreState = restoreState,
			RemainingFrames = 30
		};
	}

	private void QueueImGuiWindowFocus(string name, int frames = 30) {
		if (string.IsNullOrWhiteSpace(name))
			return;

		var trimmed = name.Trim();
		if (trimmed.Length == 0)
			return;

		var current = pendingImGuiWindowFocusRequests.GetValueOrDefault(trimmed);
		pendingImGuiWindowFocusRequests[trimmed] = Math.Max(current, Math.Max(1, frames));
	}

	private void ApplyPendingImGuiWindowFocusRequests() {
		if (pendingImGuiWindowFocusRequests.Count == 0)
			return;

		List<string>? toRemove = null;

		foreach (var (name, remainingFrames) in pendingImGuiWindowFocusRequests.ToArray()) {
			if (IsImGuiWindowHiddenAny(name))
				continue;

			if (remainingFrames <= 0) {
				(toRemove ??= []).Add(name);
				continue;
			}

			pendingImGuiWindowFocusRequests[name] = remainingFrames - 1;

			try {
				ImGui.SetWindowCollapsed(name, false, ImGuiCond.Always);
				ImGui.SetWindowFocus(name);
			} catch {
			}
		}

		if (toRemove == null)
			return;

		foreach (var name in toRemove)
			pendingImGuiWindowFocusRequests.Remove(name);
	}

	private void ShowAndFocusImGuiWindow(string windowName) {
		if (string.IsNullOrWhiteSpace(windowName))
			return;

		var trimmed = windowName.Trim();
		if (trimmed.Length == 0)
			return;

		if (IsImGuiWindowTransientHidden(trimmed))
			SetImGuiWindowTransientHidden(trimmed, false);

		var wasHidden = IsImGuiWindowHidden(trimmed);

		ImGuiWindowRestoreState? restoreState = null;
		if (wasHidden && ModuleConfig.HiddenImGuiWindowRestoreStates.TryGetValue(trimmed, out var stored))
			restoreState = stored;

		if (wasHidden)
			SetImGuiWindowHidden(trimmed, false);

		if (wasHidden) {
			if (restoreState == null)
				QueueImGuiWindowRestore(trimmed, BuildFallbackVisibleRestoreState());
			else if (IsLikelyHiddenRestoreState(restoreState))
				QueueImGuiWindowRestore(trimmed, BuildVisibleRestoreStateFrom(restoreState));
		}

		QueueImGuiWindowFocus(trimmed, 45);
	}

	private bool TryGetHiddenImGuiWindowRestoreStateByKey(string windowNameOrId, out ImGuiWindowRestoreState restoreState) {
		restoreState = null!;

		var key = NormalizeImGuiWindowNameKey(windowNameOrId);
		if (key.Length == 0)
			return false;

		foreach (var (name, state) in ModuleConfig.HiddenImGuiWindowRestoreStates) {
			if (state == null)
				continue;

			if (NormalizeImGuiWindowNameKey(name).Equals(key, StringComparison.OrdinalIgnoreCase)) {
				restoreState = state;
				return true;
			}
		}

		return false;
	}

	private void ShowAndFocusImGuiWindowTemporary(string windowNameOrId) {
		if (string.IsNullOrWhiteSpace(windowNameOrId))
			return;

		var trimmed = windowNameOrId.Trim();
		if (trimmed.Length == 0)
			return;

		var wasPermanentlyHidden = IsImGuiWindowHiddenByKey(trimmed);
		if (wasPermanentlyHidden)
			SetImGuiWindowForceVisible(trimmed, true);

		ClearTransientHiddenByKey(trimmed);

		if (wasPermanentlyHidden) {
			if (!TryGetHiddenImGuiWindowRestoreStateByKey(trimmed, out var restoreState) ||
			    restoreState == null) {
				QueueImGuiWindowRestore(trimmed, BuildFallbackVisibleRestoreState());
			} else if (IsLikelyHiddenRestoreState(restoreState)) {
				QueueImGuiWindowRestore(trimmed, BuildVisibleRestoreStateFrom(restoreState));
			} else {
				QueueImGuiWindowRestore(trimmed, restoreState);
			}
		}

		QueueImGuiWindowFocus(trimmed, 45);
	}

	private static bool IsLikelyHiddenRestoreState(ImGuiWindowRestoreState restoreState) {
		if (restoreState.HasSize && (restoreState.Size.X <= 1f || restoreState.Size.Y <= 1f))
			return true;

		if (restoreState.HasPosition) {
			if (MathF.Abs(restoreState.Position.X) > 50_000f || MathF.Abs(restoreState.Position.Y) > 50_000f)
				return true;

			try {
				var displaySize = ImGui.GetIO().DisplaySize;
				if (displaySize.X > 100f && displaySize.Y > 100f) {
					var assumedSize = restoreState.HasSize && restoreState.Size.X > 1f && restoreState.Size.Y > 1f
						? restoreState.Size
						: new Vector2(200f, 150f);

					var x1 = restoreState.Position.X;
					var y1 = restoreState.Position.Y;
					var x2 = x1 + assumedSize.X;
					var y2 = y1 + assumedSize.Y;

					var fullyOffScreen = x2 < 0f || y2 < 0f || x1 > displaySize.X || y1 > displaySize.Y;
					if (fullyOffScreen)
						return true;
				}
			} catch {
			}
		}

		return false;
	}

	private static ImGuiWindowRestoreState BuildFallbackVisibleRestoreState() {
		var pos = new Vector2(100f, 100f);
		var size = new Vector2(420f, 240f);

		try {
			var displaySize = ImGui.GetIO().DisplaySize;
			if (displaySize.X > 500f && displaySize.Y > 400f) {
				pos = new Vector2(displaySize.X * 0.25f, displaySize.Y * 0.25f);
				size = new Vector2(
					Math.Clamp(displaySize.X * 0.35f, 260f, 800f),
					Math.Clamp(displaySize.Y * 0.35f, 180f, 600f));
			}
		} catch {
		}

		return new ImGuiWindowRestoreState {
			HasPosition = true,
			Position = pos,
			HasSize = true,
			Size = size,
			HasCollapsed = true,
			Collapsed = false
		};
	}

	private static ImGuiWindowRestoreState BuildVisibleRestoreStateFrom(ImGuiWindowRestoreState restoreState) {
		var visible = BuildFallbackVisibleRestoreState();

		if (restoreState != null) {
			var usePosition = restoreState.HasPosition &&
			                  MathF.Abs(restoreState.Position.X) <= 50_000f &&
			                  MathF.Abs(restoreState.Position.Y) <= 50_000f;

			if (usePosition) {
				try {
					var displaySize = ImGui.GetIO().DisplaySize;
					if (displaySize.X > 100f && displaySize.Y > 100f) {
						var assumedSize = restoreState.HasSize && restoreState.Size.X > 1f && restoreState.Size.Y > 1f
							? restoreState.Size
							: visible.Size;

						var x1 = restoreState.Position.X;
						var y1 = restoreState.Position.Y;
						var x2 = x1 + assumedSize.X;
						var y2 = y1 + assumedSize.Y;

						var fullyOffScreen = x2 < 0f || y2 < 0f || x1 > displaySize.X || y1 > displaySize.Y;
						if (fullyOffScreen)
							usePosition = false;
					}
				} catch {
				}
			}

			if (usePosition) {
				visible.HasPosition = true;
				visible.Position = restoreState.Position;
			}

			if (restoreState.HasSize &&
			    restoreState.Size.X > 1f &&
			    restoreState.Size.Y > 1f) {
				visible.HasSize = true;
				visible.Size = restoreState.Size;
			}
		}

		visible.HasCollapsed = true;
		visible.Collapsed = false;

		return visible;
	}

	private static bool TryCaptureImGuiWindowRestoreState(string windowName, out ImGuiWindowRestoreState restoreState) {
		restoreState = new ImGuiWindowRestoreState();

		if (!TryGetImGuiIniText(out var iniText, out _))
			return false;

		return TryParseImGuiWindowRestoreStateFromIni(windowName, iniText, out restoreState);
	}

	private static bool TryParseImGuiWindowRestoreStateFromIni(string windowName, string iniText, out ImGuiWindowRestoreState restoreState) {
		restoreState = new ImGuiWindowRestoreState();

		using var reader = new StringReader(iniText);
		var found = false;
		var inSection = false;
		var targetKey = NormalizeImGuiWindowNameKey(windowName);

		while (reader.ReadLine() is { } line) {
			if (line.StartsWith("[", StringComparison.Ordinal)) {
				inSection = false;

				if (!line.StartsWith("[Window][", StringComparison.Ordinal))
					continue;

				var start = "[Window][".Length;
				var end = line.IndexOf(']', start);
				if (end <= start)
					continue;

				var name = line.Substring(start, end - start);
				inSection = NormalizeImGuiWindowNameKey(name).Equals(targetKey, StringComparison.OrdinalIgnoreCase);
				found |= inSection;
				continue;
			}

			if (!inSection)
				continue;

			if (line.StartsWith("Pos=", StringComparison.Ordinal)) {
				if (TryParseIniVector2(line.Substring("Pos=".Length), out var pos)) {
					restoreState.HasPosition = true;
					restoreState.Position = pos;
				}

				continue;
			}

			if (line.StartsWith("Size=", StringComparison.Ordinal)) {
				if (TryParseIniVector2(line.Substring("Size=".Length), out var size)) {
					restoreState.HasSize = true;
					restoreState.Size = size;
				}

				continue;
			}

			if (line.StartsWith("Collapsed=", StringComparison.Ordinal)) {
				var value = line.Substring("Collapsed=".Length).Trim();
				if (int.TryParse(value, out var collapsedInt)) {
					restoreState.HasCollapsed = true;
					restoreState.Collapsed = collapsedInt != 0;
				}
			}
		}

		return found && (restoreState.HasPosition || restoreState.HasSize || restoreState.HasCollapsed);
	}

	private static bool TryParseIniVector2(string raw, out Vector2 value) {
		value = Vector2.Zero;
		if (string.IsNullOrWhiteSpace(raw))
			return false;

		var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 2)
			return false;

		if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
			return false;

		if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
			return false;

		value = new Vector2(x, y);
		return true;
	}

	internal void DrawOverlayUi() => DrawDockContents();

	internal void OnOverlayClosed() {
		ModuleConfig.DockOpen = false;
		SaveConfig(ModuleConfig);
	}

	private void EnsureLoadedPluginsCache() {
		var now = Environment.TickCount64;
		if (now < loadedPluginsCacheNextRefreshAtTick)
			return;

		loadedPluginsCacheNextRefreshAtTick = now + 1000;

		try {
			var plugins = GetLoadedPlugins();
			if (!string.IsNullOrWhiteSpace(selfPluginInternalName)) {
				plugins = plugins
					.Where(p => !p.InternalName.Equals(selfPluginInternalName, StringComparison.OrdinalIgnoreCase))
					.ToList();
			}
			loadedPluginsCache = plugins;
			loadedPluginByInternalNameCache = plugins.ToDictionary(p => p.InternalName, StringComparer.OrdinalIgnoreCase);
		} catch {
			loadedPluginsCache = [];
			loadedPluginByInternalNameCache = new Dictionary<string, IExposedPlugin>(StringComparer.OrdinalIgnoreCase);
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
		DService.PI.InstalledPlugins
			.Where(p => p.IsLoaded)
			.OrderBy(p => p.Name)
			.ToList();

	private static List<IExposedPlugin> GetUiPlugins() =>
		GetLoadedPlugins()
			.Where(p => p.HasMainUi || p.HasConfigUi)
			.ToList();

	private static void DrawDockLabelWithHelp(string label, string help) {
		ImGui.AlignTextToFramePadding();
		ImGui.TextUnformatted(label);
		ImGui.SameLine();
		if (!string.IsNullOrWhiteSpace(help)) {
			ImGuiOm.HelpMarker(help);
			ImGui.SameLine();
		}
	}

	private static float CalcSmallButtonWidth(string label) =>
		ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;

	private static int GetValidUtf8Length(byte[] bytes, int maxLength) {
		var length = Math.Min(bytes.Length, maxLength);

		// 确保不会截断在 UTF-8 多字节字符的中间
		if (length > 0 && length < bytes.Length) {
			// 从 length-1 向前扫描，跳过所有续字节 (10xxxxxx)
			var pos = length - 1;
			while (pos > 0 && (bytes[pos] & 0xC0) == 0x80)
				pos--;

			// 现在 pos 指向一个非续字节（可能是 ASCII 或多字节起始）
			var b = bytes[pos];
			int charLen;
			if ((b & 0x80) == 0) charLen = 1; // ASCII: 0xxxxxxx
			else if ((b & 0xE0) == 0xC0) charLen = 2; // 2字节: 110xxxxx
			else if ((b & 0xF0) == 0xE0) charLen = 3; // 3字节: 1110xxxx
			else if ((b & 0xF8) == 0xF0) charLen = 4; // 4字节: 11110xxx
			else charLen = 1; // 无效字节，当作单字节

			// 检查从 pos 开始的字符是否完整包含在 length 内
			if (pos + charLen > length)
				length = pos;
		}

		return length;
	}

	private static string DecodeUtf8Buffer(byte[] buffer) {
		var length = Array.IndexOf(buffer, (byte)0);
		if (length < 0)
			length = buffer.Length;

		return length <= 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, length);
	}

	private static bool InputTextUtf8(string id, ref string value, int maxLength, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None) {
		if (maxLength <= 0)
			return false;

		var buffer = new byte[maxLength];
		var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
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

	private static bool InputTextMultilineUtf8(string id, ref string value, int maxLength, Vector2 size, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None) {
		if (maxLength <= 0)
			return false;

		var buffer = new byte[maxLength];
		var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
		var length = GetValidUtf8Length(bytes, maxLength - 1);
		Array.Copy(bytes, buffer, length);

		var changed = ImGui.InputTextMultiline(id, buffer, size, flags);
		if (!changed)
			return false;

		var newValue = DecodeUtf8Buffer(buffer);
		if (newValue == value)
			return false;

		value = newValue;
		return true;
	}

	private static bool DrawDockTextInputRow(string label, string help, string id, ref string value, int maxLength) {
		DrawDockLabelWithHelp(label, help);
		ImGui.SetNextItemWidth(-1f);
		return InputTextUtf8(id, ref value, maxLength);
	}

	private static bool DrawDockTextInputWithPicker(
		string label,
		string help,
		string inputId,
		ref string value,
		int maxLength,
		string pickLabel,
		string pickButtonId,
		Action onPick) {
		DrawDockLabelWithHelp(label, help);
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
		string help,
		string inputId,
		ref int value,
		string pickLabel,
		string pickButtonId,
		Action onPick) {
		DrawDockLabelWithHelp(label, help);
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
				SaveConfig(ModuleConfig);
			},
			1,
			startPath);
	}

	private string? ResolveDockHeaderIconPickerStartPath() {
		var current = ModuleConfig.DockHeaderIconPath?.Trim() ?? string.Empty;
		if (current.Length == 0)
			return null;

		if (Directory.Exists(current))
			return current;

		if (!Path.IsPathRooted(current))
			return null;

		var dir = Path.GetDirectoryName(current);
		return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : null;
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
				? string.IsNullOrWhiteSpace(item.CustomDisplayName)
					? "命令"
					: item.CustomDisplayName!.Trim()
				: string.IsNullOrWhiteSpace(item.CustomDisplayName)
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
				SaveConfig(ModuleConfig);
				ImGui.PopID();
				return;
			}

			ImGui.SameLine();
			var hidden = item.Hidden;
			if (ImGui.Checkbox("隐藏", ref hidden)) {
				item.Hidden = hidden;
				SaveConfig(ModuleConfig);
			}

			ImGui.SameLine();
			var kindText = item.Kind switch {
				DockItemKind.Plugin => "[插件]",
				DockItemKind.ImGuiWindow => "[窗口]",
				DockItemKind.Command => "[命令]",
				_ => $"[{item.Kind}]"
			};
			var kindColor = item.Kind switch {
				DockItemKind.ImGuiWindow => DockItemKindColorWindow,
				DockItemKind.Command => DockItemKindColorCommand,
				_ => DockItemKindColorPlugin
			};
			ImGui.TextColored(kindColor, kindText);
			ImGui.SameLine();
			ImGui.TextUnformatted(displayName);

			if (ImGui.IsItemHovered()) {
				if (item.Kind == DockItemKind.Command) {
					var cmd = (item.ClickCommand ?? string.Empty).Trim();
					ImGui.BeginTooltip();
					ImGui.TextUnformatted(cmd.Length == 0 ? "未设置命令" : cmd);
					ImGui.EndTooltip();
				} else if (!string.IsNullOrWhiteSpace(item.CustomDisplayName) &&
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
				var customDisplayName = item.CustomDisplayName ?? string.Empty;
				if (DrawDockTextInputRow("显示名称", "用于该条目的显示别名（Tooltip/无图标文字按钮优先使用）；设置后 Tooltip 会简化，仅显示左键与状态；不影响实际插件/窗口名。留空=使用原名。", "##dock_custom_name", ref customDisplayName, 128)) {
					item.CustomDisplayName = customDisplayName.Trim();
					SaveConfig(ModuleConfig);
				}

				if (item.Kind == DockItemKind.ImGuiWindow) {
					var winName = item.InternalName?.Trim() ?? string.Empty;

					var linkName = item.LinkedPluginInternalName ?? string.Empty;
					var linkDisplay = string.IsNullOrWhiteSpace(linkName)
						? "（无）"
						: pluginByInternalName.TryGetValue(linkName, out var linked)
							? linked.Name
							: linkName;

					if (ImGui.BeginCombo("关联插件", linkDisplay)) {
						var noneSelected = string.IsNullOrWhiteSpace(linkName);
						if (ImGui.Selectable("（无）", noneSelected)) {
							item.LinkedPluginInternalName = string.Empty;
							SaveConfig(ModuleConfig);
						}

						foreach (var plugin in plugins) {
							var selected = plugin.InternalName.Equals(linkName, StringComparison.OrdinalIgnoreCase);
							if (ImGui.Selectable(plugin.Name, selected)) {
								item.LinkedPluginInternalName = plugin.InternalName;
								SaveConfig(ModuleConfig);
							}

							if (selected)
								ImGui.SetItemDefaultFocus();
						}

						ImGui.EndCombo();
					}

					var preferConfig = item.PreferConfigUiOnClick;
					if (ImGui.Checkbox("点击优先打开设置界面", ref preferConfig)) {
						item.PreferConfigUiOnClick = preferConfig;
						SaveConfig(ModuleConfig);
					}

					var toggleOnClick = item.ToggleOnClick;
					if (ImGui.Checkbox("使用目标窗口（高级）", ref toggleOnClick)) {
						item.ToggleOnClick = toggleOnClick;
						SaveConfig(ModuleConfig);
					}

					ImGui.SameLine();
					ImGuiOm.HelpMarker("左键默认切换当前窗口的显示/隐藏（写入隐藏列表）。勾选后：改为切换“目标窗口”（可配置/自动）。");

					if (item.ToggleOnClick) {
						var toggleTarget = item.ToggleTargetWindowName ?? string.Empty;
						if (InputTextUtf8("切换目标窗口名（留空=当前窗口）", ref toggleTarget, 260)) {
							item.ToggleTargetWindowName = toggleTarget.Trim();
							SaveConfig(ModuleConfig);
						}
					}

					if (!string.IsNullOrWhiteSpace(winName)) {
						var hideWindow = IsImGuiWindowInHiddenListByKey(winName);
						if (ImGui.Checkbox("隐藏该窗口", ref hideWindow))
							SetImGuiWindowHiddenByKey(winName, hideWindow);

						ImGui.SameLine();
						if (ImGui.SmallButton("显示/聚焦")) {
							var focusKey = NormalizeImGuiWindowNameKey(winName);
							var focusTarget = focusKey.Length > 0 ? focusKey : winName;

							try {
								if (TryGetWindowManagerWindow(winName, out var window) ||
								    focusKey.Length > 0 && TryGetWindowManagerWindow(focusKey, out window)) {
									window.IsOpen = true;
								}
							} catch {
							}

							ShowAndFocusImGuiWindowTemporary(focusTarget);
							SetImGuiWindowHiddenByKey(winName, false);
						}
					}
				}

				var clickCommand = item.ClickCommand ?? string.Empty;
				if (DrawDockTextInputRow("点击命令", "左键点击该图标时先执行命令（例如 /xxx）。也支持直接粘贴代码片段：DService.Command.ProcessCommand(\"/xxx\"); 插件会执行对应命令。", "##dock_click_cmd", ref clickCommand, 260)) {
					item.ClickCommand = clickCommand.Trim();
					SaveConfig(ModuleConfig);
				}

				DrawDockItemContextMacrosEditor(item);

				var gameIconId = (int)item.CustomGameIconId;
				if (DrawDockIntInputWithPicker("游戏图标ID", "0=不用。优先级：游戏图标ID > 自定义图标路径 > 游戏图标字符(SeIconChar) > 插件默认图标。", "##dock_game_icon_id", ref gameIconId, "素材库", "##pick_game_icon",
					    () => OpenIconLibraryForDockItem(item, IconLibraryApplyField.GameIconId, IconLibraryMode.GameIcon))) {
					item.CustomGameIconId = (uint)Math.Clamp(gameIconId, 0, int.MaxValue);
					SaveConfig(ModuleConfig);
				}

				var customIconPath = item.CustomIconPath ?? string.Empty;
				if (DrawDockTextInputRow("自定义图标路径", "可留空。支持绝对路径、相对路径（相对于本模块配置目录），或 dalamud:/sys: 前缀（从 Dalamud 资源目录读取）。", "##dock_custom_icon_path", ref customIconPath, 260)) {
					var trimmed = customIconPath.Trim();
					item.CustomIconPath = trimmed.Length == 0 ? null : trimmed;
					if (!string.IsNullOrWhiteSpace(item.InternalName))
						localIconPathCache.Remove(item.InternalName);
					SaveConfig(ModuleConfig);
				}

				var seIconText = item.CustomSeIconChar ?? string.Empty;
				if (DrawDockTextInputWithPicker("游戏图标字符", "仅在未设置游戏图标ID/自定义图标路径时生效；可填 SeIconChar 枚举名(如 HighQuality) 或数值(如 57404 / 0xE03C)。", "##dock_se_icon_char", ref seIconText, 260, "素材库", "##pick_se_icon",
					    () => OpenIconLibraryForDockItem(item, IconLibraryApplyField.SeIconChar, IconLibraryMode.SeIconChar))) {
					item.CustomSeIconChar = seIconText.Trim();
					SaveConfig(ModuleConfig);
				}
			}

			ImGui.PopID();
			ImGui.Separator();
		}
	}

	private void DrawDockItemContextMacrosEditor(DockItem item) {
		DrawDockLabelWithHelp("右键宏命令", "在该图标的右键菜单中显示的宏命令。名称可留空，菜单将显示命令文本。支持 /xxx 或 DService.Command.ProcessCommand(\"/xxx\")。");
		ImGui.NewLine();
		ImGui.Indent();

		item.ContextMenuMacros ??= [];

		for (var i = 0; i < item.ContextMenuMacros.Count; i++) {
			var macro = item.ContextMenuMacros[i];
			ImGui.PushID(i);

			ImGui.TextDisabled($"宏 {i + 1}");

			var name = macro.Name ?? string.Empty;
			if (DrawDockTextInputRow("名称", string.Empty, "##dock_macro_name", ref name, 128)) {
				macro.Name = name.Trim();
				SaveConfig(ModuleConfig);
			}

			var command = macro.Command ?? string.Empty;
			if (DrawDockTextInputRow("命令", string.Empty, "##dock_macro_cmd", ref command, 260)) {
				macro.Command = command.Trim();
				SaveConfig(ModuleConfig);
			}

			if (ImGui.SmallButton("删除宏##dock_macro_remove")) {
				item.ContextMenuMacros.RemoveAt(i);
				SaveConfig(ModuleConfig);
				ImGui.PopID();
				i--;
				continue;
			}

			ImGui.PopID();
			ImGui.Separator();
		}

		if (ImGui.SmallButton("新增宏命令##dock_macro_add")) {
			item.ContextMenuMacros.Add(new DockItemMacro());
			SaveConfig(ModuleConfig);
		}

		ImGui.Unindent();
	}

	private void RemoveDockItemAtIndex(int index) {
		if (index < 0 || index >= ModuleConfig.Items.Count)
			return;

		var item = ModuleConfig.Items[index];

		if (item.Kind == DockItemKind.ImGuiWindow &&
		    !string.IsNullOrWhiteSpace(item.InternalName)) {
			SetImGuiWindowHiddenByKey(item.InternalName, false, false);

			var configuredTarget = item.ToggleTargetWindowName?.Trim() ?? string.Empty;
			if (!string.IsNullOrWhiteSpace(configuredTarget)) {
				SetImGuiWindowHiddenByKey(configuredTarget, false, false);
			} else if (item.ToggleOnClick &&
			           item.InternalName.EndsWith(SmallIconSuffix, StringComparison.OrdinalIgnoreCase) &&
			           TryResolveDailyRoutinesSmallIconTarget(item.InternalName, out var autoTarget)) {
				SetImGuiWindowHiddenByKey(autoTarget.WindowName, false, false);
			}
		}

		ModuleConfig.Items.RemoveAt(index);

		if (ModuleConfig.Items.Count == 0) {
			pinnedEditorSelectedIndex = -1;
		} else {
			if (pinnedEditorSelectedIndex == index)
				pinnedEditorSelectedIndex = Math.Clamp(index, 0, ModuleConfig.Items.Count - 1);
			else if (pinnedEditorSelectedIndex > index)
				pinnedEditorSelectedIndex--;
		}

		SaveConfig(ModuleConfig);
	}

	private void DrawPluginPicker(IReadOnlyList<IExposedPlugin> plugins) {
		var filter = search?.Trim() ?? string.Empty;
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
			if (!string.IsNullOrWhiteSpace(selfPluginInternalName) &&
			    plugin.InternalName.Equals(selfPluginInternalName, StringComparison.OrdinalIgnoreCase))
				continue;

			if (!string.IsNullOrWhiteSpace(filter)) {
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
						CustomDisplayName = string.Empty,
						Hidden = false,
						CustomIconPath = string.Empty,
						CustomGameIconId = 0,
						CustomSeIconChar = string.Empty,
						ClickCommand = string.Empty,
						ToggleOnClick = false,
						ToggleTargetWindowName = string.Empty
					});
					pinnedSet.Add(plugin.InternalName);
				} else {
					ModuleConfig.Items.RemoveAll(x =>
						x.Kind == DockItemKind.Plugin &&
						x.InternalName.Equals(plugin.InternalName, StringComparison.OrdinalIgnoreCase));
					pinnedSet.Remove(plugin.InternalName);
				}

				SaveConfig(ModuleConfig);
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

	private void DrawDockContents() {
		DrawDockLayout();
		HandleAutoHideAfterExpand();
	}

	private void DrawDockLayout() {
		var origin = ImGui.GetCursorPos();
		var iconSize = ModuleConfig.IconSize;
		var spacing = ModuleConfig.IconSpacing;

		var shift = Vector2.Zero;
		var visibleDockItems = visibleDockItemsScratch;
		visibleDockItems.Clear();
		var showPlaceholder = false;

		if (!ModuleConfig.Collapsed) {
			var pluginByInternalName = GetLoadedPluginByInternalNameCached();

			foreach (var item in ModuleConfig.Items) {
				if (item.Hidden) continue;
				if (item.Kind == DockItemKind.ImGuiWindow) {
					if (!string.IsNullOrWhiteSpace(item.InternalName)) {
						if (IsSelfRelatedImGuiWindow(item.InternalName))
							continue;

						var linked = item.LinkedPluginInternalName ?? string.Empty;
						var linkedPlugin = !string.IsNullOrWhiteSpace(linked) && pluginByInternalName.TryGetValue(linked, out var linkedValue)
							? linkedValue
							: null;
						visibleDockItems.Add((item, linkedPlugin));
					}
					continue;
				}

				if (item.Kind == DockItemKind.Command) {
					if (!string.IsNullOrWhiteSpace(item.InternalName))
						visibleDockItems.Add((item, null));
					continue;
				}

				if (!pluginByInternalName.TryGetValue(item.InternalName, out var plugin)) continue;
				visibleDockItems.Add((item, plugin));
			}

			showPlaceholder = visibleDockItems.Count == 0;

			var maxPerLine = ModuleConfig.MaxIconsPerRow <= 0 ? int.MaxValue : ModuleConfig.MaxIconsPerRow;
			var step = iconSize + spacing;

			Vector2 GetItemPos(int index) {
				var indexInLine = index % maxPerLine;
				var lineIndex = index / maxPerLine;

				var wrapY = ModuleConfig.WrapDirection == DockWrapDirection.Up ? -1f : 1f;
				var wrapX = ModuleConfig.WrapDirection == DockWrapDirection.Left ? -1f : 1f;

				return ModuleConfig.ExpandDirection switch {
					DockExpandDirection.Right => new Vector2(step * (indexInLine + 1), wrapY * (lineIndex * step)),
					DockExpandDirection.Left => new Vector2(-step * (indexInLine + 1), wrapY * (lineIndex * step)),
					DockExpandDirection.Down => new Vector2(wrapX * (lineIndex * step), step * (indexInLine + 1)),
					DockExpandDirection.Up => new Vector2(wrapX * (lineIndex * step), -step * (indexInLine + 1)),
					_ => Vector2.Zero
				};
			}

			var min = Vector2.Zero;
			var layoutCount = showPlaceholder ? 1 : visibleDockItems.Count;

			for (var i = 0; i < layoutCount; i++) {
				var pos = GetItemPos(i);
				min = Vector2.Min(min, pos);
			}

			shift = -min;
		}

		var headerLocalPos = origin + shift;
		ApplyDockAnchor(headerLocalPos);

		ImGui.SetCursorPos(headerLocalPos);
		DrawDockHeader();

		if (ModuleConfig.Collapsed)
			return;

		var maxPerDrawLine = ModuleConfig.MaxIconsPerRow <= 0 ? int.MaxValue : ModuleConfig.MaxIconsPerRow;
		var stepDraw = iconSize + spacing;

		Vector2 GetItemPosDraw(int index) {
			var indexInLine = index % maxPerDrawLine;
			var lineIndex = index / maxPerDrawLine;

			var wrapY = ModuleConfig.WrapDirection == DockWrapDirection.Up ? -1f : 1f;
			var wrapX = ModuleConfig.WrapDirection == DockWrapDirection.Left ? -1f : 1f;

			return ModuleConfig.ExpandDirection switch {
				DockExpandDirection.Right => new Vector2(stepDraw * (indexInLine + 1), wrapY * (lineIndex * stepDraw)),
				DockExpandDirection.Left => new Vector2(-stepDraw * (indexInLine + 1), wrapY * (lineIndex * stepDraw)),
				DockExpandDirection.Down => new Vector2(wrapX * (lineIndex * stepDraw), stepDraw * (indexInLine + 1)),
				DockExpandDirection.Up => new Vector2(wrapX * (lineIndex * stepDraw), -stepDraw * (indexInLine + 1)),
				_ => Vector2.Zero
			};
		}

		for (var i = 0; i < visibleDockItems.Count; i++) {
			var pos = GetItemPosDraw(i);
			ImGui.SetCursorPos(origin + shift + pos);

			var (item, plugin) = visibleDockItems[i];
			if (item.Kind == DockItemKind.ImGuiWindow)
				DrawOneImGuiWindowDockItem(item, plugin);
			else if (item.Kind == DockItemKind.Command)
				DrawOneCommandDockItem(item);
			else if (plugin != null)
				DrawOneDockItem(plugin, item);
		}

		if (showPlaceholder) {
			var pos = GetItemPosDraw(0);
			ImGui.SetCursorPos(origin + shift + pos);
			ImGui.TextDisabled("空");
		}
	}

	private void ApplyDockAnchor(Vector2 headerLocalPos) {
		var windowPos = ImGui.GetWindowPos();

		if (!ModuleConfig.DockAnchorInitialized) {
			ModuleConfig.DockAnchorInitialized = true;
			ModuleConfig.DockAnchor = windowPos + headerLocalPos;
			dockAnchorDirty = true;
		}

		var isDragging = UpdateDockDragState();

		var desiredWindowPos = ModuleConfig.DockAnchor - headerLocalPos;
		if (Vector2.DistanceSquared(windowPos, desiredWindowPos) > 0.25f)
			ImGui.SetWindowPos(desiredWindowPos, ImGuiCond.Always);

		if (!isDragging && dockAnchorDirty) {
			dockAnchorDirty = false;
			SaveConfig(ModuleConfig);
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

	private void HandleAutoHideAfterExpand() {
		if (ModuleConfig.Collapsed) {
			dockAutoCollapseAtTime = 0;
			return;
		}

		var seconds = ModuleConfig.AutoHideSeconds;
		if (seconds <= 0f)
			return;

		var now = ImGui.GetTime();
		if (dockAutoCollapseAtTime <= 0)
			dockAutoCollapseAtTime = now + seconds;

		var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByPopup | ImGuiHoveredFlags.RootAndChildWindows);
		if (hovered)
			dockAutoCollapseAtTime = now + seconds;

		if (hovered || now < dockAutoCollapseAtTime)
			return;

		ModuleConfig.Collapsed = true;
		dockAutoCollapseAtTime = 0;
		ApplyOverlayState();
		SaveConfig(ModuleConfig);
	}

	private void DrawDockHeader() {
		ImGui.PushID("dock_header");

		var size = new Vector2(ModuleConfig.IconSize, ModuleConfig.IconSize);
		var clicked = false;

		var headerIcon = GetDockHeaderIcon();
		if (headerIcon.TryGetWrap(out var wrap, out _) && wrap != null) {
			if (ModuleConfig.TransparentButtons)
				PushTransparentIconButtonStyle();

			clicked = ImGui.ImageButton(wrap.Handle, size);

			if (ModuleConfig.TransparentButtons)
				PopTransparentIconButtonStyle();
		} else {
			clicked = DrawDockHeaderFallbackIcon(size);
		}

		if (ImGui.IsItemHovered()) {
			ImGui.BeginTooltip();
			ImGui.TextUnformatted("插件收纳栏");
			ImGui.TextDisabled(ModuleConfig.Collapsed ? "左键展开 / 右键更多" : "左键收起 / 右键更多");
			ImGui.EndTooltip();
		}

		if (clicked) {
			ModuleConfig.Collapsed = !ModuleConfig.Collapsed;
			ApplyOverlayState();
			SaveConfig(ModuleConfig);
		}

		if (ImGui.BeginPopupContextItem("ctx")) {
			if (ImGui.MenuItem(ModuleConfig.Collapsed ? "展开" : "收起")) {
				ModuleConfig.Collapsed = !ModuleConfig.Collapsed;
				ApplyOverlayState();
				SaveConfig(ModuleConfig);
			}

			if (ImGui.MenuItem("锁定位置", ModuleConfig.LockPosition)) {
				ModuleConfig.LockPosition = !ModuleConfig.LockPosition;
				ApplyOverlayState();
				SaveConfig(ModuleConfig);
			}

			ImGui.Separator();

			if (ImGui.MenuItem("打开配置"))
				RequestOpenOverlayConfig();

			if (ImGui.MenuItem("关闭收纳栏")) {
				ModuleConfig.DockOpen = false;
				ApplyOverlayState();
				SaveConfig(ModuleConfig);
			}

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

	private void DrawDockItemContextMacrosMenu(DockItem item, bool includeLeadingSeparator) {
		if (item.ContextMenuMacros == null || item.ContextMenuMacros.Count == 0)
			return;

		var hasEntries = false;
		for (var i = 0; i < item.ContextMenuMacros.Count; i++) {
			var macro = item.ContextMenuMacros[i];
			var command = (macro.Command ?? string.Empty).Trim();
			if (command.Length == 0)
				continue;

			if (!hasEntries) {
				if (includeLeadingSeparator)
					ImGui.Separator();
				ImGui.TextDisabled("宏命令");
				hasEntries = true;
			}

			var name = (macro.Name ?? string.Empty).Trim();
			var displayName = SanitizeMacroDisplay(name, 40);
			var label = string.IsNullOrWhiteSpace(displayName) ? $"宏 {i + 1}" : displayName;
			if (ImGui.MenuItem($"{label}##dock_macro_{i}"))
				TryExecuteClickCommand(command);
		}

		if (hasEntries)
			ImGui.Separator();
	}

	private static string SanitizeMacroDisplay(string input, int maxLength) {
		if (string.IsNullOrWhiteSpace(input) || maxLength <= 0)
			return string.Empty;

		var builder = new StringBuilder(input.Length);
		foreach (var ch in input) {
			if (ch < ' ' || ch == '\u007f')
				continue;

			builder.Append(ch);
			if (builder.Length >= maxLength)
				break;
		}

		var result = builder.ToString().Trim();
		if (builder.Length >= maxLength && maxLength > 3 && result.Length > maxLength - 3)
			result = result.Substring(0, Math.Max(0, maxLength - 3)).TrimEnd() + "...";

		return result;
	}

	private void DrawOneCommandDockItem(DockItem item) {
		var commandItemKey = item.InternalName?.Trim() ?? string.Empty;
		if (commandItemKey.Length == 0)
			return;

		ImGui.PushID($"cmd_{commandItemKey}");

		var customName = item.CustomDisplayName?.Trim() ?? string.Empty;
		var displayName = string.IsNullOrWhiteSpace(customName) ? "命令" : customName;

		var size = new Vector2(ModuleConfig.IconSize, ModuleConfig.IconSize);
		var tex = GetIconForCommand(item);
		var hasSeIcon = TryGetDockItemSeIcon(item, out var seIcon);
		var clicked = DrawDockItemButton(DockItemKind.Command, size, tex, hasSeIcon, seIcon, displayName);

		var cmd = (item.ClickCommand ?? string.Empty).Trim();
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

			if (ImGui.MenuItem("从收纳栏移除")) {
				ModuleConfig.Items.RemoveAll(x =>
					x.Kind == DockItemKind.Command &&
					x.InternalName.Equals(commandItemKey, StringComparison.OrdinalIgnoreCase));
				SaveConfig(ModuleConfig);
			}

			ImGui.EndPopup();
		}

		ImGui.PopID();
	}

	private void DrawOneDockItem(IExposedPlugin plugin, DockItem item) {
		ImGui.PushID(plugin.InternalName);

		var customName = item.CustomDisplayName?.Trim() ?? string.Empty;
		var displayName = string.IsNullOrWhiteSpace(customName) ? plugin.Name : customName;

		var size = new Vector2(ModuleConfig.IconSize, ModuleConfig.IconSize);
		var tex = GetIconFor(plugin, item);
		var hasSeIcon = TryGetDockItemSeIcon(item, out var seIcon);
		var clicked = DrawDockItemButton(DockItemKind.Plugin, size, tex, hasSeIcon, seIcon, displayName);

		if (ImGui.IsItemHovered()) {
			ImGui.BeginTooltip();
			ImGui.TextUnformatted(displayName);
			if (!string.IsNullOrWhiteSpace(customName) &&
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

			if (ImGui.MenuItem("从收纳栏移除")) {
				ModuleConfig.Items.RemoveAll(x =>
					x.Kind == DockItemKind.Plugin &&
					x.InternalName.Equals(plugin.InternalName, StringComparison.OrdinalIgnoreCase));
				SaveConfig(ModuleConfig);
			}

			ImGui.EndPopup();
		}

		ImGui.PopID();
	}

	private void DrawOneImGuiWindowDockItem(DockItem item, IExposedPlugin? linkedPlugin) {
		var windowName = item.InternalName?.Trim() ?? string.Empty;
		if (string.IsNullOrWhiteSpace(windowName))
			return;

		ImGui.PushID($"imguiwin_{windowName}");

		var size = new Vector2(ModuleConfig.IconSize, ModuleConfig.IconSize);
		var tex = GetIconForImGuiWindow(item, linkedPlugin);
		var hasSeIcon = TryGetDockItemSeIcon(item, out var seIcon);
		var clicked = DrawDockItemButton(DockItemKind.ImGuiWindow, size, tex, hasSeIcon, seIcon, "?");

		var customName = item.CustomDisplayName?.Trim() ?? string.Empty;
		var displayName = string.IsNullOrWhiteSpace(customName) ? windowName : customName;

		var permanentlyHidden = IsImGuiWindowInHiddenListByKey(windowName);
		var configuredToggleTarget = item.ToggleTargetWindowName?.Trim() ?? string.Empty;
		var hasConfiguredToggleTarget = !string.IsNullOrWhiteSpace(configuredToggleTarget);

		var toggleTargetWindowName = hasConfiguredToggleTarget ? configuredToggleTarget : windowName;
		var autoSmallIconTarget = false;
		IWindow? windowManagerToggleTarget = null;

		if (item.ToggleOnClick && !hasConfiguredToggleTarget &&
		    windowName.EndsWith(SmallIconSuffix, StringComparison.OrdinalIgnoreCase) &&
		    TryResolveDailyRoutinesSmallIconTarget(windowName, out var autoTarget)) {
			windowManagerToggleTarget = autoTarget;
			toggleTargetWindowName = autoTarget.WindowName;
			autoSmallIconTarget = true;
		} else if (TryGetWindowManagerWindow(toggleTargetWindowName, out var wmTarget)) {
			windowManagerToggleTarget = wmTarget;
			toggleTargetWindowName = wmTarget.WindowName;
		}

		var toggleTargetHidden = IsImGuiWindowInHiddenListByKey(toggleTargetWindowName);
		var simplifiedTooltip = !string.IsNullOrWhiteSpace(customName);
		if (ImGui.IsItemHovered()) {
			ImGui.BeginTooltip();
			ImGui.TextUnformatted(displayName);
			if (!simplifiedTooltip) {
				if (!toggleTargetWindowName.Equals(windowName, StringComparison.OrdinalIgnoreCase))
					ImGui.TextDisabled($"目标窗口：{toggleTargetWindowName}");
				if (autoSmallIconTarget)
					ImGui.TextDisabled("目标窗口：自动（DailyRoutines SmallIcon -> 模块悬浮窗）");
				if (linkedPlugin != null)
					ImGui.TextDisabled($"{linkedPlugin.Name} ({linkedPlugin.InternalName})");
			}

			if (item.ToggleOnClick) {
				if (simplifiedTooltip) {
					ImGui.TextDisabled("左键：显示/隐藏");

					var showing = !toggleTargetHidden &&
					              (windowManagerToggleTarget == null || windowManagerToggleTarget.IsOpen);
					var statusText = showing
						? "状态：显示中（点击隐藏）"
						: toggleTargetHidden
							? "状态：隐藏中（点击显示）"
							: "状态：已关闭（点击打开）";
					if (!showing && !toggleTargetHidden)
						statusText += "（如点击无效，请先在原插件中手动启用该窗口）";

					ImGui.TextDisabled(statusText);
				} else {
					ImGui.TextDisabled("左键：显示/隐藏（写入隐藏列表）");
					ImGui.TextDisabled(toggleTargetHidden
						? "状态：隐藏中（点击显示）"
						: windowManagerToggleTarget != null && !windowManagerToggleTarget.IsOpen
							? "状态：已关闭（点击打开）"
							: "状态：显示中（点击隐藏）");
					if (windowManagerToggleTarget != null)
						ImGui.TextDisabled(windowManagerToggleTarget.IsOpen ? "WindowSystem：已打开" : "WindowSystem：已关闭");
				}
			} else {
				if (simplifiedTooltip) {
					ImGui.TextDisabled("左键：显示/隐藏");

					var closedInWindowSystem = TryGetWindowManagerWindow(windowName, out var statusWindow) && !statusWindow.IsOpen;
					var showing = !permanentlyHidden && !closedInWindowSystem;
					var statusText = showing
						? "状态：显示中（点击隐藏）"
						: permanentlyHidden
							? "状态：隐藏中（点击显示）"
							: "状态：已关闭（点击打开）";
					if (!showing && !permanentlyHidden)
						statusText += "（如点击无效，请先在原插件中手动启用该窗口）";

					ImGui.TextDisabled(statusText);
				} else {
					ImGui.TextDisabled("左键：显示/隐藏（写入隐藏列表）");
					ImGui.TextDisabled(permanentlyHidden
						? "状态：隐藏中（点击显示）"
						: TryGetWindowManagerWindow(windowName, out var statusWindow) && !statusWindow.IsOpen
							? "状态：已关闭（点击打开）"
							: "状态：显示中（点击隐藏）");
				}
			}
			ImGui.EndTooltip();
		}

		if (clicked) {
			TryExecuteClickCommand(item.ClickCommand);

			if (item.ToggleOnClick) {
				var targetKey = NormalizeImGuiWindowNameKey(toggleTargetWindowName);
				if (windowManagerToggleTarget != null && targetKey.Length > 0)
					toggleOnClickOpenedTargets.Remove(targetKey);

				var openedFallback = targetKey.Length > 0 && toggleOnClickOpenedTargets.Contains(targetKey);
				var opened = windowManagerToggleTarget?.IsOpen ?? openedFallback;
				var shouldOpen = toggleTargetHidden || !opened;

				if (shouldOpen) {
					if (linkedPlugin != null)
						TryOpenPlugin(linkedPlugin, item.PreferConfigUiOnClick);

					if (windowManagerToggleTarget != null)
						windowManagerToggleTarget.IsOpen = true;
					else if (targetKey.Length > 0)
						toggleOnClickOpenedTargets.Add(targetKey);

					var focusKey = NormalizeImGuiWindowNameKey(toggleTargetWindowName);
					ShowAndFocusImGuiWindowTemporary(focusKey.Length > 0 ? focusKey : toggleTargetWindowName);
					SetImGuiWindowHiddenByKey(toggleTargetWindowName, false);
				} else {
					SetImGuiWindowHiddenByKey(toggleTargetWindowName, true);
					if (windowManagerToggleTarget != null)
						windowManagerToggleTarget.IsOpen = false;
					if (targetKey.Length > 0)
						toggleOnClickOpenedTargets.Remove(targetKey);
				}
			} else {
				var key = NormalizeImGuiWindowNameKey(windowName);
				IWindow? windowManagerWindow = null;
				if (TryGetWindowManagerWindow(windowName, out var wmWindow)) {
					windowManagerWindow = wmWindow;
					var wmKey = NormalizeImGuiWindowNameKey(wmWindow.WindowName);
					if (wmKey.Length > 0)
						toggleOnClickOpenedTargets.Remove(wmKey);
				}

				var openedFallback = key.Length > 0 && toggleOnClickOpenedTargets.Contains(key);
				var opened = windowManagerWindow?.IsOpen ?? openedFallback;
				var shouldOpen = permanentlyHidden || !opened;
				var effectiveWindowName = windowManagerWindow?.WindowName ?? windowName;

				if (shouldOpen) {
					if (linkedPlugin != null)
						TryOpenPlugin(linkedPlugin, item.PreferConfigUiOnClick);

					if (windowManagerWindow != null)
						windowManagerWindow.IsOpen = true;
					else if (key.Length > 0)
						toggleOnClickOpenedTargets.Add(key);

					var focusKey = NormalizeImGuiWindowNameKey(effectiveWindowName);
					ShowAndFocusImGuiWindowTemporary(focusKey.Length > 0 ? focusKey : effectiveWindowName);
					SetImGuiWindowHiddenByKey(effectiveWindowName, false);
				} else {
					SetImGuiWindowHiddenByKey(effectiveWindowName, true);
					if (windowManagerWindow != null)
						windowManagerWindow.IsOpen = false;
					if (key.Length > 0)
						toggleOnClickOpenedTargets.Remove(key);
				}
			}
		}

		if (ImGui.BeginPopupContextItem("ctx")) {
			if (item.ToggleOnClick) {
				var targetKey = NormalizeImGuiWindowNameKey(toggleTargetWindowName);
				if (windowManagerToggleTarget != null && targetKey.Length > 0)
					toggleOnClickOpenedTargets.Remove(targetKey);

				var openedFallback = targetKey.Length > 0 && toggleOnClickOpenedTargets.Contains(targetKey);
				var opened = windowManagerToggleTarget?.IsOpen ?? openedFallback;
				var shouldOpen = toggleTargetHidden || !opened;

				if (ImGui.MenuItem(shouldOpen ? "显示目标窗口" : "隐藏目标窗口")) {
					TryExecuteClickCommand(item.ClickCommand);

					if (shouldOpen) {
						if (linkedPlugin != null)
							TryOpenPlugin(linkedPlugin, item.PreferConfigUiOnClick);

						if (windowManagerToggleTarget != null)
							windowManagerToggleTarget.IsOpen = true;
						else if (targetKey.Length > 0)
							toggleOnClickOpenedTargets.Add(targetKey);

						var focusKey = NormalizeImGuiWindowNameKey(toggleTargetWindowName);
						ShowAndFocusImGuiWindowTemporary(focusKey.Length > 0 ? focusKey : toggleTargetWindowName);
						SetImGuiWindowHiddenByKey(toggleTargetWindowName, false);
					} else {
						SetImGuiWindowHiddenByKey(toggleTargetWindowName, true);
						if (windowManagerToggleTarget != null)
							windowManagerToggleTarget.IsOpen = false;
						if (targetKey.Length > 0)
							toggleOnClickOpenedTargets.Remove(targetKey);
					}
				}

				ImGui.Separator();
			}

			if (ImGui.MenuItem(permanentlyHidden ? "显示窗口" : "隐藏窗口"))
				SetImGuiWindowHiddenByKey(windowName, !permanentlyHidden);

			DrawDockItemContextMacrosMenu(item, true);

			if (ImGui.MenuItem("从收纳中移除")) {
				SetImGuiWindowHiddenByKey(windowName, false, false);
				SetImGuiWindowHiddenByKey(toggleTargetWindowName, false, false);

				ModuleConfig.Items.RemoveAll(x =>
					x.Kind == DockItemKind.ImGuiWindow &&
					x.InternalName.Equals(windowName, StringComparison.OrdinalIgnoreCase));
				SaveConfig(ModuleConfig);
			}

			ImGui.EndPopup();
		}

		ImGui.PopID();
	}

	private static void PushTransparentIconButtonStyle() {
		ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
		ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
		ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.15f));
		ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 1f, 1f, 0.25f));
	}

	private static void PopTransparentIconButtonStyle() {
		ImGui.PopStyleColor(3);
		ImGui.PopStyleVar(2);
	}

	private static Vector4 GetDockItemKindColor(DockItemKind kind) {
		return kind switch {
			DockItemKind.ImGuiWindow => DockItemKindColorWindow,
			DockItemKind.Command => DockItemKindColorCommand,
			_ => DockItemKindColorPlugin
		};
	}

	private bool TryGetDockItemSeIcon(DockItem item, out SeIconChar seIcon) {
		if (item.CustomGameIconId != 0 || !string.IsNullOrWhiteSpace(item.CustomIconPath)) {
			seIcon = default;
			return false;
		}

		return TryGetSeIconChar(item.CustomSeIconChar, out seIcon);
	}

	private bool DrawDockItemButton(
		DockItemKind kind,
		Vector2 size,
		ISharedImmediateTexture? texture,
		bool hasSeIcon,
		SeIconChar seIcon,
		string fallbackText) {
		if (ModuleConfig.TransparentButtons)
			PushTransparentIconButtonStyle();

		ImGui.PushStyleColor(ImGuiCol.Border, GetDockItemKindColor(kind));
		ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);

		bool clicked;
		if (hasSeIcon)
			clicked = ImGui.Button(seIcon.ToIconString(), size);
		else if (texture != null && texture.TryGetWrap(out var wrap, out _))
			clicked = ImGui.ImageButton(wrap.Handle, size);
		else
			clicked = ImGui.Button(fallbackText, size);

		ImGui.PopStyleVar();
		ImGui.PopStyleColor();

		if (ModuleConfig.TransparentButtons)
			PopTransparentIconButtonStyle();

		return clicked;
	}

	private static string NormalizeImGuiWindowNameKey(string windowName) {
		var trimmed = (windowName ?? string.Empty).Trim();
		if (trimmed.Length == 0)
			return string.Empty;

		var idx = trimmed.IndexOf("###", StringComparison.Ordinal);
		return idx >= 0 ? trimmed.Substring(idx) : trimmed;
	}

	private string GetImGuiWindowNameKeyCached(string windowNameOrId) {
		if (string.IsNullOrWhiteSpace(windowNameOrId))
			return string.Empty;

		var trimmed = windowNameOrId.Trim();
		if (trimmed.Length == 0)
			return string.Empty;

		if (imGuiWindowKeyCacheByName.TryGetValue(trimmed, out var cached))
			return cached;

		var key = NormalizeImGuiWindowNameKey(trimmed);
		imGuiWindowKeyCacheByName[trimmed] = key;
		return key;
	}

	private unsafe void ApplyHardHiddenImGuiWindowsByKeys(HashSet<string> keysToHide) {
		if (keysToHide.Count == 0)
			return;

		try {
			var ctxPtr = ImGuiNative.GetCurrentContext();
			if (ctxPtr == null)
				return;

			var ctx = new ImGuiContextPtr(ctxPtr);
			if (ctx.IsNull)
				return;

			ref var windows = ref ctx.Windows;
			for (var i = 0; i < windows.Size; i++) {
				var window = windows[i];
				if (window.Handle == null)
					continue;

				var namePtr = window.Name;
				if (namePtr == null)
					continue;

				var nameAddress = (nint)namePtr;
				if (!imGuiWindowKeyCacheByNamePtr.TryGetValue(nameAddress, out var windowKey)) {
					var name = Marshal.PtrToStringUTF8((IntPtr)namePtr) ?? string.Empty;
					windowKey = NormalizeImGuiWindowNameKey(name);
					imGuiWindowKeyCacheByNamePtr[nameAddress] = windowKey;
				}

				if (windowKey.Length == 0 || !keysToHide.Contains(windowKey))
					continue;

				const sbyte frameCount = 2;
				window.Hidden = true;
				window.SkipItems = true;
				window.HiddenFramesCanSkipItems = frameCount;
				window.HiddenFramesCannotSkipItems = frameCount;
				window.HiddenFramesForRenderOnly = frameCount;

				try {
					ref var drawList = ref window.DrawList;
					drawList.CmdBuffer.Clear();
					drawList.IdxBuffer.Clear();
					drawList.VtxBuffer.Clear();
				} catch {
				}
			}
		} catch {
		}
	}

	private static unsafe bool TrySetImGuiWindowHardHiddenByKey(string windowNameOrId, bool hidden) {
		if (string.IsNullOrWhiteSpace(windowNameOrId))
			return false;

		var key = NormalizeImGuiWindowNameKey(windowNameOrId);
		if (key.Length == 0)
			return false;

		try {
			var ctxPtr = ImGuiNative.GetCurrentContext();
			if (ctxPtr == null)
				return false;

			var ctx = new ImGuiContextPtr(ctxPtr);
			if (ctx.IsNull)
				return false;

			var changed = false;
			ref var windows = ref ctx.Windows;

			for (var i = 0; i < windows.Size; i++) {
				var window = windows[i];
				if (window.Handle == null)
					continue;

				var namePtr = window.Name;
				if (namePtr == null)
					continue;

				var name = Marshal.PtrToStringUTF8((IntPtr)namePtr) ?? string.Empty;
				if (!NormalizeImGuiWindowNameKey(name).Equals(key, StringComparison.OrdinalIgnoreCase))
					continue;

				if (hidden) {
					const sbyte frameCount = 2;

					window.Hidden = true;
					window.SkipItems = true;
					window.HiddenFramesCanSkipItems = frameCount;
					window.HiddenFramesCannotSkipItems = frameCount;
					window.HiddenFramesForRenderOnly = frameCount;

					try {
						ref var drawList = ref window.DrawList;
						drawList.CmdBuffer.Clear();
						drawList.IdxBuffer.Clear();
						drawList.VtxBuffer.Clear();
					} catch {
					}
				} else {
					window.Hidden = false;
					window.SkipItems = false;
					window.HiddenFramesCanSkipItems = 0;
					window.HiddenFramesCannotSkipItems = 0;
					window.HiddenFramesForRenderOnly = 0;
				}

				changed = true;
			}

			return changed;
		} catch {
			return false;
		}
	}

	private bool TryGetWindowManagerWindow(string windowNameOrId, out IWindow window) {
		if (TryGetWindowFromSystem(windowSystem, windowNameOrId, out window))
			return true;

		var dailySystem = TryGetDailyRoutinesWindowSystem();
		return dailySystem != null && TryGetWindowFromSystem(dailySystem, windowNameOrId, out window);
	}

	private static bool TryGetWindowFromSystem(WindowSystem system, string windowNameOrId, out IWindow window) {
		window = null!;

		if (string.IsNullOrWhiteSpace(windowNameOrId))
			return false;

		var key = NormalizeImGuiWindowNameKey(windowNameOrId);
		if (key.Length == 0)
			return false;

		try {
			foreach (var w in system.Windows) {
				if (NormalizeImGuiWindowNameKey(w.WindowName).Equals(key, StringComparison.OrdinalIgnoreCase)) {
					window = w;
					return true;
				}
			}
		} catch {
		}

		return false;
	}

	private static void CollectOpenWindowKeys(WindowSystem system, HashSet<string> keys) {
		foreach (var w in system.Windows) {
			if (!w.IsOpen)
				continue;

			var key = NormalizeImGuiWindowNameKey(w.WindowName);
			if (key.Length > 0)
				keys.Add(key);
		}
	}

	private static WindowSystem? TryGetDailyRoutinesWindowSystem() {
		try {
			var assembly = AppDomain.CurrentDomain.GetAssemblies()
				.FirstOrDefault(a => string.Equals(a.GetName().Name, "DailyRoutines", StringComparison.OrdinalIgnoreCase));
			if (assembly == null)
				return null;

			var type = assembly.GetType("DailyRoutines.Managers.WindowManager");
			var prop = type?.GetProperty("WindowSystem", BindingFlags.Public | BindingFlags.Static);
			return prop?.GetValue(null) as WindowSystem;
		} catch {
			return null;
		}
	}

	private static string? TryResolveDailyRoutinesOverlayKey(string moduleName) {
		try {
			var assembly = AppDomain.CurrentDomain.GetAssemblies()
				.FirstOrDefault(a => string.Equals(a.GetName().Name, "DailyRoutines", StringComparison.OrdinalIgnoreCase));
			if (assembly == null)
				return null;

			var type = assembly.GetType("DailyRoutines.Managers.ModuleManager");
			var method = type?.GetMethod("GetModuleByName", BindingFlags.Public | BindingFlags.Static);
			if (method == null)
				return null;

			var module = method.Invoke(null, new object?[] { moduleName });
			if (module == null)
				return null;

			return "###" + module;
		} catch {
			return null;
		}
	}

	private bool TryResolveDailyRoutinesSmallIconTarget(string smallIconWindowName, out IWindow window) {
		window = null!;

		if (string.IsNullOrWhiteSpace(smallIconWindowName))
			return false;

		var trimmed = smallIconWindowName.Trim();
		if (!trimmed.EndsWith(SmallIconSuffix, StringComparison.OrdinalIgnoreCase))
			return false;

		var moduleName = trimmed.Substring(0, trimmed.Length - SmallIconSuffix.Length).Trim();
		if (moduleName.Length == 0)
			return false;

		var system = TryGetDailyRoutinesWindowSystem();
		if (system == null)
			return false;

		var overlayKey = TryResolveDailyRoutinesOverlayKey(moduleName) ?? "###" + moduleName;
		return TryGetWindowFromSystem(system, overlayKey, out window);
	}

	private void ClearTransientHiddenByKey(string windowNameOrId) {
		var key = NormalizeImGuiWindowNameKey(windowNameOrId);
		if (key.Length == 0)
			return;

		foreach (var name in transientHiddenImGuiWindows.ToArray()) {
			if (!NormalizeImGuiWindowNameKey(name).Equals(key, StringComparison.OrdinalIgnoreCase))
				continue;

			SetImGuiWindowTransientHidden(name, false);
		}
	}

	private void RemoveTransientHiddenByKey(string windowNameOrId) {
		var key = NormalizeImGuiWindowNameKey(windowNameOrId);
		if (key.Length == 0)
			return;

		foreach (var name in transientHiddenImGuiWindows.ToArray()) {
			if (!NormalizeImGuiWindowNameKey(name).Equals(key, StringComparison.OrdinalIgnoreCase))
				continue;

			transientHiddenImGuiWindows.Remove(name);
			transientHiddenImGuiWindowRestoreStates.Remove(name);
		}
	}

	private static bool TryGetSeIconChar(string? raw, out SeIconChar icon) {
		icon = default;

		var text = (raw ?? string.Empty).Trim();
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
		var customPath = ModuleConfig.DockHeaderIconPath?.Trim() ?? string.Empty;
		if (customPath.Length > 0) {
			if (Path.IsPathRooted(customPath)) {
				if (textureCache.TryGetValue(customPath, out var cached))
					return cached;

				if (File.Exists(customPath)) {
					var texture = DService.Texture.GetFromFileAbsolute(customPath);
					textureCache[customPath] = texture;
					return texture;
				}
			}
		}

		return GetFallbackPluginIcon();
	}

	private ISharedImmediateTexture GetIconForCommand(DockItem item) {
		var gameIcon = GetGameIconTexture(item.CustomGameIconId);
		if (gameIcon != null)
			return gameIcon;

		var resolvedPath = ResolveCustomIconPath(item.CustomIconPath);
		if (string.IsNullOrWhiteSpace(resolvedPath) || !Path.IsPathRooted(resolvedPath))
			return GetFallbackPluginIcon();

		if (textureCache.TryGetValue(resolvedPath, out var cached))
			return cached;

		if (!File.Exists(resolvedPath))
			return GetFallbackPluginIcon();

		var texture = DService.Texture.GetFromFileAbsolute(resolvedPath);
		textureCache[resolvedPath] = texture;
		return texture;
	}

	private ISharedImmediateTexture GetIconForImGuiWindow(DockItem item, IExposedPlugin? linkedPlugin) {
		var gameIcon = GetGameIconTexture(item.CustomGameIconId);
		if (gameIcon != null)
			return gameIcon;

		if (linkedPlugin != null) {
			var fromPlugin = GetIconFor(linkedPlugin, item);
			if (fromPlugin != null)
				return fromPlugin;
		}

		var resolvedPath = ResolveIconPathForImGuiWindow(item);
		if (string.IsNullOrWhiteSpace(resolvedPath) || !Path.IsPathRooted(resolvedPath))
			return GetFallbackPluginIcon();

		if (textureCache.TryGetValue(resolvedPath, out var cached))
			return cached;

		if (!File.Exists(resolvedPath))
			return GetFallbackPluginIcon();

		var texture = DService.Texture.GetFromFileAbsolute(resolvedPath);
		textureCache[resolvedPath] = texture;
		return texture;
	}

	private string? ResolveIconPathForImGuiWindow(DockItem item) => ResolveCustomIconPath(item.CustomIconPath);

	private ISharedImmediateTexture? GetGameIconTexture(uint iconId) {
		if (iconId == 0)
			return null;

		if (gameIconCache.TryGetValue(iconId, out var cached))
			return cached;

		try {
			var texture = DService.Texture.GetFromGameIcon(new GameIconLookup(iconId));
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
		if (string.IsNullOrWhiteSpace(resolvedPath) || !Path.IsPathRooted(resolvedPath))
			return GetFallbackPluginIcon();

		if (textureCache.TryGetValue(resolvedPath, out var cached))
			return cached;

		if (!File.Exists(resolvedPath))
			return GetFallbackPluginIcon();

		var texture = DService.Texture.GetFromFileAbsolute(resolvedPath);
		textureCache[resolvedPath] = texture;
		return texture;
	}

	private ISharedImmediateTexture GetFallbackPluginIcon() {
		if (fallbackPluginIcon != null)
			return fallbackPluginIcon;

		try {
			var bundledIcon = TryResolveBundledIconPath();
			if (!string.IsNullOrWhiteSpace(bundledIcon) && File.Exists(bundledIcon))
				return fallbackPluginIcon = DService.Texture.GetFromFileAbsolute(bundledIcon);
		} catch {
		}

		try {
			var assetsDir = DService.PI.DalamudAssetDirectory.FullName;
			var defaultIcon = Path.Combine(assetsDir, "3", "UIRes", "defaultIcon.png");
			if (!File.Exists(defaultIcon)) {
				defaultIcon = Directory.EnumerateFiles(assetsDir, "defaultIcon.png", SearchOption.AllDirectories)
					.FirstOrDefault() ?? defaultIcon;
			}

			if (File.Exists(defaultIcon))
				return fallbackPluginIcon = DService.Texture.GetFromFileAbsolute(defaultIcon);
		} catch {
		}

		try {
			return fallbackPluginIcon = DService.Texture.GetFromGameIcon(new GameIconLookup(60001));
		} catch {
			return fallbackPluginIcon = DService.Texture.GetFromGameIcon(new GameIconLookup(1));
		}
	}

	private static string? TryResolveBundledIconPath() {
		try {
			var assemblyLocation = DService.PI.AssemblyLocation;
			var baseDir = assemblyLocation.DirectoryName ?? string.Empty;
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
		var custom = ResolveCustomIconPath(item.CustomIconPath);
		if (!string.IsNullOrWhiteSpace(custom))
			return custom;

		var localPath = GetLocalIconPath(plugin);
		if (!string.IsNullOrWhiteSpace(localPath))
			return localPath;

		var cachedUrlPath = GetCachedIconPathFromIconUrl(plugin);
		if (!string.IsNullOrWhiteSpace(cachedUrlPath))
			return cachedUrlPath;

		return null;
	}

	private string? ResolveCustomIconPath(string? rawPath) {
		var raw = (rawPath ?? string.Empty).Trim();
		if (raw.Length == 0)
			return null;

		if (raw.StartsWith("dalamud:", StringComparison.OrdinalIgnoreCase) ||
		    raw.StartsWith("sys:", StringComparison.OrdinalIgnoreCase)) {
			var idx = raw.IndexOf(':');
			var relative = idx >= 0 ? raw.Substring(idx + 1).Trim() : string.Empty;
			if (relative.Length == 0)
				return FindDalamudDefaultIconPath();

			relative = relative.Replace('/', Path.DirectorySeparatorChar)
				.Replace('\\', Path.DirectorySeparatorChar)
				.TrimStart(Path.DirectorySeparatorChar);

			try {
				var assetsDir = DService.PI.DalamudAssetDirectory.FullName;
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
			var assetsDir = DService.PI.DalamudAssetDirectory.FullName;
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
			return string.IsNullOrWhiteSpace(cached) ? null : cached;

		var iconPath = FindPluginIcon(plugin);
		localIconPathCache[plugin.InternalName] = iconPath ?? string.Empty;
		return iconPath;
	}

	private string? GetCachedIconPathFromIconUrl(IExposedPlugin plugin) {
		var iconUrl = plugin.Manifest.IconUrl;
		if (string.IsNullOrWhiteSpace(iconUrl))
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
		if (string.IsNullOrWhiteSpace(ext))
			return false;

		return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
		       ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
		       ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
	}

	private static string SanitizeFileName(string name) {
		if (string.IsNullOrEmpty(name))
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
				DService.Log.Warning(ex, $"PluginDock 图标下载失败: {internalName}");
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
			if (string.IsNullOrWhiteSpace(versionDir) || !Directory.Exists(versionDir))
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
		var trimmed = (command ?? string.Empty).Trim();
		if (trimmed.Length == 0)
			return;

		if (!TryNormalizeClickCommand(trimmed, out var normalized, out var normalizeError)) {
			if (!string.IsNullOrWhiteSpace(normalizeError))
				HelpersOm.NotificationError(normalizeError);
			return;
		}

		try {
			if (DService.Framework.IsInFrameworkUpdateThread) {
				ExecuteCommandInternal(normalized);
				return;
			}

			_ = DService.Framework.RunOnFrameworkThread(() => ExecuteCommandInternal(normalized));
		} catch (Exception ex) {
			DService.Log.Error(ex, $"PluginDock 执行命令失败: {normalized}");
			HelpersOm.NotificationError($"执行命令失败: {normalized}");
		}
	}

	private static void ExecuteCommandInternal(string normalized) {
		try {
			var handled = DService.Command.ProcessCommand(normalized);
			if (!handled)
				TryFallbackEchoCommand(normalized);
		} catch (Exception ex) {
			DService.Log.Error(ex, $"PluginDock 执行命令失败: {normalized}");
			HelpersOm.NotificationError($"执行命令失败: {normalized}");
		}
	}

	private static void TryFallbackEchoCommand(string command) {
		var text = (command ?? string.Empty).Trim();
		if (text.Length == 0)
			return;

		if (text[0] == '\\')
			text = "/" + text.Substring(1);

		if (!text.StartsWith("/", StringComparison.Ordinal))
			return;

		var spaceIndex = text.IndexOf(' ');
		var commandName = spaceIndex > 1 ? text.Substring(1, spaceIndex - 1) : text.Substring(1);
		var args = spaceIndex > 0 && spaceIndex + 1 < text.Length ? text.Substring(spaceIndex + 1) : string.Empty;

		if (!commandName.Equals("e", StringComparison.OrdinalIgnoreCase) &&
		    !commandName.Equals("echo", StringComparison.OrdinalIgnoreCase))
			return;

		if (args.Length == 0)
			return;

		var entry = new XivChatEntry {
			Type = XivChatType.Echo,
			Message = new SeStringBuilder().AddText(args).Build()
		};
		DService.Chat.Print(entry);
	}

	private static bool TryNormalizeClickCommand(string input, out string normalized, out string error) {
		normalized = string.Empty;
		error = string.Empty;

		var text = (input ?? string.Empty).Trim();
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
			error = "命令解析为空（请填写 /xxx 或 DService.Command.ProcessCommand(\"/xxx\")）";
			return false;
		}

		if (text[0] == '／')
			text = "/" + text.Substring(1);
		else if (text[0] == '＼')
			text = "\\" + text.Substring(1);

		if (!text.StartsWith("/", StringComparison.Ordinal) &&
		    !text.StartsWith("\\", StringComparison.Ordinal))
			text = "/" + text;

		normalized = text;
		return true;
	}

	private static void TryOpenPlugin(IExposedPlugin plugin, bool preferConfig) {
		try {
			if (preferConfig) {
				if (plugin.HasConfigUi) {
					plugin.OpenConfigUi();
					return;
				}

				if (plugin.HasMainUi) {
					plugin.OpenMainUi();
					return;
				}

				plugin.OpenConfigUi();
				plugin.OpenMainUi();
				return;
			}

			if (plugin.HasMainUi) {
				plugin.OpenMainUi();
				return;
			}

			if (plugin.HasConfigUi) {
				plugin.OpenConfigUi();
				return;
			}

			plugin.OpenMainUi();
			plugin.OpenConfigUi();
		} catch (Exception ex) {
			DService.Log.Error(ex, $"打开插件界面失败: {plugin.InternalName}");
		}
	}
}